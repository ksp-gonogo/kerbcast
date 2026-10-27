using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Kerbcast
{
    /* Points a KerbcastCamera's layer stack from a fixed station at its
       target (the active craft in flight, a chosen craft at the Space Center;
       see CommandCameraTarget). Each rendered tick it recomputes the station's world
       position from body + lat/lon/alt (so floating-origin shifts, Krakensbane
       and body rotation are absorbed), aims at the craft's CoM with the horizon
       level, zooms so the craft spans a set fraction of the frame, and replans
       the local layers' clip planes for the range (see CommandCameraMath.PlanClips).

       Every layer, clip, occlusion and frame transition is logged once with UT
       and range under [Kerbcast][CMDCAM], so a pop or blank frame in a
       capture can be pinned to its cause. */
    internal sealed class CommandCameraRig : ICameraRig, IAnchorRig
    {
        /* The subject's anchor id; its owner is AnchorRegistry.KerbcastOwner. */
        public const string SubjectAnchorId = "subject";
        /* Where the aim point lands in the rendered frame: the centre, except while the view slews onto it. */
        public const string AimAnchorId = "aim";

        private const string Tag = "[Kerbcast][CMDCAM]";
        private const double FovTau = 0.4;
        private const double FovSnapAfter = 1.0;
        private const float StatusLogSeconds = 1.0f;
        private const float SkyProbeSeconds = 10.0f;
        private const double ModelProxyDistance = 50000.0;
        private const int TerrainSamples = 48;
        /* Below this many pixels across, the model is drawn by the supersampled
           sub-pixel camera, because a direct render this small aliases and
           flickers; below the floor its light is too faint to register. */
        private const double SubpixelBelowPx = 6.0;
        private const double SubpixelFloorPx = 0.02;
        private const int MaxOriginShiftLogsPerSecond = 4;
        /* How far out a Direction aim's point sits when there is no subject range to use. */
        private const double DirectionDefaultRangeM = 10000.0;

        private const float PollStaleSeconds = 0.1f;
        private const int EventCapacity = 32;
        /* Forecast reach: this much real time at the current warp rate, capped
           in UT, sampled evenly and each edge bisected to ForecastToleranceUt
           or ForecastBisectSteps halvings, whichever comes first. */
        private const double ForecastRealSeconds = 4.0;
        private const double ForecastMaxUt = 3600.0;
        private const int ForecastSamples = 32;
        private const int ForecastBisectSteps = 10;
        private const double ForecastToleranceUt = 0.001;

        private readonly KerbcastCamera _cam;
        private readonly CommandCameraMount _mount;
        private readonly SubjectSelection _selection;
        private readonly IFixSource _known;
        private readonly DrawnSubject _drawn;
        private readonly CommandAuthority _authority;
        private readonly CommandTrackEventRing _events = new CommandTrackEventRing(EventCapacity);
        private readonly CommandCameraOcclusion _occlusion;
        private readonly CommandCameraForecast _forecast;
        /* Per-frame occlusion test cost, smoothed over about ten solves. */
        private double _occlusionMs;
        private const double OcclusionMsAlpha = 0.1;
        private string _occluderSource;
        private readonly List<CommandTrackEvent> _trackEvents = new List<CommandTrackEvent>();
        private readonly List<KeyValuePair<double, CommandTrackEvent>> _edges = new List<KeyValuePair<double, CommandTrackEvent>>();
        private double _forecastMs;
        private int _forecastCount;
        private bool _forecastFailLogged;
        private readonly uint _flightId;
        private readonly double _frameFraction;
        private readonly Func<double> _fovMinSource;
        private double _fovMin;
        private float _fovMinCheckedAt;
        private readonly double _fovMax;

        private readonly float _nearNativeNear;
        private readonly float _nearNativeFar;
        private readonly bool _hasFar;
        private readonly float _farNativeNear;
        private readonly float _farNativeFar;
        private Camera _midCam;
        private CommandCameraSubpixel _subpixel;
        private bool _subpixelActive;
        private bool _subpixelLogged;
        private bool _subpixelWas;
        private float _subpixelNear, _subpixelFar;

        private CommandClipPlan _plan;
        private double _fov;
        private float _lastPrepare = -1f;

        private bool _first = true;
        private bool _wasSplit;
        private bool _wasOccluded;
        private bool _wasFarExtended;
        private bool _wasFovFloor;
        private bool _wasFovCeiling;
        private bool _wasContinuousOrigin;
        private bool _wasKrakensbane;
        private int _pqsState = -1;
        private string _occluderName;
        private uint _targetPid;
        private bool _targetLogged;
        private bool _stockCheckLogged;
        private string _targetBody;
        private float _solvedAt = float.MinValue;
        private double _lastDistance;
        private float _statusAt = float.MinValue;
        private float _skyProbeAt = float.MinValue;
        private bool _stationMeshStock;
        private bool _modelModeLogged;
        private bool _modelProxied;
        private bool _stationMeshLogged;
        private int _originShifts;
        private double _scaledSkewMaxPx;
        private string _vantageSource;
        private double _stationFromOrigin;
        private int _scaledSkewFrames;
        private int _originShiftLogsThisSecond;
        private float _originShiftWindowAt;

        /* Latest geometry for the status file, refreshed on every render and
           by Poll when nothing is rendering. */
        public bool HasFix { get; private set; }
        public bool Occluded { get; private set; }
        public string Blocker { get; private set; } = "";
        public double RangeM { get; private set; }
        public double ElevationDeg { get; private set; }
        public uint SubjectPersistentId => _known.SubjectPersistentId;
        public string SubjectName => _known.SubjectName;
        /* Nothing between the target and the Sun but empty space. */
        public bool Sunlit { get; private set; }
        private bool _sunlitLogged;

        /* The readings above are knowledge, taken at the aimed point. The
           picture draws the craft where it truly is, so how it looks from the
           lens is kept apart for the render. */
        private bool _drawnOk;
        private DVec3 _drawnPos;
        private CelestialBody _drawnBody;
        private double _drawnDistance;
        private bool _drawnOccluded;
        private bool _drawnSunlit;

        /* The lens pose of the frame last rendered, for the anchor projector. */
        private bool _poseValid;
        private DVec3 _poseEye, _poseForward, _poseUp;
        /* From the lens to the aim point the pose turns toward. */
        private DVec3 _poseAimToward;
        private readonly AnchorSpec _subjectAnchor = new AnchorSpec { Id = SubjectAnchorId, Kind = AnchorKind.Vessel, Occlusion = true };
        private readonly AnchorSpec _aimAnchor = new AnchorSpec { Id = AimAnchorId, Kind = AnchorKind.Direction, HasVector = true, NeverObserved = true };

        /* The bracket contract (see AppendCommandTrack in KerbcastCore). Kept
           for clients older than 1.12; newer ones draw the subject's anchor. */
        public double TrackUt { get; private set; }
        public double BracketHalfPx { get; private set; }
        public int FrameHeightPx => FrameRows();
        /* Angle between the frame's axis (the aim, once any slew has settled)
           and the true craft as seen from the station, in capture pixels; null
           without a true fix. */
        public double? TrueOffsetPx { get; private set; }
        /* The true craft is in frame and not occluded: the picture shows it. */
        public bool TrueVisible { get; private set; }
        private DVec3 _solveEye, _solveForward, _solveUp;

        /* TrueOffsetPx as the status file sends it. Under a held lock it is the
           true craft's position, so it goes out only while the picture shows
           the craft, like the subject anchor. */
        public double? ReportedTrueOffsetPx =>
            _authority != null && _authority.Registered && !TrueVisible ? null : TrueOffsetPx;
        public bool FramingFits { get; private set; } = true;
        public string Blank { get; private set; }
        public CommandTrackEventRing Events => _events;
        public string AuthorityOwner => _authority != null ? _authority.Owner : null;
        internal CommandFeedOrders CurrentOrders => _authority != null && _authority.Registered ? _authority.Orders(_flightId) : null;
        public string StationNodeName => _mount.Site.NodeName;
        internal CommandCameraSite Site => _mount.Site;
        /* The widest field the lens opens to, in degrees. */
        internal double FovMax => _fovMax;
        public string StationDisplayName =>
            string.IsNullOrEmpty(_mount.Site.DisplayName) ? _mount.Site.NodeName : _mount.Site.DisplayName;

        /* Where the camera points; null for its default craft. */
        public CommandAim Aim => _aim;
        internal Orbit AimOrbit => _aimOrbit;

        /* The craft this lens rides no longer exists (crashed, recovered, or
           gone some way other than a merge, which the lens follows). */
        public bool VantageLost => _mount.Site.VantageGone;

        /* The lens rides its vantage craft; a ground site rides none. */
        public Vessel LensCraft => _mount.Site.HasVantage ? _mount.Site.Vantage : null;

        /* What the frame last prepared shows, taken once its pose, field and
           bracket are settled, so the frame's metadata carries the state of
           its own instant rather than whatever a later poll found. */
        public CommandFrameState FrameState { get; private set; } = CommandFrameState.Empty;

        /* The known subject as the last solve read it; unread when the solve stopped before reaching it. */
        private bool _subjectRead;
        private CraftFixState _subjectState;
        private string _subjectRefusalCode;
        private string _subjectRefusalText;
        private double _subjectUt = double.NaN;

        private void TakeFrameState() => FrameState = StateNow();

        /* The feed's state as the last solve left it. The world model is read at the current UT, never an older solve's, which would read as time running back. */
        public CommandFrameState StateNow()
        {
            double ut = Planetarium.GetUniversalTime();
            var s = CommandFrameState.Empty;
            s.HasFix = HasFix;
            if (HasFix)
            {
                s.RangeM = RangeM;
                s.BracketHalfPx = BracketHalfPx;
            }
            s.FrameHeightPx = FrameHeightPx;
            s.FramingFits = FramingFits;
            var offset = ReportedTrueOffsetPx;
            if (offset.HasValue) s.TrueOffsetPx = offset.Value;
            s.Occluded = Occluded;
            s.Occluder = Blocker ?? "";
            s.Sunlit = Sunlit;
            s.Blank = Blank;
            s.AuthorityOwner = AuthorityOwner;
            s.SubjectPersistentId = SubjectPersistentId;
            if (_aim != null) s.AimKind = KerbcastCore.AimKindTag(_aim.Kind);
            if (_subjectRead)
            {
                s.Refusal = CommandFrameState.RefusalFor(SubjectPersistentId, _subjectState, _subjectRefusalCode);
                if (s.Refusal == CommandCameraMath.EligibleRefused)
                {
                    s.RefusalCode = _subjectRefusalCode;
                    s.RefusalText = _subjectRefusalText;
                }
                if (_subjectState == CraftFixState.Positioned) s.SubjectUt = _subjectUt;
            }
            var store = _authority != null ? _authority.WorldModel : null;
            var view = store != null
                ? store.Capture(Time.frameCount, ut)
                : new WorldModelView { Mode = WorldModelMode.Truth };
            s.WorldModel = WorldModelTag(view.Mode);
            if (view.Snapshot != null)
            {
                s.WorldModelSeq = view.Snapshot.Sequence;
                s.KnowledgeUt = view.Snapshot.KnowledgeUt;
                s.Scope = view.Snapshot.ScopeForSite(_mount.Site.NodeName);
            }
            return s;
        }

        private static string WorldModelTag(WorldModelMode mode)
        {
            switch (mode)
            {
                case WorldModelMode.Waiting: return "waiting";
                case WorldModelMode.Model: return "model";
                case WorldModelMode.Stale: return "stale";
                default: return "truth";
            }
        }

        /* A merge (dock or claw grab) moves both ends: the watched craft and
           the craft the lens rides each follow into the survivor. Under a
           world model the watched craft follows the model's aliases instead,
           so only the lens moves here. */
        private void OnMerged(uint oldPid, uint newPid)
        {
            if (_known.IdentityIsKsp) _selection.Merged(oldPid, newPid);
            if (_mount.Site.FollowMerge(oldPid, newPid))
                Debug.Log($"{Tag} cam={_flightId} vantage merged: persistentId={oldPid} -> {newPid} ('{_mount.Site.VantageName}')");
        }

        /* Points the camera by `aim` (null for the default craft) from the
           next solve, with `orbit` built for an Orbit aim. The aim's subject,
           or the default craft without one, becomes the true craft drawn and
           bracketed wherever it really is. */
        public void SetAim(CommandAim aim, Orbit orbit)
        {
            _aim = aim;
            _aimOrbit = orbit;
            uint subject = aim?.SubjectPersistentId ?? 0;
            if (subject != _selection.ChosenPersistentId) _selection.Choose(subject);
            Debug.Log($"{Tag} cam={_flightId} aim {(aim == null ? "default (the default craft)" : aim.ToString())}");
        }

        private CommandAim _aim;
        private Orbit _aimOrbit;
        private DVec3 _lastAimPoint;

        /* The lens limit can change mid-scene (a Tracking Station upgrade), so
           it is re-read once a second. */
        private double FovMinNow()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _fovMinCheckedAt < 1f) return _fovMin;
            _fovMinCheckedAt = now;
            double next = _fovMinSource();
            if (next != _fovMin)
            {
                Debug.Log($"{Tag} cam={_flightId} lens limit {_fovMin:F3} -> {next:F3} deg");
                _fovMin = next;
            }
            return _fovMin;
        }
        private double FramingMargin => _aim?.FramingMarginM ?? 0.0;

        /* The subject's radius for framing, bracket and the occlusion test at a craft aim. */
        private double KnownRadius
        {
            get
            {
                double r = _known.SubjectRadiusM;
                return double.IsNaN(r) ? CommandCameraMath.NominalCraftRadiusM : r;
            }
        }
        /* What the view frames: the subject's radius, how far from the aim a
           craft aim's subject may be (the source's uncertainty, centred on
           the aim), and the operator's margin. */
        private double FramingRadius => KnownRadius + (AimIsCraft ? _knownUncertainty : 0.0) + FramingMargin;
        private double _knownUncertainty;

        private bool _blankLogged;
        private string _loggedBlank;
        private string _loggedAim;

        private CommandCameraMarker _marker;
        private CommandCameraUtStripe _utStripe;
        private double _frameUt;
        /* The model wins over the marker when both are set. */
        private readonly bool _wantModel;
        private CommandCameraTargetModel _standIn;
        private uint _modelFailedPid;
        private Composition _modelFailedParts;
        /* The drawn craft's body, or the aim's without one, for the scaled skew log. */
        private CelestialBody _frameBody;

        public CommandCameraRig(KerbcastCamera cam, CommandCameraMount mount, SubjectSelection selection, IFixSource known,
            DrawnSubject drawn,
            CommandAuthority authority, double frameFraction, Func<double> fovMin, double fovMax, bool targetMarker, bool targetModel,
            bool utStripe)
        {
            _cam = cam;
            _mount = mount;
            _selection = selection;
            _known = known;
            _drawn = drawn;
            _authority = authority;
            _wantModel = targetModel;
            _flightId = cam.FlightId;
            _frameFraction = frameFraction;
            _fovMinSource = fovMin;
            _fovMin = fovMin();
            _fovMinCheckedAt = Time.realtimeSinceStartup;
            _fovMax = fovMax;
            _fov = fovMax;
            _occlusion = new CommandCameraOcclusion(TerrainSamples, _flightId);
            _forecast = new CommandCameraForecast(_occlusion);

            var near = cam.NearLayer;
            _nearNativeNear = near != null ? near.nearClipPlane : 1f;
            _nearNativeFar = near != null ? near.farClipPlane : 750000f;
            var far = cam.FarLayer;
            _hasFar = far != null;
            if (_hasFar)
            {
                _farNativeNear = far.nearClipPlane;
                _farNativeFar = far.farClipPlane;
                _midCam = BuildMidLayer(far);
            }
            if (targetModel)
                _subpixel = CommandCameraSubpixel.Create(far != null ? far : near, cam.FlightId);
            if (targetMarker && !targetModel)
                _marker = CommandCameraMarker.Create(new[] { cam.NearLayer, cam.FarLayer, _midCam }, cam.FlightId);
            if (utStripe)
                _utStripe = CommandCameraUtStripe.Create(cam.FlightId);

            var scaled = cam.ScaledLayer;
            if (scaled != null)
            {
                /* The galaxy cube's faces sit a few scaled units from the lens.
                   In the scaled pass they draw opaque and write depth before the
                   atmosphere shell, hiding it wherever the shell lies farther
                   than the face: every low view. The galaxy layer draws them
                   behind everything already. */
                _scaledMaskSource = scaled.cullingMask;
                scaled.cullingMask &= ~(1 << SkyboxLayer);
                Debug.Log($"{Tag} cam={_flightId} scaled layer: hdr={scaled.allowHDR} clear={scaled.clearFlags} "
                    + $"clip=[{scaled.nearClipPlane:G4},{scaled.farClipPlane:G4}] mask=0x{scaled.cullingMask:X8} (source 0x{_scaledMaskSource:X8}, skybox layer {SkyboxLayer} removed) "
                    + $"renders whatever the split (far skipped only while occluded)");
            }

            GameEvents.onFloatingOriginShift.Add(OnOriginShift);
            GameEvents.onVesselPersistentIdChanged.Add(OnMerged);
            Debug.Log($"{Tag} cam={_flightId} rig up: station {mount.Site} "
                + $"near=[{_nearNativeNear:F2},{_nearNativeFar:F0}] "
                + (_hasFar ? $"far=[{_farNativeNear:F0},{_farNativeFar:F0}] mid layer ready" : "no far layer (single local camera)")
                + $" nearMask=0x{(near != null ? near.cullingMask : 0):X8} farMask=0x{(far != null ? far.cullingMask : 0):X8}"
                + $" frameFraction={_frameFraction:F2} fov=[{_fovMin:F3},{_fovMax:F1}]");
        }

        /* A clone of the far layer covering native-near..just-short-of-the-craft
           once the split is on. Rendered by hand between far and near, so its
           depth value is irrelevant; it inherits clear-depth from Camera 01. */
        private Camera BuildMidLayer(Camera far)
        {
            var go = new GameObject($"Kerbcast_{_flightId}_Mid");
            var mid = go.AddComponent<Camera>();
            mid.CopyFrom(far);
            mid.name = $"Kerbcast_{_flightId}_Mid";
            go.transform.parent = far.transform.parent;
            go.transform.localPosition = far.transform.localPosition;
            go.transform.localRotation = far.transform.localRotation;
            mid.enabled = false;
            go.AddComponent<CanvasHack>();
            return mid;
        }

        /*
         * One evaluation of the feed: geometry, then the authority's verdict,
         * then the discrete state recorded into the event ring at this UT.
         * Runs on every rendered frame and at least every PollStaleSeconds
         * otherwise, so the recorded edges land within a frame or two of the
         * real transition.
         */
        private bool Solve(double ut, float now, out DVec3 station, out DVec3 aim, out DVec3 localUp,
            out DVec3 north, out string occluder)
        {
            TrackUt = ut;
            _solvedAt = now;
            _subjectRead = false;
            bool governed = _authority != null && _authority.Registered;
            bool timedOut = governed && _authority.TimedOut(Time.realtimeSinceStartup);
            var orders = governed ? _authority.Orders(_flightId) : null;

            bool ok = SolveGeometry(ut, out station, out aim, out localUp, out north, out occluder);
            if (!ok)
            {
                /* Without a fix the readings at the aim describe nothing; kept, they would carry the last solve, perhaps truth from before a lock, into the reports. */
                HasFix = false;
                Occluded = false;
                Blocker = "";
                Sunlit = false;
            }
            else
            {
                /* The pose turns onto a moved aim rather than snapping; everything
                   projected through the frame (TrueInFrame, anchors) uses it. */
                var toward = aim - station;
                double range = toward.Magnitude;
                double clock = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                bool jumped = AimJumped(ut);
                var view = range > 0 ? _slew.Step(toward * (1.0 / range), double.IsNaN(_slewAt) ? double.MaxValue : clock - _slewAt, jumped, _fov) : toward;
                _slewAt = clock;
                NoteSlew(ut);
                CommandCameraMath.AimBasis(station, station + view * range, localUp, north, out _solveForward, out _solveUp);
                _solveEye = station;
            }
            FramingFits = !ok || CommandCameraMath.FramingFits(FramingRadius, RangeM, _fovMax);
            Blank = CommandCameraMath.BlankReason(governed, timedOut, orders?.Blank, VantageLost, _authority?.SilentText);
            UpdateBracket(now);
            NoteGovernance(ut, governed);
            _events.Record(new CommandTrackEvent(ut, Occluded, Blocker, Sunlit, Blank));
            return ok;
        }

        /* Station, aim point and drawn craft positions, and the camera-axis
           readings at the aim point: the known craft for the default or a
           Vessel aim, otherwise the point the aim resolves to. */
        private bool SolveGeometry(double ut,
            out DVec3 station, out DVec3 aim, out DVec3 localUp, out DVec3 north, out string occluder)
        {
            station = aim = localUp = north = default;
            occluder = null;
            ForgetDrawn();
            var site = _mount.Site;
            Vector3d vantage = default;
            string vantageSource = null;
            if (site.HasVantage && !site.TryVantagePosition(ut, out vantage, out vantageSource))
                return false;
            var body = site.Body;
            if (body == null) return false;

            var frame = body.BodyFrame;
            var bodyPos = ToD(body.position);
            station = vantageSource != null
                ? ToD(vantage)
                : CommandCameraMath.SurfacePosition(
                    bodyPos, ToD(frame.X), ToD(frame.Y), ToD(frame.Z),
                    body.Radius, site.Latitude, site.Longitude, site.Altitude);
            _vantageSource = vantageSource;
            _stationFromOrigin = station.Magnitude;
            localUp = (station - bodyPos).Normalized;
            var z = ToD(frame.Z);
            north = new DVec3(z.X, z.Z, z.Y);

            var subject = KnownSubject(ut, station, body);
            _knownUncertainty = subject.Positioned && subject.UncertaintyM > 0 ? subject.UncertaintyM : 0.0;
            _subjectRead = true;
            _subjectState = subject.State;
            _subjectRefusalCode = subject.RefusalCode;
            _subjectRefusalText = subject.RefusalText;
            LocateDrawn(ut, station, body);
            CelestialBody aimBody;
            bool aimed = !AimIsCraft;
            if (!aimed)
            {
                if (!subject.Positioned)
                {
                    ForgetDrawn();
                    return false;
                }
                aim = subject.World;
                aimBody = subject.Body;
            }
            else if (_aim.Kind == CommandAimKind.Direction)
            {
                aim = DirectionPoint(body, station, localUp, subject);
                aimBody = body;
            }
            else if (!ResolveAim(_aim, _aimOrbit, ut, out aim, out aimBody))
            {
                ForgetDrawn();
                return false;
            }
            _lastAimPoint = aim;

            long occT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _occlusion.UseSite(site);
            occluder = _occlusion.FindOccluder(station, aim, out string occluderSource, aimed ? 0 : KnownRadius);
            double distance = (aim - station).Magnitude;
            _lastDistance = distance;
            HasFix = true;
            RangeM = distance;
            ElevationDeg = CommandCameraMath.ElevationDeg(aim - station, localUp);
            Occluded = occluder != null;
            Blocker = occluder ?? "";
            UpdateSunlit(ut, distance, aim);

            SolveDrawn(station, aim, distance, !aimed && _known.KnowsWhatIsDrawn, aimBody);
            double occMs = (System.Diagnostics.Stopwatch.GetTimestamp() - occT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _occlusionMs = _occlusionMs > 0 ? _occlusionMs + OcclusionMsAlpha * (occMs - _occlusionMs) : occMs;

            NoteTargetAndOcclusion(ut, distance, aimBody, DrawnOrAim(aim), occluder, occluderSource);
            return true;
        }

        private readonly AimSlew _slew = new AimSlew();
        /* Real seconds, double, of the last slew step; NaN before the first. */
        private double _slewAt = double.NaN;
        private bool _slewLogged;
        private CommandAim _jumpAim;
        private uint _jumpSubject, _jumpVantage;
        private long _jumpGeneration;

        /*
         * Whether the aim point jumped since the last solve, as its causes say
         * rather than as its motion suggests: a new aim, a new subject, a new
         * vantage, or a new world-model publish that may correct where the
         * subject is. Between these the view follows the aim exactly.
         */
        private bool AimJumped(double ut)
        {
            uint subject = _known.SubjectPersistentId;
            uint vantage = _mount.Site.VantagePersistentId;
            var store = _authority != null ? _authority.WorldModel : null;
            var snap = store != null ? store.Capture(Time.frameCount, ut).Snapshot : null;
            long generation = snap != null ? snap.Generation : 0;
            bool jumped = !CommandAim.SameAs(_aim, _jumpAim) || subject != _jumpSubject || vantage != _jumpVantage
                || generation != _jumpGeneration;
            _jumpAim = _aim;
            _jumpSubject = subject;
            _jumpVantage = vantage;
            _jumpGeneration = generation;
            return jumped;
        }

        private void NoteSlew(double ut)
        {
            bool slewing = _slew.Slewing;
            if (slewing == _slewLogged) return;
            _slewLogged = slewing;
            Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} event={(slewing ? "slew-start" : "slew-end")}"
                + (slewing ? $" offsetDeg={_slew.OffsetRad * 180.0 / Math.PI:F3}" : ""));
        }

        /* Clears the drawn craft, so a failed solve leaves no stale picture readings. */
        private void ForgetDrawn() => _drawnOk = false;

        /* The lens is placed for the camera's picture pass too, so other craft
           and anchors are drawn from the same lens. It keeps its solves to
           itself: the known subject is solved from the station with no spread,
           and the drawn one must be solved the same way to agree with it. */
        private void LocateDrawn(double ut, DVec3 station, CelestialBody lensBody)
        {
            var lens = new PictureLens
            {
                Key = PictureLens.OwnKey(_flightId),
                Ut = ut,
                Point = station,
                Body = lensBody,
            };
            _cam.Picture.Place(lens);
            _drawnOk = _drawn.TryLocate(lens, out _drawnPos, out _drawnBody);
            if (!LightTime.SignalSpeed.HasValue)
            {
                _drawnLight = -1;
                return;
            }
            int light = !_drawnOk ? 0 : _drawn.ShownPast ? 2 : 1;
            float now = Time.unscaledTime;
            if (light == _drawnLight || now - _lightLoggedAt < LightLogSeconds) return;
            _drawnLight = light;
            _lightLoggedAt = now;
            Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} event=drawn-light state={(light == 2 ? "past" : light == 1 ? "live" : "none")} "
                + $"lightTimeS={(light == 2 ? (ut - _drawn.ShownUt).ToString("F3", CultureInfo.InvariantCulture) : "0")}");
        }

        /* Whether the drawn craft was last none, live or at its past instant (0, 1, 2), for the log. */
        private int _drawnLight = -1;
        private float _lightLoggedAt = float.MinValue;
        private const float LightLogSeconds = 5f;

        /*
         * The subject as the source knows it at its own estimate of the past
         * instant: the latest root of the light-time equation on the source's
         * positions, never the picture's, in the frame of the common ancestor
         * of the lens's body and the subject's, as the picture solves it. Render
         * UT with no signal speed. The instant never steps back while the
         * subject stays the same. A source that cannot be solved back to that
         * instant has no fix.
         */
        private CraftFix KnownSubject(double ut, DVec3 station, CelestialBody lensBody)
        {
            _subjectUt = ut;
            double? c = LightTime.SignalSpeed;
            if (!c.HasValue || lensBody == null)
            {
                _knownHold.Reset();
                return _known.Subject(ut);
            }
            var now = _known.Subject(ut);
            if (!now.Positioned || now.Body == null) return now;
            if (now.PersistentId != _knownHoldPid)
            {
                _knownHold.Reset();
                _knownHoldPid = now.PersistentId;
            }
            var tree = KeplerBodyTree.World;
            int lensIndex = lensBody.flightGlobalsIndex;
            bool moves = _known.SubjectMotion(ut, out var motion, out _) && motion.Body != null;
            /* The frame from the body the subject is at by now, then again from
               its body at the solved instant, as the picture chooses it. */
            int frame = PastInstant.CommonAncestor(ref tree, lensIndex, now.Body.flightGlobalsIndex);
            double firstTr = double.NaN;
            int firstFrame = -1;
            for (int pass = 0; pass < 2; pass++)
            {
                var frameBody = tree.Body(frame);
                if (frameBody == null) break;
                var path = new KnownPath { Source = _known, Tree = tree, Frame = frame };
                double bound = moves ? CommandCameraForecast.SpeedBound(motion, frameBody) : 0;
                var lens = station - ToD(frameBody.position);
                if (PastInstant.Solve(ref path, lens, ut, c.Value, bound, double.NegativeInfinity, ut, out double tr) != PastStatus.Found)
                {
                    if (pass == 0) break;
                    /* The second frame would not solve: keep the first frame's instant. */
                    tr = firstTr;
                    frame = firstFrame;
                    frameBody = tree.Body(frame);
                    path.Frame = frame;
                    lens = station - ToD(frameBody.position);
                    return HeldKnown(ref path, lens, station, ut, c.Value, lensIndex, tr);
                }
                var then = _known.Subject(tr);
                int next = then.Body != null ? PastInstant.CommonAncestor(ref tree, lensIndex, then.Body.flightGlobalsIndex) : frame;
                bool settled = next == frame || pass == 1;
                if (!settled)
                {
                    firstTr = tr;
                    firstFrame = frame;
                    frame = next;
                    continue;
                }
                if (next != frame && firstTr < tr)
                {
                    /* Neither frame agrees with itself across the crossing: the older instant. */
                    tr = firstTr;
                    frameBody = tree.Body(firstFrame);
                    path.Frame = firstFrame;
                    lens = station - ToD(frameBody.position);
                }
                return HeldKnown(ref path, lens, station, ut, c.Value, lensIndex, tr);
            }
            _knownHold.Reset();
            now.State = CraftFixState.Listed;
            return now;
        }

        /* The subject at its solved instant, held as the picture holds it: an
           older solution leaves the held instant while its light has arrived
           in the frame it was solved in. */
        private CraftFix HeldKnown(ref KnownPath path, DVec3 lens, DVec3 station, double ut, double c, int lensIndex, double tr)
        {
            double held = _knownHold.NotBefore(ut, 0, c, lensIndex);
            if (held > tr)
            {
                var kept = path;
                var heldBody = path.Tree.Body(_knownHold.Frame);
                if (heldBody != null) kept.Frame = _knownHold.Frame;
                var keptLens = heldBody != null ? station - ToD(heldBody.position) : lens;
                if (PastInstant.Arrived(ref kept, keptLens, ut, c, held))
                {
                    path = kept;
                    tr = held;
                }
            }
            _knownHold.Shown(ut, tr, 0, c, lensIndex, path.Frame);
            _subjectUt = tr;
            return _known.Subject(tr);
        }

        private InstantHold _knownHold;
        private uint _knownHoldPid;

        /* The subject's known positions about the frame body as a path back in
           time. A fix's World is its body's present position plus its offset at
           the UT asked, as Orbit.getPositionAtUT gives it. */
        private struct KnownPath : IPastPath
        {
            public IFixSource Source;
            public KeplerBodyTree Tree;
            public int Frame;

            public bool TryAt(double ut, out DVec3 position)
            {
                position = default;
                var fix = Source.Subject(ut);
                if (!fix.Positioned || fix.Body == null) return false;
                if (!PastInstant.TryAbout(ref Tree, fix.Body.flightGlobalsIndex, Frame, ut, out var body)) return false;
                position = body + (fix.World - ToD(fix.Body.position));
                return true;
            }
        }

        /*
         * How the drawn craft looks from the lens. A craft drawn at the aimed
         * point is seen as the aim's readings say: `aimIsDrawn` when the fix
         * source reads the same truth the picture draws and the aim is the
         * craft, otherwise when the two points coincide.
         */
        private void SolveDrawn(DVec3 station, DVec3 aim, double distance, bool aimIsDrawn, CelestialBody aimBody)
        {
            if (_drawnOk)
            {
                if (aimIsDrawn || (aim - _drawnPos).Magnitude < 1e-3)
                {
                    _drawnDistance = distance;
                    _drawnOccluded = Occluded;
                    _drawnSunlit = Sunlit;
                }
                else
                {
                    _drawnDistance = (_drawnPos - station).Magnitude;
                    _drawnOccluded = _occlusion.FindOccluder(station, _drawnPos, out _, _drawn.Radius) != null;
                    _drawnSunlit = IsSunlit(_drawnPos);
                }
            }
            _frameBody = _drawnOk ? _drawnBody : aimBody;
        }

        private DVec3 DrawnOrAim(DVec3 aim) => _drawnOk ? _drawnPos : aim;

        /*
         * A Direction aim's point: along the held direction at the subject's
         * range, so the view is zoomed for the subject as it passes through.
         * North and east come from stepping the station's own latitude and
         * longitude, which holds for any body and any vantage.
         */
        private DVec3 DirectionPoint(CelestialBody body, DVec3 station, DVec3 localUp, CraftFix subject)
        {
            var st = new Vector3d(station.X, station.Y, station.Z);
            double lat = body.GetLatitude(st), lon = body.GetLongitude(st), alt = body.GetAltitude(st);
            const double Step = 0.01;
            double northSign = lat > 89.0 ? -1.0 : 1.0;
            var p0 = ToD(body.GetWorldSurfacePosition(lat, lon, alt));
            var towardNorth = (ToD(body.GetWorldSurfacePosition(lat + northSign * Step, lon, alt)) - p0) * northSign;
            var towardEast = ToD(body.GetWorldSurfacePosition(lat, lon + Step, alt)) - p0;
            var dir = CommandCameraMath.DirectionVector(localUp, towardNorth, towardEast, _aim.Azimuth, _aim.Elevation);
            double range = subject.Positioned ? (subject.World - station).Magnitude : DirectionDefaultRangeM;
            if (!(range > 1.0)) range = DirectionDefaultRangeM;
            return station + dir * range;
        }

        /* An Orbit, Surface or PositionAt aim's point in world space, on the
           same UT (and so the same physics tick) as the station and the
           scaled bodies. */
        private bool ResolveAim(CommandAim aim, Orbit aimOrbit, double ut, out DVec3 world, out CelestialBody body)
        {
            world = default;
            body = null;
            switch (aim.Kind)
            {
                case CommandAimKind.Orbit:
                {
                    var orbit = aimOrbit;
                    if (orbit == null || orbit.referenceBody == null) return false;
                    body = orbit.referenceBody;
                    world = ToD(orbit.getPositionAtUT(ut));
                    return true;
                }
                case CommandAimKind.Surface:
                {
                    body = FlightGlobals.GetBodyByName(aim.Body);
                    if (body == null) return false;
                    world = ToD(body.GetWorldSurfacePosition(aim.Latitude, aim.Longitude, aim.Altitude));
                    return true;
                }
                case CommandAimKind.PositionAt:
                {
                    body = FlightGlobals.GetBodyByName(aim.Body);
                    if (body == null || !AimPath(aim, body, out var path) || !path.TryAt(ut, out var rel, out _)) return false;
                    world = KinematicsWorld.Now().ToWorld(body, rel);
                    return true;
                }
            }
            return false;
        }

        /* A PositionAt aim's ballistic path under its body's gravity, built once per aim. */
        private bool AimPath(CommandAim aim, CelestialBody body, out KeplerPath path)
        {
            if (!ReferenceEquals(_aimPathFor, aim))
            {
                _aimPathFor = aim;
                var kb = KinematicsWorld.Now().Of(body);
                _aimPathOk = kb.Known && KeplerPath.TryFromState(ToD(aim.BodyRelativePosition), ToD(aim.BodyRelativeVelocity),
                    aim.Ut, kb.Mu, out _aimPath, kb.Radius);
            }
            path = _aimPath;
            return _aimPathOk;
        }

        private CommandAim _aimPathFor;
        private KeplerPath _aimPath;
        private bool _aimPathOk;

        /* In pixels of the delivered frame: the field just rendered, or, with
           nothing rendering, the field the next frame would snap to. */
        private void UpdateBracket(float now)
        {
            if (!HasFix)
            {
                BracketHalfPx = 0;
                TrueOffsetPx = null;
                return;
            }
            double radius = FramingRadius;
            bool rendering = _lastPrepare >= 0 && now - _lastPrepare <= FovSnapAfter;
            double fov = rendering
                ? _fov
                : CommandCameraMath.FovForFraction(radius, RangeM, _frameFraction, FovMinNow(), _fovMax);
            int rows = FrameRows();
            BracketHalfPx = CommandCameraMath.BracketHalfPx(radius, RangeM, fov, rows);
            UpdateDrawnOffset(fov, rows);
        }

        /* Where the drawn craft sits against the frame's axis, which is the aim except while the view slews, in the frame `fov` gives. */
        private void UpdateDrawnOffset(double fov, int rows)
        {
            TrueOffsetPx = _drawnOk
                ? CommandCameraMath.AngleInPixels(CommandCameraMath.AngleBetween(_solveForward, _drawnPos - _solveEye), fov, rows)
                : (double?)null;
            TrueVisible = _drawnOk && !_drawnOccluded && TrueInFrame(fov, rows);
        }

        /* Whether the drawn craft lands inside the frame the aim gives. */
        private bool TrueInFrame(double fov, int rows)
        {
            var rt = _cam.NearLayer != null ? _cam.NearLayer.targetTexture : null;
            int cols = rt != null ? rt.width : _cam.RenderWidth;
            if (cols <= 0 || rows <= 0) return false;
            CommandCameraMath.PerspectiveTerms(fov, (double)cols / rows, out double m00, out double m11);
            return CommandCameraMath.Project(_solveEye, _solveForward, _solveUp, m00, m11, 0, 0, cols, rows, _drawnPos).InFrame;
        }

        private int FrameRows()
        {
            var rt = _cam.NearLayer != null ? _cam.NearLayer.targetTexture : null;
            return rt != null ? rt.height : _cam.RenderHeight;
        }

        private void NoteGovernance(double ut, bool governed)
        {
            string at = $"{Tag} cam={_flightId} UT={F(ut)} d={_lastDistance:F0}";
            if (!_blankLogged || Blank != _loggedBlank)
            {
                if (_blankLogged || Blank != null)
                    Debug.Log(Blank != null ? $"{at} event=blank reason='{Blank}'" : $"{at} event=blank-off");
                _blankLogged = true;
                _loggedBlank = Blank;
            }
            string aim = (_aim != null ? _aim.ToString() : "default") + (governed ? $" lock={_authority.Owner}" : "");
            if (aim != _loggedAim)
            {
                Debug.Log($"{at} event=aim aim={aim}");
                _loggedAim = aim;
            }
        }

        private static bool IsSunlit(DVec3 point)
        {
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null) return false;
            var sunPos = ToD(sun.position);
            var bodies = FlightGlobals.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null || b == sun) continue;
                if (!CommandCameraMath.SegmentClearOfSphere(point, sunPos, ToD(b.position), b.Radius)) return false;
            }
            return true;
        }

        private void UpdateSunlit(double ut, double d, DVec3 target)
        {
            bool lit = IsSunlit(target);
            if (!_sunlitLogged || lit != Sunlit)
            {
                _sunlitLogged = true;
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={d:F0} event={(lit ? "sunlit" : "eclipsed")} target='{_known.SubjectName}'");
            }
            Sunlit = lit;
        }

        /* Built once per target, or, drawn at its past instant, once per set
           of parts the history holds for it then (a dock, an undock, a part
           lost). A craft that cannot be built is not retried. */
        private void EnsureModel(KerbcastCamera cam)
        {
            var parts = _drawn.ShownParts;
            if (_drawn.ShownPast && parts == null)
            {
                /* The save's record is the craft now, not as its light shows it: no model at all. */
                _standIn?.Dispose();
                _standIn = null;
                return;
            }
            uint pid = parts != null ? _drawn.ShownPid : _drawn.PersistentId;
            if (_standIn != null && _standIn.PersistentId == pid && _standIn.Parts == parts) return;
            if (_modelFailedPid == pid && _modelFailedParts == parts && pid != 0) return;
            if (_standIn != null && parts != null && _standIn.PersistentId == pid && _standIn.Update(parts, out int added, out int removed))
            {
                /* A moved centre of mass alone changes no part. */
                if (added > 0 || removed > 0)
                    Debug.Log($"{Tag} cam={_flightId} target model parts changed: pid={pid} partsUt={parts.Ut:F1} added={added} removed={removed} radius={_standIn.Radius:F1}m");
                return;
            }
            _standIn?.Dispose();
            _standIn = null;
            try
            {
                var layers = new[] { cam.NearLayer, cam.FarLayer, _midCam };
                _standIn = parts != null
                    ? CommandCameraTargetModel.Build(pid, parts, _flightId, layers)
                    : CommandCameraTargetModel.Build(_drawn.Proto, _flightId, layers);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} cam={_flightId} target model build threw: {ex}");
            }
            if (_standIn == null)
            {
                _modelFailedPid = pid;
                _modelFailedParts = parts;
            }
            else if (_subpixel != null) _standIn.AttachSubpixel(_subpixel.Camera);
        }

        /* Keeps the status geometry and the occlusion log live while nobody is
           watching the feed (PrepareRender only runs for a granted capture). */
        public void Poll()
        {
            float now = Time.unscaledTime;
            if (now - _solvedAt < PollStaleSeconds) return;
            Solve(Planetarium.GetUniversalTime(), now, out _, out _, out _, out _, out _);
        }

        /* The recorded transitions, oldest first, then the forecast camera-axis
           edges (flagged predicted). Forecasts are rebuilt on every call and
           never enter the ring. */
        public IReadOnlyList<CommandTrackEvent> TrackEvents()
        {
            _trackEvents.Clear();
            for (int i = 0; i < _events.Count; i++) _trackEvents.Add(_events[i]);
            _forecastCount = 0;
            if (!HasFix || _events.Count == 0) return _trackEvents;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                double now = Planetarium.GetUniversalTime();
                double horizon = Math.Min(ForecastRealSeconds * Math.Max(1.0, TimeWarp.CurrentRate), ForecastMaxUt);
                _occlusion.UseSite(_mount.Site);
                /* A world model's motion holds only until its record's window ends. */
                bool moving = TryMotions(now, out var station, out var aim, out double until);
                if (moving) horizon = Math.Min(horizon, until - now);
                if (moving && horizon > 0 && _forecast.Begin(now, horizon, station, aim, AimIsCraft ? KnownRadius : 0))
                {
                    CommandCameraMath.FindEdges(now, now + horizon, ForecastSamples, ForecastBisectSteps, ForecastToleranceUt,
                        _forecast.StateAt, (a, b) => a.SameCameraAxisAs(b), _edges);
                    if (_forecast.Unplaced) _edges.Clear();
                    var last = _events[_events.Count - 1];
                    int before = _trackEvents.Count;
                    CommandTrackEvent.AppendPredicted(_trackEvents, _edges, TrackUt, last.Blank);
                    _forecastCount = _trackEvents.Count - before;
                }
            }
            catch (Exception ex)
            {
                if (!_forecastFailLogged)
                {
                    _forecastFailLogged = true;
                    Debug.LogWarning($"{Tag} cam={_flightId} forecast threw (logged once): {ex}");
                }
            }
            _forecastMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            return _trackEvents;
        }

        /* How the station and the aim point move from now on, read fresh so
           both are in this frame's world, and the UT that holds until.
           Mirrors SolveGeometry and ResolveAim. */
        private bool TryMotions(double ut, out BodyMotion station, out BodyMotion aim, out double validUntilUt)
        {
            aim = default;
            validUntilUt = double.PositiveInfinity;
            var site = _mount.Site;
            if (!site.TryStationMotion(ut, out station)) return false;

            var a = _aim;
            if (a == null || a.Kind == CommandAimKind.Vessel) return _known.SubjectMotion(ut, out aim, out validUntilUt);
            switch (a.Kind)
            {
                case CommandAimKind.Direction:
                {
                    /* Held still against the ground, as near as the forecast needs. */
                    var body = site.StationBody;
                    if (body == null) return false;
                    aim = BodyMotion.OnSurface(body, new Vector3d(_lastAimPoint.X, _lastAimPoint.Y, _lastAimPoint.Z));
                    return true;
                }
                case CommandAimKind.Orbit:
                {
                    var orbit = _aimOrbit;
                    if (orbit == null || orbit.referenceBody == null) return false;
                    aim = BodyMotion.OnOrbit(orbit);
                    return true;
                }
                case CommandAimKind.Surface:
                {
                    var body = FlightGlobals.GetBodyByName(a.Body);
                    if (body == null) return false;
                    aim = BodyMotion.OnSurface(body, body.GetWorldSurfacePosition(a.Latitude, a.Longitude, a.Altitude));
                    return true;
                }
                case CommandAimKind.PositionAt:
                {
                    var body = FlightGlobals.GetBodyByName(a.Body);
                    if (body == null || !AimPath(a, body, out var path)) return false;
                    aim = BodyMotion.OnPath(body, path, KinematicsWorld.Now().Zup);
                    return true;
                }
            }
            return false;
        }

        public RigFrame PrepareRender(KerbcastCamera cam)
        {
            var site = _mount.Site;
            var anchor = _mount.PartTransform;
            if (anchor == null) return RigFrame.Skip;

            float now = Time.unscaledTime;
            double dt = _lastPrepare < 0 ? double.MaxValue : now - _lastPrepare;
            _lastPrepare = now;
            double ut = Planetarium.GetUniversalTime();
            _frameUt = ut;
            _poseValid = false;

            bool solved = Solve(ut, now, out var station, out var aim, out var localUp, out var north, out var occluder);
            if (Blank != null)
            {
                TakeFrameState();
                return RigFrame.Blank;
            }
            if (!solved) return RigFrame.Skip;

            var fwd = _solveForward;
            var up = _solveUp;
            anchor.position = new Vector3((float)station.X, (float)station.Y, (float)station.Z);
            anchor.rotation = Quaternion.LookRotation(ToF(fwd), ToF(up));
            _poseValid = true;
            _poseEye = station;
            _poseForward = fwd;
            _poseAimToward = aim - station;
            _poseUp = up;
            SyncScaledBodies(cam, anchor.position);
            SyncScaledPose(cam);
            UpdateStationBodyMesh(cam, ut, _lastDistance);

            double distance = _lastDistance;
            var drawn = PlaceDrawn(cam, ut, now, station, aim, occluder, distance);

            double fovTarget = CommandCameraMath.FovForFraction(FramingRadius, distance, _frameFraction, FovMinNow(), _fovMax);
            _fov = CommandCameraMath.SmoothFov(_fov, fovTarget, dt, FovTau, FovSnapAfter);
            cam.SetRigFov((float)_fov);
            UpdateBracket(now);
            FinishDrawn(cam, ut, station, anchor.rotation, drawn);

            if (_first)
            {
                _first = false;
                var ksp = site.Body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Altitude);
                double err = (ToD(ksp) - station).Magnitude;
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={distance:F0} event=first-frame "
                    + $"station={station} aim={aim} kspSurfaceCheckErr={err:F4}m craftRadius={KnownRadius:F1} fov={_fov:F3}");
            }

            if (now - _skyProbeAt >= SkyProbeSeconds)
            {
                _skyProbeAt = now;
                cam.RigScaledProbe = true;
            }

            TakeFrameState();
            LogTransitions(ut, distance, aim, fwd, localUp, now);
            return RigFrame.Render;
        }

        /* What PlaceDrawn settles for the rest of the frame's picture. */
        private struct DrawnPlan
        {
            public double Distance;
            public bool Blocked;
            public bool Lit;
            public bool StandIn;
            public DVec3 Toward;
            public double PlanDistance;
            public double PlanScale;
        }

        /* The aim frames the view; the drawn craft, wherever it really is,
           decides what is drawn and how the layers are clipped around it.
           With no craft drawn, the layers are clipped around the aim. Runs
           before the field is chosen, since a stand-in model sets the radius. */
        private DrawnPlan PlaceDrawn(KerbcastCamera cam, double ut, float now, DVec3 station, DVec3 aim, string occluder,
            double distance)
        {
            bool drawnOk = _drawnOk;
            var p = new DrawnPlan
            {
                Distance = drawnOk ? _drawnDistance : distance,
                Blocked = drawnOk ? _drawnOccluded : occluder != null,
                Lit = drawnOk ? _drawnSunlit : Sunlit,
            };
            _drawn.Measure(drawnOk, _drawnPos, now);

            /* A loaded craft draws itself; the model and the marker stand in only
               for an unloaded one, and only where a real craft would be seen. */
            p.StandIn = drawnOk && _drawn.Loaded == null;
            p.Toward = ((drawnOk ? _drawnPos : aim) - station).Normalized;
            p.PlanDistance = p.Distance;
            p.PlanScale = 1.0;
            if (_wantModel && p.StandIn)
            {
                EnsureModel(cam);
                if (_standIn != null)
                {
                    _drawn.Radius = _standIn.Radius;
                    /* Beyond ModelProxyDistance the model stands in at that
                       distance on the true ray, shrunk by the same ratio: the
                       same angular size and perspective, with local-space
                       precision and clip planes that stay simple. */
                    p.PlanDistance = Math.Min(p.Distance, ModelProxyDistance);
                    p.PlanScale = p.PlanDistance / p.Distance;
                    var at = station + p.Toward * p.PlanDistance;
                    var rotation = _drawn.SavedRotation(_drawnBody);
                    var parts = _standIn.Parts;
                    if (parts != null)
                    {
                        /* The history follows the centre of mass; the model hangs from its root part. */
                        rotation = _drawn.ShownRotation;
                        var com = rotation * new Vector3(parts.ComX, parts.ComY, parts.ComZ) * (float)p.PlanScale;
                        at = at - new DVec3(com.x, com.y, com.z);
                    }
                    _standIn.Place(p.Lit && !p.Blocked, at, rotation, (float)p.PlanScale);
                    NoteModelMode(ut, p.Distance, p.PlanDistance, p.PlanScale);
                }
            }
            return p;
        }

        /* The sub-pixel camera, the clip plan and the marker, once the field is set. */
        private void FinishDrawn(KerbcastCamera cam, double ut, DVec3 station, Quaternion lensRotation, DrawnPlan p)
        {
            UpdateSubpixel(cam, ut, p.Distance, p.PlanDistance, p.PlanScale, p.Blocked, p.Lit, p.StandIn);

            _plan = CommandCameraMath.PlanClips(p.PlanDistance, _drawn.Radius * p.PlanScale, p.Blocked, _hasFar, _wasSplit,
                _nearNativeNear, _nearNativeFar, _farNativeNear, _farNativeFar);
            ApplyPlan(cam);

            if (_marker != null)
            {
                var rt = cam.NearLayer != null ? cam.NearLayer.targetTexture : null;
                _marker.Place(p.Lit && p.StandIn && !p.Blocked, station, p.Toward, p.Distance, lensRotation,
                    (float)_fov, rt != null ? rt.height : cam.RenderHeight);
            }
        }

        public bool TryRenderPose(out DVec3 eye, out DVec3 forward, out DVec3 up)
        {
            eye = _poseEye;
            forward = _poseForward;
            up = _poseUp;
            return _poseValid;
        }

        /* The aim point, as a direction from the lens, so a client can put the
           bracket where the aim lands while the view slews onto it. While the
           view slews it says where the aim is apart from the pose: knowledge
           the operator holds, so it is never observed-only, and it is always
           placed, in frame or not. Then the subject as a vessel anchor with
           occlusion on: where the craft really is in each frame, wherever the
           camera aims, sized by its radius plus the framing margin. Kerbcast's
           other anchors are observed-only (AnchorRules), so they carry only
           what the picture shows. Under a held lock the owner can withhold
           both. */
        public void AddOwnAnchors(List<KeyValuePair<string, AnchorSpec>> into)
        {
            bool withheld = _authority != null && _authority.Registered && !_authority.SubjectAnchorVisible;
            if (_poseValid && !withheld)
            {
                /* A direction from the lens projects where the point along it does; the vector is z up. */
                _aimAnchor.VectorX = _poseAimToward.X;
                _aimAnchor.VectorY = _poseAimToward.Z;
                _aimAnchor.VectorZ = _poseAimToward.Y;
                into.Add(new KeyValuePair<string, AnchorSpec>(AnchorRegistry.KerbcastOwner, _aimAnchor));
            }
            uint pid = _drawn.PersistentId;
            if (pid == 0 || !_drawnOk) return;
            if (withheld) return;
            _subjectAnchor.Vessel = pid;
            _subjectAnchor.MarginM = FramingMargin;
            into.Add(new KeyValuePair<string, AnchorSpec>(AnchorRegistry.KerbcastOwner, _subjectAnchor));
        }

        /* The subject where this rig solved it for the frame, with the radius
           it frames and the terrain-aware occlusion it already ran. Unloaded
           craft (the Space Center, or beyond physics range) are located from
           their orbit, which the generic projector can not do. */
        /* The default and Vessel aims point at the craft itself, so its size counts. */
        private bool AimIsCraft => _aim == null || _aim.Kind == CommandAimKind.Vessel;

        public bool TryLocateVessel(uint pid, out DVec3 world, out double radius, out bool occluded, out bool unseen)
        {
            unseen = false;
            if (_drawnOk && pid != 0 && pid == _drawn.PersistentId)
            {
                world = _drawnPos;
                radius = _drawn.AnchorRadius;
                occluded = _drawnOccluded;
                return true;
            }
            world = default;
            radius = 0;
            occluded = false;
            if (pid == 0 || !_poseValid) return false;
            /* Any other craft, or the subject while it is not drawn, is placed
               where its arriving light shows it, so an anchor never names a
               place the picture would not. */
            switch (_cam.Picture.Locate(pid, out world, out radius))
            {
                case OtherSight.Past:
                    occluded = _occlusion.FindOccluder(_poseEye, world, out _, radius) != null;
                    return true;
                case OtherSight.Unseen:
                    unseen = true;
                    return false;
                default:
                    return false;
            }
        }

        private void NoteModelMode(double ut, double d, double proxy, double scale)
        {
            bool proxied = scale < 1.0;
            if (_modelModeLogged && proxied == _modelProxied) return;
            _modelModeLogged = true;
            _modelProxied = proxied;
            Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={d:F0} event=model-{(proxied ? "proxy" : "true-distance")} "
                + $"proxyDistance={proxy:F0}m scale={scale:G4} threshold={ModelProxyDistance:F0}m");
        }

        /* Stock moves the scaled bodies in its own LateUpdate, which can run
           after this capture, leaving them where the previous physics tick put
           them while the aim is solved for this one. At a 0.02 degree field one
           tick of Kerbin's spin is several pixels, so the whole frame jumps and
           returns on the frames that cross a tick. Placing them here, exactly as
           stock is about to, keeps the sky and the aim on the same tick. */
        private void SyncScaledBodies(KerbcastCamera cam, Vector3 cameraLocal)
        {
            var bodies = FlightGlobals.Bodies;
            if (bodies == null) return;
            Vector3 eye = ScaledSpace.LocalToScaledSpace(cameraLocal);
            var rt = cam.NearLayer != null ? cam.NearLayer.targetTexture : null;
            int rows = rt != null ? rt.height : cam.RenderHeight;
            double perPx = CommandCameraMath.PixelSizeAtUnit(_fov, rows);
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                var t = b != null && b.scaledBody != null ? b.scaledBody.transform : null;
                if (t == null) continue;
                Vector3 was = t.position;
                Vector3 now = ScaledSpace.LocalToScaledSpace(b.position);
                if (b == _frameBody && perPx > 0)
                {
                    var a = new Vector3d(was.x - eye.x, was.y - eye.y, was.z - eye.z);
                    var c = new Vector3d(now.x - eye.x, now.y - eye.y, now.z - eye.z);
                    double px = Math.Atan2(Vector3d.Cross(a, c).magnitude, Vector3d.Dot(a, c)) / perPx;
                    if (px > 0.05) _scaledSkewFrames++;
                    if (px > _scaledSkewMaxPx) _scaledSkewMaxPx = px;
                }
                t.position = now;
            }
        }

        /* LayerCamRotator places the scaled layer in OnPreRender, after Unity
           has culled with last tick's pose, which at a 0.02 degree field points
           well away from this tick's view. Set it before culling too. */
        private static void SyncScaledPose(KerbcastCamera cam)
        {
            var scaled = cam.ScaledLayer;
            var near = cam.NearLayer;
            if (scaled == null || near == null) return;
            var t = scaled.transform;
            t.position = ScaledSpace.LocalToScaledSpace(near.transform.position);
            t.rotation = near.transform.rotation;
        }

        /* KerbcastCamera force-enables every scaled planet so a part camera far
           from the main view still sees them. The main camera standing on a
           body never draws that body's scaled mesh while its terrain is local
           (the fader turns it off), and the command camera stands on it too, so
           while the station's PQS is active its mesh is left as stock has it.
           With the PQS inactive the mesh is the only ground there is. */
        private void UpdateStationBodyMesh(KerbcastCamera cam, double ut, double d)
        {
            var body = _mount.Site.Body;
            var pqs = body != null ? body.pqsController : null;
            bool stock = pqs != null && pqs.isActive;
            var r = stock && body.scaledBody != null ? body.scaledBody.GetComponent<Renderer>() : null;
            cam.RigStockFaderRenderer = r;
            bool nowStock = r != null;
            if (nowStock != _stationMeshStock || !_stationMeshLogged)
            {
                _stationMeshLogged = true;
                _stationMeshStock = nowStock;
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={d:F0} event=station-body-mesh "
                    + (nowStock ? "stock (left to the fader, terrain is local)" : "forced on (no local terrain)"));
            }
        }

        /* AtmosphereFromGround fills the sky shader's camera inputs every frame
           from the main scaled camera, and the shader colours each vertex of
           the shell from them. For this camera they must describe this
           camera, so they are swapped in for the scaled render (the way
           AtmosphereFromGround.UpdateAtmosphere derives them) and put back. */
        private static readonly int CameraPosId = Shader.PropertyToID("_v4CameraPos");
        private static readonly int CameraHeightId = Shader.PropertyToID("_fCameraHeight");
        private static readonly int CameraHeight2Id = Shader.PropertyToID("_fCameraHeight2");
        private static readonly int CamHeightUnderwaterId = Shader.PropertyToID("_fCamHeightUnderwater");
        private static readonly int LightDotId = Shader.PropertyToID("_lightDot");
        private readonly MaterialPropertyBlock _afgBlock = new MaterialPropertyBlock();
        private Renderer _afgRenderer;
        private AtmosphereFromGround _afgOverridden;
        private bool _afgOverride = true;
        private AfgInputs _afgOurs;
        private AfgInputs _afgStock;

        private struct AfgInputs
        {
            public Vector3 CameraPos;
            public float Height;
            public float Underwater;
            public float LightDot;
        }

        private static AfgInputs AfgInputsFor(AtmosphereFromGround afg, Vector3 camPos)
        {
            var lhs = afg.planet.scaledBody.transform.position - camPos;
            float h = lhs.magnitude;
            float depth = (afg.pRadius - h) * ScaledSpace.ScaleFactor;
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            float dot = sun != null
                ? Mathf.Clamp01(Vector3.Dot(lhs, camPos - sun.scaledBody.transform.position) * afg.dawnFactor)
                : afg.lightDot;
            return new AfgInputs
            {
                CameraPos = camPos,
                Height = h,
                Underwater = depth >= 0f ? Mathf.Min(depth * afg.oceanDepthRecip, 1f) : 0f,
                LightDot = dot,
            };
        }

        private static void ApplyAfg(MaterialPropertyBlock mpb, AfgInputs v)
        {
            mpb.SetVector(CameraPosId, new Vector4(v.CameraPos.x, v.CameraPos.y, v.CameraPos.z, 0f));
            mpb.SetFloat(CameraHeightId, v.Height);
            mpb.SetFloat(CameraHeight2Id, v.Height * v.Height);
            mpb.SetFloat(CamHeightUnderwaterId, v.Underwater);
            mpb.SetFloat(LightDotId, v.LightDot);
        }

        public void BeforeScaledRender(KerbcastCamera cam)
        {
            _afgOverridden = null;
            var body = _mount.Site.Body;
            var afg = body != null ? body.afg : null;
            var scaled = cam.ScaledLayer;
            if (afg == null || afg.planet == null || afg.planet.scaledBody == null || afg.mainCamera == null || scaled == null) return;
            if (_afgRenderer == null || _afgRenderer.gameObject != afg.gameObject) _afgRenderer = afg.GetComponent<Renderer>();
            if (_afgRenderer == null) return;
            _afgStock = AfgInputsFor(afg, afg.mainCamera.position);
            _afgOurs = AfgInputsFor(afg, scaled.transform.position);
            if (!_afgOverride) return;
            _afgRenderer.GetPropertyBlock(_afgBlock);
            ApplyAfg(_afgBlock, _afgOurs);
            _afgRenderer.SetPropertyBlock(_afgBlock);
            _afgOverridden = afg;
        }

        public void AfterScaledRender(KerbcastCamera cam)
        {
            if (_afgOverridden == null || _afgRenderer == null) return;
            _afgRenderer.GetPropertyBlock(_afgBlock);
            ApplyAfg(_afgBlock, _afgStock);
            _afgRenderer.SetPropertyBlock(_afgBlock);
            _afgOverridden = null;
        }

        /* Reads the backdrop (galaxy plus scaled, before any local layer) three
           ways: as rendered with this camera's sky inputs, with the main
           camera's inputs as stock leaves them, and without the atmosphere
           shell. Logs the inputs and this camera's scaled pose alongside, then
           re-renders the frame as it was. */
        public void OnScaledProbe(KerbcastCamera cam, Color asRendered)
        {
            var body = _mount.Site.Body;
            var atmo = body != null && body.afg != null ? body.afg.GetComponent<Renderer>() : null;
            string stockInputs = "n/a", noAtmo = "n/a";
            bool atmoWas = atmo != null && atmo.enabled;
            try
            {
                _afgOverride = false;
                stockInputs = Rgb(cam.RenderBackdropForProbe());
                _afgOverride = true;
                if (atmoWas)
                {
                    atmo.enabled = false;
                    noAtmo = Rgb(cam.RenderBackdropForProbe());
                    atmo.enabled = true;
                }
            }
            finally
            {
                _afgOverride = true;
                if (atmo != null) atmo.enabled = atmoWas;
                cam.RenderBackdropForProbe();
            }
            Debug.Log($"{Tag} cam={_flightId} UT={F(Planetarium.GetUniversalTime())} d={_lastDistance:F0} event=sky-probe "
                + $"elev={ElevationDeg:F2} asRendered={Rgb(asRendered)} stockInputs={stockInputs} noAtmosphere={noAtmo} "
                + $"far={(_plan.FarRender ? "rendered" : "skipped")} mid={(_plan.MidActive ? "on" : "off")} "
                + $"occluded={Occluded} sunlit={Sunlit}");
            LogSkyInputs(cam, body);
            LogSkyBlockers(cam, body);
        }

        /* Scaled units the near plane is pushed to for the nearPushed sample:
           past anything hugging the lens, short of the shell's far side
           (about 5 to 8 units for a view 15 to 30 deg up from Kerbin's surface). */
        private const float ProbeNearPushed = 3f;
        private const int SkyboxLayer = 18;
        private const int LocalLayersMask = (1 << 4) | (1 << 15);
        private int _scaledMaskSource;
        private const float ProbeRayLength = 20f;
        private static readonly int OpacityId = Shader.PropertyToID("_Opacity");
        private static readonly int FadeAltitudeId = Shader.PropertyToID("_FadeAltitude");

        /* Names what lies on the view ray in the scaled scene. Renders the
           backdrop twice more, once without the station body's scaled mesh and
           once with the near plane pushed out, then lists every renderer the
           scaled camera can see whose bounds the forward ray crosses. */
        private void LogSkyBlockers(KerbcastCamera cam, CelestialBody body)
        {
            var scaled = cam.ScaledLayer;
            if (scaled == null) return;
            var bodyR = body != null && body.scaledBody != null ? body.scaledBody.GetComponent<Renderer>() : null;
            string noBody = "n/a", nearPushed = "n/a", bodyState = "none", withSkybox = "n/a", noLocal = "n/a";
            if (bodyR != null)
            {
                var m = bodyR.sharedMaterial;
                string fade = m != null && m.HasProperty(FadeAltitudeId) ? m.GetFloat(FadeAltitudeId).ToString("F3", CultureInfo.InvariantCulture) : "-";
                string opacity = m != null && m.HasProperty(OpacityId) ? m.GetFloat(OpacityId).ToString("F3", CultureInfo.InvariantCulture) : "-";
                bodyState = $"enabled={bodyR.enabled} active={bodyR.gameObject.activeInHierarchy} shader={(m != null ? m.shader.name : "-")} "
                    + $"queue={(m != null ? m.renderQueue : -1)} fadeAltitude={fade} opacity={opacity} riggedOut={cam.RigStockFaderRenderer == bodyR}";
            }
            float nearWas = scaled.nearClipPlane;
            int maskWas = scaled.cullingMask;
            bool bodyWas = bodyR != null && bodyR.enabled;
            try
            {
                if (bodyWas)
                {
                    bodyR.enabled = false;
                    noBody = Rgb(cam.RenderBackdropForProbe());
                    bodyR.enabled = true;
                }
                scaled.nearClipPlane = ProbeNearPushed;
                nearPushed = Rgb(cam.RenderBackdropForProbe());
                scaled.nearClipPlane = nearWas;
                scaled.cullingMask = maskWas | (1 << SkyboxLayer);
                withSkybox = Rgb(cam.RenderBackdropForProbe());
                scaled.cullingMask = maskWas & ~LocalLayersMask;
                noLocal = Rgb(cam.RenderBackdropForProbe());
            }
            finally
            {
                scaled.nearClipPlane = nearWas;
                scaled.cullingMask = maskWas;
                if (bodyR != null) bodyR.enabled = bodyWas;
                cam.RenderBackdropForProbe();
            }

            var ray = new Ray(scaled.transform.position, scaled.transform.forward);
            var hits = new List<string>();
            int mask = scaled.cullingMask;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if ((mask & (1 << r.gameObject.layer)) == 0) continue;
                var b = r.bounds;
                bool inside = b.Contains(ray.origin);
                if (!inside && (!b.IntersectRay(ray, out float dist) || dist > ProbeRayLength)) continue;
                var m = r.sharedMaterial;
                hits.Add($"{r.name}[layer={r.gameObject.layer} {(inside ? "lensInside" : "hit")} "
                    + $"shader={(m != null ? m.shader.name : "-")} queue={(m != null ? m.renderQueue : -1)}]");
            }
            Debug.Log($"{Tag} cam={_flightId} event=sky-blockers elev={ElevationDeg:F2} noBody={noBody} nearPushed={nearPushed} withSkybox={withSkybox} noLocal={noLocal} "
                + $"stationBody: {bodyState} | onRay({hits.Count}): {string.Join(", ", hits)}");
        }

        private void LogSkyInputs(KerbcastCamera cam, CelestialBody body)
        {
            var afg = body != null ? body.afg : null;
            var scaled = cam.ScaledLayer;
            if (afg == null || scaled == null || body.scaledBody == null) return;
            var t = scaled.transform;
            var centre = body.scaledBody.transform.position;
            var up = (t.position - centre).normalized;
            float camAltM = ((t.position - centre).magnitude - afg.pRadius) * ScaledSpace.ScaleFactor;
            float fwdElev = Mathf.Asin(Mathf.Clamp(Vector3.Dot(t.forward, up), -1f, 1f)) * Mathf.Rad2Deg;
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            var toSun = sun != null ? (sun.scaledBody.transform.position - t.position).normalized : Vector3.zero;
            float sunElev = Mathf.Asin(Mathf.Clamp(Vector3.Dot(toSun, up), -1f, 1f)) * Mathf.Rad2Deg;
            float sunAngle = Vector3.Angle(t.forward, toSun);
            float stockAltM = (_afgStock.Height - afg.pRadius) * ScaledSpace.ScaleFactor;
            Debug.Log($"{Tag} cam={_flightId} event=sky-inputs ours: camAlt={camAltM:F0}m height={_afgOurs.Height:F4} lightDot={_afgOurs.LightDot:F3} "
                + $"underwater={_afgOurs.Underwater:F3} | stock: camAlt={stockAltM:F0}m height={_afgStock.Height:F4} lightDot={_afgStock.LightDot:F3} "
                + $"camGap={(_afgOurs.CameraPos - _afgStock.CameraPos).magnitude * ScaledSpace.ScaleFactor:F0}m | "
                + $"shell outer={afg.outerRadius:F3} inner={afg.innerRadius:F3} pRadius={afg.pRadius:F3} bodyCentre={centre} | "
                + $"pose: fwdElev={fwdElev:F2} (rig elev={ElevationDeg:F2}) sunElev={sunElev:F1} sunToView={sunAngle:F1}deg "
                + $"near={scaled.nearClipPlane:G3} far={scaled.farClipPlane:G3}");
        }

        private static string Rgb(Color c) =>
            string.Format(CultureInfo.InvariantCulture, "({0:F3},{1:F3},{2:F3})", c.r, c.g, c.b);

        private void ApplyPlan(KerbcastCamera cam)
        {
            cam.RigSkipFar = _hasFar && !_plan.FarRender;
            var far = cam.FarLayer;
            if (far != null && _plan.FarRender)
            {
                far.nearClipPlane = _plan.FarNear;
                far.farClipPlane = _plan.FarFar;
            }
            var near = cam.NearLayer;
            if (near != null) near.farClipPlane = _plan.NearFar;
            if (_midCam != null && _plan.MidActive)
            {
                _midCam.nearClipPlane = _plan.MidNear;
                _midCam.farClipPlane = _plan.MidFar;
            }
        }

        public void RenderBetweenFarAndNear(KerbcastCamera cam)
        {
            var near = cam.NearLayer;
            if (near == null) return;
            if (_midCam != null && _plan.MidActive && (cam.Layers & CameraLayers.Far) != 0)
            {
                _midCam.targetTexture = near.targetTexture;
                _midCam.fieldOfView = (float)_fov;
                _midCam.Render();
            }
            /* After far and mid, before near: anything in the near layer still
               draws over the craft's light. */
            if (_subpixelActive && _subpixel != null)
            {
                var rt = near.targetTexture;
                var pose = cam.FarLayer != null ? cam.FarLayer.transform : near.transform;
                _subpixel.Render(pose, (float)_fov, rt != null ? rt.height : cam.RenderHeight, _subpixelNear, _subpixelFar);
                _subpixel.Composite(rt);
            }
        }

        public void AfterLayersRender(KerbcastCamera cam)
        {
            if (_utStripe == null) return;
            var near = cam.NearLayer;
            _utStripe.Draw(near != null ? near.targetTexture : null, _frameUt);
        }

        /* Chooses between drawing the model in the frame's layers and through
           the sub-pixel camera, from its true angular size in pixels. */
        private void UpdateSubpixel(KerbcastCamera cam, double ut, double distance, double planDistance, double planScale,
            bool occluded, bool lit, bool standIn)
        {
            _subpixelActive = false;
            if (_standIn == null || _subpixel == null || !standIn)
            {
                if (_standIn != null) _standIn.SubpixelMode = false;
                return;
            }
            var rt = cam.NearLayer != null ? cam.NearLayer.targetTexture : null;
            int rows = rt != null ? rt.height : cam.RenderHeight;
            double perPx = CommandCameraMath.PixelSizeAtUnit(_fov, rows);
            double across = perPx > 0 && distance > 0 ? 2.0 * _standIn.Radius / (distance * perPx) : 0;
            bool sub = across < SubpixelBelowPx;
            _standIn.SubpixelMode = sub;
            _subpixelActive = sub && across >= SubpixelFloorPx && lit && !occluded;
            double span = CommandCameraMath.ClipSpan(_standIn.Radius * planScale);
            _subpixelNear = (float)Math.Max(0.05, planDistance - span);
            _subpixelFar = (float)(planDistance + span);
            if (!_subpixelActive) _subpixel.Reset();
            if (!_subpixelLogged || sub != _subpixelWas)
            {
                _subpixelLogged = true;
                _subpixelWas = sub;
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={distance:F0} event=model-{(sub ? "subpixel" : "direct")} "
                    + $"across={across:G3}px fov={_fov:F4} rows={rows} clip=[{_subpixelNear:F1},{_subpixelFar:F1}]");
            }
        }

        private void NoteTargetAndOcclusion(double ut, double d, CelestialBody targetBody, DVec3 target, string occluder,
            string source)
        {
            string at = $"{Tag} cam={_flightId} UT={F(ut)} d={d:F0}";

            if (!_targetLogged || _known.SubjectPersistentId != _targetPid)
            {
                _targetLogged = true;
                _targetPid = _known.SubjectPersistentId;
                Debug.Log($"{at} event=target vessel='{_known.SubjectName}' persistentId={_known.SubjectPersistentId} "
                    + $"{DrawnLogFields()} elev={ElevationDeg:F2}");
                NoteStockCheck(at, target);
            }
            string tb = targetBody != null ? targetBody.bodyName : "?";
            if (tb != _targetBody)
            {
                if (_targetBody != null) Debug.Log($"{at} event=target-soi {_targetBody}->{tb}");
                _targetBody = tb;
            }
            bool occluded = occluder != null;
            if (occluded != _wasOccluded || (occluded && (occluder != _occluderName || source != _occluderSource)))
            {
                Debug.Log(occluded
                    ? $"{at} event=occluded-on by={occluder} occluderSource={source} elev={ElevationDeg:F2} target='{_known.SubjectName}' occlusionMs={_occlusionMs:F3}"
                    : $"{at} event=occluded-off was={_occluderName} occluderSource={_occluderSource} elev={ElevationDeg:F2} target='{_known.SubjectName}' occlusionMs={_occlusionMs:F3}");
                _wasOccluded = occluded;
                _occluderName = occluder;
                _occluderSource = source;
            }
        }

        private string DrawnLogFields() => $"situation={_drawn.Situation} loaded={_drawn.Loaded != null}";

        /* Once per rig: the orbit-derived position against stock's own, to
           show the floating-origin conversion is right. */
        private void NoteStockCheck(string at, DVec3 target)
        {
            if (_stockCheckLogged || _drawn.Loaded != null) return;
            var stock = _drawn.StockWorldPosition();
            if (!stock.HasValue) return;
            _stockCheckLogged = true;
            Debug.Log($"{at} event=target-position-check orbit={target} stock={stock.Value} err={(stock.Value - target).Magnitude:F1}m");
        }

        private void LogTransitions(double ut, double d, DVec3 target, DVec3 fwd, DVec3 localUp, float now)
        {
            string at = $"{Tag} cam={_flightId} UT={F(ut)} d={d:F0}";

            if (_plan.Split != _wasSplit)
            {
                _wasSplit = _plan.Split;
                Debug.Log(_plan.Split
                    ? $"{at} event=split-on mid=[{_plan.MidNear:F0},{_plan.MidFar:F0}] far=[{_plan.FarNear:F0},{_plan.FarFar:F0}]"
                    : $"{at} event=split-off far=[{_plan.FarNear:F0},{_plan.FarFar:F0}]");
            }
            bool farExtended = _hasFar ? _plan.FarFar > _farNativeFar : _plan.NearFar > _nearNativeFar;
            if (farExtended != _wasFarExtended)
            {
                _wasFarExtended = farExtended;
                Debug.Log($"{at} event={(farExtended ? "far-plane-extended" : "far-plane-native")} "
                    + (_hasFar ? $"far=[{_plan.FarNear:F0},{_plan.FarFar:F0}]" : $"nearFar={_plan.NearFar:F0}"));
            }
            bool atFloor = _fov <= _fovMin * 1.0001;
            if (atFloor != _wasFovFloor)
            {
                _wasFovFloor = atFloor;
                Debug.Log($"{at} event={(atFloor ? "fov-floor-reached" : "fov-floor-left")} fov={_fov:F4} craftRadius={KnownRadius:F1}");
            }
            bool atCeiling = _fov >= _fovMax * 0.9999;
            if (atCeiling != _wasFovCeiling)
            {
                _wasFovCeiling = atCeiling;
                Debug.Log($"{at} event={(atCeiling ? "fov-ceiling-reached" : "fov-ceiling-left")} fov={_fov:F3}");
            }

            var fo = FloatingOrigin.fetch;
            bool continuous = fo != null && fo.continuous;
            if (continuous != _wasContinuousOrigin)
            {
                _wasContinuousOrigin = continuous;
                Debug.Log($"{at} event={(continuous ? "origin-continuous-on" : "origin-continuous-off")}");
            }
            double frameVel = KrakensbaneFrameSpeed();
            bool kraken = frameVel > 0.0;
            if (kraken != _wasKrakensbane)
            {
                _wasKrakensbane = kraken;
                Debug.Log($"{at} event={(kraken ? "krakensbane-on" : "krakensbane-off")} frameVel={frameVel:F1}");
            }

            var pqs = _mount.Site.Body.pqsController;
            int pqsState = pqs == null ? 0 : (pqs.isActive ? 2 : 1);
            if (pqsState != _pqsState)
            {
                if (_pqsState >= 0 || pqsState != 2)
                    Debug.Log($"{at} event=station-pqs-{(pqsState == 2 ? "active" : pqsState == 1 ? "inactive" : "absent")} "
                        + "(inactive means the station's surroundings draw from scaled space only)");
                _pqsState = pqsState;
            }

            if (now - _originShiftWindowAt >= 1f)
            {
                _originShiftWindowAt = now;
                _originShiftLogsThisSecond = 0;
            }

            if (now - _statusAt >= StatusLogSeconds)
            {
                _statusAt = now;
                double elev = CommandCameraMath.ElevationDeg(fwd, localUp);
                Debug.Log($"{at} event=status elev={elev:F2} fov={_fov:F4} craftRadius={KnownRadius:F1} "
                    + $"split={_plan.Split} occluded={Occluded} "
                    + (_hasFar ? $"far={(_plan.FarRender ? $"[{_plan.FarNear:F0},{_plan.FarFar:F0}]" : "skipped")} mid={(_plan.MidActive ? $"[{_plan.MidNear:F0},{_plan.MidFar:F0}]" : "off")} " : "")
                    + (_subpixelActive && _subpixel != null ? $"subpixelEnergy={_subpixel.LastEnergy:G3} subpixelCoverPx={_subpixel.LastCoverage:G3} subpixelPeak={_subpixel.LastPeak:G3} " : "")
                    + $"nearFar={_plan.NearFar:F0} originShifts={_originShifts} scaledSkewMaxPx={_scaledSkewMaxPx:F2} scaledSkewFrames={_scaledSkewFrames} "
                    + $"trueOffsetPx={(TrueOffsetPx.HasValue ? TrueOffsetPx.Value.ToString("F2", CultureInfo.InvariantCulture) : "none")} "
                    + $"forecast={_forecastCount} forecastMs={_forecastMs:F2} forecastSamples={_forecast.LastSamples} "
                    + $"occlusionMs={_occlusionMs:F3} maskBins={_occlusion.MaskBinsBuilt} maskFallbacks={_occlusion.MaskFallbacks} "
                    + (_vantageSource != null ? $"vantageSource={_vantageSource} " : "")
                    + $"stationFromOriginM={_stationFromOrigin:F0} "
                    + $"alt={TargetAltitude(target):F0} body={_targetBody}");
                _originShifts = 0;
                _scaledSkewMaxPx = 0;
                _scaledSkewFrames = 0;
            }
        }

        private double TargetAltitude(DVec3 target)
        {
            var b = FlightGlobals.GetBodyByName(_targetBody ?? "");
            return b != null ? (target - ToD(b.position)).Magnitude - b.Radius : double.NaN;
        }

        /* Krakensbane's singleton is private and its static accessor throws
           before it exists. */
        private static double KrakensbaneFrameSpeed()
        {
            try { return Krakensbane.GetFrameVelocity().magnitude; }
            catch (NullReferenceException) { return 0.0; }
        }

        private void OnOriginShift(Vector3d offset, Vector3d nonFrame)
        {
            _originShifts++;
            var fo = FloatingOrigin.fetch;
            if (fo != null && fo.continuous) return; // continuous mode: counted in the status line only
            /* KSP fires zero-offset shifts constantly; only a real move is worth a line. */
            if (offset.magnitude < 0.05) return;
            if (_originShiftLogsThisSecond >= MaxOriginShiftLogsPerSecond) return;
            _originShiftLogsThisSecond++;
            Debug.Log($"{Tag} cam={_flightId} UT={F(Planetarium.GetUniversalTime())} d={_lastDistance:F0} "
                + $"event=origin-shift offset={offset.magnitude:F1}");
        }

        public void Dispose()
        {
            GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
            GameEvents.onVesselPersistentIdChanged.Remove(OnMerged);
            _marker?.Dispose();
            _marker = null;
            _utStripe?.Dispose();
            _utStripe = null;
            _standIn?.Dispose();
            _standIn = null;
            if (_midCam != null) UnityEngine.Object.Destroy(_midCam.gameObject);
            _midCam = null;
            _subpixel?.Dispose();
            _subpixel = null;
            _mount.DestroyAnchor();
            Debug.Log($"{Tag} cam={_flightId} rig down");
        }

        private static string F(double ut) => ut.ToString("F2", CultureInfo.InvariantCulture);
        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
        private static DVec3 ToD(Vector3 v) => new DVec3(v.x, v.y, v.z);
        private static Vector3 ToF(DVec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
    }
}
