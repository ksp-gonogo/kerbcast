using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * Each frame's live bound from its lens and the craft the lens rides
     * (CommandCameraMath.LiveLightTimeS). The parts are walked for every
     * frame, so a dock or undock steps the bound on the frame it happens.
     * A part's reach about its origin comes from ReachCache, which only
     * lets it grow: a part whose shape is moving (a deployable or animated
     * module moving, a parachute out) is measured on every frame, any other
     * one now and then.
     */
    internal static class LiveLightTime
    {
        private static readonly ReachCache s_reach = new ReachCache();
        private static readonly Func<Part, double> s_measure = Measure;
        private static readonly Dictionary<int, PartShape> s_shapes = new Dictionary<int, PartShape>();
        private static readonly List<DVec3> s_origins = new List<DVec3>();
        private static readonly List<double> s_radii = new List<double>();

        /* What can change a part's shape, found once per part. */
        private sealed class PartShape
        {
            public List<IScalarModule> Movers;
            public ModuleParachute Parachute;
        }

        /* The live bound of a frame from a lens at `lens` on `craft` (null for none, such as a ground site). 0 with no signal speed. */
        public static double For(Vector3d lens, Vessel craft)
        {
            double? c = LightTime.SignalSpeed;
            if (!c.HasValue) return 0;
            if (!KerbcastSettings.EnableTelemetry) return CommandCameraMath.LiveLightTimeS(c, FarthestPartM(lens, craft));
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double bound = CommandCameraMath.LiveLightTimeS(c, FarthestPartM(lens, craft));
            Telemetry.LiveLight.Record(Telemetry.Ms(System.Diagnostics.Stopwatch.GetTimestamp() - t0));
            return bound;
        }

        /* The boundary distance the picture works to; NaN with no signal speed. */
        public static double BoundaryM => CommandCameraMath.BoundaryM(LightTime.SignalSpeed);

        private static double FarthestPartM(Vector3d lens, Vessel craft)
        {
            if (craft == null || !craft.loaded || craft.parts == null) return 0;
            s_origins.Clear();
            s_radii.Clear();
            long frame = Time.frameCount;
            var parts = craft.parts;
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p == null || p.transform == null) continue;
                var at = p.transform.position;
                s_origins.Add(new DVec3(at.x, at.y, at.z));
                s_radii.Add(s_reach.Reach(p.GetInstanceID(), frame, Moving(p), s_measure, p));
            }
            return CommandCameraMath.FarthestPartM(new DVec3(lens.x, lens.y, lens.z), s_origins, s_radii);
        }

        private static bool Moving(Part part)
        {
            int key = part.GetInstanceID();
            if (!s_shapes.TryGetValue(key, out var shape))
            {
                if (s_shapes.Count >= ReachCache.Cap) s_shapes.Clear();
                shape = new PartShape
                {
                    Movers = part.FindModulesImplementing<IScalarModule>() ?? new List<IScalarModule>(),
                    Parachute = part.FindModuleImplementing<ModuleParachute>(),
                };
                s_shapes[key] = shape;
            }
            if (shape.Parachute != null && shape.Parachute.deploymentState != ModuleParachute.deploymentStates.STOWED)
                return true;
            for (int i = 0; i < shape.Movers.Count; i++)
            {
                var m = shape.Movers[i];
                if (m != null && m.IsMoving()) return true;
            }
            return false;
        }

        /*
         * How far the part's model reaches from its origin: the distance to
         * its bounds' centre plus their half diagonal. Only renderers that are
         * drawn count: a disabled one or one on an inactive object reports
         * empty bounds at the world origin, which would read as a reach of
         * the part's distance from the floating origin. NaN with none drawn.
         */
        private static double Measure(Part part)
        {
            var renderers = part.FindModelComponents<Renderer>();
            if (renderers == null) return double.NaN;
            bool any = false;
            var bounds = default(Bounds);
            foreach (var rend in renderers)
            {
                if (rend == null || rend is ParticleSystemRenderer || !rend.enabled || !rend.gameObject.activeInHierarchy) continue;
                var b = rend.bounds;
                if (b.extents == Vector3.zero) continue;
                if (!any) { bounds = b; any = true; }
                else bounds.Encapsulate(b);
            }
            if (!any) return double.NaN;
            return (bounds.center - part.transform.position).magnitude + bounds.extents.magnitude;
        }
    }
}
