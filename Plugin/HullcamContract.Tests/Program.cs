// Contract test: pin the HullcamVDSContinued.dll surface that kerbcam's
// per-camera filter blit (KerbcamCamera + HullcamFilterBlit, c00fe16)
// reflects over or behaviourally depends on, so an upstream Hullcam update
// that renames a field, drops a filter class, or changes which classes
// rewrite _TitleTex fails THIS test instead of silently degrading the
// stream. Metadata-only (Mono.Cecil): no Unity, no KSP runtime.
//
// What is pinned, and why (traced from Plugin/Kerbcam/KerbcamCamera.cs and
// Plugin/Kerbcam/HullcamBlit/HullcamFilterBlit.cs):
//   - CameraFilter.mtShader        static Material, the field the blit
//                                  redirects via reflection
//   - filmVignette / nvMesh / noise static Texture2Ds read to seed the
//                                  private material's NightVision slots
//   - the nine CameraFilter* classes + eCameraMode values 0..8 (kerbcam
//                                  casts (int)cameraMode straight to enum)
//   - CreateFilter / Activate / Deactivate / RenderTitlePage /
//     RenderImageWithFilter / LoadTextureFile signatures kerbcam calls
//   - "_Title" / "_TitleTex" written by RenderTitlePage, and the
//     "_VignetteTex" / "_Overlay1Tex" / "_Overlay2Tex" slot names kerbcam
//     seeds (asserted as string literals in the DLL's own writes)
//   - the reticle policy split: BWLoResTV / BWHiResTV / NightVision rewrite
//     _TitleTex inside RenderImageWithFilter (reticle suppressed); the
//     other filter classes do not (kerbcam's title=true reticle shows).
//     NightVision additionally never writes the three overlay slots, which
//     is exactly why HullcamFilterBlit seeds them.
// Exit code 0 = pass (or explicit SKIP when the DLL is absent), 1 = fail.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

string dllPath =
    (args.Length > 0 ? args[0] : null)
    ?? Environment.GetEnvironmentVariable("KERBCAM_HULLCAM_DLL")
    ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "personal", "gonogo", "local_docs", "syncthing", "kspdata",
        "GameData", "HullCameraVDS", "Plugins", "HullcamVDSContinued.dll");

Console.WriteLine($"HullcamVDSContinued.dll: {dllPath}");
if (!File.Exists(dllPath))
{
    Console.WriteLine(
        "SKIP: HullcamVDSContinued.dll not found at the path above. " +
        "Pass the path as the first argument or set KERBCAM_HULLCAM_DLL. " +
        "(Expected on runners without the ksp-managed checkout.)");
    return 0;
}

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond)
    {
        Console.WriteLine("  ok   " + msg);
    }
    else
    {
        Console.Error.WriteLine("  FAIL " + msg);
        failures++;
    }
}

using var module = ModuleDefinition.ReadModule(dllPath);

TypeDefinition FindType(string fullName) =>
    module.Types.FirstOrDefault(t => t.FullName == fullName);

// All ldstr operands in a method body (empty when the method has no body).
IEnumerable<string> StringLiterals(MethodDefinition m)
{
    if (m == null || !m.HasBody) yield break;
    foreach (var ins in m.Body.Instructions)
        if (ins.OpCode == OpCodes.Ldstr && ins.Operand is string s)
            yield return s;
}

// ------------------------------------------------------------------
// CameraFilter base class: the reflected statics + called methods
// ------------------------------------------------------------------
var cameraFilter = FindType("HullcamVDS.CameraFilter");
Check(cameraFilter != null, "type HullcamVDS.CameraFilter exists");
if (cameraFilter == null) return Fail();

var mtShader = cameraFilter.Fields.FirstOrDefault(f => f.Name == "mtShader");
Check(mtShader != null, "field CameraFilter.mtShader exists");
Check(mtShader != null && mtShader.IsStatic, "mtShader is static");
Check(mtShader != null && mtShader.FieldType.FullName == "UnityEngine.Material",
    "mtShader is UnityEngine.Material");

