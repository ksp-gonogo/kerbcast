namespace Kerbcast
{
    /* Unity-free camera wire-id helpers. Kept out of Unity-bound code so the
       pure id math is unit-testable and reusable by camera-source providers. */
    public static class CameraId
    {
        /* Bit 31 is the namespace tag: part ids clear it, kerbal ids set it, so
           the two id spaces are disjoint BY CONSTRUCTION. This does NOT rely on
           KSP flightIDs being "small" — they aren't:
           ShipConstruction.GetUniqueFlightID returns (uint)Guid.GetHashCode(), a
           full 32-bit value that sets bit 31 ~half the time. Masking part ids to
           31 bits is what guarantees the split; without it a multi-camera part's
           hash (or a raw flightID) could land on a kerbal id. */
        private const uint KerbalTag = 0x80000000u;
        private const uint PartMask = 0x7FFFFFFFu;

        /* Stable wire id for a camera module, in the PART namespace (bit 31 clear).
           Module 0 uses the part flightID; modules 1+ get a Knuth-hash mix of id +
           index + name so a multi-camera part yields distinct, stable ids. Masked
           to 31 bits so a part id can never collide with a kerbal id. */
        public static uint Synthetic(uint baseId, int moduleIdx, string cameraName)
        {
            if (moduleIdx == 0) return baseId & PartMask;
            unchecked
            {
                uint h = baseId;
                h = h * 2654435761u + (uint)moduleIdx;
                if (!string.IsNullOrEmpty(cameraName))
                {
                    foreach (var ch in cameraName) h = h * 2654435761u + ch;
                }
                return h & PartMask;
            }
        }

        /* Wire id for a kerbal camera, in the KERBAL namespace (bit 31 set).
           Derived from a stable hash of the kerbal's roster name, which is unique
           and stable across seat<->EVA — unlike persistentID, which KSP reassigns
           on EVA (GetUniquepersistentId). So the SAME kerbal maps to the SAME id
           whether seated or on EVA, giving one continuous feed. Uses the same
           deterministic Knuth mix as Synthetic, NOT String.GetHashCode (which is
           not stable across Mono / runs). */
        public static uint KerbalWireId(string kerbalName)
        {
            return (NameHash(kerbalName) & PartMask) | KerbalTag;
        }

        /* Wire id for a command camera. Shares the bit-31 space
           with kerbal ids rather than carving out a new tag, because every 31-bit
           value is already reachable by both existing namespaces: a third tag
           would mean re-hashing kerbal ids and breaking stored ones. The "ground:"
           prefix keys the hash on gonogo's centre id format, so the station
           and a kerbal can only meet on a 1-in-2^31 hash collision, which the
           provider logs. The manifest's kind, not the id, says which it is. */
        public static uint CommandCameraWireId(string stationNodeName)
        {
            return (NameHash(CommandCentreId(stationNodeName)) & PartMask) | KerbalTag;
        }

        /* gonogo's command-centre id for a CommNet home: "ground:<nodeName>". */
        public static string CommandCentreId(string stationNodeName)
        {
            return "ground:" + (stationNodeName ?? string.Empty);
        }

        /* True when a wire-id is in the KERBAL namespace (bit 31 set), i.e. a
           kerbal camera rather than a part camera. Lets Unity-free code tell the
           two id spaces apart without duplicating the tag constant. Command
           camera ids also set bit 31, so this reads "not a part camera". */
        public static bool IsKerbalId(uint flightId)
        {
            return (flightId & KerbalTag) != 0;
        }

        /* Human label for a kerbal camera. Prefers the display name, falling back
           to the roster name when displayName is empty. A kerbal sourced fresh
           from the EVA part can have an empty displayName (the seated path
           populates it, the EVA-construction path may not), which would leave the
           EVA POV camera unlabelled; the roster name is always populated and is
           the same lineage the wire-id derives from, so the label and the FlightId
           always agree. Returns "" (never null) when both are absent. */
        public static string KerbalCameraName(string displayName, string rosterName)
        {
            if (!string.IsNullOrEmpty(displayName)) return displayName;
            return string.IsNullOrEmpty(rosterName) ? string.Empty : rosterName;
        }

        private static uint NameHash(string name)
        {
            unchecked
            {
                uint h = 2166136261u; // fixed non-zero seed
                if (!string.IsNullOrEmpty(name))
                    foreach (var ch in name) h = h * 2654435761u + ch;
                return h;
            }
        }
    }
}
