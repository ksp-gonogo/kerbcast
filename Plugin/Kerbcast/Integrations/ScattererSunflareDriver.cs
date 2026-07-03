/* Per-clone sunflare driver for a kerbcast near clone. Owns the render-time
   material writes the copied SunflareCameraHook used to make; the copy stays
   permanently disabled and only holds the live SunFlare reference (re-pointed
   by ScattererIntegration.PerFrame). Unity documents no ordering between two
   components' OnPreRender on one camera, so an override component running
   alongside an enabled hook could be silently clobbered; one writer removes
   the hazard.

   OnPreRender: call SunFlare.updateProperties() (positions the flare and runs
   Scatterer's occlusion), replicate the hook's writes (renderOnCurrentCamera=1,
   useDbufferOnCamera), then widen the occlusion verdict. Scatterer raycasts
   once at the sun's CENTER, so the flare pops off the moment a part edge
   crosses it while most of the disk still shows. When Scatterer says hidden,
   re-test with a ring of rays across the sun's apparent disk from this clone;
   any clear ray means a sliver of sun (or near-limb glare) is visible and the
   flare stays on. All rays blocked keeps it off. Binary on/off matches how
   Scatterer itself drives renderSunFlare (always 0 or 1).

   Main-view safety: the floats live on Scatterer's shared material, but
   Scatterer's own hooks on the stock cameras rewrite them in their OnPreRender
   before every main render, so nothing set here leaks into the player's view.

   Reflection-only: no compile-time Scatterer reference. */

using System;
using System.Reflection;
using UnityEngine;

namespace Kerbcast
{
    internal sealed class ScattererSunflareDriver : MonoBehaviour
    {
        private const string LogTag = "[Kerbcast-Scatterer]";

        /* Same masks Scatterer uses in updateProperties: parts + local scenery
           for the near-space rays, scaled-space bodies for the planet ray. */
        private const int NearOcclusionLayers = (1 << 0) | (1 << 15);
        private const int ScaledOcclusionLayers = 1 << 10;

        /* Disk sampling: center plus a ring of rays at RingScale x the sun's
           apparent angular radius (asin(bodyRadius/distance)). The 1.15 margin
           keeps strong glare while only the corona just past the limb shows. */
        private const int RingSamples = 8;
        private const float RingScale = 1.15f;

        // Configured by ScattererIntegration after AddComponent.
        public Behaviour Hook;                 // copied SunflareCameraHook (disabled; flare holder)
        public FieldInfo HookFlareField;       // SunflareCameraHook.flare -> SunFlare
        public MethodInfo UpdatePropertiesMethod;  // SunFlare.updateProperties()
        public MethodInfo ClearExtinctionMethod;   // SunFlare.ClearExtinction()
        public FieldInfo MaterialField;        // SunFlare.sunglareMaterial
        public PropertyInfo FlareRenderingProp; // SunFlare.FlareRendering (bool)
        public FieldInfo SourceScaledTransformField; // SunFlare.sourceScaledTransform
        public PropertyInfo InstanceProp;      // Scatterer.Scatterer.Instance
        public FieldInfo ScaledField;          // Instance.scaledSpaceCamera
        public FieldInfo CbmField;             // Instance.scattererCelestialBodiesManager
        public FieldInfo UnderwaterField;      // <cbm>.underwater
        public float UseDbufferOnCamera;       // carried over from the copied hook

        private static readonly int RenderSunFlareId = Shader.PropertyToID("renderSunFlare");
        private static readonly int RenderOnCurrentCameraId = Shader.PropertyToID("renderOnCurrentCamera");
        private static readonly int UseDbufferOnCameraId = Shader.PropertyToID("useDbufferOnCamera");

        private bool _errorLogged;

        private void OnPreRender()
        {
            var mat = LiveMaterial(out var flare);
            if (mat == null) return;
            try
            {
                UpdatePropertiesMethod.Invoke(flare, null);
                mat.SetFloat(RenderOnCurrentCameraId, 1f);
                mat.SetFloat(UseDbufferOnCameraId, UseDbufferOnCamera);
                OverrideOcclusion(flare, mat);
            }
            catch (Exception ex) { LogOnce("pre-render", ex); }
        }

        private void OnPostRender()
        {
            var mat = LiveMaterial(out var flare);
            if (mat == null) return;
            try
            {
                ClearExtinctionMethod?.Invoke(flare, null);
                mat.SetFloat(RenderOnCurrentCameraId, 0f);
                mat.SetFloat(UseDbufferOnCameraId, UseDbufferOnCamera);
            }
            catch (Exception ex) { LogOnce("post-render", ex); }
        }

