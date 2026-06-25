using System;
using Kerbcam;

int failures = 0;
void Check(bool cond, string msg) { Console.WriteLine((cond ? "  ok   " : "  FAIL ") + msg); if (!cond) failures++; }

// Parse
Check(QualityTiers.TryParse("hd", out var t1) && t1 == QualityTier.Hd, "parse 'hd'");
Check(QualityTiers.TryParse("SD", out var t2) && t2 == QualityTier.Sd, "parse 'SD' case-insensitive");
Check(QualityTiers.TryParse("fullhd", out var t3) && t3 == QualityTier.FullHd, "parse 'fullhd'");
Check(!QualityTiers.TryParse("ultra", out _), "unknown tier rejected");

// Values: every tier even dims
foreach (QualityTier t in Enum.GetValues(typeof(QualityTier))) {
    var (w, h, _) = QualityTiers.Values(t);
    Check(w % 2 == 0 && h % 2 == 0, $"{t} dims even ({w}x{h})");
}
// SD == today's default, bitrate 0 (inherit)
Check(QualityTiers.Values(QualityTier.Sd) == (1024, 576, 0), "SD = 1024x576, bitrate inherit");
Check(QualityTiers.Values(QualityTier.Hd) == (1280, 720, 6_000_000), "HD = 1280x720 @ 6Mbps");

// Resolve precedence: explicit overrides tier
Check(QualityTiers.Resolve(QualityTier.Hd, null, null, null) == (1280, 720, 6_000_000), "HD resolves to tier values");
Check(QualityTiers.Resolve(QualityTier.Hd, 800, 600, null) == (800, 600, 6_000_000), "explicit W/H override tier");
Check(QualityTiers.Resolve(QualityTier.Hd, null, null, 3_000_000) == (1280, 720, 3_000_000), "explicit bitrate overrides tier");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
