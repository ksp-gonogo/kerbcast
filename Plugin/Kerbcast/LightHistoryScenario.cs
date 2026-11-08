namespace Kerbcast
{
    /* Carries the light-time history and the slowest light speed in the save file. */
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.SPACECENTER, GameScenes.FLIGHT, GameScenes.TRACKSTATION)]
    internal sealed class LightHistoryScenario : ScenarioModule
    {
        public override void OnSave(ConfigNode node) => Timed(Telemetry.Save, () => LightHistoryHost.SaveTo(node));
        public override void OnLoad(ConfigNode node) => Timed(Telemetry.Load, () => LightHistoryHost.LoadFrom(node));

        /* The telemetry block's saveMs and loadMs, timed only while telemetry is on. */
        private static void Timed(CostMeter meter, System.Action work)
        {
            if (!KerbcastSettings.EnableTelemetry)
            {
                work();
                return;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            work();
            meter.Record(Telemetry.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0));
        }
    }
}
