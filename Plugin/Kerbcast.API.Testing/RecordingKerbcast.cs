using System;
using System.Collections.Generic;

namespace Kerbcast.Api.Testing
{
    /**
     * <summary>
     * A headless Kerbcast for consumers' tests: no Kerbcast.dll, no Unity, no KSP. <see cref="Install"/>
     * makes it <see cref="KerbcastApi.Instance"/> until disposed. It keeps the threading contract
     * (ICameras and ICommand throw off the thread that created it), records what callers do, and lets
     * the test arrange cameras, feeds, identity events, scene changes and which features are supported.
     * </summary>
     * <example>
     * <code>
     * using (var kc = RecordingKerbcast.Install())
     * {
     *     kc.Cameras.Add(new CameraView { FlightId = 7, Kind = CameraKind.Part, VesselPersistentId = 42 });
     *     RunCodeUnderTest();
     *     Assert.Equal("my-mod", kc.Command.LockOwner);
     * }
     * </code>
     * </example>
     */
    public sealed class RecordingKerbcast : IKerbcast, IDisposable
    {
        internal readonly object Gate = new object();
        internal readonly MainThread Main = new MainThread();
        readonly List<string> calls = new List<string>();
        Func<ApiFeature, bool> supports = _ => true;
        bool installed;

        public RecordingKerbcast()
        {
            Cameras = new RecordingCameras(this);
            Command = new RecordingCommand(this);
        }

        /** <summary>A new recorder registered as KerbcastApi.Instance, with every feature supported, until disposed.</summary> */
        public static RecordingKerbcast Install()
        {
            var kc = new RecordingKerbcast();
            kc.installed = true;
            KerbcastApi.Register(kc);
            KerbcastApi.OverrideFeatures(kc.Supports);
            return kc;
        }

        public RecordingCameras Cameras { get; }
        public RecordingCommand Command { get; }
        ICameras IKerbcast.Cameras => Cameras;
        ICommand IKerbcast.Command => Command;

        public bool SidecarAlive { get; set; } = true;
        public string BindAddress { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 8090;
        public bool BindIsLoopback { get; set; } = true;

        /** <summary>What KerbcastApi.Supports answers while installed; every feature by default (and with null). A member of an unsupported feature throws NotSupportedException.</summary> */
        public void SetSupported(Func<ApiFeature, bool> features)
        {
            supports = features ?? (_ => true);
        }

        public bool Supports(ApiFeature feature) => supports(feature);

        /** <summary>Every call made through the API, oldest first, as "Member arg arg".</summary> */
        public IReadOnlyList<string> Calls
        {
            get { lock (Gate) return calls.ToArray(); }
        }

        public void ClearCalls()
        {
            lock (Gate) calls.Clear();
        }

        public void Dispose()
        {
            if (!installed) return;
            installed = false;
            KerbcastApi.Unregister(this);
            KerbcastApi.OverrideFeatures(null);
        }

        internal void Record(string call)
        {
            lock (Gate) calls.Add(call);
        }

        internal void Enter(string member, ApiFeature feature)
        {
            Main.Check(member);
            if (!supports(feature)) throw MainThread.NotYet(member);
        }
    }

    /** <summary>The recorder's cameras, craft map, identity events and throttle.</summary> */
    public sealed class RecordingCameras : ICameras
    {
        readonly RecordingKerbcast owner;
        readonly List<CameraView> views = new List<CameraView>();
        readonly Dictionary<uint, Guid> vessels = new Dictionary<uint, Guid>();
        readonly Dictionary<Guid, uint> persistentIds = new Dictionary<Guid, uint>();
        readonly Dictionary<uint, Vec3d> aimedAt = new Dictionary<uint, Vec3d>();
        readonly MainThread main;

        internal RecordingCameras(RecordingKerbcast owner)
        {
            this.owner = owner;
            main = owner.Main;
            Throttle = new RecordingThrottle(owner);
        }

        public RecordingThrottle Throttle { get; }
        ThrottleSettings ICameras.Throttle
        {
            get
            {
                owner.Enter("Throttle", ApiFeature.Throttle);
                return Throttle;
            }
        }

        /** <summary>Adds or replaces a camera by FlightId; its craft ids join TryVessel's map.</summary> */
        public void Add(CameraView view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            Remove(view.FlightId);
            views.Add(view.Clone());
            if (view.VesselPersistentId != 0 && view.VesselId != Guid.Empty) Learn(view.VesselPersistentId, view.VesselId);
        }

        public bool Remove(uint flightId) => views.RemoveAll(v => v.FlightId == flightId) > 0;

        /** <summary>Adds a craft to TryVessel's and TryPersistentId's maps; a later call for the same Vessel.id is a renumber.</summary> */
        public void Learn(uint persistentId, Guid vesselId)
        {
            vessels[persistentId] = vesselId;
            persistentIds[vesselId] = persistentId;
        }

        /** <summary>Raises KerbcastApi.Identity as Kerbcast would. A subscriber's exception propagates to the test.</summary> */
        public void Raise(IdentityEvent identityEvent)
        {
            main.Check("Raise");
            KerbcastApi.RaiseIdentity(identityEvent, null);
        }

