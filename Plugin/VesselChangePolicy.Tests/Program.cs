using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    Console.WriteLine((cond ? "ok   " : "FAIL ") + msg);
    if (!cond) failures++;
}

// Going EVA fires onVesselChange (control follows the kerbal onto the EVA
// vessel), but the ship stays loaded and in physics range — like a dock/stage
// part-set change. So an EVA-entry switch must be ADDITIVE (disposeMissing
// false), retaining the ship's cameras, whereas a real craft switch keeps the
// deliberate scope-and-drop (disposeMissing true).
Check(VesselChangePolicy.DisposeMissingOnVesselChange(newVesselIsEva: true) == false,
    "going EVA is additive (retain existing cameras)");
Check(VesselChangePolicy.DisposeMissingOnVesselChange(newVesselIsEva: false) == true,
    "switching to a non-EVA craft scopes-and-drops as before");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
