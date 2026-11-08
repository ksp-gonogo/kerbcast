// Unit test for QualityClamp: the precedence model behind the viewer
// quality clamp. The binding contract under test is
//
//   effective = min(operator config ceiling, adaptive shed level, viewer target)
//
// expressed as min() over resolution scales applied to the operator dims:
// a shed demote always wins over a viewer target, the controller's recovery
// hands control back to the viewer target, and nothing a viewer sends can
// raise quality past the operator ceiling. The shed scales used here mirror
// KerbcastCamera.ShedTable (Unity, so this harness can't compile it); if the
// table's ResScale steps ever change, update BOTH.
//
// Exit code 0 = pass, 1 = fail.

using System;
using Kerbcast;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

// KerbcastCamera.ShedTable's ResScale column (levels 0..5).
float[] shedScales = { 1.00f, 0.75f, 0.50f, 0.50f, 0.25f, 0.25f };

(int w, int h) Effective(int opW, int opH, int shedLevel, int viewerLevel)
{
    float scale = QualityClamp.EffectiveScale(shedScales[shedLevel], viewerLevel);
    return (QualityClamp.ScaleDimension(opW, scale), QualityClamp.ScaleDimension(opH, scale));
}

// --- The viewer scale table mirrors the shed ladder's distinct steps. ---
Check(QualityClamp.ViewerScales.Length == 4, "four viewer presets");
Check(QualityClamp.ViewerScales[0] == 1.00f, "level 0 = full (no clamp)");
Check(QualityClamp.ViewerScales[3] == 0.25f, "level 3 = quarter");
foreach (float s in QualityClamp.ViewerScales)
    Check(s > 0f && s <= 1f, $"viewer scale {s} can only lower quality");

// --- No clamps: operator ceiling passes through untouched. ---
Check(Effective(1024, 576, 0, 0) == (1024, 576), "shed 0 + viewer 0 = operator dims");

// --- Viewer-only clamp (AdaptiveQuality flag off: shed pinned at 0). ---
Check(Effective(1024, 576, 0, 2) == (512, 288), "viewer half on idle controller");
Check(Effective(1024, 576, 0, 3) == (256, 144), "viewer quarter on idle controller");

// --- Shed-only (no viewer request): today's behaviour, bit-for-bit. ---
Check(Effective(1024, 576, 2, 0) == (512, 288), "shed level 2 unchanged by viewer 0");
Check(Effective(1024, 576, 4, 0) == (256, 144), "shed level 4 unchanged by viewer 0");

// --- Precedence: the demote always wins over the viewer target... ---
Check(Effective(1024, 576, 4, 2) == (256, 144), "shed 0.25 beats viewer half");
// --- ...and a deeper viewer target wins over a gentle demote. ---
Check(Effective(1024, 576, 1, 3) == (256, 144), "viewer quarter beats shed 0.75");
// --- Equal scales agree (half preset == shed level 2). ---
Check(Effective(1024, 576, 2, 2) == (512, 288), "equal scales agree");

// --- Recovery: controller promotes back to 0, viewer target honored again. ---
{
    var demoted = Effective(1024, 576, 5, 2);
    var recovered = Effective(1024, 576, 0, 2);
    Check(demoted == (256, 144), "during demote: emergency scale applies");
    Check(recovered == (512, 288), "after recovery: viewer half is restored");
}

// --- Untrusted wire levels clamp into the table, never throw. ---
Check(QualityClamp.ClampViewerLevel(-1) == 0, "negative level clamps to 0");
Check(QualityClamp.ClampViewerLevel(99) == QualityClamp.MaxViewerLevel,
    "oversized level clamps to the deepest preset");
Check(Effective(1024, 576, 0, 99) == (256, 144), "oversized level degrades sanely");

// --- Dimension rounding: multiples of 16 (H.264 macroblocks), floor, never below 16. ---
Check(QualityClamp.ScaleDimension(1030, 0.75f) == 768, "other products floor to 16");
Check(QualityClamp.ScaleDimension(2, 0.25f) == 16, "tiny dims floor at 16");
Check(QualityClamp.ScaleDimension(1024, 0.75f) == 768, "exact products untouched");

// --- The ceiling is the ceiling: no input combination exceeds it. ---
{
    bool exceeded = false;
    for (int shed = 0; shed < shedScales.Length; shed++)
        for (int viewer = -1; viewer <= QualityClamp.MaxViewerLevel + 1; viewer++)
        {
            var (w, h) = Effective(1024, 576, shed, viewer);
            if (w > 1024 || h > 576) exceeded = true;
        }
    Check(!exceeded, "no shed/viewer combination exceeds the operator ceiling");
}