        /** <summary>The camera as it stands now, with every control applied; null when unknown.</summary> */
        public CameraView Peek(uint flightId) => Find(flightId)?.Clone();

        /** <summary>The last AimAt target for a camera.</summary> */
        public bool TryAimedAt(uint flightId, out Vec3d target) => aimedAt.TryGetValue(flightId, out target);

        public IReadOnlyList<CameraView> CamerasFor(uint vesselPersistentId)
        {
            owner.Enter("CamerasFor", ApiFeature.CameraControl);
            owner.Record($"CamerasFor {vesselPersistentId}");
            return Select(v => v.VesselPersistentId == vesselPersistentId);
        }

        public IReadOnlyList<CameraView> CamerasFor(Guid vesselId)
        {
            owner.Enter("CamerasFor", ApiFeature.CraftIdentity);
            owner.Record($"CamerasFor {vesselId}");
            return Select(v => v.VesselId == vesselId);
        }

        public CameraView View(uint flightId)
        {
            owner.Enter("View", ApiFeature.CameraControl);
            return Find(flightId)?.Clone();
        }

        public bool SetFov(uint flightId, double fov)
        {
            owner.Enter("SetFov", ApiFeature.CameraControl);
            owner.Record($"SetFov {flightId} {fov}");
            var v = PartCamera(flightId);
            if (v == null || !v.SupportsZoom || !Vec3d.Finite(fov)) return false;
            v.Fov = Clamp(fov, v.FovMin, v.FovMax);
            return true;
        }

        public bool SetPan(uint flightId, double yaw, double pitch)
        {
            owner.Enter("SetPan", ApiFeature.CameraControl);
            owner.Record($"SetPan {flightId} {yaw} {pitch}");
            var v = PartCamera(flightId);
            if (v == null || !v.SupportsPan || !Vec3d.Finite(yaw) || !Vec3d.Finite(pitch)) return false;
            v.PanYaw = Clamp(yaw, v.PanYawMin, v.PanYawMax);
            v.PanPitch = Clamp(pitch, v.PanPitchMin, v.PanPitchMax);
            return true;
        }

        public bool AimAt(uint flightId, Vec3d target)
        {
            owner.Enter("AimAt", ApiFeature.CameraControl);
            owner.Record($"AimAt {flightId} {target}");
            if (PartCamera(flightId) == null || !target.IsFinite) return false;
            aimedAt[flightId] = target;
            return true;
        }

        public bool SetTrackMode(uint flightId, TrackMode mode)
        {
            owner.Enter("SetTrackMode", ApiFeature.CameraControl);
            owner.Record($"SetTrackMode {flightId} {mode}");
            var v = PartCamera(flightId);
            if (v == null || !(v.SupportsPan && v.SupportsZoom)) return false;
            v.TrackMode = mode;
            return true;
        }

        public TrackMode GetTrackMode(uint flightId)
        {
            owner.Enter("GetTrackMode", ApiFeature.CameraControl);
            return Find(flightId)?.TrackMode ?? TrackMode.None;
        }

        public bool TryVessel(uint persistentId, out Guid vesselId)
        {
            owner.Enter("TryVessel", ApiFeature.CraftIdentity);
            return vessels.TryGetValue(persistentId, out vesselId);
        }

        public bool TryPersistentId(Guid vesselId, out uint persistentId)
        {
            owner.Enter("TryPersistentId", ApiFeature.CraftIdentity);
            return persistentIds.TryGetValue(vesselId, out persistentId);
        }

        CameraView Find(uint flightId)
        {
            for (int i = 0; i < views.Count; i++)
                if (views[i].FlightId == flightId) return views[i];
            return null;
        }

        /* The controls leave command feeds to ICommand, as Kerbcast does. */
        CameraView PartCamera(uint flightId)
        {
            var v = Find(flightId);
            return v != null && v.Kind == CameraKind.Part ? v : null;
        }

        IReadOnlyList<CameraView> Select(Func<CameraView, bool> match)
        {
            var result = new List<CameraView>();
            foreach (var v in views)
                if (v.Kind == CameraKind.Part && match(v)) result.Add(v.Clone());
            return result;
        }

        static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    }

    /** <summary>The recorder's throttle, backed by <see cref="Value"/>.</summary> */
    public sealed class RecordingThrottle : ThrottleSettings
    {
        readonly RecordingKerbcast owner;

        internal RecordingThrottle(RecordingKerbcast owner)
        {
            this.owner = owner;
        }

        /** <summary>The save's value; null means no save is loaded.</summary> */
        public bool? Value { get; set; } = false;

        public override bool? MainScreen
        {
            get
            {
                owner.Main.Check("Throttle.MainScreen");
                return Value;
            }
        }

        public override bool SetMainScreen(bool on)
        {
            owner.Main.Check("Throttle.SetMainScreen");
            owner.Record($"Throttle.SetMainScreen {on}");
            if (Value == null) return false;
            Value = on;
            return true;
        }
    }
}
