using System;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Kerbcast
{
    /* Where a lens stands for one render UT, as the picture solves light times from it. */
    internal struct PictureLens
    {
        /* The craft the lens rides, by persistentId, or OwnKey for a lens on no craft. */
        public long Key;
        public double Ut;
        /* The point every camera on the craft solves from, in world space. */
        public DVec3 Point;
        public CelestialBody Body;
        /* How far this camera's eye is from Point. */
        public double Spread;

        public bool Valid => Body != null;

        /* A lens that shares its solves with nobody, keyed apart from every craft. */
        public static long OwnKey(uint flightId) => -1L - flightId;

        /* A camera at `eye` on craft `v`. */
        public static PictureLens OnCraft(Vessel v, double ut, DVec3 eye)
        {
            if (v == null || v.mainBody == null || !PicturePass.TryCraftPoint(v, ut, out var point, out _)) return default;
            var p = new DVec3(point.x, point.y, point.z);
            return new PictureLens { Key = v.persistentId, Ut = ut, Point = p, Body = v.mainBody, Spread = (eye - p).Magnitude };
        }
    }

    /* One craft drawn at its past instant, in the identity it had then. */
    internal struct PastPicture
    {
        /* The craft id drawn: the one asked about, or the craft it was part of or not yet merged from. */
        public uint Pid;
        public DVec3 World;
        /* The craft's root orientation in world space, and its parts, at the instant. */
        public Quaternion Rotation;
        public Composition Parts;
        public CelestialBody Body;
        public double Ut;
        public double SizeM;
    }

    internal enum OtherSight
    {
        /* Drawn by the scene as it is now. */
        Live,
        /* Drawn, or to be drawn, at its past instant. */
        Past,
        /* Its light is not arriving: nothing of it may show. */
        Unseen,
    }

    /*
     * The light-time picture of one camera: where each craft is drawn from
     * this lens, for the stand-ins, the observed anchors and onboard tracking.
     * Every camera has one, a command camera's rig included, and the past
     * instants are solved once a frame per lens craft and shared by every
     * camera on it (LensSights).
     *
     * It takes plain Unity and KSP handles rather than a KerbcastCamera, so a
     * camera that drives KSP's own render stack can run the same pass around
     * it. The render hooks run in this order each frame: Prepare, the scaled
     * pair around the scaled layer, then BeforeLocalLayers and AfterLayers
     * around the far, mid and near layers.
     *
     * With no signal speed it reads nothing and changes nothing: every craft
     * is Live and the hooks do no work.
     */
    internal sealed class PicturePass
    {
        private const string Tag = "[Kerbcast][PICTURE]";
        private const double MinRadiusM = 2.0;
        /* Room for a lens that slews between the solve and the render. */
        private const double SpreadPadM = 0.5;
        private const float CostLogSeconds = 10f;

        private static readonly LensSightsTable s_lenses = new LensSightsTable();

        private readonly Func<Vessel> _craft;
        private readonly Func<Vector3> _eye;
        private PictureLens _lens;
        private int _lensFrame = -1;

        /* `craft` is the craft the lens rides and `eye` where it looks from;
           null `craft` leaves the lens to Place. */
        public PicturePass(Func<Vessel> craft, Func<Vector3> eye)
        {
            _craft = craft;
            _eye = eye;
        }

        /* A rig that solves its own lens places it, for every read this frame. */
        public void Place(PictureLens lens)
        {
            _lens = lens;
            _lensFrame = Time.frameCount;
        }

        /* Before any layer renders. Blank to send black, for a frame whose picture cannot be drawn without showing something early. */
        public RigFrame Prepare() => RigFrame.Render;

        /* Before the scaled layer renders, ahead of the visual-mod integrations'
           per-layer call, so they read the bodies where this camera draws them:
           bodies at their past instants are placed here. AfterScaledLayer puts
           them back where stock had them, for the other cameras. */
        public void BeforeScaledLayer() { }
        public void AfterScaledLayer() { }

        /* Before the local layers render: where a loaded craft beyond the
           boundary is hidden from this camera and stand-ins for craft the scene
           does not draw are switched on, on a layer every shed level still
           renders. AfterLayers switches both back once the frame is drawn. It
           always runs after BeforeLocalLayers, even when that or a render threw
           part way, so it must undo exactly what was done and nothing twice. */
        public void BeforeLocalLayers() { }
        public void AfterLayers() { }

        /*
         * How craft `pid` is drawn from this lens now: live while it is loaded
         * or there is no signal speed, otherwise at its past instant, sized by
         * its parts then, or not at all.
         */
        public OtherSight Locate(uint pid, out DVec3 world, out double radius)
        {
            world = default;
            radius = MinRadiusM;
            double? c = LightTime.SignalSpeed;
            if (!c.HasValue) return OtherSight.Live;
            if (FlightGlobals.FindVessel(pid, out var v) && v != null && v.loaded) return OtherSight.Live;
            if (!TryFrameLens(out var lens) || !TrySee(pid, lens, out var seen)) return OtherSight.Unseen;
            world = seen.World;
            if (seen.SizeM > 0) radius = Math.Max(MinRadiusM, seen.SizeM);
            return OtherSight.Past;
        }

        /* A world point on craft `v` as this lens draws it: `live` while the
           craft is drawn live, its past centre while it is drawn at its past
           instant, null while none of its light is arriving. */
        public Vector3? Drawn(Vessel v, Vector3 live)
        {
            if (v == null) return live;
            switch (Locate(v.persistentId, out var world, out _))
            {
                case OtherSight.Past: return new Vector3((float)world.X, (float)world.Y, (float)world.Z);
                case OtherSight.Unseen: return null;
                default: return live;
            }
        }

        /* The lens for this frame: placed by a rig, or worked out once a frame
           from the craft the camera rides. None from an earlier frame. */
        private bool TryFrameLens(out PictureLens lens)
        {
            var craft = _craft?.Invoke();
            if (craft != null && _lensFrame != Time.frameCount)
            {
                var eye = _eye();
                _lens = PictureLens.OnCraft(craft, Planetarium.GetUniversalTime(), new DVec3(eye.x, eye.y, eye.z));
                if (_lens.Valid) _lens.Spread += SpreadPadM;
                _lensFrame = Time.frameCount;
            }
            lens = _lens;
            return lens.Valid && _lensFrame == Time.frameCount;
        }

        /*
         * Craft `pid` as the ring shows it to `lens` at its UT: the light time
         * solved in the frame of the lens's and the craft's common ancestor
         * body, with each chain of bodies at its own instant, then the craft put
         * against its body as the scene draws that body now. A craft that has
         * since come off another is shown as part of it until the split's light
         * arrives. False while none of its light is arriving, and with no
         * signal speed.
         */
        public bool TrySee(uint pid, PictureLens lens, out PastPicture seen)
        {
            uint drawn = pid;
            return TrySee(pid, ref drawn, false, lens, out seen);
        }

        /* A feed's subject, now named `subject` and last drawn as `drawn`: a
           craft it merged into at the present is drawn only once the merge's
           light arrives (LensSights.SeeSubject). */
        public bool TrySeeSubject(uint subject, ref uint drawn, PictureLens lens, out PastPicture seen) =>
            TrySee(subject, ref drawn, true, lens, out seen);

        private bool TrySee(uint pid, ref uint drawn, bool subject, PictureLens lens, out PastPicture seen)
        {
            seen = default;
            double? c = LightTime.SignalSpeed;
            var bodies = FlightGlobals.Bodies;
            if (!c.HasValue || pid == 0 || bodies == null || !lens.Valid) return false;
            long t0 = Stopwatch.GetTimestamp();
            var ring = LightHistoryHost.History;
            var r = Planetarium.Rotation;
            var deRot = new DQuat(r.x, r.y, r.z, r.w).Conjugate;
            var lensOffset = deRot.Rotate(lens.Point - ToD(lens.Body.position));
            var sights = s_lenses.For(lens.Key, lens.Ut);
            sights.Cover(lens.Ut, lens.Spread);
            long solves = sights.Solves;
            LensSight sight;
            if (ring.SampleEverything)
            {
                var tree = new RingBodies(ring);
                sight = See(sights, ring, pid, ref drawn, subject, lens, lensOffset, c.Value, ref tree);
            }
            else
            {
                var tree = new KeplerBodyTree(bodies, deRot);
                sight = See(sights, ring, pid, ref drawn, subject, lens, lensOffset, c.Value, ref tree);
            }
            int index = sight.State.Body;
            bool found = sight.Found && index >= 0 && index < bodies.Count && bodies[index] != null;
            if (found)
            {
                var back = deRot.Conjugate;
                seen.Pid = sight.Pid;
                seen.Body = bodies[index];
                seen.World = ToD(seen.Body.position) + back.Rotate(PastInstant.AboutDrawnBody(sight.State, lens.Ut));
                var q = (back * PastInstant.AttitudeAboutDrawnBody(sight.State, lens.Ut)).Normalized;
                seen.Rotation = new Quaternion((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);
                seen.Parts = sight.Parts;
                seen.Ut = sight.Ut;
                seen.SizeM = sight.SizeM;
            }
            Cost.Add(Stopwatch.GetTimestamp() - t0, sights.Solves > solves);
            return found;
        }

        private static LensSight See<TB>(LensSights sights, LightHistory ring, uint pid, ref uint drawn, bool subject, PictureLens lens,
            DVec3 lensOffset, double c, ref TB tree) where TB : IBodyTree
        {
            int body = lens.Body.flightGlobalsIndex;
            return subject
                ? sights.SeeSubject(ring, pid, ref drawn, lens.Ut, body, lensOffset, c, ref tree)
                : sights.SeeAsItWas(ring, pid, lens.Ut, body, lensOffset, c, ref tree);
        }

        /* Craft `from` became `to` by docking into it: a lens on it keeps its sights and holds. */
        public static void CraftMerged(uint from, uint to) => s_lenses.Rekey(from, to);

        /* Lenses with sights kept, for the telemetry block. */
        public static int LensCount => s_lenses.Count;

        /*
         * Where a craft stands for the light time to and from it: its centre of
         * mass while it flies loaded, the ground under it once landed or packed,
         * otherwise its conic. A command camera on a craft stands exactly here.
         */
        public static bool TryCraftPoint(Vessel v, double ut, out Vector3d world, out string source)
        {
            world = default;
            source = null;
            if (v.loaded && !v.packed)
            {
                world = v.CoMD;
                source = "com";
            }
            else if (v.LandedOrSplashed)
            {
                world = v.mainBody.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
                source = "surface";
            }
            else
            {
                if (v.orbit == null) return false;
                world = v.orbit.getPositionAtUT(ut);
                source = "orbit";
            }
            return true;
        }

        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);

        /*
         * The pass's cost over every camera, per rendered frame: time in the
         * sight reads, solves run and sights shared. Logged every
         * CostLogSeconds while a signal speed is set, so a Deck run can read
         * what the light-time picture costs with several cameras on one craft.
         */
        private static class Cost
        {
            private static int s_frame = -1;
            private static long s_frameTicks;
            private static int s_frameSolves, s_frameShared;
            private static int s_frames;
            private static double s_sumMs, s_maxMs;
            private static long s_solves, s_shared;
            private static float s_loggedAt;

            public static void Add(long ticks, bool solved)
            {
                int frame = Time.frameCount;
                if (frame != s_frame)
                {
                    EndFrame();
                    s_frame = frame;
                }
                s_frameTicks += ticks;
                if (solved) s_frameSolves++;
                else s_frameShared++;
            }

            private static void EndFrame()
            {
                if (s_frame >= 0)
                {
                    double ms = s_frameTicks * 1000.0 / Stopwatch.Frequency;
                    if (KerbcastSettings.EnableTelemetry) Telemetry.Picture.Record(ms);
                    s_frames++;
                    s_sumMs += ms;
                    if (ms > s_maxMs) s_maxMs = ms;
                    s_solves += s_frameSolves;
                    s_shared += s_frameShared;
                }
                s_frameTicks = 0;
                s_frameSolves = s_frameShared = 0;
                float now = Time.realtimeSinceStartup;
                if (now - s_loggedAt < CostLogSeconds || s_frames == 0) return;
                var inv = CultureInfo.InvariantCulture;
                Debug.Log($"{Tag} cost over {s_frames} frames: meanMs={(s_sumMs / s_frames).ToString("F4", inv)} "
                    + $"maxMs={s_maxMs.ToString("F4", inv)} solves/frame={((double)s_solves / s_frames).ToString("F1", inv)} "
                    + $"shared/frame={((double)s_shared / s_frames).ToString("F1", inv)} lenses={s_lenses.Count}");
                s_loggedAt = now;
                s_frames = 0;
                s_sumMs = s_maxMs = 0;
                s_solves = s_shared = 0;
            }
        }
    }
}
