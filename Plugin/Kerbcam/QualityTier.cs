namespace Kerbcam
{
    public enum QualityTier { Low, Sd, Hd, FullHd }

    /* Single source of the resolution+bitrate bundle for each named tier.
       Pure and Unity-free so it is unit-testable; KerbcamSettings consumes it. */
    public static class QualityTiers
    {
        public static bool TryParse(string s, out QualityTier tier)
        {
            tier = QualityTier.Sd;
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "low": tier = QualityTier.Low; return true;
                case "sd": tier = QualityTier.Sd; return true;
                case "hd": tier = QualityTier.Hd; return true;
                case "fullhd": case "full-hd": case "fhd": tier = QualityTier.FullHd; return true;
                default: return false;
            }
        }

        public static (int width, int height, int bitrateBps) Values(QualityTier t)
        {
            switch (t)
            {
                case QualityTier.Low: return (640, 360, 2_000_000);
                case QualityTier.Hd: return (1280, 720, 6_000_000);
                case QualityTier.FullHd: return (1920, 1080, 10_000_000);
                case QualityTier.Sd:
                default: return (1024, 576, 0);
            }
        }

        public static (int width, int height, int bitrateBps) Resolve(
            QualityTier tier, int? explicitWidth, int? explicitHeight, int? explicitBitrateBps)
        {
            var (w, h, b) = Values(tier);
            return (explicitWidth ?? w, explicitHeight ?? h, explicitBitrateBps ?? b);
        }
    }
}