foreach (var name in new[] { "filmVignette", "nvMesh", "noise" })
{
    var f = cameraFilter.Fields.FirstOrDefault(x => x.Name == name);
    Check(f != null && f.IsStatic && f.FieldType.FullName == "UnityEngine.Texture2D",
        $"field CameraFilter.{name} is a static UnityEngine.Texture2D (NightVision seed source)");
}

MethodDefinition Method(TypeDefinition t, string name, params string[] paramTypes) =>
    t.Methods.FirstOrDefault(m =>
        m.Name == name
        && m.Parameters.Count == paramTypes.Length
        && m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(paramTypes));

var createFilter = Method(cameraFilter, "CreateFilter", "HullcamVDS.CameraFilter/eCameraMode");
Check(createFilter != null && createFilter.IsStatic,
    "static CameraFilter.CreateFilter(eCameraMode) exists");

var activate = Method(cameraFilter, "Activate");
Check(activate != null && activate.IsVirtual && activate.ReturnType.FullName == "System.Boolean",
    "virtual bool CameraFilter.Activate() exists");

var deactivate = Method(cameraFilter, "Deactivate");
Check(deactivate != null && deactivate.IsVirtual, "virtual CameraFilter.Deactivate() exists");

var renderTitlePage = Method(cameraFilter, "RenderTitlePage",
    "System.Boolean", "UnityEngine.Texture2D");
Check(renderTitlePage != null && renderTitlePage.IsVirtual,
    "virtual CameraFilter.RenderTitlePage(bool, Texture2D) exists");

var renderImage = Method(cameraFilter, "RenderImageWithFilter",
    "UnityEngine.RenderTexture", "UnityEngine.RenderTexture");
Check(renderImage != null && renderImage.IsVirtual,
    "virtual CameraFilter.RenderImageWithFilter(RenderTexture, RenderTexture) exists");

var loadTexture = Method(cameraFilter, "LoadTextureFile", "System.String");
Check(loadTexture != null && loadTexture.IsStatic
        && loadTexture.ReturnType.FullName == "UnityEngine.Texture2D",
    "static Texture2D CameraFilter.LoadTextureFile(string) exists (dockingdisplay.png load)");

// The base-class title page must still drive the two uniforms kerbcam's
// per-blit RenderTitlePage(true, dockingdisplay) call relies on.
var titleLiterals = StringLiterals(renderTitlePage).ToList();
Check(titleLiterals.Contains("_Title"), "RenderTitlePage writes \"_Title\"");
Check(titleLiterals.Contains("_TitleTex"), "RenderTitlePage writes \"_TitleTex\"");

// ------------------------------------------------------------------
// eCameraMode: kerbcam casts (int)hullcam.cameraMode directly to this
// enum, so both the names and the numeric values are load-bearing.
// ------------------------------------------------------------------
var modeEnum = cameraFilter.NestedTypes.FirstOrDefault(t => t.Name == "eCameraMode");
Check(modeEnum != null && modeEnum.IsEnum, "nested enum CameraFilter.eCameraMode exists");

var expectedModes = new (string Name, int Value)[]
{
    ("Normal", 0), ("DockingCam", 1), ("BlackAndWhiteFilm", 2),
    ("BlackAndWhiteLoResTV", 3), ("BlackAndWhiteHiResTV", 4), ("ColorFilm", 5),
    ("ColorLoResTV", 6), ("ColorHiResTV", 7), ("NightVision", 8),
};
if (modeEnum != null)
{
    foreach (var (name, value) in expectedModes)
    {
        var f = modeEnum.Fields.FirstOrDefault(x => x.Name == name);
        Check(f != null && f.HasConstant && Convert.ToInt32(f.Constant) == value,
            $"eCameraMode.{name} == {value}");
    }
}