        /* Widen Scatterer's center-ray verdict to "off only when fully hidden".
           Only rescues a false verdict caused by near-space blocking: every
           other gate updateProperties applies (sun behind the main view, a
           scaled-space body in front, underwater) is re-checked and respected,
           so the flare never shines through a planet or the ocean. */
        private void OverrideOcclusion(object flare, Material mat)
        {
            /* Flight, non-map only: Scatterer's map/tracking path casts no
               near-space ray, so there is no center-ray pop to widen there. */
            if (!HighLogic.LoadedSceneIsFlight || MapView.MapIsEnabled) return;
            if (FlareRenderingProp == null || (bool)FlareRenderingProp.GetValue(flare, null))
                return; // Scatterer already rendering the flare; nothing to rescue

            var inst = InstanceProp?.GetValue(null, null);
            var scaledCam = inst != null ? ScaledField?.GetValue(inst) as Camera : null;
            var sst = SourceScaledTransformField?.GetValue(flare) as Transform;
            if (scaledCam == null || sst == null) return;

            /* updateProperties only refreshes the flare's position/scale inputs
               while the sun is in front of the main scaled camera; with stale
               inputs the flare would draw in the wrong place, so keep Scatterer's
               verdict. Same main-view dependency the shared float always had. */
            Vector3 vp = scaledCam.WorldToViewportPoint(sst.position);
            if (vp.z <= 0f) return;

            if (IsUnderwater(inst)) return;
            if (ScaledSpaceOccluded(scaledCam, sst)) return;
            if (!AnyDiskSampleClear()) return;

            mat.SetFloat(RenderSunFlareId, 1f);
        }

        /* Scatterer's scaled-space planet ray, reproduced: a hit on the sun's
           own scaled transform is the sun itself, not an occluder. */
        private static bool ScaledSpaceOccluded(Camera scaledCam, Transform sst)
        {
            Vector3 origin = scaledCam.transform.position;
            Vector3 dir = (sst.position - origin).normalized;
            return Physics.Raycast(origin, dir, out var hit, Mathf.Infinity, ScaledOcclusionLayers)
                && hit.transform != sst;
        }

        private bool IsUnderwater(object inst)
        {
            if (inst == null || CbmField == null || UnderwaterField == null) return false;
            var cbm = CbmField.GetValue(inst);
            return cbm != null && UnderwaterField.GetValue(cbm) is bool b && b;
        }

        /* True when any ray from this clone toward the sun's disk (center + ring
           at RingScale x angular radius) clears the part/scenery layers, i.e. at
           least a sliver of sun still throws glare at this camera. */
        private bool AnyDiskSampleClear()
        {
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null) return false;

            Vector3 origin = transform.position;
            Vector3d toSun = sun.position - new Vector3d(origin.x, origin.y, origin.z);
            double dist = toSun.magnitude;
            if (dist <= sun.Radius) return true; // inside the star; nothing can occlude

            Vector3 dir = (Vector3)(toSun / dist);
            if (!Physics.Raycast(origin, dir, out _, Mathf.Infinity, NearOcclusionLayers))
                return true;

            float ring = (float)Math.Asin(Math.Min(1.0, sun.Radius / dist)) * RingScale;
            float sinR = Mathf.Sin(ring), cosR = Mathf.Cos(ring);
            Vector3 right = Vector3.Cross(dir, Vector3.up);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.Cross(dir, Vector3.right);
            right.Normalize();
            Vector3 up = Vector3.Cross(dir, right);

            for (int i = 0; i < RingSamples; i++)
            {
                float a = 2f * Mathf.PI * i / RingSamples;
                Vector3 d = dir * cosR + (right * Mathf.Cos(a) + up * Mathf.Sin(a)) * sinR;
                if (!Physics.Raycast(origin, d, out _, Mathf.Infinity, NearOcclusionLayers))
                    return true;
            }
            return false;
        }

        /* The live flare's shared material, or null while the hook reference is
           dead (Scatterer mid-rebuild; PerFrame re-points it next frame). */
        private Material LiveMaterial(out object flare)
        {
            flare = null;
            if (Hook == null || HookFlareField == null || MaterialField == null
                || UpdatePropertiesMethod == null)
                return null;
            var f = HookFlareField.GetValue(Hook) as UnityEngine.Object;
            if (f == null) return null;
            flare = f;
            return MaterialField.GetValue(f) as Material;
        }

        private void LogOnce(string phase, Exception ex)
        {
            if (_errorLogged) return;
            _errorLogged = true;
            Debug.LogError($"{LogTag} sunflare driver {phase} on {name} failed: {ex.Message}");
        }
    }
}
