using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Kerbcast
{
    /* Makes KSP's main-view cameras nearly free without disabling them.

       Disabling them (the old FlightCamera.DisableCamera route) stopped every
       render callback on them, and Scatterer drives its per-frame state from
       exactly those callbacks: SkyNode, added to Camera ScaledSpace, pushes the
       sky, scaled-scattering and EVE cloud uniforms and switches the screen-space
       haze on from OnPreRender alone. Feeds then rendered a black sky, unlit
       clouds and no haze. Here the cameras stay enabled, so those callbacks keep
       firing, but each renders into an 8x8 pixel viewport and the local cameras
       cull everything.

       What the throttle changed is saved and written back exactly on Restore. */
    internal sealed class MainScreenThrottle
    {
        private struct Saved
        {
            public Camera Camera;
            public string Name;
            public bool ManagesMask;
            public int Mask;
            public Rect Rect;
            public bool ReapplyLogged;
        }

        private readonly List<Saved> _saved = new List<Saved>();
        private Rect _cheapRect;

        public bool Applied { get; private set; }

        public void Apply(IEnumerable<KeyValuePair<Camera, ThrottledCameraRole>> targets, StringBuilder log)
        {
            if (Applied) return;
            MainScreenThrottlePolicy.CheapViewport(Screen.width, Screen.height, out float w, out float h);
            _cheapRect = new Rect(0f, 0f, w, h);
            _saved.Clear();
            foreach (var kv in targets)
            {
                var cam = kv.Key;
                if (cam == null || Contains(cam)) continue;
                var saved = new Saved
                {
                    Camera = cam,
                    Name = cam.name,
                    ManagesMask = MainScreenThrottlePolicy.ManagesCullingMask(kv.Value),
                    Mask = cam.cullingMask,
                    Rect = cam.rect,
                };
                _saved.Add(saved);
                Throttle(saved);
                log?.Append($" '{saved.Name}'(enabled={cam.enabled} mask=0x{saved.Mask:X8}{(saved.ManagesMask ? "->0" : " kept")} rect={FormatRect(saved.Rect)}->{FormatRect(_cheapRect)})");
            }
            Applied = true;
        }

        /* KSP writes some of these cameras on its own (PlanetariumCamera.Deactivate
           resets the scaled camera's rect on map exit), so a write that lands
           while throttled is undone here each frame. The saved state is left
           alone: Restore returns the camera to what it was before the throttle. */
        public void Enforce()
        {
            if (!Applied) return;
            for (int i = 0; i < _saved.Count; i++)
            {
                var s = _saved[i];
                var cam = s.Camera;
                if (cam == null) continue;
                var r = cam.rect;
                if (!MainScreenThrottlePolicy.NeedsReapply(
                        s.ManagesMask, cam.cullingMask,
                        r.x, r.y, r.width, r.height,
                        _cheapRect.width, _cheapRect.height))
                    continue;
                if (!s.ReapplyLogged)
                {
                    Debug.Log($"[Kerbcast] throttle: '{s.Name}' mask=0x{cam.cullingMask:X8} rect={FormatRect(r)} was changed while throttled, re-applied (logged once per camera)");
                    s.ReapplyLogged = true;
                    _saved[i] = s;
                }
                Throttle(s);
            }
        }

        /* Returns how many cameras were restored. A destroyed camera (Camera 00
           and Camera 01 go with their scene) is skipped. */
        public int Restore(StringBuilder log)
        {
            int restored = 0;
            foreach (var s in _saved)
            {
                if (s.Camera == null)
                {
                    log?.Append($" '{s.Name}'(gone)");
                    continue;
                }
                if (s.ManagesMask) s.Camera.cullingMask = s.Mask;
                s.Camera.rect = s.Rect;
                restored++;
                log?.Append($" '{s.Name}'(mask=0x{s.Camera.cullingMask:X8} rect={FormatRect(s.Rect)})");
            }
            _saved.Clear();
            Applied = false;
            return restored;
        }

        private void Throttle(Saved s)
        {
            if (s.ManagesMask) s.Camera.cullingMask = 0;
            s.Camera.rect = _cheapRect;
        }

        private bool Contains(Camera cam)
        {
            foreach (var s in _saved)
                if (s.Camera == cam) return true;
            return false;
        }

        private static string FormatRect(Rect r) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "({0:0.####},{1:0.####},{2:0.####},{3:0.####})", r.x, r.y, r.width, r.height);
    }
}
