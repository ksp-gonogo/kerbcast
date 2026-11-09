using System;
using System.Collections.Generic;
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
        /* The craft the lens rides, by persistentId, never hidden from it; 0 for a lens on a body. */
        public uint Rides;

        public bool Valid => Body != null;

        /* A lens that shares its solves with nobody, keyed apart from every craft. */
        public static long OwnKey(uint flightId) => -1L - flightId;

        /* A camera at `eye` on craft `v`. */
        public static PictureLens OnCraft(Vessel v, double ut, DVec3 eye)
        {
            if (v == null || v.mainBody == null || !PicturePass.TryCraftPoint(v, ut, out var point, out _)) return default;
            var p = new DVec3(point.x, point.y, point.z);
            return new PictureLens { Key = v.persistentId, Ut = ut, Point = p, Body = v.mainBody, Spread = (eye - p).Magnitude, Rides = v.persistentId };
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
        /* The body whose present centre World hangs from: Body, or the frame it was solved in when Body is drawn moved or left out. */
        public CelestialBody About;
        public double Ut;
        public double SizeM;
        /* Why the solve found the craft or not; Unsolved when nothing was solved. */
        public PastStatus Status;
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
     * this lens, for the stand-ins, the observed anchors and onboard tracking,
     * and each body in the scaled layer.
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
     * Loaded craft beyond one frame of light time are hidden and drawn at
     * their past instants (PictureBoundary); the craft the lens rides never is.
     *
     * With no light speed it reads nothing and changes nothing: every craft
     * is Live, every body stays where stock has it, and the hooks do no work.
     */
    internal sealed class PicturePass
    {
        private const string Tag = "[Kerbcast][PICTURE]";
        private const double MinRadiusM = 2.0;
        /* Room for a lens that slews between the solve and the render. */
        private const double SpreadPadM = 0.5;
        private const float CostLogSeconds = 10f;

        private static readonly LensSightsTable s_lenses = new LensSightsTable();
        private static readonly Dictionary<long, BodyPlan> s_bodies = new Dictionary<long, BodyPlan>();
        private const int MaxBodyPlans = 32;
        /*
         * Reads of a body every lens together may take in one frame, the most
         * one lens may take of them, and the least one body is given, enough
         * to hold its last instant.
         */
        private const int FrameBudget = 4096;
        private const int LensBudget = 2048;
        private const int MinBodyReads = 2;
        private static readonly BodyReadPool s_reads = new BodyReadPool(FrameBudget, LensBudget);

        private readonly Func<Vessel> _craft;
        private readonly Func<Vector3> _eye;
        private readonly Func<Camera> _view;
        private readonly Func<RenderTexture> _target;
        private readonly Func<double> _nativeFar;
        private readonly Func<double> _reachFar;
        private readonly Func<Camera> _scaled;
        private readonly CraftHider _hider;
        private readonly CraftStandIns _standIns;
        private readonly HashSet<uint> _walked = new HashSet<uint>();
        private DVec3[] _bodies = new DVec3[0];
        private PictureLens _lens;
        private int _lensFrame = -1;
        /* The scaled transforms this camera moved, as stock had them, to put back after its scaled render. */
        private readonly List<MovedBody> _moved = new List<MovedBody>();
        private int _scaledDepth;
        /* Terrain renderers this camera switched off for its local layers. */
        private readonly List<Renderer> _terrainOff = new List<Renderer>();
        private static readonly SharedWalk<PQS, Renderer> s_terrain = new SharedWalk<PQS, Renderer>();
        private static readonly Action<PQS, List<Renderer>> s_walkTerrain = WalkTerrain;
        private static readonly List<PQ> s_storedQuads = new List<PQ>();
        private static readonly List<PQ> s_ownQuads = new List<PQ>();
        private static readonly List<Renderer> s_quadRenderers = new List<Renderer>();
        private static readonly Func<PQ, PQS> s_sphereOf = q => q.sphereRoot;

        private struct MovedBody
        {
            public Transform Transform;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            /* The atmosphere shell's renderer and the centre stock gave its shader, or null for a body with none. */
            public Renderer Afg;
            public Vector4 AfgOffset;
            /* The renderer whose sun direction was rewritten for its spun-back axes, and the value stock left in its block, or null for none. */
            public Renderer Lit;
            public int LitProperty;
            public Vector4 LitValue;
            /* The shell's light inputs as stock left them, or a null renderer for a body with no shell. */
            public Renderer ShellLit;
            public Vector4 ShellDir;
            public float ShellDot;
            /* The body, its list slot and where it is drawn this frame, for its light. Transform stays null for a body left in place. */
            public CelestialBody Body;
            public int Slot;
            public DVec3 Centre;
            public bool Hidden;
        }

        /* The components LightBodies writes to for one body, found once. */
        private sealed class LightSources
        {
            public GameObject Scaled;
            public MaterialSetDirection Setter;
            public Renderer Rim;
            public string ValueName;
            public int RimProperty;
            public AtmosphereFromGround Afg;
            public Renderer Shell;
        }

        private struct HeldCorona
        {
            public Transform Transform;
            public Vector3 LocalPosition;
            public Quaternion Local;
            public Vector3 WorldPosition;
            public Quaternion World;
        }

        /* Bodies, by index, whose spin-only terrain this camera is hiding, for FarTerrain.SpinHides. */
        private readonly HashSet<int> _spinHiding = new HashSet<int>();

        private readonly List<HeldCorona> _coronas = new List<HeldCorona>();
        private readonly List<SunCoronas> _coronaScratch = new List<SunCoronas>();

        private static readonly int AfgOffsetId = Shader.PropertyToID("_OffsetTransform");
        private readonly MaterialPropertyBlock _afgBlock = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _lightBlock = new MaterialPropertyBlock();
        private static readonly int ShellLightDirId = Shader.PropertyToID("_v4LightDir");
        private static readonly int ShellLightDotId = Shader.PropertyToID("_lightDot");
        private static readonly HashSet<string> s_lightLogged = new HashSet<string>();
        private readonly Dictionary<CelestialBody, LightSources> _lightSources = new Dictionary<CelestialBody, LightSources>();
        private GameScenes _lightSourcesScene;
        private double[] _discs = new double[0];
        /* The list slot of the body this camera aimed the light at last frame, so the choice holds between frames. */
        private int _litSlot = -1;
        private readonly SystemLights _systems = new SystemLights();
        private int[] _parents = new int[0];
        private bool[] _stars = new bool[0];
        private double[] _best = new double[0];
        private int[] _assigned = new int[0];
        /* The spare layer each system root held last frame, or -1, so a system keeps its light between frames. */
        private int[] _systemLayer = new int[0];
        private DVec3[] _centres = new DVec3[0];
        private readonly int[] _spareLayers = new int[3];
        private readonly List<CelestialBody> _systemBodies = new List<CelestialBody>();
        private readonly List<int> _systemLayers = new List<int>();
        private readonly List<Vector3> _systemDirs = new List<Vector3>();
        private int _loggedMainRoot = -2;
        private Transform _sunHeld;
        private Quaternion _sunHeldLocal;

        /* `craft` is the craft the lens rides, `eye` where it looks from and
           `view` the camera whose frustum the stand-ins are drawn for, drawing
           into `target` if it targets that only while it renders; null `craft`
           leaves the lens to Place. `nativeFar` is how far the local layers
           reach of themselves, the terrain they exist to draw, and `reachFar`
           how far they reach this frame, stretched to a subject; both default
           to the view camera's far plane. `scaled` is the camera that
           draws the scaled layer, for a light per planetary system. */
        public PicturePass(Func<Vessel> craft, Func<Vector3> eye, Func<Camera> view, Func<RenderTexture> target = null,
            Func<double> nativeFar = null, Func<double> reachFar = null, Func<Camera> scaled = null)
        {
            _scaled = scaled;
            _craft = craft;
            _eye = eye;
            _view = view;
            _target = target;
            _nativeFar = nativeFar;
            _reachFar = reachFar;
            _hider = new CraftHider(this);
            _standIns = new CraftStandIns(this);
        }

        /* The camera the frame is drawn from, for the stand-ins and the points. */
        internal Camera View => _view?.Invoke();

        /* The texture the frame is drawn into, when the view camera only targets it during its render. */
        internal RenderTexture Target => _target?.Invoke();

        public void Dispose() => _standIns.Dispose();

        /* A rig that solves its own lens places it, for every read this frame. */
        public void Place(PictureLens lens)
        {
            _lens = lens;
            _lensFrame = Time.frameCount;
            LastRides = lens.Rides;
            _hider.LensPlaced();
        }

        /* Before any layer renders. Blank to send black, for a frame whose picture cannot be drawn without showing something early. */
        public RigFrame Prepare()
        {
            if (!_hider.Prepare()) return RigFrame.Blank;
            _standIns.Prepare(_hider);
            return RigFrame.Render;
        }

        /* Whether this camera hides loaded craft `v` this frame, beyond the boundary, and draws it at its past instant instead. */
        public bool Hides(Vessel v) => _hider.Hides(v);

        /* The lens stands inside loaded craft `v` this frame: its parts are hidden from this camera's local layers only. */
        public void HideOwn(Vessel v) => _hider.HideOwn(v);

        /* Whether `v` is the craft the lens stands in this frame, so nothing of it stands in this camera's way. */
        public bool HidesOwn(Vessel v) => _hider.HidesOwn(v);

        /* A command rig draws its subject's stand-in itself this frame, so the pass leaves it out. */
        public void RigDraws(RigDrawn drawn) => _hider.RigDraws(drawn);

        /*
         * Before the scaled layer renders, ahead of the visual-mod integrations'
         * per-layer call, so they read the bodies where this camera draws them:
         * each body's scaled transform is put at its past instant, turned back
         * by its spin over its light time, or shrunk to nothing while the
         * picture has no instant for it. AfterScaledLayer puts every moved
         * transform back exactly as stock had it, for the other cameras and the
         * main view. A call nested inside the pair (a probe re-render) does
         * nothing, and AfterScaledLayer undoes only what was moved, so a throw
         * part way through leaves nothing behind. Each body is then lit from
         * the Sun as this camera draws it (LightBodies); with no light speed
         * set, that alone runs, and only if the player's setting asks for it.
         */
        public void BeforeScaledLayer()
        {
            if (_scaledDepth++ > 0) return;
            var bodies = FlightGlobals.Bodies;
            LogBodyLight(bodies);
            double? c = LightTime.LightSpeed;
            if (bodies == null || !BodyLight.Applies(c.HasValue, KerbcastSettings.PlanetPhases) || !TryFrameLens(out var lens)) return;
            var plan = c.HasValue ? BodiesFor(lens, c.Value) : null;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                var t = b != null && b.scaledBody != null ? b.scaledBody.transform : null;
                if (t == null) continue;
                _moved.Add(new MovedBody { Body = b, Slot = i, Centre = ToD(b.position) });
                if (plan == null || KeepsPresent(b, lens)) continue;
                var moved = _moved[_moved.Count - 1];
                moved.Transform = t;
                moved.Position = t.localPosition;
                moved.Rotation = t.localRotation;
                moved.Scale = t.localScale;
                /* No instant to draw it at yet: shrunk to nothing, its atmosphere shell with it, rather than drawn early. */
                if (plan.Hides(b.flightGlobalsIndex))
                {
                    moved.Hidden = true;
                    _moved[_moved.Count - 1] = moved;
                    t.localScale = Vector3.zero;
                    continue;
                }
                bool placed = plan.TryDrawn(b.flightGlobalsIndex, out var centre, out double dt);
                if (!placed && !plan.TrySpin(b.flightGlobalsIndex, out dt))
                {
                    moved.Transform = null;
                    _moved[_moved.Count - 1] = moved;
                    continue;
                }
                if (placed) moved.Centre = centre;
                _moved[_moved.Count - 1] = moved;
                if (placed) t.position = ScaledSpace.LocalToScaledSpace(new Vector3d(centre.X, centre.Y, centre.Z));
                /* ScaledMovement gives the mesh the body transform's rotation; this is that rotation dt seconds earlier. */
                if (b.rotates && b.bodyTransform != null)
                {
                    var parent = t.parent;
                    var spun = DQuat.FromRotationVector(ToD(b.angularVelocity) * dt);
                    var q = new Quaternion((float)spun.X, (float)spun.Y, (float)spun.Z, (float)spun.W);
                    int held = _coronas.Count;
                    if (b.isStar) HoldCoronas(b);
                    t.rotation = q * (parent != null ? parent.rotation : Quaternion.identity) * b.bodyTransform.rotation;
                    for (int h = held; h < _coronas.Count; h++) _coronas[h].Transform.SetPositionAndRotation(_coronas[h].WorldPosition, _coronas[h].World);
                }
                if (!placed) continue;
                /* AtmosphereFromGround gave its shader the shell's centre in LateUpdate, before the move. */
                var afg = b.afg != null ? b.afg.GetComponent<Renderer>() : null;
                if (afg != null)
                {
                    afg.GetPropertyBlock(_afgBlock);
                    moved.Afg = afg;
                    moved.AfgOffset = _afgBlock.GetVector(AfgOffsetId);
                    _moved[_moved.Count - 1] = moved;
                    _afgBlock.SetVector(AfgOffsetId, b.afg.transform.position);
                    afg.SetPropertyBlock(_afgBlock);
                }
            }
            LightBodies(bodies, plan, lens);
        }

        /* Whether this camera's scaled render lights each body from the Sun itself this frame, so a rig leaves the Sun and the shells' light alone. */
        public bool BodiesLit { get; private set; }

        /*
         * Lights every body from the Sun as this camera draws it. The rim and
         * the atmosphere shell read per-renderer inputs and take each body's
         * own direction. The surface's terminator comes from Unity's one
         * directional light, which can only face one way per render, so it is
         * turned to the body with the widest disc in frame, passing over a
         * lens's own planet while it fills the frame and another body that
         * can be seen is in it. Every input is put back in AfterScaledLayer.
         */
        private void LightBodies(List<CelestialBody> bodies, BodyPlan plan, PictureLens lens)
        {
            var sunBody = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sunBody == null || sunBody.scaledBody == null) return;
            BodiesLit = true;
            var sun = ToD(sunBody.position);
            if (plan != null && plan.TryDrawn(sunBody.flightGlobalsIndex, out var sunDrawn, out _)) sun = sunDrawn;
            var view = View;
            var ahead = default(DVec3);
            double halfDiagonal = 0;
            if (view != null)
            {
                var f = view.transform.forward;
                ahead = new DVec3(f.x, f.y, f.z);
                halfDiagonal = BodyLight.HalfDiagonalRadians(view.fieldOfView, view.aspect);
            }
            if (_discs.Length < bodies.Count) _discs = new double[bodies.Count];
            Array.Clear(_discs, 0, _discs.Length);
            Vector3 lensScaled = ScaledSpace.LocalToScaledSpace(new Vector3d(lens.Point.X, lens.Point.Y, lens.Point.Z));
            Vector3 sunScaled = sunBody.scaledBody.transform.position;
            int underfoot = -1;
            var ownCentre = default(DVec3);
            for (int i = 0; i < _moved.Count; i++)
            {
                var m = _moved[i];
                if (m.Hidden) continue;
                var sources = SourcesFor(m.Body);
                LightRim(ref m, sun, sources);
                _moved[i] = m;
                LightShell(ref m, sun, lensScaled, sunScaled, sources);
                _moved[i] = m;
                if (view != null && !m.Body.isStar)
                {
                    _discs[m.Slot] = BodyLight.DiscInFrame(lens.Point, m.Centre, m.Body.Radius, ReachOf(m.Body), ahead, BodyLight.ConeFor(halfDiagonal, m.Slot == _litSlot));
                    if (m.Body == lens.Body && BodyLight.FillsFrame(_discs[m.Slot], halfDiagonal))
                    {
                        underfoot = m.Slot;
                        ownCentre = m.Centre;
                    }
                }
            }
            if (underfoot >= 0)
            {
                var rt = Target ?? view.targetTexture;
                int rows = rt != null ? rt.height : view.pixelHeight;
                for (int i = 0; i < _moved.Count; i++)
                {
                    var m = _moved[i];
                    if (m.Slot != underfoot && _discs[m.Slot] > 0 && !BodyLight.OutranksOwn(lens.Point, ownCentre, lens.Body.Radius, m.Centre, _discs[m.Slot], view.fieldOfView, rows, m.Slot == _litSlot))
                        _discs[m.Slot] = 0;
                }
            }
            _litSlot = BodyLight.Pick(_discs, bodies.Count, _litSlot, underfoot);
            for (int i = 0; i < _moved.Count; i++)
                if (_moved[i].Slot == _litSlot && !_moved[i].Hidden)
                {
                    TurnSunTo(BodyLight.Direction(sun, _moved[i].Centre, default));
                    break;
                }
            if (view != null) LightSystems(bodies, lens, sun, ahead, halfDiagonal, underfoot, ownCentre);
        }

        /*
         * Gives each other planetary system in frame a light of its own, from
         * the Sun to its planet as drawn, so a second planet in the picture
         * shows its true phase too (SystemLights). The main light's system,
         * and any past the spare layers, keep the main light.
         */
        private void LightSystems(List<CelestialBody> bodies, PictureLens lens, DVec3 sun, DVec3 ahead, double halfDiagonal, int underfoot, DVec3 ownCentre)
        {
            int n = bodies.Count;
            if (_parents.Length < n)
            {
                _parents = new int[n];
                _stars = new bool[n];
                _best = new double[n];
                _assigned = new int[n];
                _centres = new DVec3[n];
                int[] grown = new int[n];
                for (int i = 0; i < n; i++) grown[i] = i < _systemLayer.Length ? _systemLayer[i] : -1;
                _systemLayer = grown;
            }
            for (int i = 0; i < n; i++)
            {
                var b = bodies[i];
                var up = b != null ? b.referenceBody : null;
                _parents[i] = up != null && up != b ? up.flightGlobalsIndex : -1;
                _stars[i] = b == null || b.isStar;
                _best[i] = 0;
                _centres[i] = b != null ? ToD(b.position) : default;
            }
            int mainRoot = _litSlot >= 0 ? BodyLight.SystemRoot(_parents, _stars, _litSlot) : -1;
            for (int i = 0; i < _moved.Count; i++)
            {
                var m = _moved[i];
                _centres[m.Slot] = m.Centre;
                if (m.Hidden || m.Body.isStar) continue;
                int root = BodyLight.SystemRoot(_parents, _stars, m.Slot);
                if (root < 0) continue;
                bool held = root == mainRoot || _systemLayer[root] >= 0;
                double disc = BodyLight.DiscInFrame(lens.Point, m.Centre, m.Body.Radius, ReachOf(m.Body), ahead, BodyLight.ConeFor(halfDiagonal, held));
                if (disc > 0 && underfoot >= 0 && m.Slot != underfoot && BodyLight.BehindLimb(lens.Point, ownCentre, lens.Body.Radius, m.Centre)) disc = 0;
                if (disc > _best[root]) _best[root] = disc;
            }
            int spares = SystemLights.Spares(_spareLayers);
            BodyLight.AssignLayers(_best, n, mainRoot, _systemLayer, _spareLayers, spares, _assigned);
            _systemBodies.Clear();
            _systemLayers.Clear();
            _systemDirs.Clear();
            for (int i = 0; i < _moved.Count; i++)
            {
                var m = _moved[i];
                int root = m.Body.isStar ? -1 : BodyLight.SystemRoot(_parents, _stars, m.Slot);
                if (root < 0 || _assigned[root] < 0 || m.Hidden) continue;
                var d = BodyLight.Direction(sun, _centres[root], default);
                if (d.Magnitude <= 0) continue;
                _systemBodies.Add(m.Body);
                _systemLayers.Add(_assigned[root]);
                _systemDirs.Add(new Vector3((float)d.X, (float)d.Y, (float)d.Z));
            }
            if (_systemBodies.Count == 0 || !_systems.Begin(_scaled?.Invoke(), ScaledSunLightHelper.GetScaledSunLight(), _systemBodies, _systemLayers, _systemDirs))
                for (int i = 0; i < n; i++) _assigned[i] = -1;
            LogSystems(bodies, mainRoot);
            Array.Copy(_assigned, _systemLayer, n);
        }

        /* How far from its centre `b` is drawn, for whether any of it reaches the frame. */
        private static double ReachOf(CelestialBody b)
        {
            var pqs = b.pqsController;
            return BodyLight.ReachRadius(b.Radius, pqs != null ? pqs.radiusMax : 0, b.atmosphere, b.atmosphereDepth);
        }

        /* One line whenever this camera's systems change light, for checking a capture against what lit it. */
        private void LogSystems(List<CelestialBody> bodies, int mainRoot)
        {
            bool changed = mainRoot != _loggedMainRoot;
            for (int i = 0; i < bodies.Count && !changed; i++) changed = _assigned[i] != _systemLayer[i];
            if (!changed) return;
            _loggedMainRoot = mainRoot;
            var line = new System.Text.StringBuilder();
            line.Append($"{Tag} view={(View != null ? View.name : "none")} event=body-light main={(mainRoot >= 0 ? bodies[mainRoot].bodyName : "none")}@{SystemLights.ScaledLayer}(Scaledspace SunLight)");
            for (int i = 0; i < bodies.Count; i++)
            {
                if (_assigned[i] >= 0) line.Append($" group={bodies[i].bodyName}@{_assigned[i]}(Kerbcast system light {_assigned[i]})");
                else if (_best[i] > 0 && i != mainRoot) line.Append($" shared={bodies[i].bodyName}");
            }
            Debug.Log(line.ToString());
        }

        /* Turns the Sun, and the scaled light that hangs from it, to shine along `direction`, unless stock's light is already that close. */
        private void TurnSunTo(DVec3 direction)
        {
            var sun = Sun.Instance != null ? Sun.Instance.transform : null;
            if (sun == null || direction.Magnitude <= 0) return;
            var stock = sun.forward;
            if (CommandCameraMath.SunTurnDeg(new DVec3(stock.x, stock.y, stock.z), direction) <= CommandCameraMath.SunTurnMinDeg) return;
            _sunHeld = sun;
            _sunHeldLocal = sun.localRotation;
            var forward = new Vector3((float)direction.X, (float)direction.Y, (float)direction.Z).normalized;
            sun.rotation = Quaternion.FromToRotation(stock, forward) * sun.rotation;
        }

        /* The shell's light inputs for this body, as AtmosphereFromGround derives them, from the Sun to the body as drawn and the lens as placed. */
        private void LightShell(ref MovedBody m, DVec3 sun, Vector3 lensScaled, Vector3 sunScaled, LightSources sources)
        {
            var afg = m.Body.afg;
            var r = sources.Shell;
            if (r == null) return;
            r.GetPropertyBlock(_afgBlock);
            m.ShellLit = r;
            m.ShellDir = _afgBlock.GetVector(ShellLightDirId);
            m.ShellDot = _afgBlock.GetFloat(ShellLightDotId);
            var stock = afg.sunLightDirection;
            var toSun = BodyLight.Direction(m.Centre, sun, new DVec3(stock.x, stock.y, stock.z));
            _afgBlock.SetVector(ShellLightDirId, new Vector4((float)toSun.X, (float)toSun.Y, (float)toSun.Z, 0f));
            Vector3 down = m.Body.scaledBody.transform.position - lensScaled;
            _afgBlock.SetFloat(ShellLightDotId, Mathf.Clamp01(Vector3.Dot(down, lensScaled - sunScaled) * afg.dawnFactor));
            r.SetPropertyBlock(_afgBlock);
        }

        /*
         * A star's coronas hang under its mesh, and stock turns them to a
         * camera in FixedUpdate. Noted before the mesh is turned back, so they
         * keep the place and orientation stock gave them while the mesh alone
         * turns. Stock never sets their local position, so that comes from the
         * prefab or a planet pack and may be anything.
         */
        private void HoldCoronas(CelestialBody b)
        {
            b.scaledBody.GetComponentsInChildren(false, _coronaScratch);
            for (int i = 0; i < _coronaScratch.Count; i++)
            {
                var t = _coronaScratch[i].transform;
                _coronas.Add(new HeldCorona { Transform = t, LocalPosition = t.localPosition, Local = t.localRotation, WorldPosition = t.position, World = t.rotation });
            }
            _coronaScratch.Clear();
        }

        /*
         * The rim's light direction, from the Sun to this body's drawn centre.
         * MaterialSetDirection stored one shared direction in the mesh's own
         * axes in Update, from the rotation the mesh had then, so a mesh
         * turned back would carry it round with it. Written for the axes the
         * mesh has now, in the property block the setter uses, and put back
         * in AfterScaledLayer.
         */
        private void LightRim(ref MovedBody m, DVec3 sun, LightSources sources)
        {
            var setter = sources.Setter;
            var r = setter != null && setter.target != null ? sources.Rim : null;
            if (r == null || string.IsNullOrEmpty(setter.valueName)) return;
            if (sources.ValueName != setter.valueName)
            {
                sources.ValueName = setter.valueName;
                sources.RimProperty = Shader.PropertyToID(setter.valueName);
            }
            int id = sources.RimProperty;
            r.GetPropertyBlock(_lightBlock);
            m.Lit = r;
            m.LitProperty = id;
            m.LitValue = _lightBlock.GetVector(id);
            var rot = m.Body.scaledBody.transform.rotation;
            var f = setter.target.forward;
            var away = BodyLight.Direction(sun, m.Centre, new DVec3(f.x, f.y, f.z));
            var local = PictureBoundary.LightInBodyAxes(new DQuat(rot.x, rot.y, rot.z, rot.w), away);
            _lightBlock.SetVector(id, new Vector4((float)local.X, (float)local.Y, (float)local.Z, 0f));
            r.SetPropertyBlock(_lightBlock);
        }

        /*
         * This camera's components for a body, looked up again when one was
         * destroyed or the body was given another. A body with no setter or
         * shell renderer is looked up every time, so one added late is found.
         */
        private LightSources SourcesFor(CelestialBody b)
        {
            if (_lightSourcesScene != HighLogic.LoadedScene)
            {
                _lightSourcesScene = HighLogic.LoadedScene;
                _lightSources.Clear();
            }
            if (!_lightSources.TryGetValue(b, out var s))
            {
                s = new LightSources();
                _lightSources[b] = s;
            }
            if (s.Scaled != b.scaledBody || s.Setter == null || s.Rim == null)
            {
                s.Scaled = b.scaledBody;
                s.Setter = b.scaledBody.GetComponent<MaterialSetDirection>();
                s.Rim = s.Setter != null ? s.Setter.GetComponent<Renderer>() : null;
            }
            if (s.Afg != b.afg || s.Shell == null)
            {
                s.Afg = b.afg;
                s.Shell = b.afg != null ? b.afg.GetComponent<Renderer>() : null;
            }
            return s;
        }

        /* Names, once per body, what lights it, for checking a planet pack or a game update against what this pass writes. */
        private static void LogBodyLight(List<CelestialBody> bodies)
        {
            if (bodies == null || s_lightLogged.Count >= bodies.Count) return;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null || b.scaledBody == null || !s_lightLogged.Add(b.bodyName)) continue;
                var setter = b.scaledBody.GetComponent<MaterialSetDirection>();
                var r = b.scaledBody.GetComponent<Renderer>();
                var shader = r != null && r.sharedMaterial != null ? r.sharedMaterial.shader : null;
                Debug.Log($"[Kerbcast] event=body-light body={b.bodyName} valueName={(setter != null ? setter.valueName : "none")} target={(setter != null && setter.target != null ? setter.target.name : "none")} shader={(shader != null ? shader.name : "none")}");
            }
        }

        public void AfterScaledLayer()
        {
            if (--_scaledDepth > 0) return;
            _scaledDepth = 0;
            try { _systems.End(); }
            finally { RestoreScaled(); }
        }

        private void RestoreScaled()
        {
            BodiesLit = false;
            if (_sunHeld != null)
            {
                _sunHeld.localRotation = _sunHeldLocal;
                _sunHeld = null;
            }
            for (int i = 0; i < _coronas.Count; i++)
            {
                var held = _coronas[i];
                if (held.Transform == null) continue;
                held.Transform.localPosition = held.LocalPosition;
                held.Transform.localRotation = held.Local;
            }
            _coronas.Clear();
            for (int i = _moved.Count - 1; i >= 0; i--)
            {
                var m = _moved[i];
                if (m.Afg != null)
                {
                    m.Afg.GetPropertyBlock(_afgBlock);
                    _afgBlock.SetVector(AfgOffsetId, m.AfgOffset);
                    m.Afg.SetPropertyBlock(_afgBlock);
                }
                if (m.ShellLit != null)
                {
                    m.ShellLit.GetPropertyBlock(_afgBlock);
                    _afgBlock.SetVector(ShellLightDirId, m.ShellDir);
                    _afgBlock.SetFloat(ShellLightDotId, m.ShellDot);
                    m.ShellLit.SetPropertyBlock(_afgBlock);
                }
                if (m.Lit != null)
                {
                    m.Lit.GetPropertyBlock(_lightBlock);
                    _lightBlock.SetVector(m.LitProperty, m.LitValue);
                    m.Lit.SetPropertyBlock(_lightBlock);
                }
                if (m.Transform == null) continue;
                m.Transform.localPosition = m.Position;
                m.Transform.localRotation = m.Rotation;
                m.Transform.localScale = m.Scale;
            }
            _moved.Clear();
        }

        /* Whether this camera draws any body away from where stock has it, or hides one, this frame. */
        public bool MovesBodies()
        {
            double? c = LightTime.LightSpeed;
            if (!c.HasValue || !TryFrameLens(out var lens)) return false;
            var plan = BodiesFor(lens, c.Value);
            var bodies = FlightGlobals.Bodies;
            if (plan == null || bodies == null) return false;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b != null && !KeepsPresent(b, lens) && (plan.Moves(b.flightGlobalsIndex) || plan.Hides(b.flightGlobalsIndex) || plan.TrySpin(b.flightGlobalsIndex, out _))) return true;
            }
            return false;
        }

        /*
         * Whether this camera leaves body `b` at the present: its terrain is
         * built in local space, where stock has it, and is close enough to
         * this lens for the local layers to draw it, so the scaled sphere must
         * stay with its ground. Only the local layers' own reach counts, not a
         * far plane stretched to a subject, or a telescope on a craft at a
         * planet would keep that planet at the present for as long as it
         * looked at the craft.
         */
        private bool KeepsPresent(CelestialBody b, PictureLens lens) => TerrainLocal(b, lens.Point);

        /* Whether terrain of `b` is built and within this camera's own reach of `eye`, so the local layers draw that body's ground. */
        public bool TerrainLocal(CelestialBody b, DVec3 eye)
        {
            var pqs = b != null ? b.pqsController : null;
            if (pqs == null || !pqs.isActive) return false;
            double far = _nativeFar != null ? _nativeFar() : View != null ? View.farClipPlane : 0;
            return PictureBoundary.TerrainInReach((eye - ToD(b.position)).Magnitude, Math.Max(b.Radius, pqs.radiusMax), far);
        }

        /* Whether this camera leaves body `b` out of its picture this frame, its light not yet placed. */
        public bool HidesBody(CelestialBody b)
        {
            double? c = LightTime.LightSpeed;
            if (b == null || !c.HasValue || !TryFrameLens(out var lens)) return false;
            var plan = BodiesFor(lens, c.Value);
            return plan != null && !KeepsPresent(b, lens) && plan.Hides(b.flightGlobalsIndex);
        }

        /* Where this camera draws body `b` this frame when it is drawn at its
           past instant: its centre in world space, and the seconds from the
           render UT back to that instant. False for a body drawn where stock
           has it, and with no light speed, with `dt` still its spin back when
           only its spin moves (the lens's own body and those above it). */
        public bool TryDrawnBody(CelestialBody b, out DVec3 centre, out double dt)
        {
            centre = default;
            dt = 0;
            double? c = LightTime.LightSpeed;
            if (b == null || !c.HasValue || !TryFrameLens(out var lens)) return false;
            var plan = BodiesFor(lens, c.Value);
            if (plan == null || KeepsPresent(b, lens)) return false;
            if (plan.TryDrawn(b.flightGlobalsIndex, out centre, out dt)) return true;
            plan.TrySpin(b.flightGlobalsIndex, out dt);
            return false;
        }

        /*
         * Every body where this camera draws it this frame, by list index,
         * into `centres` and `radii` (grown to fit): at its past instant,
         * where stock has it, or NaN for one the picture leaves out. Radii
         * stop just short of the surface, so a point on it is not hidden by
         * it. Returns how many bodies there are.
         */
        internal int DrawnBodies(ref DVec3[] centres, ref double[] radii)
        {
            var list = FlightGlobals.Bodies;
            if (list == null) return 0;
            if (centres == null || centres.Length < list.Count) centres = new DVec3[list.Count];
            if (radii == null || radii.Length < list.Count) radii = new double[list.Count];
            double? c = LightTime.LightSpeed;
            PictureLens lens = default;
            var plan = c.HasValue && TryFrameLens(out lens) ? BodiesFor(lens, c.Value) : null;
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                radii[i] = b != null ? b.Radius * (1.0 - 1e-7) : 0;
                bool kept = b != null && plan != null && KeepsPresent(b, lens);
                if (b == null || (plan != null && !kept && plan.Hides(b.flightGlobalsIndex)))
                    centres[i] = new DVec3(double.NaN, double.NaN, double.NaN);
                else if (plan == null || kept || !plan.TryDrawn(b.flightGlobalsIndex, out centres[i], out _))
                    centres[i] = ToD(b.position);
            }
            return list.Count;
        }

        /* Before the local layers render: each loaded craft beyond the
           boundary is hidden from this camera (CraftHider) and stand-ins are
           switched on at their past instants (CraftStandIns). AfterLayers
           switches both back once the frame is drawn. It always runs after
           BeforeLocalLayers, even when that or a render threw part way, and
           undoes exactly what was done. */
        public void BeforeLocalLayers()
        {
            _hider.Hide();
            _standIns.Show();
            HideFarTerrain();
        }

        public void AfterLayers()
        {
            try { _hider.Restore(); }
            finally
            {
                try { _standIns.Restore(); }
                finally { RestoreFarTerrain(); }
            }
        }

        /*
         * A local layer stretched to a far subject can reach terrain built in
         * local space on a body this camera draws at its past instant, or
         * turns back by enough to see at this field, which would paint the
         * body as it is now over the scaled sphere. Its renderers are switched
         * off for this camera's local layers alone, as the craft hider does,
         * and only those that were on are put back.
         */
        private void HideFarTerrain()
        {
            double? c = LightTime.LightSpeed;
            var bodies = FlightGlobals.Bodies;
            if (!c.HasValue || bodies == null)
            {
                _spinHiding.Clear();
                return;
            }
            if (!TryFrameLens(out var lens)) return;
            var plan = BodiesFor(lens, c.Value);
            if (plan == null) return;
            double reach = _reachFar != null ? _reachFar() : View != null ? View.farClipPlane : 0;
            var view = View;
            var rt = view != null ? Target ?? view.targetTexture : null;
            int rows = rt != null ? rt.height : view != null ? view.pixelHeight : 0;
            double fov = view != null ? view.fieldOfView : 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null) continue;
                int index = b.flightGlobalsIndex;
                bool moves = plan.Moves(index), left = plan.Hides(index);
                /* Before the reach tests, so what is held depends on the field and the light speed alone, never on range. */
                bool spinHides = StepSpinHide(index, !moves && !left && plan.TrySpin(index, out _), b, c.Value, fov, rows);
                var pqs = b.pqsController;
                if (pqs == null || !pqs.isActive || KeepsPresent(b, lens)) continue;
                if (!FarTerrain.DrawnAway(moves, left, spinHides)) continue;
                if (!PictureBoundary.TerrainInReach((lens.Point - ToD(b.position)).Magnitude, Math.Max(b.Radius, pqs.radiusMax), reach)) continue;
                SwitchOff(pqs);
                var children = pqs.ChildSpheres;
                if (children == null) continue;
                for (int s = 0; s < children.Length; s++)
                    if (children[s] != null) SwitchOff(children[s]);
            }
        }

        /* This camera's held decision for body `index`, moved on by this frame's slip. */
        private bool StepSpinHide(int index, bool spinOnly, CelestialBody b, double c, double fov, int rows)
        {
            bool was = _spinHiding.Contains(index);
            bool now = FarTerrain.SpinHides(was, spinOnly, spinOnly ? FarTerrain.SpinSlipPx(b.angularV, b.Radius, c, fov, rows) : 0);
            if (now && !was) _spinHiding.Add(index);
            else if (was && !now) _spinHiding.Remove(index);
            return now;
        }

        private void SwitchOff(PQS sphere)
        {
            var renderers = s_terrain.For(sphere, Time.frameCount, QuadsInUse(), s_walkTerrain);
            for (int i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r == null || r.forceRenderingOff) continue;
                r.forceRenderingOff = true;
                _terrainOff.Add(r);
            }
        }

        /* Stock's count of quads handed out to spheres, which moves when a sphere builds or drops one. */
        private static int QuadsInUse()
        {
            var cache = PQSCache.Instance;
            return cache != null ? cache.cachePQAssignedCount : 0;
        }

        private static void WalkTerrain(PQS sphere, List<Renderer> into)
        {
            sphere.GetComponentsInChildren(false, into);
            AddStoredQuads(sphere, into);
        }

        /* Adds the renderers of `sphere`'s quads in the storage all spheres share, where stock keeps the most detailed ones. */
        private static void AddStoredQuads(PQS sphere, List<Renderer> into)
        {
            if (sphere.LocalSpacePQStorage == null) return;
            sphere.LocalSpacePQStorage.GetComponentsInChildren(false, s_storedQuads);
            FarTerrain.OwnedQuads(s_storedQuads, sphere, s_sphereOf, s_ownQuads);
            for (int i = 0; i < s_ownQuads.Count; i++)
            {
                s_ownQuads[i].GetComponentsInChildren(false, s_quadRenderers);
                into.AddRange(s_quadRenderers);
            }
            s_storedQuads.Clear();
            s_ownQuads.Clear();
            s_quadRenderers.Clear();
        }

        private void RestoreFarTerrain()
        {
            for (int i = 0; i < _terrainOff.Count; i++)
                if (_terrainOff[i] != null) _terrainOff[i].forceRenderingOff = false;
            _terrainOff.Clear();
        }

        /*
         * The ring's craft this camera's stand-ins may draw besides those
         * already in `into`: every track not drawn by the scene (loaded, or
         * the craft the lens rides) nor by the rig (`rigDraws`), less those
         * the cull skips, added by persistentId. A craft is left out only
         * when every track under its id is skipped. Returns how many were.
         */
        internal int Candidates(in CullView view, RigDrawn rigDraws, CraftHider hider, List<uint> into)
        {
            double? c = LightTime.LightSpeed;
            var list = FlightGlobals.Bodies;
            if (!c.HasValue || list == null || !TryFrameLens(out var lens)) return 0;
            int lensBody = lens.Body.flightGlobalsIndex;
            var ring = LightHistoryHost.History;
            if (_bodies.Length != list.Count) _bodies = new DVec3[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i];
                _bodies[i] = b != null ? ToD(b.position) : new DVec3(double.NaN, double.NaN, double.NaN);
            }
            var r = Planetarium.Rotation;
            var toWorld = new DQuat(r.x, r.y, r.z, r.w);
            double now = Planetarium.GetUniversalTime();
            /* The eye is lens.Spread from the solve point, and the solve covers lenses up to its own spread from that point. */
            double spread = lens.Spread + Math.Max(lens.Spread, s_lenses.SpreadOf(lens.Key));
            int skipped = 0;
            _walked.Clear();
            for (int i = 0; i < into.Count; i++) _walked.Add(into[i]);
            foreach (var t in ring.Tracks)
            {
                if (t.IsBody || t.Pid == 0 || rigDraws.Draws(t.Pid) || hider.SceneDraws(t.Pid)) continue;
                if (_walked.Contains(t.Pid)) continue;
                if (PictureCull.Skip(ring, t, now, view, toWorld, _bodies, c.Value, lensBody, spread))
                {
                    skipped++;
                    continue;
                }
                _walked.Add(t.Pid);
                into.Add(t.Pid);
            }
            return skipped;
        }

        /*
         * How craft `pid` is drawn from this lens now: live while it is loaded
         * inside the boundary or there is no light speed, otherwise at its
         * past instant, sized by its parts then, or not at all.
         */
        public OtherSight Locate(uint pid, out DVec3 world, out double radius)
        {
            world = default;
            radius = MinRadiusM;
            double? c = LightTime.LightSpeed;
            if (!c.HasValue) return OtherSight.Live;
            if (FlightGlobals.FindVessel(pid, out var v) && v != null && v.loaded && !_hider.Hides(pid)) return OtherSight.Live;
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

        /* Whether craft `pid` still flies under its own id in the ring, not ended or ghosted. */
        internal bool FliesOn(uint pid)
        {
            var t = LightHistoryHost.History.Track(LightHistory.CraftKey(pid));
            return t != null && !t.Ended && !t.IsGhost;
        }

        /* The craft the last placed lens rode, for a frame a rig has not placed one yet. */
        internal uint LastRides { get; private set; }

        /* The craft the camera rides, for a camera that places no lens. */
        internal Vessel Craft => _craft?.Invoke();

        /* Where this camera looks from now, in world space. */
        internal DVec3 EyePoint()
        {
            var e = _eye();
            return new DVec3(e.x, e.y, e.z);
        }

        internal bool TryLens(out PictureLens lens) => TryFrameLens(out lens);

        /* Craft `pid` at its past instant from this frame's lens, and its light time. */
        internal bool TryPast(uint pid, out PastPicture seen, out double lightS)
        {
            seen = default;
            lightS = double.PositiveInfinity;
            if (!TryFrameLens(out var lens) || !TrySee(pid, lens, out seen)) return false;
            lightS = lens.Ut - seen.Ut;
            return true;
        }

        /*
         * The turn and shift that carry a point on craft `pid`, hidden and
         * drawn at its past instant, from where the live craft is to where
         * its stand-in is drawn: drawn = pivot + turn (live - livePivot).
         * False while none of its light is arriving.
         */
        public bool TryCarry(uint pid, DVec3 livePivot, Quaternion liveRotation, out DVec3 drawnPivot, out Quaternion turn)
        {
            drawnPivot = default;
            turn = Quaternion.identity;
            if (!TryPast(pid, out var seen, out _)) return false;
            drawnPivot = seen.World;
            var t = PictureBoundary.Turn(ToQ(liveRotation), ToQ(seen.Rotation));
            turn = new Quaternion((float)t.X, (float)t.Y, (float)t.Z, (float)t.W);
            return true;
        }

        /*
         * Part `partPid` as the stand-in of a craft this camera draws at its
         * past instant draws it: its recorded pose in whichever hidden,
         * lingering or other drawn craft carried it at that craft's past
         * instant, wherever the part is now (decoupled, destroyed or docked
         * onto another craft). False when no such craft shows it.
         */
        public bool TryHiddenPart(uint partPid, out DVec3 world, out Quaternion rotation, out double radius)
        {
            world = default;
            rotation = Quaternion.identity;
            radius = 0;
            if (partPid == 0 || !LightTime.LightSpeed.HasValue) return false;
            if (_ownersFrame != Time.frameCount)
            {
                _ownersFrame = Time.frameCount;
                _owners.Clear();
                foreach (uint pid in _hider.HiddenIds()) Own(pid);
                foreach (uint pid in _hider.Lingering.Pids) Own(pid);
                var solved = _standIns.Solved;
                for (int i = 0; i < solved.Count; i++) Own(solved[i]);
            }
            return _owners.TryOwner(partPid, out uint craft) && TryDrawnPart(craft, partPid, out world, out rotation, out radius);
        }

        /* Claims for craft `pid` every part its record holds at its past instant. */
        private void Own(uint pid)
        {
            if (!TryPast(pid, out var seen, out _) || seen.Parts?.Parts == null) return;
            foreach (var r in seen.Parts.Parts) _owners.Add(r.Pid, pid);
        }

        private readonly PartOwners _owners = new PartOwners();
        private int _ownersFrame = -1;

        private bool TryDrawnPart(uint pid, uint partPid, out DVec3 world, out Quaternion rotation, out double radius)
        {
            world = default;
            rotation = Quaternion.identity;
            radius = 0;
            if (!TryPast(pid, out var seen, out _) || seen.Parts?.Parts == null) return false;
            foreach (var r in seen.Parts.Parts)
            {
                if (r.Pid != partPid) continue;
                var q = ToQ(seen.Rotation);
                world = PictureBoundary.PartAt(new DVec3(r.Px, r.Py, r.Pz), new DVec3(seen.Parts.ComX, seen.Parts.ComY, seen.Parts.ComZ), seen.World, q);
                rotation = seen.Rotation * new Quaternion(r.Qx, r.Qy, r.Qz, r.Qw);
                radius = r.RadiusM;
                return true;
            }
            return false;
        }

        private static DQuat ToQ(Quaternion q) => new DQuat(q.x, q.y, q.z, q.w);

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
         * body, with each chain of bodies at its own instant, and the craft
         * placed where its body is drawn: at its own past position when the
         * body is drawn at its past instant, otherwise on the body as drawn now.
         * A craft that has since come off another is shown as part of it until
         * the split's light arrives. False while none of its light is arriving,
         * and with no light speed.
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
            seen.Status = PastStatus.Unsolved;
            double? c = LightTime.LightSpeed;
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
            if (ring.BodiesOffKepler > 0)
            {
                var tree = new RingBodies<KeplerBodyTree>(ring, new KeplerBodyTree(bodies, deRot));
                sight = See(sights, ring, pid, ref drawn, subject, lens, lensOffset, c.Value, ref tree);
            }
            else
            {
                var tree = new KeplerBodyTree(bodies, deRot);
                sight = See(sights, ring, pid, ref drawn, subject, lens, lensOffset, c.Value, ref tree);
            }
            seen.Status = sight.Status;
            int index = sight.State.Body;
            bool found = sight.Found && index >= 0 && index < bodies.Count && bodies[index] != null;
            if (found)
            {
                var back = deRot.Conjugate;
                seen.Pid = sight.Pid;
                seen.Body = bodies[index];
                /* Against a body drawn at its own past instant, or left out of the picture, the craft is at its own past
                   position and attitude in the frame both were solved in, whose centre is the lens's body or above it and
                   so drawn where it is.
                   Against a body drawn where stock has it, it sits on that body as drawn, carried round with its spin, and in flight
                   about the lens's body turned as its light time was solved (PastInstant.Turn). */
                var plan = BodiesFor(lens, c.Value);
                int frame = sight.Frame;
                DQuat q;
                if (plan != null && !KeepsPresent(seen.Body, lens) && (plan.Moves(index) || plan.Hides(index)) && frame >= 0 && frame < bodies.Count && bodies[frame] != null)
                {
                    seen.World = ToD(bodies[frame].position) + back.Rotate(sight.About);
                    seen.About = bodies[frame];
                    q = (back * sight.State.Attitude).Normalized;
                }
                else
                {
                    var spin = ring.SpinOf(lens.Body.flightGlobalsIndex);
                    /* A body whose spin is drawn turned back (the lens's own or one above it) carries the craft on it only as far as it is drawn. */
                    double drawnAt = lens.Ut;
                    if (plan != null && !KeepsPresent(seen.Body, lens) && plan.TrySpin(index, out double spinBack)) drawnAt += spinBack;
                    seen.World = ToD(seen.Body.position) + back.Rotate(PastInstant.AboutDrawnBody(sight.State, drawnAt, spin));
                    seen.About = seen.Body;
                    q = (back * PastInstant.AttitudeAboutDrawnBody(sight.State, drawnAt, spin)).Normalized;
                }
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

        /*
         * Where every body is drawn from `lens` this frame, solved once per lens
         * point and shared by every camera that solves from it. Each camera
         * then leaves a body at the present for itself where its local layers
         * can draw that body's terrain (KeepsPresent). The solve and the move
         * read every position through Planetarium's rotation, so a body the
         * world's axes turn with (inverseRotation) is solved like any other.
         * A body that has left its Kepler path is read from its ring track
         * while the track covers the instant.
         *
         * A body that may outrun light is followed by the plan's trace for it,
         * within the lens's fair share of the frame's reads, the bodies taking
         * turns at going first. A body with no instant to draw, its search
         * carrying on with nothing to hold, is hidden rather than drawn where
         * it is now.
         */
        private static BodyPlan BodiesFor(PictureLens lens, double c)
        {
            var bodies = FlightGlobals.Bodies;
            var ring = LightHistoryHost.History;
            if (bodies == null || ring == null || !lens.Valid) return null;
            int now = Time.frameCount;
            if (!s_bodies.TryGetValue(lens.Key, out var plan))
            {
                if (s_bodies.Count >= MaxBodyPlans) EvictOldestPlan();
                s_bodies[lens.Key] = plan = new BodyPlan();
            }
            plan.UsedFrame = now;
            if (plan.IsFor(lens, c, ring.Generation)) return plan;
            long t0 = Stopwatch.GetTimestamp();
            var r = Planetarium.Rotation;
            var deRot = new DQuat(r.x, r.y, r.z, r.w).Conjugate;
            var lensOffset = deRot.Rotate(lens.Point - ToD(lens.Body.position));
            int lensBody = lens.Body.flightGlobalsIndex;
            plan.Begin(lens, c, ring.Generation, bodies.Count);
            plan.ReadTracks(ring, bodies);
            int share = s_reads.Offer(now);
            int left = share, carried = 0;
            int first = plan.NextFirst(bodies.Count);
            for (int n = 0; n < bodies.Count; n++)
            {
                int i = (first + n) % bodies.Count;
                var b = bodies[i];
                if (b == null || b.scaledBody == null) continue;
                int index = b.flightGlobalsIndex;
                var trace = plan.Trace(index);
                if (trace == null) continue;
                int budget = Math.Min(BodyTrace.DefaultBudget, Math.Max(MinBodyReads, left));
                PastSight sight;
                if (ring.BodiesOffKepler > 0)
                {
                    var tree = new RingBodies<KeplerBodyTree>(ring, new KeplerBodyTree(bodies, deRot));
                    sight = PastInstant.Body(ref tree, ring, index, lensBody, lensOffset, lens.Ut, c, double.NegativeInfinity, trace, budget);
                }
                else
                {
                    var tree = new KeplerBodyTree(bodies, deRot);
                    sight = PastInstant.Body(ref tree, ring, index, lensBody, lensOffset, lens.Ut, c, double.NegativeInfinity, trace, budget);
                }
                left -= trace.Reads;
                if (trace.Carried) carried++;
                int frame = sight.Frame;
                if (!sight.Found && frame >= 0 && frame != index) plan.Hide(index);
                /* The lens's own body and those above it are the frame's centre: placed right already, so only the spin goes back. */
                if (sight.Found && frame == index && b.rotates)
                    plan.Spin(index, PictureBoundary.SpinBackS((lens.Point - ToD(b.position)).Magnitude, b.Radius, c));
                if (!sight.Found || frame == index || frame < 0 || frame >= bodies.Count || bodies[frame] == null) continue;
                plan.Place(index, ToD(bodies[frame].position) + deRot.Conjugate.Rotate(sight.About), sight.Ut - lens.Ut);
            }
            s_reads.Spend(share - left);
            Cost.AddBodies(Stopwatch.GetTimestamp() - t0, plan.Moved, carried, plan.Hidden, share - left);
            return plan;
        }

        private static void EvictOldestPlan()
        {
            long oldest = 0;
            int oldestFrame = int.MaxValue;
            foreach (var kv in s_bodies)
                if (kv.Value.UsedFrame < oldestFrame)
                {
                    oldestFrame = kv.Value.UsedFrame;
                    oldest = kv.Key;
                }
            s_bodies.Remove(oldest);
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
         * Every body's drawn centre from one lens point in one frame, for the
         * bodies drawn at their past instants, and the bodies hidden while
         * their search carries on. It keeps each body's trace across frames,
         * started afresh when the lens's body, the light speed or the ring's
         * generation changes.
         */
        private sealed class BodyPlan
        {
            private DVec3[] _centre = new DVec3[0];
            private double[] _dt = new double[0];
            private bool[] _moves = new bool[0];
            private bool[] _hidden = new bool[0];
            private bool[] _spins = new bool[0];
            private double[] _spinDt = new double[0];
            private BodyTrace[] _traces = new BodyTrace[0];
            private double _t = double.NaN, _c;
            private DVec3 _point;
            private int _lensBody = -1;
            private long _generation;
            private int _first;
            /* Each body's track as the traces last read it: its first instant drawn from it and its last entry, NaN for none. */
            private double[] _trackStart = new double[0], _trackLast = new double[0];

            public int Moved { get; private set; }
            public int Hidden { get; private set; }
            public int Spun { get; private set; }
            /* The frame this plan was last asked for, so the longest unused is dropped first. */
            public int UsedFrame;

            public bool IsFor(PictureLens lens, double c, long generation) =>
                lens.Ut == _t && c == _c && generation == _generation && lens.Body.flightGlobalsIndex == _lensBody
                && lens.Point.X == _point.X && lens.Point.Y == _point.Y && lens.Point.Z == _point.Z;

            public void Begin(PictureLens lens, double c, long generation, int count)
            {
                int lensBody = lens.Body.flightGlobalsIndex;
                if (c != _c || generation != _generation || lensBody != _lensBody)
                    foreach (var trace in _traces) trace?.Reset();
                _t = lens.Ut;
                _c = c;
                _generation = generation;
                _lensBody = lensBody;
                _point = lens.Point;
                if (_moves.Length < count)
                {
                    _centre = new DVec3[count];
                    _dt = new double[count];
                    _moves = new bool[count];
                    _hidden = new bool[count];
                    _spins = new bool[count];
                    _spinDt = new double[count];
                    Array.Resize(ref _traces, count);
                }
                Array.Clear(_moves, 0, _moves.Length);
                Array.Clear(_hidden, 0, _hidden.Length);
                Array.Clear(_spins, 0, _spins.Length);
                Moved = Hidden = Spun = 0;
            }

            public BodyTrace Trace(int index)
            {
                if (index < 0 || index >= _traces.Length) return null;
                return _traces[index] ?? (_traces[index] = new BodyTrace());
            }

            /*
             * Where a body's track changed since the last frame: entries after
             * its last one replace the prediction past it, a ring that forgets
             * its oldest hands the instants before its new start back to the
             * Kepler orbit, and a track that appears or goes changes everything
             * after its start. The traces of that body and of every body below
             * it are told.
             */
            public void ReadTracks(LightHistory ring, List<CelestialBody> bodies)
            {
                int count = bodies.Count;
                if (_trackStart.Length < count)
                {
                    int old = _trackStart.Length;
                    Array.Resize(ref _trackStart, count);
                    Array.Resize(ref _trackLast, count);
                    for (int i = old; i < count; i++) _trackStart[i] = _trackLast[i] = double.NaN;
                }
                bool used = ring.BodiesOffKepler > 0;
                for (int i = 0; i < count; i++)
                {
                    var track = used ? ring.Track(LightHistory.BodyKey(i)) : null;
                    double start = double.NaN, last = double.NaN;
                    if (track != null && track.HasEntries)
                    {
                        start = Math.Max(track.StartUt, ring.FloorUt);
                        last = track.Last.Ut;
                    }
                    double wasStart = _trackStart[i], wasLast = _trackLast[i];
                    _trackStart[i] = start;
                    _trackLast[i] = last;
                    double above = double.PositiveInfinity, below = double.NegativeInfinity;
                    if (double.IsNaN(start) != double.IsNaN(wasStart)) above = double.IsNaN(start) ? wasStart : start;
                    else if (!double.IsNaN(start))
                    {
                        if (start > wasStart) below = start;
                        else if (start < wasStart) above = start;
                        if (last != wasLast) above = Math.Min(above, Math.Min(last, wasLast));
                    }
                    if (double.IsPositiveInfinity(above) && double.IsNegativeInfinity(below)) continue;
                    for (int b = 0; b < count && b < _traces.Length; b++)
                        if (_traces[b] != null && Below(bodies, b, i)) _traces[b].Revise(above, below);
                }
            }

            /* Whether body `b` is body `i` or orbits it, however far down. */
            private static bool Below(List<CelestialBody> bodies, int b, int i)
            {
                var body = bodies[b];
                var target = bodies[i];
                for (int guard = 0; guard < 16 && body != null; guard++)
                {
                    if (body == target) return true;
                    var up = body.referenceBody;
                    if (up == body) return false;
                    body = up;
                }
                return false;
            }

            /* The body to solve first this frame, one further on each frame, so a tight budget is shared out in turn. */
            public int NextFirst(int count)
            {
                if (count <= 0) return 0;
                _first = (_first + 1) % count;
                return _first;
            }

            public void Hide(int index)
            {
                if (index < 0 || index >= _hidden.Length || _hidden[index]) return;
                _hidden[index] = true;
                Hidden++;
            }

            public bool Hides(int index) => index >= 0 && index < _hidden.Length && _hidden[index];

            public void Place(int index, DVec3 centre, double dt)
            {
                if (index < 0 || index >= _moves.Length) return;
                _centre[index] = centre;
                _dt[index] = dt;
                if (!_moves[index]) Moved++;
                _moves[index] = true;
            }

            public void Spin(int index, double dt)
            {
                if (index < 0 || index >= _spins.Length) return;
                if (!_spins[index]) Spun++;
                _spins[index] = true;
                _spinDt[index] = dt;
            }

            /* A body left where it is, its spin alone drawn `dt` seconds back. */
            public bool TrySpin(int index, out double dt)
            {
                bool spins = index >= 0 && index < _spins.Length && _spins[index];
                dt = spins ? _spinDt[index] : 0;
                return spins;
            }

            public bool Moves(int index) => index >= 0 && index < _moves.Length && _moves[index];

            public bool TryDrawn(int index, out DVec3 centre, out double dt)
            {
                bool moves = Moves(index);
                centre = moves ? _centre[index] : default;
                dt = moves ? _dt[index] : 0;
                return moves;
            }
        }

        /*
         * The pass's cost over every camera, per rendered frame: time in the
         * sight reads, solves run and sights shared. Logged every
         * CostLogSeconds while a light speed is set, so a Deck run can read
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
            private static long s_frameBodyTicks;
            private static int s_framePlans, s_frameMoved, s_frameCarried, s_frameHidden, s_frameReads;
            private static double s_bodySumMs, s_bodyMaxMs;
            private static long s_plans, s_moved, s_carried, s_hidden, s_reads;
            private static float s_loggedAt;

            /*
             * One lens's bodies solved, `moved` of them drawn at their past
             * instants, `carried` searches carried into the next frame,
             * `hidden` bodies left out with no instant to draw them at, in
             * `reads` reads of a body.
             */
            public static void AddBodies(long ticks, int moved, int carried, int hidden, int reads)
            {
                Touch();
                s_frameBodyTicks += ticks;
                s_framePlans++;
                s_frameMoved += moved;
                s_frameCarried += carried;
                s_frameHidden += hidden;
                s_frameReads += reads;
            }

            public static void Add(long ticks, bool solved)
            {
                Touch();
                s_frameTicks += ticks;
                if (solved) s_frameSolves++;
                else s_frameShared++;
            }

            private static void Touch()
            {
                int frame = Time.frameCount;
                if (frame == s_frame) return;
                EndFrame();
                s_frame = frame;
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
                    double bodyMs = s_frameBodyTicks * 1000.0 / Stopwatch.Frequency;
                    if (KerbcastSettings.EnableTelemetry)
                    {
                        Telemetry.BodySolve.Record(bodyMs);
                        Telemetry.AddBodyCounts(s_frameCarried, s_frameHidden);
                    }
                    s_bodySumMs += bodyMs;
                    if (bodyMs > s_bodyMaxMs) s_bodyMaxMs = bodyMs;
                    s_plans += s_framePlans;
                    s_moved += s_frameMoved;
                    s_carried += s_frameCarried;
                    s_hidden += s_frameHidden;
                    s_reads += s_frameReads;
                }
                s_frameTicks = s_frameBodyTicks = 0;
                s_frameSolves = s_frameShared = s_framePlans = s_frameMoved = s_frameCarried = s_frameHidden = s_frameReads = 0;
                float now = Time.realtimeSinceStartup;
                if (now - s_loggedAt < CostLogSeconds || s_frames == 0) return;
                var inv = CultureInfo.InvariantCulture;
                Debug.Log($"{Tag} cost over {s_frames} frames: meanMs={(s_sumMs / s_frames).ToString("F4", inv)} "
                    + $"maxMs={s_maxMs.ToString("F4", inv)} solves/frame={((double)s_solves / s_frames).ToString("F1", inv)} "
                    + $"shared/frame={((double)s_shared / s_frames).ToString("F1", inv)} lenses={s_lenses.Count} "
                    + $"bodyMeanMs={(s_bodySumMs / s_frames).ToString("F4", inv)} bodyMaxMs={s_bodyMaxMs.ToString("F4", inv)} "
                    + $"bodyLenses/frame={((double)s_plans / s_frames).ToString("F1", inv)} "
                    + $"bodiesMoved/lens={(s_plans > 0 ? (double)s_moved / s_plans : 0).ToString("F1", inv)} "
                    + $"bodyReads/lens={(s_plans > 0 ? (double)s_reads / s_plans : 0).ToString("F1", inv)} "
                    + $"bodySearchesCarried={s_carried} bodiesHidden={s_hidden}");
                s_loggedAt = now;
                s_frames = 0;
                s_sumMs = s_maxMs = s_bodySumMs = s_bodyMaxMs = 0;
                s_solves = s_shared = s_plans = s_moved = s_carried = s_hidden = s_reads = 0;
            }
        }
    }
}
