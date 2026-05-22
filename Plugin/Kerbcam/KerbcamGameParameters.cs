// KSP Difficulty Settings integration for kerbcam's runtime-toggleable
// flags. Lives in the per-save game state via KSP's
// `GameParameters.CustomParameterNode` API, so the toggles persist with
// each save independently — a gonogo-driven save can keep
// ThrottleMainScreen on while a regular career save keeps it off.
//
// Default values are seeded from `settings.cfg` (KerbcamSettings) the
// first time a save loads with kerbcam present. After that the value
// written into the save by the operator (via Pause → Difficulty Settings
// → Kerbcam) wins on every subsequent load. Sync between settings.cfg
// and saves is one-way: settings.cfg is the default for *new* saves,
// not a live source of truth that overrides existing ones.

using UnityEngine;

namespace Kerbcam
{
    public class KerbcamGameParameters : GameParameters.CustomParameterNode
    {
        public override string Title => "Kerbcam";
        public override string DisplaySection => "Kerbcam";
        public override string Section => "Kerbcam";
        public override int SectionOrder => 1;
        public override GameParameters.GameMode GameMode => GameParameters.GameMode.ANY;
        public override bool HasPresets => false;

        // Per-save throttle of the KSP main flight render. When true,
        // the plugin disables Camera 00 / Camera ScaledSpace /
        // GalaxyCamera so their per-frame work doesn't compete with
        // kerbcam's per-Hullcam encode pipeline. Pop-up GUI overlay
        // tells the operator how to undo. Settings.cfg
        // `ThrottleMainScreen` seeds the default on a new save.
        [GameParameters.CustomParameterUI(
            "Throttle KSP main render",
            toolTip =
                "Disable KSP's main flight cameras to free GPU/CPU for kerbcam streams. " +
                "Designed for gonogo / mission-control workflows where the operator " +
                "watches the dashboard, not the KSP viewport. UI, staging, kOS terminal, " +
                "and physics all still work. You can't click on parts in the world while " +
                "throttled."
        )]
        public bool ThrottleMainScreen;

        // Override default-init so newly-created saves pick up the
        // settings.cfg seed. Existing saves with a stored value
        // ignore this (the loaded ConfigNode wins).
        public override void SetDifficultyPreset(GameParameters.Preset preset)
        {
            // Presets don't apply — the value is independent of
            // Easy/Normal/Hard. Falls through to the field default
            // and the settings.cfg seed below.
        }

        public KerbcamGameParameters()
        {
            // Seed from settings.cfg the first time this node is
            // constructed (i.e. before Load() restores a stored value
            // from the save file). KSP calls Load() right after
            // construction, which will overwrite this if the save had
            // the field stored.
            ThrottleMainScreen = KerbcamSettings.SeedThrottleMainScreen;
        }
    }
}
