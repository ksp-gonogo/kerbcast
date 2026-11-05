namespace Kerbcast
{
    /* The sidecar's launch command line, built from settings.cfg. Unity-free
       so the plugin-to-sidecar plumbing is testable. */
    public static class SidecarArgs
    {
        public static string Build(
            string ringDir,
            string httpBind,
            int width,
            int height,
            int bitrateBps,
            bool manualStreamTierTesting)
        {
            var args =
                $"--shm-dir \"{ringDir}\" " +
                $"--http-bind {httpBind} " +
                $"--max-width {width} " +
                $"--max-height {height}";

            /* BitrateBps = 0 (the default) means auto: omit the flag so the
               sidecar derives its default from the encoder backend it selects
               (hardware backends default higher than the software fallback). */
            if (bitrateBps > 0)
            {
                args += $" --bitrate-bps {bitrateBps}";
            }
            if (manualStreamTierTesting)
            {
                args += " --manual-tier-switch";
            }
            return args;
        }
    }
}
