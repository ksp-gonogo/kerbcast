// Editor build script used by .github/workflows/build-hullcam-shaders.yml
// to rebuild HullcamVDSContinued's shader AssetBundle for Linux against
// a modern Unity 2019.4 LTS. Replaces the upstream
// HullCameraAssets/Assets/Editor/CreateAssetBundles.cs at build time —
// upstream targets the deprecated StandaloneLinuxUniversal enum (gone
// since Unity 2019.2), which is the most likely reason their shipped
// shaders.linux bundle doesn't run on modern Mesa OpenGL.
//
// Copied into the cloned HullCameraAssets/Assets/Editor/ folder by the
// workflow before invoking Unity in batchmode. Triggered via
//   -executeMethod KerbcastCI.BuildShaderBundle.BuildLinux
//
// Output: writes the AssetBundle to Bundles-linux/ relative to the
// project root. The workflow then uploads Bundles-linux/shaders as
// `shaders.linux` artifact for kerbcast to ship.

using System.IO;
using UnityEditor;

namespace KerbcastCI
{
    public static class BuildShaderBundle
    {
        public static void BuildLinux()
        {
            const string outputDir = "Bundles-linux";
            if (!Directory.Exists(outputDir))
                Directory.CreateDirectory(outputDir);

            BuildPipeline.BuildAssetBundles(
                outputDir,
                BuildAssetBundleOptions.UncompressedAssetBundle,
                BuildTarget.StandaloneLinux64);
        }
    }
}
