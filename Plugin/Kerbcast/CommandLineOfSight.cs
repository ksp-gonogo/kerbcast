using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * ICommand.LineOfSight: whether a lens at a vantage could see a point at
     * one instant. The vantage, every body's centre and the vantage's body
     * spin are all taken at that instant in the world frame of now, as the
     * forecast places them, and each body is tested with
     * CommandCameraOcclusion.Blocks, the cameras' own test. Each vantage
     * keeps its resolved site and its skyline between calls, so a caller
     * asking often allocates nothing once its vantages are known. No camera
     * reads it.
     */
    internal sealed class CommandLineOfSight
    {
        private const int MaxVantages = 64;
        private const float RetrySeconds = 1f;

        private sealed class Entry
        {
            public CommandCameraSite Site;
            public CommandCameraOcclusion Occlusion;
            public float RetryAt;
        }

        private readonly Dictionary<CommandVantageKey, Entry> _entries = new Dictionary<CommandVantageKey, Entry>();
        private object _scene;

        public LineOfSightAnswer Check(Api.NetworkVantage vantage, double ut, int bodyIndex, DVec3 position, double radiusM)
        {
            var core = KerbcastCore.Instance;
            if (!ReferenceEquals(core, _scene))
            {
                _entries.Clear();
                _scene = core;
            }
            if (vantage == null || !Finite(ut) || !Finite(position.X) || !Finite(position.Y) || !Finite(position.Z))
                return LineOfSightMath.Unknown;
            var world = KinematicsWorld.Now();
            var craftBody = world.Body(bodyIndex);
            if (craftBody == null) return LineOfSightMath.Unknown;
            var e = EntryFor(vantage);
            var site = e?.Site;
            if (site == null || site.VantageGone) return LineOfSightMath.Unknown;
            bool switchedOff = !site.StationEnabled(GameStationRules.Now());

            double now = Planetarium.GetUniversalTime();
            if (!site.TryStationMotion(now, out var motion) || motion.Body == null) return LineOfSightMath.Unknown;
            double dt = ut - now;
            var standsOn = motion.Body;
            var homeAt = ToD(standsOn.getTruePositionAtUT(ut));
            var station = motion.Kind == BodyMotionKind.Orbit
                ? homeAt + ToD(motion.Orbit.getRelativePositionAtUT(ut).xzy)
                : CommandCameraMath.CarriedByBody(motion.RelNow, homeAt, CommandCameraOcclusion.Spin(standsOn), dt);
            var target = ToD(craftBody.getTruePositionAtUT(ut)) + world.Zup.ToWorld(position);
            double radius = Finite(radiusM) && radiusM > 0 ? radiusM : 0;
            if (switchedOff) return LineOfSightMath.SwitchedOff(station, target);

            e.Occlusion.UseSite(site);
            int fallbacks = e.Occlusion.MaskFallbacks;
            string occluder = null;
            var bodies = FlightGlobals.Bodies;
            for (int i = 0; bodies != null && i < bodies.Count && occluder == null; i++)
            {
                var b = bodies[i];
                if (b == null) continue;
                var at = ToD(b.getTruePositionAtUT(ut));
                if (e.Occlusion.Blocks(b, station, target, at, ToD(b.position), CommandCameraOcclusion.Spin(b), dt, out _, radius))
                    occluder = b.bodyName;
            }
            /* A skyline bin the line needed fell back to the smooth horizon: the answer is not whole, so it is not given. */
            if (e.Occlusion.MaskFallbacks != fallbacks) return LineOfSightMath.Unknown;
            return LineOfSightMath.Answer(station, target, occluder, standsOn.bodyName);
        }

        /* The vantage's resolved site and skyline, kept; null while it does not resolve, tried again a second later. */
        private Entry EntryFor(Api.NetworkVantage v)
        {
            /* An unnamed launch site is the active craft's, which can change within a scene: that is part of what is asked. */
            bool unnamedSite = v.Kind == Api.SiteKind.LaunchSite && string.IsNullOrEmpty(v.Name);
            string launchedFrom = unnamedSite ? FlightGlobals.ActiveVessel?.launchedFrom : null;
            var key = new CommandVantageKey(v, launchedFrom);
            float now = Time.realtimeSinceStartup;
            if (_entries.TryGetValue(key, out var e))
            {
                if (e.Site != null || now < e.RetryAt) return e;
            }
            else
            {
                if (_entries.Count >= MaxVantages) _entries.Clear();
                e = new Entry();
                _entries[key] = e;
            }
            var cv = KerbcastApiImpl.ToCommandVantage(v);
            if (cv == null || cv.Invalid() != null)
            {
                e.RetryAt = float.MaxValue;
                return e;
            }
            e.Site = CommandCameraProvider.ResolveVantage(cv, unnamedSite ? launchedFrom : cv.Name, out _, out string fatal);
            if (e.Site == null)
            {
                /* Only a named place can be missing for good; an unnamed launch site is tried again. */
                e.RetryAt = fatal != null && !unnamedSite ? float.MaxValue : now + RetrySeconds;
                return e;
            }
            e.Occlusion = new CommandCameraOcclusion(CommandCameraRig.TerrainSampleCount, 0);
            return e;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
    }

    /* A NetworkVantage by value, so a caller building a fresh one each call still finds its kept site. */
    internal readonly struct CommandVantageKey : System.IEquatable<CommandVantageKey>
    {
        private readonly Api.SiteKind _kind;
        private readonly string _name;
        private readonly double _height, _distance, _bearing, _lat, _lon;
        private readonly int _body;
        private readonly uint _pid;
        private readonly string _launchedFrom;

        public CommandVantageKey(Api.NetworkVantage v, string launchedFrom)
        {
            _launchedFrom = launchedFrom ?? "";
            _kind = v.Kind;
            _name = v.Name ?? "";
            _height = v.Height;
            _distance = v.Distance;
            _bearing = v.Bearing;
            _lat = v.Latitude;
            _lon = v.Longitude;
            _body = v.BodyIndex;
            _pid = v.PersistentId;
        }

        public bool Equals(CommandVantageKey o) =>
            _kind == o._kind && _name == o._name && _height.Equals(o._height) && _distance.Equals(o._distance)
            && _bearing.Equals(o._bearing) && _lat.Equals(o._lat) && _lon.Equals(o._lon) && _body == o._body && _pid == o._pid
            && _launchedFrom == o._launchedFrom;

        public override bool Equals(object obj) => obj is CommandVantageKey k && Equals(k);

        public override int GetHashCode()
        {
            unchecked
            {
                int h = (int)_kind;
                h = h * 31 + _name.GetHashCode();
                h = h * 31 + _height.GetHashCode();
                h = h * 31 + _distance.GetHashCode();
                h = h * 31 + _bearing.GetHashCode();
                h = h * 31 + _lat.GetHashCode();
                h = h * 31 + _lon.GetHashCode();
                h = h * 31 + _body;
                h = h * 31 + _launchedFrom.GetHashCode();
                return h * 31 + (int)_pid;
            }
        }
    }
}
