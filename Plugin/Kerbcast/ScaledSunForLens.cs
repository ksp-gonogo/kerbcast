using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * The Sun as a lens far from the active craft sees it, for one scaled
     * render. Stock turns the Sun's transform every frame to face the main
     * camera, and the scaled scene's sunlight follows it: the scaled sun light
     * is its child, each planet's MaterialSetDirection copies its forward into
     * the planet's material, and each atmosphere takes its light direction
     * from it. A lens on Kerbin with the active craft at Duna sees the Sun up
     * to 180 degrees from there, so a daytime sky renders as if the Sun were
     * below the horizon: black. This turns all three to face the lens and puts
     * them back after the render.
     */
    internal sealed class ScaledSunForLens
    {
        private struct Saved
        {
            public Renderer Renderer;
            public int Property;
            public Vector4 Value;
        }

        private readonly MaterialPropertyBlock _block = new MaterialPropertyBlock();
        private readonly List<Saved> _saved = new List<Saved>();
        private Transform _sun;
        private Quaternion _sunWas;
        private bool _turnedLogged;
        private bool _turnedWas;

        /* The turn applied by the last Begin, identity when none; an atmosphere's stock light direction times this faces the lens. */
        public Quaternion Turn { get; private set; } = Quaternion.identity;

        /* Turns the Sun to face `scaled`, the lens's scaled layer camera, when stock's light misses it by more than SunTurnMinDeg. */
        public void Begin(Camera scaled, string tag)
        {
            Turn = Quaternion.identity;
            _sun = null;
            _saved.Clear();
            var sunBody = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            var sun = Sun.Instance != null ? Sun.Instance.transform : null;
            if (scaled == null || sunBody == null || sun == null) return;

            Vector3d sunAt = ScaledSpace.LocalToScaledSpace(sunBody.position);
            Vector3 lens = scaled.transform.position;
            var toLens = new DVec3(lens.x - sunAt.x, lens.y - sunAt.y, lens.z - sunAt.z);
            var stock = sun.forward;
            double turnDeg = CommandCameraMath.SunTurnDeg(new DVec3(stock.x, stock.y, stock.z), toLens);
            bool turned = turnDeg > CommandCameraMath.SunTurnMinDeg && toLens.Magnitude > 0;
            if (!_turnedLogged || turned != _turnedWas)
            {
                _turnedLogged = true;
                _turnedWas = turned;
                Debug.Log($"{tag} event=scaled-sun {(turned ? "turned to the lens" : "as stock")} turn={turnDeg:F3}deg");
            }
            if (!turned) return;

            var forward = new Vector3((float)toLens.X, (float)toLens.Y, (float)toLens.Z).normalized;
            Turn = Quaternion.FromToRotation(stock, forward);
            _sun = sun;
            _sunWas = sun.rotation;
            sun.rotation = Turn * sun.rotation;

            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return;
            for (int i = 0; i < bodies.Count; i++)
            {
                var scaledBody = bodies[i] != null ? bodies[i].scaledBody : null;
                var setter = scaledBody != null ? scaledBody.GetComponent<MaterialSetDirection>() : null;
                var r = setter != null ? setter.GetComponent<Renderer>() : null;
                if (r == null || string.IsNullOrEmpty(setter.valueName)) continue;
                int id = Shader.PropertyToID(setter.valueName);
                r.GetPropertyBlock(_block);
                _saved.Add(new Saved { Renderer = r, Property = id, Value = _block.GetVector(id) });
                _block.SetVector(id, r.transform.InverseTransformDirection(forward).normalized);
                r.SetPropertyBlock(_block);
            }
        }

        public void End()
        {
            for (int i = 0; i < _saved.Count; i++)
            {
                var s = _saved[i];
                if (s.Renderer == null) continue;
                s.Renderer.GetPropertyBlock(_block);
                _block.SetVector(s.Property, s.Value);
                s.Renderer.SetPropertyBlock(_block);
            }
            _saved.Clear();
            if (_sun != null) _sun.rotation = _sunWas;
            _sun = null;
            Turn = Quaternion.identity;
        }
    }
}
