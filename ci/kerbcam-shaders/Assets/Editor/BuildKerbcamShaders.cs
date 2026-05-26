// Editor build script invoked by .github/workflows/build-kerbcam-shaders.yml.
// Run via: -executeMethod KerbcamCI.BuildKerbcamShaders.BuildLinux
// Output: Bundles-linux/kerbcam-shaders (shipped as GameData/Kerbcam/kerbcam-shaders)
using System.IO;
using UnityEditor;
namespace KerbcamCI
{
    public static class BuildKerbcamShaders
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
