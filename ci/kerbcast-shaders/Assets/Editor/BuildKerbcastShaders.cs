// Editor build script invoked by .github/workflows/build-kerbcast-shaders.yml
// and release.yml. Run via -executeMethod with one of the entry points below.
// Output: Bundles-<platform>/kerbcast-shaders, shipped as
// GameData/Kerbcast/kerbcast-shaders (Linux, legacy unsuffixed name) plus
// kerbcast-shaders.windows and kerbcast-shaders.osx.
using System.IO;
using UnityEditor;
namespace KerbcastCI
{
    public static class BuildKerbcastShaders
    {
        /* Shader variants are compiled per BuildTarget: a bundle built for one
           platform cross-loads elsewhere but its shaders have no variant for
           the running graphics API (Unity renders magenta). So build one
           bundle per supported KSP platform.

           CI runs BuildUnix on the Linux editor and BuildWindows on a real
           Windows editor. The Windows bundle MUST NOT ship from the Linux
           editor: the 2019.4 Linux cross-compile emitted d3d11 vertex shader
           blobs the Windows D3D11 runtime rejects at draw time with
           E_INVALIDARG (0x80070057) while shader.isSupported stayed true,
           which black-screened every kerbcast-bundle camera in v0.19.0. The
           macOS metal cross-compile from Linux is equally unproven; macOS is
           tier-2 and the plugin's KerbcastFxAssets render probe degrades a bad
           bundle gracefully at runtime. */
        public static void BuildAll()
        {
            BuildUnix();
            BuildWindows();
        }

        public static void BuildUnix()
        {
            Build("Bundles-linux", BuildTarget.StandaloneLinux64);
            Build("Bundles-osx", BuildTarget.StandaloneOSX);
        }

        public static void BuildWindows()
        {
            Build("Bundles-windows", BuildTarget.StandaloneWindows64);
        }

        private static void Build(string outputDir, BuildTarget target)
        {
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);
            BuildPipeline.BuildAssetBundles(
                outputDir,
                BuildAssetBundleOptions.UncompressedAssetBundle,
                target);
            // BuildAssetBundles silently produces nothing when player support
            // for the target is missing (run 27307004282: no Bundles-windows/,
            // step still green). Fail the editor invocation instead so the CI
            // step that broke is the build, not a later copy.
            var bundle = Path.Combine(outputDir, "kerbcast-shaders");
            if (!File.Exists(bundle))
                throw new IOException(
                    $"BuildAssetBundles produced no {bundle} for {target}; " +
                    "is the player-support module installed?");
        }
    }
}
