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

// --- Dimension rounding: even (H.264 chroma), floor, never below 2. ---
Check(QualityClamp.ScaleDimension(1030, 0.75f) == 772, "odd product floors to even");
Check(QualityClamp.ScaleDimension(2, 0.25f) == 2, "tiny dims floor at 2");
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

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;
