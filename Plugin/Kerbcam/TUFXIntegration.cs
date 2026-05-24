// TUFX (TexturesUnlimitedFX) post-processing integration. Lifted
// near-verbatim from JustReadTheInstructions (RELMYMathieu's KSP
// camera-streaming mod) — three implementations of the same problem
// space (OCISLY, JTI, kerbcam) and JTI's TUFX hookup is the most
// recent / most reflection-isolated.
//
// Reflection-only on purpose: kerbcam has zero compile-time
// reference to TUFX. AssemblyLoader scan at IsAvailable check picks
// TUFX up at runtime if installed, otherwise the integration
// silently no-ops. The plugin compiles + ships fine without TUFX.
//
// Wired into KerbcamCamera.SetCameras: applied to ALL THREE layered
// cameras (Near / Scaled / Galaxy) when EnableTUFX=true in
// settings.cfg. Matches JTI's pattern, not OCISLY's near-only call —
// JTI is the more recently maintained codebase and the all-three
// choice is the more current data point.
//
// Without TUFX, kerbcam streams suffer the "dark Kerbin / black
// hole horizon" issue: atmospheric scattering on Kerbin has very
// wide dynamic range that needs tonemapping post-process to display
// correctly. HDR (allowHDR=true) alone helps but doesn't close it.
// TUFX provides the tonemap + bloom + colour-grading pass KSP
// players already configure via the TUFX in-game UI; we inherit
// that config per-camera here.

