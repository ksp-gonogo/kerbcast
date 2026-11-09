using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * A light of its own for each planetary system in one camera's scaled
     * render beyond the one the main scaled light faces. Unity lights each
     * renderer from the lights whose culling mask holds its layer, so for the
     * render alone each such system's scaled renderers move from the scaled
     * layer to a spare one, with a directional light that reaches only it.
     * Every other light and projector reaches the spare layers exactly as it
     * reaches the scaled layer, so a moved body is drawn as before but for
     * its light's direction. End puts all of it back.
     *
     * KSP names every layer but 3, 6 and 7 (7 is declared for highlighting
     * and never used), and Scatterer, EVE, Firefly, Deferred, TUFX and
     * Parallax use none of them. A spare layer anything is drawn on is
     * dropped, so three systems at most get a light; the rest share the main.
     */
    internal sealed class SystemLights
    {
        public const int ScaledLayer = 10;
        private static readonly int[] Candidates = { 3, 6, 7 };
        private const float ScanSeconds = 30f;

        private static readonly Light[] s_lights = new Light[32];
        private static int s_free = -1;
        private static float s_scannedAt;
        private static GameScenes s_scannedScene;
        private static bool s_open;
        private static Light[] s_sceneLights = new Light[0];
        private static Projector[] s_sceneProjectors = new Projector[0];
        private static int s_sceneFrame = -1;

        private struct Was<T>
        {
            public T Item;
            public int Value;
        }

        private readonly List<Was<GameObject>> _layers = new List<Was<GameObject>>();
        private readonly List<Was<Light>> _masks = new List<Was<Light>>();
        private readonly List<Was<Projector>> _ignores = new List<Was<Projector>>();
        private readonly List<Light> _on = new List<Light>();
        private readonly List<Renderer> _scratch = new List<Renderer>();
        private Camera _camera;
        private int _cameraAdded;
        private bool _begun;

        /* The spare layers free to use now, lowest first, into `layers`; returns how many. Rescanned on a scene change and every ScanSeconds, never while a render holds them. */
        public static int Spares(int[] layers)
        {
            if (s_open) return 0;
            if (s_free < 0 || s_scannedScene != HighLogic.LoadedScene || Time.realtimeSinceStartup - s_scannedAt > ScanSeconds)
            {
                s_scannedScene = HighLogic.LoadedScene;
                s_scannedAt = Time.realtimeSinceStartup;
                int used = 0;
                var all = Object.FindObjectsOfType<Renderer>();
                for (int i = 0; i < all.Length; i++) used |= 1 << all[i].gameObject.layer;
                int free = BodyLight.FreeLayers(Candidates, used);
                if (free != s_free) Debug.Log($"[Kerbcast] event=body-light spare-layers=0x{free:X} (in use 0x{used & ~free & SpareMaskAll():X})");
                s_free = free;
            }
            return BodyLight.LayersIn(s_free, layers);
        }

        private static int SpareMaskAll() => BodyLight.FreeLayers(Candidates, 0);

        /*
         * Moves `bodies` of each system to its layer and lights it from
         * `directions`, for `scaled`'s render. `main` is the scaled sun light
         * the new lights copy and the moved bodies leave. False when nothing
         * could be done, so every body keeps the main light. Whatever was done
         * when this throws is undone by End.
         */
        public bool Begin(Camera scaled, Light main, IList<CelestialBody> bodies, IList<int> layerOf, IList<Vector3> directionOf)
        {
            if (s_open || scaled == null || main == null || (scaled.cullingMask & (1 << ScaledLayer)) == 0) return false;
            int spareMask = 0;
            for (int i = 0; i < layerOf.Count; i++)
                if (layerOf[i] >= 0) spareMask |= 1 << layerOf[i];
            if (spareMask == 0) return false;
            s_open = true;
            _begun = true;

            _camera = scaled;
            _cameraAdded = spareMask & ~scaled.cullingMask;
            scaled.cullingMask |= _cameraAdded;

            RefreshScene();
            for (int i = 0; i < s_sceneLights.Length; i++)
            {
                var l = s_sceneLights[i];
                if (l == null || l == main || IsOurs(l)) continue;
                int mirrored = BodyLight.MirrorMask(l.cullingMask, ScaledLayer, spareMask);
                if (mirrored == l.cullingMask) continue;
                _masks.Add(new Was<Light> { Item = l, Value = l.cullingMask });
                l.cullingMask = mirrored;
            }
            for (int i = 0; i < s_sceneProjectors.Length; i++)
            {
                var p = s_sceneProjectors[i];
                if (p == null) continue;
                int mirrored = BodyLight.MirrorMask(p.ignoreLayers, ScaledLayer, spareMask);
                if (mirrored == p.ignoreLayers) continue;
                _ignores.Add(new Was<Projector> { Item = p, Value = p.ignoreLayers });
                p.ignoreLayers = mirrored;
            }

            for (int i = 0; i < bodies.Count; i++)
            {
                int layer = layerOf[i];
                var scaledBody = bodies[i] != null ? bodies[i].scaledBody : null;
                if (layer < 0 || scaledBody == null) continue;
                scaledBody.GetComponentsInChildren(true, _scratch);
                for (int r = 0; r < _scratch.Count; r++)
                {
                    var go = _scratch[r].gameObject;
                    if (go.layer != ScaledLayer) continue;
                    _layers.Add(new Was<GameObject> { Item = go, Value = go.layer });
                    go.layer = layer;
                }
                _scratch.Clear();
            }

            for (int i = 0; i < layerOf.Count; i++)
            {
                int layer = layerOf[i];
                if (layer < 0 || _on.Contains(s_lights[layer])) continue;
                var light = LightFor(layer, main);
                light.transform.rotation = Quaternion.LookRotation(directionOf[i]);
                _on.Add(light);
                light.enabled = true;
            }
            return true;
        }

        /* Puts back everything Begin changed, each step even when one before it throws. */
        public void End()
        {
            if (!_begun) return;
            try
            {
                for (int i = 0; i < _on.Count; i++)
                    if (_on[i] != null) _on[i].enabled = false;
            }
            finally
            {
                try
                {
                    for (int i = _layers.Count - 1; i >= 0; i--)
                        if (_layers[i].Item != null) _layers[i].Item.layer = _layers[i].Value;
                }
                finally
                {
                    try
                    {
                        for (int i = 0; i < _masks.Count; i++)
                            if (_masks[i].Item != null) _masks[i].Item.cullingMask = _masks[i].Value;
                        for (int i = 0; i < _ignores.Count; i++)
                            if (_ignores[i].Item != null) _ignores[i].Item.ignoreLayers = _ignores[i].Value;
                        if (_camera != null) _camera.cullingMask &= ~_cameraAdded;
                    }
                    finally
                    {
                        _on.Clear();
                        _layers.Clear();
                        _masks.Clear();
                        _ignores.Clear();
                        _camera = null;
                        _cameraAdded = 0;
                        _begun = false;
                        s_open = false;
                    }
                }
            }
        }

        /* The light for spare `layer`, made once and kept across scenes, set up as `main` is but reaching that layer alone. */
        private static Light LightFor(int layer, Light main)
        {
            var light = s_lights[layer];
            if (light == null)
            {
                var go = new GameObject($"Kerbcast system light {layer}");
                Object.DontDestroyOnLoad(go);
                light = go.AddComponent<Light>();
                light.enabled = false;
                s_lights[layer] = light;
            }
            light.gameObject.layer = main.gameObject.layer;
            light.type = LightType.Directional;
            light.color = main.color;
            light.intensity = main.intensity;
            light.bounceIntensity = main.bounceIntensity;
            light.renderMode = main.renderMode;
            light.shadows = main.shadows;
            light.shadowStrength = main.shadowStrength;
            light.shadowBias = main.shadowBias;
            light.shadowNormalBias = main.shadowNormalBias;
            light.cookie = main.cookie;
            light.cookieSize = main.cookieSize;
            light.cullingMask = 1 << layer;
            return light;
        }

        private static bool IsOurs(Light l)
        {
            for (int i = 0; i < Candidates.Length; i++)
                if (s_lights[Candidates[i]] == l) return true;
            return false;
        }

        /* The scene's lights and projectors, found once a frame for every camera. */
        private static void RefreshScene()
        {
            if (s_sceneFrame == Time.frameCount) return;
            s_sceneFrame = Time.frameCount;
            s_sceneLights = Object.FindObjectsOfType<Light>();
            s_sceneProjectors = Object.FindObjectsOfType<Projector>();
        }
    }
}