// ------------------------------------------------------------------
// The nine filter classes, each deriving from CameraFilter
// ------------------------------------------------------------------
var filterClassNames = new[]
{
    "CameraFilterNormal", "CameraFilterDockingCam", "CameraFilterBlackAndWhiteFilm",
    "CameraFilterBlackAndWhiteLoResTV", "CameraFilterBlackAndWhiteHiResTV",
    "CameraFilterColorFilm", "CameraFilterColorLoResTV", "CameraFilterColorHiResTV",
    "CameraFilterNightVision",
};
var filterClasses = new Dictionary<string, TypeDefinition>();
foreach (var name in filterClassNames)
{
    var t = FindType("HullcamVDS." + name);
    filterClasses[name] = t;
    Check(t != null && t.BaseType != null
            && t.BaseType.FullName == "HullcamVDS.CameraFilter",
        $"class HullcamVDS.{name} exists and derives from CameraFilter");
}

// ------------------------------------------------------------------
// Reticle policy split (traced in c00fe16): suppressing classes rewrite
// _TitleTex inside their own blit; showing classes leave it alone so
// kerbcam's title=true write decides. If upstream flips one of these,
// the per-class reticle trace must be re-derived.
// ------------------------------------------------------------------
var suppressors = new[]
{
    "CameraFilterBlackAndWhiteLoResTV", "CameraFilterBlackAndWhiteHiResTV",
    "CameraFilterNightVision",
};
var showers = new[]
{
    "CameraFilterDockingCam", "CameraFilterBlackAndWhiteFilm",
    "CameraFilterColorFilm", "CameraFilterColorLoResTV", "CameraFilterColorHiResTV",
};
List<string> BlitLiterals(string className)
{
    var t = filterClasses[className];
    if (t == null) return new List<string>();
    return StringLiterals(Method(t, "RenderImageWithFilter",
        "UnityEngine.RenderTexture", "UnityEngine.RenderTexture")).ToList();
}
foreach (var name in suppressors)
    Check(BlitLiterals(name).Contains("_TitleTex"),
        $"{name}.RenderImageWithFilter rewrites _TitleTex (reticle suppressed)");
foreach (var name in showers)
    Check(!BlitLiterals(name).Contains("_TitleTex"),
        $"{name}.RenderImageWithFilter leaves _TitleTex alone (reticle shown)");

// NightVision's mtShader fallback never writes the three overlay slots;
// that gap is why HullcamFilterBlit seeds them on the private material.
// If upstream starts writing them, the seeds turn inert and the c00fe16
// trace should be revisited.
var nvLiterals = BlitLiterals("CameraFilterNightVision");
foreach (var slot in new[] { "_VignetteTex", "_Overlay1Tex", "_Overlay2Tex" })
    Check(!nvLiterals.Contains(slot),
        $"CameraFilterNightVision.RenderImageWithFilter does not write {slot} (seed stays load-bearing)");

// The slot names themselves must still be what upstream's other filter
// classes bind, or the seeds would target dead uniforms.
var allLiterals = new HashSet<string>(
    module.Types.SelectMany(t => t.Methods).SelectMany(StringLiterals));
foreach (var slot in new[] { "_VignetteTex", "_Overlay1Tex", "_Overlay2Tex" })
    Check(allLiterals.Contains(slot),
        $"\"{slot}\" is still a uniform name the DLL writes somewhere");

// ------------------------------------------------------------------
// Mode selector on the part module
// ------------------------------------------------------------------
var hullcamModule = FindType("HullcamVDS.MuMechModuleHullCamera");
Check(hullcamModule != null, "type HullcamVDS.MuMechModuleHullCamera exists");
var cameraMode = hullcamModule?.Fields.FirstOrDefault(f => f.Name == "cameraMode");
Check(cameraMode != null && cameraMode.FieldType.FullName == "System.Single",
    "MuMechModuleHullCamera.cameraMode is a float field");

Console.WriteLine();
if (failures == 0)
{
    Console.WriteLine("ALL CONTRACT CHECKS PASSED");
    return 0;
}
return Fail();

int Fail()
{
    Console.Error.WriteLine($"{failures} CONTRACT CHECK(S) FAILED");
    return 1;
}