using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Kerbcam
{
    internal static class TUFXIntegration
    {
        private static bool? _isAvailable;
        private static Type _postProcessLayerType;
        private static Type _postProcessVolumeType;
        private static Type _texturesUnlimitedFXLoaderType;
        private static Type _tufxProfileType;
        private static MethodInfo _addOrGetComponentMethod;
        private static MethodInfo _initMethod;
        private static PropertyInfo _resourcesProperty;
        private static FieldInfo _volumeLayerField;
        private static FieldInfo _isGlobalField;
        private static FieldInfo _priorityField;
        // Profile-lookup plumbing. All four nullable independently; any
        // null means the profile-attach step in ApplyToCamera falls
        // through and the volume inherits TUFX's global selection
        // (today's pre-profile-bundling behaviour).
        private static FieldInfo _instanceField;
        private static PropertyInfo _profilesProperty;
        private static MethodInfo _createPostProcessProfileMethod;
        private static FieldInfo _sharedProfileField;
        // Log-once gate so the "profile not in registry" path doesn't
        // spam KSP.log per camera per attach.
        private static string _missingProfileLogged;

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable.HasValue)
                    return _isAvailable.Value;

                try
                {
                    var tufxAssembly = AssemblyLoader.loadedAssemblies
                        .FirstOrDefault(a => a.name == "TUFX")?.assembly;

                    if (tufxAssembly == null)
                    {
                        Debug.Log("[Kerbcam-TUFX] TUFX not found - post-processing disabled");
                        _isAvailable = false;
                        return false;
                    }

                    _postProcessLayerType = tufxAssembly.GetType("UnityEngine.Rendering.PostProcessing.PostProcessLayer");
                    _postProcessVolumeType = tufxAssembly.GetType("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
                    _texturesUnlimitedFXLoaderType = tufxAssembly.GetType("TUFX.TexturesUnlimitedFXLoader");

                    if (_postProcessLayerType == null || _postProcessVolumeType == null || _texturesUnlimitedFXLoaderType == null)
                    {
                        Debug.LogWarning("[Kerbcam-TUFX] TUFX types not found - incompatible version?");
                        _isAvailable = false;
                        return false;
                    }

                    _resourcesProperty = _texturesUnlimitedFXLoaderType.GetProperty("Resources",
                        BindingFlags.Public | BindingFlags.Static);
                    _initMethod = _postProcessLayerType.GetMethod("Init",
                        BindingFlags.Public | BindingFlags.Instance);
                    _volumeLayerField = _postProcessLayerType.GetField("volumeLayer",
                        BindingFlags.Public | BindingFlags.Instance);
                    _isGlobalField = _postProcessVolumeType.GetField("isGlobal",
                        BindingFlags.Public | BindingFlags.Instance);
                    _priorityField = _postProcessVolumeType.GetField("priority",
                        BindingFlags.Public | BindingFlags.Instance);

                    // Profile-lookup plumbing — separate from the main
                    // integration probe above. Any failure here disables
                    // ONLY the profile attach; the layer + volume + global
                    // inheritance still works (strictly better fallback
                    // than disabling the whole integration). TUFX's
                    // INSTANCE field is `internal static` and Profiles is
                    // an `internal` auto-property — both need NonPublic
                    // binding flags; Public would silently return null
                    // and the bug would only surface at runtime.
                    _tufxProfileType = tufxAssembly.GetType("TUFX.TUFXProfile");
                    if (_tufxProfileType != null)
                    {
                        _instanceField = _texturesUnlimitedFXLoaderType.GetField(
                            "INSTANCE", BindingFlags.NonPublic | BindingFlags.Static);
                        _profilesProperty = _texturesUnlimitedFXLoaderType.GetProperty(
                            "Profiles", BindingFlags.NonPublic | BindingFlags.Instance);
                        _createPostProcessProfileMethod = _tufxProfileType.GetMethod(
                            "CreatePostProcessProfile", BindingFlags.Public | BindingFlags.Instance);
                        _sharedProfileField = _postProcessVolumeType.GetField(
                            "sharedProfile", BindingFlags.Public | BindingFlags.Instance);
                        if (_instanceField == null || _profilesProperty == null
                            || _createPostProcessProfileMethod == null || _sharedProfileField == null)
                        {
                            Debug.LogWarning("[Kerbcam-TUFX] profile-lookup members missing — volumes will inherit global TUFX profile");
                            _instanceField = null;
                            _profilesProperty = null;
                            _createPostProcessProfileMethod = null;
                            _sharedProfileField = null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning("[Kerbcam-TUFX] TUFX.TUFXProfile type not found — volumes will inherit global TUFX profile");
                    }

                    var extensionsType = typeof(GameObject).Assembly.GetType("UnityEngine.GameObjectExtensions")
                        ?? typeof(GameObject);
                    _addOrGetComponentMethod = extensionsType.GetMethod("AddOrGetComponent",
                        BindingFlags.Public | BindingFlags.Static,
                        null,
                        new[] { typeof(GameObject), typeof(Type) },
                        null);

                    if (_addOrGetComponentMethod == null)
                    {
                        Debug.Log("[Kerbcam-TUFX] using fallback AddOrGetComponent");
                    }

                    _isAvailable = true;
                    Debug.Log("[Kerbcam-TUFX] integration enabled");
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Kerbcam-TUFX] error checking availability: {ex.Message}");
                    _isAvailable = false;
                    return false;
                }
            }
        }

        public static void ApplyToCamera(Camera camera)
        {
            if (!IsAvailable || camera == null)
                return;

            try
            {
                Component layer = AddOrGetComponent(camera.gameObject, _postProcessLayerType);

                if (layer == null)
                {
                    Debug.LogWarning($"[Kerbcam-TUFX] failed to add PostProcessLayer to {camera.name}");
                    return;
                }

                var resources = _resourcesProperty?.GetValue(null);
                if (resources != null && _initMethod != null)
                {
                    _initMethod.Invoke(layer, new[] { resources });
                }
                else
                {
                    Debug.LogWarning($"[Kerbcam-TUFX] resources not found - removing PostProcessLayer from {camera.name}");
                    UnityEngine.Object.Destroy(layer);
                    return;
                }

                if (_volumeLayerField != null)
                {
                    LayerMask allLayers = ~0;
                    _volumeLayerField.SetValue(layer, allLayers);
                }

                Component volume = AddOrGetComponent(camera.gameObject, _postProcessVolumeType);

                if (volume == null)
                {
                    Debug.LogWarning($"[Kerbcam-TUFX] failed to add PostProcessVolume - removing PostProcessLayer from {camera.name}");
                    UnityEngine.Object.Destroy(layer);
                    return;
                }

                if (_isGlobalField != null)
                {
                    _isGlobalField.SetValue(volume, true);
                }

                if (_priorityField != null)
                {
                    _priorityField.SetValue(volume, 100);
                }

                // Attach kerbcam's curated profile to this volume so
                // operators get sensible defaults regardless of which
                // profile they've selected in TUFX's main UI. Falls
                // through silently if our profile isn't loaded — volume
                // inherits the operator's global selection.
                if (_sharedProfileField != null && !string.IsNullOrEmpty(KerbcamSettings.TUFXProfile))
                {
                    var profile = TryGetProfile(KerbcamSettings.TUFXProfile);
                    if (profile != null)
                    {
                        _sharedProfileField.SetValue(volume, profile);
                    }
                }

                Debug.Log($"[Kerbcam-TUFX] applied post-processing to {camera.name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcam-TUFX] failed to apply to {camera.name}: {ex.Message}\n{ex.StackTrace}");

                try
                {
                    var layer = camera.gameObject.GetComponent(_postProcessLayerType);
                    if (layer != null) UnityEngine.Object.Destroy(layer);

                    var volume = camera.gameObject.GetComponent(_postProcessVolumeType);
                    if (volume != null) UnityEngine.Object.Destroy(volume);
                }
                catch { }
            }
        }

        public static void RemoveFromCamera(Camera camera)
        {
            if (camera == null)
                return;

            try
            {
                if (_postProcessLayerType != null)
                {
                    var layer = camera.gameObject.GetComponent(_postProcessLayerType);
                    if (layer != null)
                    {
                        UnityEngine.Object.Destroy(layer);
                    }
                }

                if (_postProcessVolumeType != null)
                {
                    var volume = camera.gameObject.GetComponent(_postProcessVolumeType);
                    if (volume != null)
                    {
                        UnityEngine.Object.Destroy(volume);
                    }
                }

                Debug.Log($"[Kerbcam-TUFX] removed from {camera.name}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Kerbcam-TUFX] error removing from {camera.name}: {ex.Message}");
            }
        }

        // Look up a TUFX profile by name in the loader's Profiles
        // dictionary and ask TUFX to materialise a PostProcessProfile
        // ScriptableObject for it. Returns null on any failure — caller
        // skips the sharedProfile attach and the volume inherits the
        // operator's global selection (today's pre-bundle behaviour).
        //
        // CreatePostProcessProfile() allocates a fresh ScriptableObject
        // per call; the volume will own the reference until Unity GCs
        // it at scene unload. Not pooled / not shared across volumes.
        private static UnityEngine.Object TryGetProfile(string profileName)
        {
            if (_instanceField == null || _profilesProperty == null
                || _createPostProcessProfileMethod == null || _sharedProfileField == null)
            {
                return null;
            }
            try
            {
                var loaderInstance = _instanceField.GetValue(null);
                if (loaderInstance == null) return null;
                var profiles = _profilesProperty.GetValue(loaderInstance) as System.Collections.IDictionary;
                if (profiles == null) return null;
                if (!profiles.Contains(profileName))
                {
                    if (_missingProfileLogged != profileName)
                    {
                        Debug.Log($"[Kerbcam-TUFX] profile '{profileName}' not found in TUFX registry — inheriting global");
                        _missingProfileLogged = profileName;
                    }
                    return null;
                }
                var tufxProfile = profiles[profileName];
                if (tufxProfile == null) return null;
                return _createPostProcessProfileMethod.Invoke(tufxProfile, null) as UnityEngine.Object;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Kerbcam-TUFX] TryGetProfile('{profileName}') threw: {ex.Message}");
                return null;
            }
        }

        private static Component AddOrGetComponent(GameObject gameObject, Type componentType)
        {
            if (_addOrGetComponentMethod != null)
            {
                try
                {
                    return (Component)_addOrGetComponentMethod.Invoke(null, new object[] { gameObject, componentType });
                }
                catch
                {
                    // Ignore, fallback handles that
                }
            }

            // Manual fallback
            var existing = gameObject.GetComponent(componentType);
            if (existing != null)
                return existing;

            return gameObject.AddComponent(componentType);
        }
    }
}
