using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

const uint TopBit = 0x80000000u;

// Module 0 uses the flightID (masked to 31 bits), name ignored.
Check(CameraId.Synthetic(100u, 0, "fwd") == 100u, "module 0 == baseId (top-bit clear)");
Check(CameraId.Synthetic(100u, 0, "aft") == 100u, "module 0 ignores cameraName");
// A flightID with the top bit set is masked into the part namespace.
Check(CameraId.Synthetic(0x80000001u, 0, "") == 1u, "module 0 masks top bit");
// Modules 1+ are deterministic and differ from the base id.
Check(CameraId.Synthetic(100u, 1, "fwd") == CameraId.Synthetic(100u, 1, "fwd"), "module 1 stable");
Check(CameraId.Synthetic(100u, 1, "fwd") != 100u, "module 1 != baseId");
// Base id and camera name both perturb the hash.
Check(CameraId.Synthetic(100u, 1, "fwd") != CameraId.Synthetic(101u, 1, "fwd"), "baseId perturbs");
Check(CameraId.Synthetic(100u, 1, "fwd") != CameraId.Synthetic(100u, 1, "aft"), "cameraName perturbs");

// PART ids always have the top bit CLEAR — even for full-32-bit flightIDs and
// multi-camera hashes (KSP flightIDs are Guid hashes, top bit set ~half the time).
Check((CameraId.Synthetic(0xFFFFFFFFu, 0, "") & TopBit) == 0, "part module 0 top-bit clear");
Check((CameraId.Synthetic(0xFFFFFFFFu, 3, "cam") & TopBit) == 0, "part module 3 top-bit clear");
for (uint b = 1; b < 5000; b += 7)
    if ((CameraId.Synthetic(b * 2654435761u, (int)(b % 4), "c") & TopBit) != 0)
        Check(false, $"part id top-bit set for b={b}");

// KERBAL ids always have the top bit SET, and are derived from the stable name.
Check((CameraId.KerbalWireId("Jebediah Kerman") & TopBit) != 0, "kerbal id top-bit set");
Check((CameraId.KerbalWireId("") & TopBit) != 0, "kerbal id top-bit set (empty)");
// Same name -> same id (continuity across seat<->EVA); different name -> different id.
Check(CameraId.KerbalWireId("Jebediah Kerman") == CameraId.KerbalWireId("Jebediah Kerman"), "same name -> same id");
Check(CameraId.KerbalWireId("Jebediah Kerman") != CameraId.KerbalWireId("Bill Kerman"), "different name -> different id");
// Namespaces are disjoint by construction: a kerbal id can never equal a part id.
Check(CameraId.KerbalWireId("Jebediah Kerman") != CameraId.Synthetic(100u, 0, ""), "kerbal disjoint from part (module 0)");
Check(CameraId.KerbalWireId("Bob Kerman") != CameraId.Synthetic(0xFFFFFFFFu, 2, "cam"), "kerbal disjoint from part (module 2)");

// Kerbal camera LABEL: prefer the display name, fall back to the roster name.
// A kerbal sourced fresh from the EVA part can have an empty displayName (the
// seated path populates it, the EVA-construction path may not), so the EVA POV
// cam must still carry the crew identity via the name the wire-id derives from.
Check(CameraId.KerbalCameraName("Jebediah Kerman", "Jebediah Kerman") == "Jebediah Kerman",
    "label uses displayName when present");
Check(CameraId.KerbalCameraName("", "Jebediah Kerman") == "Jebediah Kerman",
    "label falls back to roster name when displayName empty");
Check(CameraId.KerbalCameraName(null, "Bob Kerman") == "Bob Kerman",
    "label falls back to roster name when displayName null");
Check(CameraId.KerbalCameraName("Jeb (call sign)", "Jebediah Kerman") == "Jeb (call sign)",
    "label keeps a custom displayName over the roster name");
Check(CameraId.KerbalCameraName(null, null) == "",
    "label is empty (not null) when both are absent");

// IsKerbalId tells the two namespaces apart.
Check(CameraId.IsKerbalId(CameraId.KerbalWireId("Jebediah Kerman")), "IsKerbalId true for a kerbal id");
Check(!CameraId.IsKerbalId(CameraId.Synthetic(100u, 0, "")), "IsKerbalId false for a part id");
Check(!CameraId.IsKerbalId(CameraId.Synthetic(0xFFFFFFFFu, 2, "cam")), "IsKerbalId false for a masked part id");

// Command camera ids: stable, outside the part space, distinct from the
// kerbal id of the same string.
uint ksc = CameraId.CommandCameraWireId("KSC");
Check(ksc == CameraId.CommandCameraWireId("KSC"), "command camera id stable");
Check((ksc & TopBit) != 0, "command camera id top-bit set (never a part id)");
Check(ksc != CameraId.KerbalWireId("KSC"), "command camera id differs from a kerbal named the same");
Check(ksc != CameraId.CommandCameraWireId("Harvester Massif"), "different stations differ");
Check(CameraId.SiteHashKey("KSC") == "ground:KSC", "a site's hash input is ground:<name>, so ids never renumber");
Check(CameraId.SiteHashKey(null) == "ground:", "the hash input tolerates a null name");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
