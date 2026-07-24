namespace Kerbcast
{
    /* Unity-free lifecycle policy for a vessel-change camera rebuild. Kept out of
       Unity-bound code so the decision is unit-testable. */
    public static class VesselChangePolicy
    {
        /* Whether a vessel-change rebuild should tear down cameras not on the new
           active vessel (scope-and-drop) or keep them (additive).

           A real craft switch scopes-and-drops (the deliberate "leaving a vessel
           drops its cameras" behaviour). But going EVA also fires onVesselChange
           (control follows the kerbal onto the EVA vessel) while the ship stays
           LOADED and in physics range — exactly like a dock/stage part-set change,
           which is additive (onVesselWasModified, disposeMissing=false). So an
           EVA-entry switch is additive too, retaining the ship's cameras; if the
           ship later unloads out of range, onPartDestroyed + the !IsAlive sweep
           tear those cameras down, so no special-casing is needed here. */
        public static bool DisposeMissingOnVesselChange(bool newVesselIsEva)
        {
            return !newVesselIsEva;
        }
    }
}