// --- Shedding never takes a render under the encoder minimum. ---
{
    int min = QualityClamp.MinEncodeDimension;
    Check(QualityClamp.ScaleSize(1024, 576, 0.25f) == (256, 144), "a large size sheds as before");
    Check(QualityClamp.ScaleSize(256, 144, 1.0f) == (256, 144), "unshed small size untouched");
    Check(QualityClamp.ScaleSize(256, 144, 0.75f) == (256, 144), "a shed 256x144 stays at the minimum");
    Check(QualityClamp.ScaleSize(256, 144, 0.25f) == (256, 144), "a deep shed too");
    Check(QualityClamp.ScaleSize(512, 288, 0.25f) == (256, 144), "shed lands on the minimum at its shape");
    Check(QualityClamp.ScaleSize(128, 448, 0.5f) == (128, 448), "a tall size keeps its shape");
    Check(QualityClamp.ScaleSize(64, 64, 1.0f) == (64, 64), "never past the operator dims");
    bool under = false;
    foreach (var (ow, oh) in new[] { (228, 128), (300, 170), (1024, 576), (170, 300), (2048, 256) })
        for (int shed = 0; shed < shedScales.Length; shed++)
            for (int viewer = 0; viewer <= QualityClamp.MaxViewerLevel; viewer++)
            {
                float scale = QualityClamp.EffectiveScale(shedScales[shed], viewer);
                var (w, h) = QualityClamp.ScaleSize(ow, oh, scale);
                if (w < min || h < min || w > ow || h > oh || w % 16 != 0 || h % 16 != 0) under = true;
            }
    Check(!under, "every shed/viewer step stays within [minimum, operator] and 16-aligned");
}

// --- Rendered sizes are multiples of 16 at the configured shape. ---
{
    Check(QualityClamp.ScaleSize(1920, 1080, 1.0f) == (1920, 1072), "fullhd rounds down inside its ring");
    Check(QualityClamp.ScaleSize(640, 360, 1.0f) == (624, 352), "low rounds down at its shape");
    Check(QualityClamp.ScaleSize(1000, 562, 1.0f) == (992, 560), "an unaligned setting rounds down at its shape");
    Check(QualityClamp.ScaleSize(1280, 720, 0.75f) == (960, 544), "a shed step covers the scaled size");
    Check(QualityClamp.ScaleSize(432, 244, 1.0f) == (400, 224), "an unaligned operator size stays inside itself, in shape");
    Check(QualityClamp.AlignDown(1080) == 1072 && QualityClamp.AlignDown(1024) == 1024 && QualityClamp.AlignDown(3) == 16,
        "AlignDown floors to 16");

    /* The same answers as the sidecar's encoder/frame_size.rs for the same inputs. */
    double wide = 16.0 / 9.0;
    Check(QualityClamp.AlignedCover(432, 244, 1024, 576, wide) == (480, 272), "matches the sidecar: 432x244");
    Check(QualityClamp.AlignedCover(434, 244, 1024, 576, wide) == (480, 272), "matches the sidecar: 434x244");
    Check(QualityClamp.AlignedCover(228, 128, 1280, 720, wide) == (256, 144), "matches the sidecar: the minimum");
    Check(QualityClamp.AlignedCover(400, 225, 1280, 720, wide) == (400, 224), "matches the sidecar: one row short");
    Check(QualityClamp.AlignedCover(320, 180, 1280, 720, wide) == (368, 208), "matches the sidecar: 320x180");
    Check(QualityClamp.AlignedCover(320, 180, 320, 180, wide) == (256, 144), "matches the sidecar: at a limit");

    /* Whatever size the sidecar chose comes back unchanged, so render == operator at rest. */
    bool moved = false;
    foreach (var (ow, oh) in new[] { (256, 144), (368, 208), (480, 272), (656, 368), (1024, 576), (1920, 1072) })
        if (QualityClamp.ScaleSize(ow, oh, 1.0f) != (ow, oh)) moved = true;
    Check(!moved, "an aligned operator size is its own render size");

    bool bad = false;
    foreach (var (ow, oh) in new[] { (640, 360), (1024, 576), (1280, 720), (1920, 1080), (1024, 768) })
        for (int shed = 0; shed < shedScales.Length; shed++)
            for (int viewer = 0; viewer <= QualityClamp.MaxViewerLevel; viewer++)
            {
                var (w, h) = QualityClamp.ScaleSize(ow, oh, QualityClamp.EffectiveScale(shedScales[shed], viewer));
                double drift = Math.Abs((double)w / h / ((double)ow / oh) - 1.0);
                if (w % 16 != 0 || h % 16 != 0 || w > ow || h > oh || drift > 0.0101) bad = true;
            }
    Check(!bad, "every tier at every shed/viewer step is aligned, inside the ceiling and in shape");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
