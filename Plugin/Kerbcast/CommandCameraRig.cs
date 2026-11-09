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
        /* The usual rendered frame, for the zoom and the slew; learned in PrepareRender, or in Poll while nothing renders, never from Poll between frames. */
        private readonly FrameStep _frames = new FrameStep(FovSnapAfter);
        private const float StatusLogSeconds = 1.0f;
        private const float SkyProbeSeconds = 10.0f;
        private const double ModelProxyDistance = 50000.0;
        /* Float steps of the lens's position a pixel spans at the stand-in model. */
        private const double ModelProxyMinUlps = 4.0;
        private const int TerrainSamples = 48;
        internal const int TerrainSampleCount = TerrainSamples;
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
        private readonly Func<double, int, double> _keptToFrame;
        private CelestialBody _keptFrame;
        private readonly DrawnSubject _drawn;
        private readonly CommandAuthority _authority;
        private readonly CommandTrackEventRing _events = new CommandTrackEventRing(EventCapacity);
        private CommandCameraOcclusion _occlusion;
        private CommandCameraForecast _forecast;
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
        private Func<double> _fovMinSource;
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
        private int _subpixelWindow = CommandCameraSubpixel.WindowPx;
        private double _subpixelAcross;

        /* Deep-space optics: null with the setting off or no shaders for it. */
        private Func<OpticsLens> _opticsLens;
        private readonly CommandCameraOptics _optics;
        private readonly SeeingNoise _seeingNoise;
        private OpticsLens _lens;
        private float _lensCheckedAt = float.MinValue;
        private OpticsFrame _opticsFrame;
        private OpticsReading _opticsReading;
        private double _opticsBlurPx;
        private bool _opticsBegun;
        private bool _opticsLogged;
        private bool _opticsWasActive;
        private string _opticsLoggedLens;
        private OpticsKnobs _opticsLoggedOver;
        private int _opticsLoggedBucket;
        private bool _opticsLoggedAir;
        private float _opticsLoggedAt = float.MinValue;

        private CommandClipPlan _plan;
        private double _fov;
        /* The subject's framing the zoom and the bracket share; reset while the view frames no subject, and for a new subject, aim or vantage. */
        private readonly SubjectFraming _framing = new SubjectFraming();
        private CommandAim _framingAim;
        private uint _framingSubject;
        private uint _framingVantage;
        private int _framingSite;
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
        /* The UT Occluded last turned on, NaN while it is off. */
        public double OccludedSinceUt { get; private set; } = double.NaN;
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
        private CelestialBody _drawnAbout;
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
        /* No subject to aim at yet: the view holds its placeholder (see SubjectWait). */
        public bool WaitingForTarget => _placeholder != PlaceholderAim.None;
        private PlaceholderAim _placeholder;
        /* What the view followed last solve, and the last subject aim, body-fixed, that a Hold keeps. */
        private SubjectAimMode _mode;
        private readonly SubjectWait _wait = new SubjectWait();
        private AimKey _waitKey;
        private CelestialBody _heldBody;
        private double _heldLat, _heldLon, _heldAlt;
        /* On truth with a light speed, how the aim placed its subject this solve, steadied across picture flicker. */
        private readonly TruthPlaceFilter _truthPlace = new TruthPlaceFilter();
        private TruthAimPlace _placed = TruthAimPlace.Source;
        private AimKey _placeKey;
        /* The last Drawn place a Kept solve aims at: the body it hangs from, its offset from there in KSP's fixed frame, its own body and instant. */
        private CelestialBody _keptAbout;
        private CelestialBody _keptBody;
        private DVec3 _keptOffset;
        private double _keptUt;
        /* The body a Hold is taken against when it is not the aim's own: the one the drawn place hangs from. */
        private CelestialBody _holdAbout;
        private bool _waitingLogged;
        public CommandTrackEventRing Events => _events;
        public string AuthorityOwner => _authority != null ? _authority.Owner : null;
        internal CommandFeedOrders CurrentOrders => _authority != null && _authority.Registered ? _authority.Orders(_flightId) : null;
        public string StationNodeName => _mount.Site.NodeName;
        /* Counts the cuts that moved the lens to another site, so every key that notes "the vantage changed" sees a station-to-station move too. */
        private int _siteGeneration;
        /* The next frame snaps its framing and zoom instead of easing them from the last site's. */
        private bool _snapNext;
        /* The site's label as the feed shows it: a network vantage's own label, else the site's name. */
        public string SiteLabel => _siteLabel ?? StationDisplayName;
        private string _siteLabel;
        /* The tracking network that moves this lens between its vantages; null for a camera at one site. */
        internal CommandCameraNetwork Network { get; private set; }
        internal CommandCameraSite Site => _mount.Site;
        /* The widest field the lens opens to, in degrees. */
        internal double FovMax => _fovMax;
        public string StationDisplayName =>
            string.IsNullOrEmpty(_mount.Site.DisplayName) ? _mount.Site.NodeName : _mount.Site.DisplayName;

        /* Where the camera points; null for its default craft. */
        public CommandAim Aim => _aim;
        internal Orbit AimOrbit => _aimOrbit;
        internal double HeldFov => _heldFov;
        internal double ShownFov => _fov;

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
        /* The UT the subject's place was read at, which a place on the lens's own ground takes at render UT, not at _subjectUt. */
        private double _subjectPlacedUt = double.NaN;

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
            s.WaitingForTarget = WaitingForTarget;
            s.SiteKey = _mount.Site.NodeName;
            s.SiteLabel = SiteLabel;
            if (Network != null) s.NetworkState = Network.NoView ? CommandFrameState.NetworkNoView : CommandFrameState.NetworkOk;
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
            _heldFov = aim?.FovDeg ?? double.NaN;
            uint subject = aim?.SubjectPersistentId ?? 0;
            if (subject != _selection.ChosenPersistentId) _selection.Choose(subject);
            Debug.Log($"{Tag} cam={_flightId} aim {(aim == null ? "default (the default craft)" : aim.ToString())}");
        }

        /* Holds the lens at `fovDeg` degrees, within its limits, until the next aim; NaN returns it to the aim's own framing. */
        internal void SetFov(double fovDeg)
        {
            _heldFov = fovDeg;
            Debug.Log($"{Tag} cam={_flightId} fov {(double.IsNaN(fovDeg) ? "back to the aim's framing" : $"held at {fovDeg:G4} deg")}");
        }

        private bool _galaxyLogged;
        private bool _galaxyOwn;

        public Color? GalaxyTint()
        {
            if (!_poseValid) return null;
            var tint = GalaxyForLens.Tint(_mount.Site.Body, _poseEye, _poseForward, _fov, out double fade);
            bool own = tint.HasValue;
            if (!_galaxyLogged || own != _galaxyOwn)
            {
                _galaxyLogged = true;
                _galaxyOwn = own;
                Debug.Log($"{Tag} cam={_flightId} event=galaxy-fade {(own ? $"at the lens fade={fade:F2}" : "as stock (the lens is near the main camera)")}");
            }
            return tint;
        }

        public float FovFloor => (float)FovMinNow();

        /* A held zoom narrows or widens by this factor each second at full rate, the same pace at every field. */
        private const double ZoomFactorPerSecond = 4.0;
        private float _zoomRate;

        public void ViewerZoomRate(float rate)
        {
            if (rate != 0f && _authority != null && _authority.Registered)
            {
                if (_zoomRate == 0f) return;
                rate = 0f;
            }
            if (rate == _zoomRate) return;
            if (rate != 0f && _zoomRate == 0f) Debug.Log($"{Tag} cam={_flightId} viewer zoom {(rate > 0 ? "in" : "out")} at rate {rate:F2}");
            _zoomRate = rate;
        }

        /* Through the facade, so a viewer is refused while a command authority holds the lock. */
        public void ViewerFov(float fov)
        {
            var result = KerbcastControl.SetFovCore(_flightId, fov, null);
            if (result != Kerbcast.Api.AimResult.Ok) Debug.Log($"{Tag} cam={_flightId} viewer fov {fov:G4} deg refused: {result}");
        }

        private CommandAim _aim;
        private Orbit _aimOrbit;
        private DVec3 _lastAimPoint;
        /* The field a client set, unclamped; NaN while the aim frames itself. */
        private double _heldFov = double.NaN;

        /* A Direction or Surface aim: no craft of its own, so no craft sizes its view. */
        private bool AimIsPoint => _aim != null && (_aim.Kind == CommandAimKind.Direction || _aim.Kind == CommandAimKind.Surface);

        /* The narrowest field now: the lens's zoom limit, or with the optics drawn where their blur toward the aim spans OpticsModel.FloorBlurPx, when that is wider. */
        private double FovMinNow()
        {
            double lens = LensFovMinNow();
            return double.IsNaN(_opticsFloor) ? lens : Math.Max(lens, _opticsFloor);
        }

        /* The frame's height at full quality, which adaptive quality never changes. */
        private int FullRows => _cam.MaxHeight;

        /* The optics floor eases toward the blur's, so seeing that changes with the aim's elevation never pumps the zoom. */
        private const double OpticsFloorTau = 1.0;
        private double _opticsFloor = double.NaN;
        private float _opticsFloorAt = -1f;

        private void UpdateOpticsFloor(double blurArcsec, float now)
        {
            /* FullRows is the provider's FrameRows, which its lens limit counts in too. */
            double target = OpticsModel.OpticsFovFloor(0, blurArcsec, FullRows);
            if (!(target > 0))
            {
                _opticsFloor = double.NaN;
                return;
            }
            double dt = _opticsFloorAt < 0 ? double.MaxValue : now - _opticsFloorAt;
            _opticsFloorAt = now;
            _opticsFloor = double.IsNaN(_opticsFloor) ? target : CommandCameraMath.SmoothFov(_opticsFloor, target, dt, OpticsFloorTau, FovSnapAfter);
        }

        /* The lens limit can change mid-scene (a Tracking Station upgrade), so
           it is re-read once a second. */
        private double LensFovMinNow()
        {
            float now = Time.realtimeSinceStartup;
            if (now - _fovMinCheckedAt < 1f) return _fovMin;
            _fovMinCheckedAt = now;
            double next = _fovMinSource();
            if (next != _fovMin)
            {
                Debug.Log($"{Tag} cam={_flightId} lens limit {_fovMin:G4} -> {next:G4} deg");
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

        /* The subject's framing sphere centre from its CoM, eased; the view looks there, plus any handover offset still easing, while everything else keeps the CoM. */
        private DVec3 _viewOffset;
        private DVec3 ViewOffset => _mode == SubjectAimMode.Subject && AimIsCraft ? _viewOffset + HandoverOffset : default;
        /* The framing the last rendered frame used, for the status log. */
        private Shape _shape;

        /* A craft aim's subject as its source frames it: a sphere off its CoM, when the source knows its shape. */
        private struct Shape
        {
            public bool Known;
            public DVec3 Centre;
            public double Half;
        }

        private Shape SubjectShape()
        {
            var shape = new Shape();
            if (_mode == SubjectAimMode.Subject && AimIsCraft)
                shape.Known = _known.SubjectShape(out shape.Centre, out shape.Half)
                    && CommandCameraMath.Finite(shape.Centre) && CommandCameraMath.Finite(shape.Half);
            return shape;
        }

        /* What the view and the bracket frame at `distance` from the CoM: the shape when known, otherwise a sphere of FramingRadius about the CoM. */
        private void Framed(double distance, Shape shape, out double radius, out double range)
        {
            radius = shape.Known ? shape.Half + _knownUncertainty + FramingMargin : FramingRadius;
            range = shape.Known ? distance + DVec3.Dot(shape.Centre, _solveForward) : distance;
        }

        private bool _blankLogged;
        private string _loggedBlank;
        private string _loggedAim;

        private CommandCameraMarker _marker;
        private CommandCameraUtStripe _utStripe;
        private double _frameUt;
        /* The model wins over the marker when both are set. */
        private readonly bool _wantModel;
        private CommandCameraTargetModel _standIn;
        /* The model stood in for the subject last frame. */
        private bool _modelShown;
        private uint _modelFailedPid;
        private Composition _modelFailedParts;
        /* The drawn craft's body, or the aim's without one, for the scaled skew log. */
        private CelestialBody _frameBody;

        public CommandCameraRig(KerbcastCamera cam, CommandCameraMount mount, SubjectSelection selection, IFixSource known,
            DrawnSubject drawn,
            CommandAuthority authority, double frameFraction, Func<double> fovMin, double fovMax, bool targetMarker, bool targetModel,
            bool utStripe, Func<OpticsLens> opticsLens = null)
        {
            _cam = cam;
            _slew = new AimSlew(_frames);
            _mount = mount;
            _selection = selection;
            _known = known;
            _keptToFrame = KeptToFrame;
            _drawn = drawn;
            _authority = authority;
            _wantModel = targetModel;
            _flightId = cam.FlightId;
            _frameFraction = frameFraction;
            _fovMinSource = fovMin;
            _fovMin = fovMin();
            _fovMinCheckedAt = Time.realtimeSinceStartup;
            _fovMax = fovMax;
            /* The field the camera was built at: its lens's widest, or the one a rebuild carried over. */
            _fov = cam.Fov;
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
            _opticsLens = opticsLens;
            if (opticsLens != null)
            {
                _optics = CommandCameraOptics.Create(cam.FlightId);
                _seeingNoise = new SeeingNoise(unchecked((int)cam.FlightId));
            }

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
        /* The local layers' far planes as they were before any plan stretched them. */
        public float LocalFarNativeM => Mathf.Max(_nearNativeFar, _hasFar ? _farNativeFar : 0f);

        /* The mid layer's far plane this frame, 0 when it does not render. */
        public float MidFarNowM => _midCam != null && _plan.MidActive ? _midCam.farClipPlane : 0f;

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
            NoteWaiting(ut);
            if (!ok) ForgetHandover();
            if (!ok || _mode != SubjectAimMode.Subject)
            {
                /* Without a fix the readings at the aim describe nothing; kept, they would carry the last solve, perhaps truth from before a lock, into the reports. */
                HasFix = false;
                Occluded = false;
                Blocker = "";
                Sunlit = false;
            }
            if (ok)
            {
                /* The pose turns onto a moved aim rather than snapping; everything
                   projected through the frame (TrueInFrame, anchors) uses it. */
                bool jumped = AimJumped(ut, out bool changed);
                double clock = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                bool handed = EaseHandover(jumped && !changed, changed, double.IsNaN(_slewAt) ? double.MaxValue : clock - _slewAt,
                    double.IsNaN(_slewUt) ? double.NaN : ut - _slewUt, out bool dropped);
                if (changed)
                {
                    /* A new subject or aim starts on its own framing, not eased from the last one's; a publish eases it on. */
                    var shape = SubjectShape();
                    _viewOffset = shape.Known ? shape.Centre : default;
                }
                var toward = aim + ViewOffset - station;
                double range = toward.Magnitude;
                /* A publish the handover eases is no jump for the slew: the eased aim moves on smoothly. An offset it dropped is one. */
                var view = range > 0
                    ? _slew.Step(toward * (1.0 / range), double.IsNaN(_slewAt) ? double.MaxValue : clock - _slewAt,
                        double.IsNaN(_slewUt) ? 0 : ut - _slewUt, (jumped && !handed) || dropped, _fov)
                    : toward;
                _slewAt = clock;
                _slewUt = ut;
                NoteSlew(ut, now);
                CommandCameraMath.AimBasis(station, station + view * range, localUp, north, out _solveForward, out _solveUp);
                _solveEye = station;
            }
            Framed(RangeM, SubjectShape(), out double fitRadius, out double fitRange);
            FramingFits = !HasFix || CommandCameraMath.FramingFits(fitRadius, fitRange, _fovMax);
            Blank = CommandCameraMath.BlankReason(governed, timedOut, orders?.Blank, VantageLost, _authority?.SilentText);
            if (Blank == null && Network != null && Network.NothingPlaceable) Blank = CommandCameraMath.BlankNoVantage;
            UpdateBracket(now);
            NoteGovernance(ut, governed);
            OccludedSinceUt = CommandCameraMath.OccludedSince(Occluded, OccludedSinceUt, ut);
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
            _aimedBody = null;
            _placeholder = PlaceholderAim.None;
            _mode = SubjectAimMode.Subject;
            ForgetDrawn();
            var site = _mount.Site;
            Vector3d vantage = default;
            string vantageSource = null;
            /* A solve that places no subject breaks the run a Kept place needs. */
            if (site.HasVantage && !site.TryVantagePosition(ut, out vantage, out vantageSource))
            {
                _truthPlace.Reset();
                return false;
            }
            var body = site.Body;
            if (body == null)
            {
                _truthPlace.Reset();
                return false;
            }

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

            var subject = PlacedSubject(ut, station, body);
            _knownUncertainty = subject.Positioned && subject.UncertaintyM > 0 ? subject.UncertaintyM : 0.0;
            _subjectRead = true;
            _subjectState = subject.State;
            _subjectRefusalCode = subject.RefusalCode;
            _subjectRefusalText = subject.RefusalText;
            CelestialBody aimBody;
            bool aimed = !AimIsCraft;
            if (!aimed)
            {
                _mode = _wait.Step(WaitKeyChanged(), subject.Positioned);
                if (_mode != SubjectAimMode.Subject)
                {
                    /* With nothing to aim at the view renders on, at its
                       default framing before the subject is first known and
                       at its last aim after, so the feed shows the scene (as
                       its light shows it) rather than freezing on a last frame
                       it may never have had. */
                    ForgetDrawn();
                    if (_mode == SubjectAimMode.Hold)
                    {
                        if (_heldBody == null) return false;
                        aim = ToD(_heldBody.GetWorldSurfacePosition(_heldLat, _heldLon, _heldAlt));
                    }
                    else
                    {
                        _placeholder = CommandCameraMath.PlaceholderFor(site.HasVantage, site.HasLaunchPoint);
                        aim = PlaceholderPoint(_placeholder, body, station, localUp);
                    }
                    _lastAimPoint = aim;
                    _lastDistance = (aim - station).Magnitude;
                    _frameBody = body;
                    return true;
                }
                aim = subject.World;
                aimBody = subject.Body;
                if (aimBody != null)
                {
                    _aimedBody = aimBody;
                    _aimedAbout = KinematicsWorld.Now().Zup.FromWorld(aim - ToD(aimBody.position));
                    _aimedAt = _knownDrawnAt;
                    var w = new Vector3d(aim.X, aim.Y, aim.Z);
                    /* Against the body the place hangs from, so a Hold stays where the craft was last drawn rather than riding a body drawn elsewhere. */
                    _heldBody = _holdAbout ?? aimBody;
                    _heldLat = _heldBody.GetLatitude(w);
                    _heldLon = _heldBody.GetLongitude(w);
                    _heldAlt = _heldBody.GetAltitude(w);
                }
            }
            else if (_aim.Kind == CommandAimKind.Direction)
            {
                double range = subject.Positioned ? (subject.World - station).Magnitude : DirectionDefaultRangeM;
                aim = DirectionPoint(body, station, localUp, _aim.Azimuth, _aim.Elevation, range);
                aimBody = body;
            }
            else if (!ResolveAim(_aim, _aimOrbit, ut, station, body, out aim, out aimBody))
            {
                ForgetDrawn();
                return false;
            }
            _lastAimPoint = aim;

            long occT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _occlusion.UseSite(site);
            /* An aim at the picture's place, drawn or kept, is read against the bodies where the picture draws them, as it shows the craft; with a light speed a Surface, Orbit or PositionAt aim is at the picture's place too. */
            bool aimDrawn = aimed ? _aim.Kind != CommandAimKind.Direction && LightTime.LightSpeed.HasValue : AimFromPicture;
            string occluderSource;
            occluder = aimDrawn
                ? _occlusion.FindDrawnOccluder(_cam.Picture, station, aim, out occluderSource, aimed ? 0 : KnownRadius)
                : _occlusion.FindOccluder(station, aim, out occluderSource, aimed ? 0 : KnownRadius);
            double distance = (aim - station).Magnitude;
            _lastDistance = distance;
            HasFix = true;
            RangeM = distance;
            ElevationDeg = CommandCameraMath.ElevationDeg(aim - station, localUp);
            Occluded = occluder != null;
            Blocker = occluder ?? "";
            UpdateSunlit(ut, distance, aim, aimDrawn);

            SolveDrawn(station, aim, distance, !aimed && _known.KnowsWhatIsDrawn, aimDrawn, aimBody);
            double occMs = (System.Diagnostics.Stopwatch.GetTimestamp() - occT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _occlusionMs = _occlusionMs > 0 ? _occlusionMs + OcclusionMsAlpha * (occMs - _occlusionMs) : occMs;

            NoteTargetAndOcclusion(ut, distance, aimBody, DrawnOrAim(aim), occluder, occluderSource);
            return true;
        }

        private readonly AimHandover _handover = new AimHandover();
        /* The handover's offset in world axes this frame. */
        private DVec3 HandoverOffset => _handover.Easing ? KinematicsWorld.Now().Zup.ToWorld(_handover.Offset) : default;
        /*
         * The craft aim's body and its place about that body at the last
         * solve, in KSP's fixed frame, and the UT it was read at; null body
         * when the last solve aimed at no craft. The fixed frame, because the
         * world's axes turn with the body below its inverse-rotation height,
         * which moves a place 600 km about Kerbin by metres a frame.
         */
        private CelestialBody _aimedBody;
        private DVec3 _aimedAbout;
        private CelestialBody _handoverBody;
        private DVec3 _handoverAbout;
        private double _handoverUt;
        /* The render UTs the aim and the handover's place were carried round to with the lens's body (PastInstant.Turn). */
        private double _aimedAt, _handoverAt;

        /*
         * Eases the aim across a world model publish for a craft aim on the
         * same body: the old record's place at the last instant against the
         * new record's at that same instant, both about the body in its fixed
         * frame, so the body's motion, its turn and any origin shift drop out.
         * Where the publish also moved the placed instant by more than the
         * frame's UT step `utStep` (a record read back only to its cap), the
         * new place at the new instant is taken instead, so the whole move
         * drawn eases. A correction too wide to ease is left to the slew
         * instead, and any other cause of a jump resets it. Steps the ease by
         * `dt` real seconds (a usual frame at most) and returns whether this
         * publish was eased; `dropped` says an offset still easing was let
         * go, which the slew must take as a jump.
         */
        private bool EaseHandover(bool published, bool changed, double dt, double utStep, out bool dropped)
        {
            bool craft = _mode == SubjectAimMode.Subject && AimIsCraft && _aimedBody != null;
            bool handed = false;
            bool easing = _handover.Easing;
            if (!craft || changed || _handoverBody != _aimedBody) _handover.Reset();
            else if (published)
            {
                double widest = AimHandover.WidestRad(_fov * Math.PI / 180.0, AimSlew.MaxRateDegPerS * Math.PI / 180.0);
                if (AimHandover.InstantStepped(_subjectPlacedUt - _handoverUt, utStep))
                    handed = _handover.Handover(_handoverAbout, _aimedAbout, _lastDistance, widest);
                else
                {
                    var then = Turned(_known.SubjectAlong(_handoverUt), _lensSpin, _handoverUt, _handoverAt);
                    if (then.Positioned && then.Body == _aimedBody)
                    {
                        var after = KinematicsWorld.Now().Zup.FromWorld(then.World - ToD(_aimedBody.position));
                        handed = _handover.Handover(_handoverAbout, after, _lastDistance, widest);
                    }
                }
            }
            dropped = easing && !_handover.Easing && !changed;
            double floor = _lastDistance * AimSlew.MinOffsetFieldFraction * _fov * Math.PI / 180.0;
            _handover.Step(_frames.Clip(dt), AimHandover.TauS, AimSlew.SnapAfterS, floor);
            _handoverBody = craft ? _aimedBody : null;
            _handoverAbout = _aimedAbout;
            _handoverAt = _aimedAt;
            _handoverUt = _subjectPlacedUt;
            return handed;
        }

        private void ForgetHandover()
        {
            _handover.Reset();
            _handoverBody = null;
        }

        private readonly AimSlew _slew;
        /* Real seconds, double, of the last slew step; NaN before the first. */
        private double _slewAt = double.NaN;
        /* UT of the last slew step; NaN before the first. */
        private double _slewUt = double.NaN;
        private bool _slewLogged;
        /* A world model publishing at physics rate can start a slew on every tick, so lines come at most once per SlewLogSeconds and the rest are counted into the next. */
        private const float SlewLogSeconds = 5f;
        private float _slewLogAt = float.NegativeInfinity;
        private bool _slewPending;
        private int _slewQuiet;
        private double _slewQuietMaxDeg;
        private CommandAim _jumpAim;
        private uint _jumpSubject, _jumpVantage;
        private int _jumpSite;
        private long _jumpGeneration;
        private SubjectAimMode _jumpMode;

        /* Whether the subject, the aim or the vantage changed since the last solve, which makes a craft aim wait for its subject again. */
        private bool WaitKeyChanged() => _waitKey.Changed(_aim, _known.SubjectPersistentId, _mount.Site.VantagePersistentId, _siteGeneration);

        /* The aim, subject and vantage a solve was keyed on, to tell when one of them changes. */
        private struct AimKey
        {
            private CommandAim _aim;
            private uint _subject, _vantage;
            private int _site;

            public bool Changed(CommandAim aim, uint subject, uint vantage, int site)
            {
                bool changed = !CommandAim.SameAs(aim, _aim) || subject != _subject || vantage != _vantage || site != _site;
                _aim = aim;
                _subject = subject;
                _vantage = vantage;
                _site = site;
                return changed;
            }
        }

        /*
         * Whether the aim point jumped since the last solve, as its causes say
         * rather than as its motion suggests: a new aim, a new subject, a new
         * vantage, a new world-model publish that may correct where the
         * subject is, or the view taking or leaving a placeholder or a held aim.
         * Between these the view follows the aim, plus the framing and
         * handover offsets, exactly. `changed` is every cause but the publish.
         */
        private bool AimJumped(double ut, out bool changed)
        {
            uint subject = _known.SubjectPersistentId;
            uint vantage = _mount.Site.VantagePersistentId;
            var store = _authority != null ? _authority.WorldModel : null;
            var snap = store != null ? store.Capture(Time.frameCount, ut).Snapshot : null;
            long generation = snap != null ? snap.Generation : 0;
            changed = !CommandAim.SameAs(_aim, _jumpAim) || subject != _jumpSubject || vantage != _jumpVantage
                || _siteGeneration != _jumpSite || _mode != _jumpMode;
            bool jumped = changed || generation != _jumpGeneration;
            _jumpMode = _mode;
            _jumpAim = _aim;
            _jumpSubject = subject;
            _jumpVantage = vantage;
            _jumpSite = _siteGeneration;
            _jumpGeneration = generation;
            return jumped;
        }

        private void NoteSlew(double ut, float now)
        {
            bool slewing = _slew.Slewing;
            bool changed = slewing != _slewLogged;
            _slewLogged = slewing;
            double offsetDeg = _slew.OffsetRad * 180.0 / Math.PI;
            bool due = now - _slewLogAt >= SlewLogSeconds;
            if (changed && !due)
            {
                _slewPending = true;
                if (slewing)
                {
                    _slewQuiet++;
                    _slewQuietMaxDeg = Math.Max(_slewQuietMaxDeg, offsetDeg);
                }
                return;
            }
            if (!changed && !(due && _slewPending)) return;
            _slewLogAt = now;
            _slewPending = false;
            string quiet = _slewQuiet > 0 ? $" unloggedStarts={_slewQuiet} unloggedMaxOffsetDeg={_slewQuietMaxDeg:F3}" : "";
            _slewQuiet = 0;
            _slewQuietMaxDeg = 0;
            if (changed)
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} event={(slewing ? "slew-start" : "slew-end")}"
                    + (slewing ? $" offsetDeg={offsetDeg:F3}" : "") + quiet);
            else
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} event=slews slewing={slewing}{quiet}");
        }

        private void NoteWaiting(double ut)
        {
            bool waiting = WaitingForTarget;
            if (waiting == _waitingLogged) return;
            _waitingLogged = waiting;
            Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} event={(waiting ? $"waiting-for-target placeholder={_placeholder}" : "waiting-end")}");
        }

        /* Clears the drawn craft, so a failed solve leaves no stale picture readings. */
        private void ForgetDrawn() => _drawnOk = false;

        /* The lens is placed for the camera's picture pass too, so other craft
           and anchors are drawn from the same lens. It keeps its solves to
           itself: the known subject is solved from the station with no spread,
           and the drawn one must be solved the same way to agree with it. */
        private void LocateDrawn(double ut, DVec3 station, CelestialBody lensBody)
        {
            var lens = LensAt(ut, station, lensBody);
            _cam.Picture.Place(lens);
            _drawnOk = _drawn.TryLocate(lens, out _drawnPos, out _drawnBody);
            _drawnAbout = _drawn.ShownAbout;
            if (!LightTime.LightSpeed.HasValue)
            {
                /* Said once, so a light speed that went away is not read as a state that never changed. */
                if (_drawnLight != -1) Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} event=drawn-light state=off");
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

        private PictureLens LensAt(double ut, DVec3 station, CelestialBody lensBody) => new PictureLens
        {
            Key = PictureLens.OwnKey(_flightId),
            Ut = ut,
            Point = station,
            Body = lensBody,
            Rides = _mount.Site.VantagePersistentId,
        };

        /* Whether the drawn craft was last none, live or at its past instant (0, 1, 2), for the log. */
        private int _drawnLight = -1;
        private float _lightLoggedAt = float.MinValue;
        private const float LightLogSeconds = 5f;

        /* A kept record's speed about its body, bounded in the frame the solve now runs in; NaN for a body the game no longer has. */
        private double KeptToFrame(double ownSpeed, int bodyIndex)
        {
            var body = KinematicsWorld.Now().Body(bodyIndex);
            return body != null && _keptFrame != null ? CommandCameraForecast.SpeedBound(ownSpeed, body, _keptFrame) : double.NaN;
        }

        /*
         * The subject as the source knows it at its own estimate of the past
         * instant: the latest root of the light-time equation on the source's
         * positions, never the picture's, in the frame of the common ancestor
         * of the lens's body and the subject's, as the picture solves it. Render
         * UT with no light speed. A source that places the subject only from
         * a later UT (a world model's record from its ValidFromUt) is read back
         * along that record's own path, as it predicts the craft at the
         * instant, so a far lens follows the craft between publishes: no
         * further back than PastInstant.MaxReadBackS and no lower than the
         * ground under its start (or the start itself, when that is lower or
         * the craft rests there). Further back the records the source kept
         * place it as the model published them for the instant, so a lens
         * whose light time is far longer than a record's age still aims where
         * the picture draws the craft; it is held only past the earliest
         * kept record's read-back. Only where it cannot be read
         * back is the subject taken as it is placed from then. Otherwise a source that cannot be solved back to that
         * instant has no fix. A world model's place on the lens's own
         * ground stands where the source puts it now, at the instant of its
         * light time from there; that instant steps back whenever the range
         * grows faster than light. Otherwise the instant never steps back while
         * the subject stays the same, except from a record's start to the root
         * when a republished record starts earlier: that start's light never
         * arrived, so nothing holds it.
         *
         * Near the lens's body the subject's path is carried round with the
         * body to render UT and the subject placed there (PastInstant.Turn),
         * as the picture draws it, so a craft lifting off stays over its own
         * ground and the aim stays on the drawn craft.
         */
        private CraftFix KnownSubject(double ut, DVec3 station, CelestialBody lensBody)
        {
            _subjectUt = ut;
            _subjectPlacedUt = ut;
            double? c = LightTime.LightSpeed;
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
            _lensSpin = SpinOf(lensBody);
            _knownDrawnAt = LensBodyDrawnAt(ut, station, lensBody);
            bool moves = _known.SubjectMotion(ut, out var motion, out double validFrom, out _) && motion.Body != null;
            if (moves && motion.Kind == BodyMotionKind.Surface && motion.Body == lensBody && !double.IsInfinity(validFrom))
            {
                /* A world model's place on the lens's own ground does not move
                   against the lens, so the light time changes only the instant and
                   its uncertainty, never where the subject stands: placed as the
                   source puts it now. On another body the picture draws the craft
                   at its past inertial place, so the solve below matches that. */
                _knownHold.Reset();
                double fixedAt = PastInstant.BodyFixedInstant((station - now.World).Magnitude, ut, c.Value, validFrom);
                var then = _known.Subject(fixedAt);
                if (then.Positioned) now.UncertaintyM = then.UncertaintyM;
                _subjectUt = fixedAt;
                /* The picture draws that ground turned back by its light time, and a craft on it with it. */
                if (!_known.KnowsWhatIsDrawn && AimOnDrawnBody(lensBody, now.World, out var onDrawnGround)) now.World = onDrawnGround;
                return now;
            }
            /* The frame from the body the subject is at by now, then again from
               its body at the solved instant, as the picture chooses it. */
            int frame = PastInstant.CommonAncestor(ref tree, lensIndex, now.Body.flightGlobalsIndex);
            double firstTr = double.NaN;
            int firstFrame = -1;
            for (int pass = 0; pass < 2; pass++)
            {
                var frameBody = tree.Body(frame);
                if (frameBody == null) break;
                var path = new KnownPath { Source = _known, Tree = tree, Frame = frame, Along = true, Spin = _lensSpin, T = ut };
                var windowed = path;
                windowed.Along = false;
                /* Read back before the record's start, the root lies no earlier than this while the craft is under half the light speed, and no read goes past the cap. */
                double kept = _known.SubjectKeptFromUt;
                double alongFrom = kept < validFrom ? kept : validFrom;
                double reach = Math.Max(PastInstant.ReadBackFloor(alongFrom), Math.Min(validFrom, ut - 2.0 * (station - now.World).Magnitude / c.Value));
                double from = double.IsInfinity(reach) ? validFrom : reach;
                double bound = moves ? CommandCameraForecast.SpeedBound(motion, frameBody, from, ut) : 0;
                /* The kept records bound the whole span the solve may search, each about its own body; a light time within the newest record's reach asks none. */
                if (moves && from < validFrom)
                {
                    _keptFrame = frameBody;
                    double keptBound = _known.SubjectKeptSpeedBound(PastInstant.ReadBackFloor(alongFrom), validFrom, _keptToFrame);
                    if (keptBound > bound) bound = keptBound;
                }
                /* A live place is never turned, so adds nothing to the bound. */
                if (moves && motion.Body == lensBody && !now.Live)
                {
                    double span = ut - from;
                    double lensDistance = (station - ToD(lensBody.position)).Magnitude;
                    bound += PastInstant.TurnSpeedBoundAbout(_lensSpin, lensIndex, bound, span,
                        (now.World - ToD(lensBody.position)).Magnitude + bound * span, lensDistance, c.Value,
                        CommandCameraForecast.BodySpeedBound(lensBody, frameBody));
                }
                var lens = station - ToD(frameBody.position);
                if (PastInstant.SolveAlong(ref path, ref windowed, lens, ut, c.Value, bound, validFrom, ut, out double tr, alongFrom) != PastStatus.Found)
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
                var then = _known.SubjectAlong(tr);
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

        /*
         * The subject the aim follows, and the picture placed from the same
         * lens. On truth with a light speed the picture decides first
         * (CommandCameraMath.TruthAimPlaceFor, steadied by TruthPlaceFilter):
         * a craft drawn in the past is aimed where and when it is drawn, with
         * no read back on truth, which knows the craft only as it is now; one
         * drawn live, or one the picture cannot judge, is solved on truth as
         * 1.11 did; one whose last light has already arrived has no fix. Truth
         * reads the subject before the picture so the shared track holds the
         * craft it resolved.
         */
        private CraftFix PlacedSubject(double ut, DVec3 station, CelestialBody body)
        {
            _placed = TruthAimPlace.Source;
            _holdAbout = null;
            _knownDrawnAt = ut;
            if (!_known.KnowsWhatIsDrawn || !LightTime.LightSpeed.HasValue)
            {
                _truthPlace.Reset();
                var known = KnownSubject(ut, station, body);
                LocateDrawn(ut, station, body);
                return known;
            }
            if (_placeKey.Changed(_aim, _known.SubjectPersistentId, _mount.Site.VantagePersistentId, _siteGeneration)) _truthPlace.Reset();
            var now = _known.Subject(ut);
            LocateDrawn(ut, station, body);
            var raw = CommandCameraMath.TruthAimPlaceFor(AimIsCraft, now.PersistentId != 0 && _drawn.PersistentId == now.PersistentId, _drawnOk, _drawn.ShownPast, _drawn.LightGone);
            _placed = _truthPlace.Step(raw, _drawnOk);
            switch (_placed)
            {
                case TruthAimPlace.Drawn:
                    _keptAbout = _drawnAbout ?? _drawnBody;
                    _keptBody = _drawnBody;
                    _keptOffset = KinematicsWorld.Now().Zup.FromWorld(_drawnPos - ToD(_keptAbout.position));
                    _keptUt = _drawn.ShownUt;
                    return AsPictured(now, _drawnPos, _drawnBody, _keptAbout, _drawn.ShownUt);
                case TruthAimPlace.Kept:
                    /* Held still about the body the last drawn place hangs from, as the picture holds an instant. */
                    return AsPictured(now, ToD(_keptAbout.position) + KinematicsWorld.Now().Zup.ToWorld(_keptOffset), _keptBody, _keptAbout, _keptUt);
                case TruthAimPlace.NoFix:
                    _knownHold.Reset();
                    now.State = CraftFixState.Listed;
                    now.Body = null;
                    return now;
                default:
                    var known = KnownSubject(ut, station, body);
                    double pathFrom = _known.SubjectPathFromUt;
                    if (known.Positioned && known.Body != null && _subjectUt < pathFrom && !CommandCameraMath.TruthPathHolds(_subjectUt, pathFrom, known.Body.flightGlobalsIndex,
                        HistoryBody(known.PersistentId, _subjectUt), HistoryBody(known.PersistentId, pathFrom - PathStartBeforeUt)))
                    {
                        /* Solved back past an SOI change into the conic truth gives (an unloaded craft): no fix rather than a place it never was. */
                        _knownHold.Reset();
                        known.State = CraftFixState.Listed;
                        known.Body = null;
                        return known;
                    }
                    if (_drawnOk && !_drawn.ShownPast && _drawn.PersistentId == known.PersistentId)
                    {
                        /* Drawn live: placed now, as the picture draws it. */
                        _subjectUt = _drawn.ShownUt;
                        _subjectPlacedUt = _drawn.ShownUt;
                    }
                    return known;
            }
        }

        /* `fix` placed at `world` about `body`, where the picture puts it at `placedUt`, with no read back on truth; a Hold after it is taken against `about`. */
        private CraftFix AsPictured(CraftFix fix, DVec3 world, CelestialBody body, CelestialBody about, double placedUt)
        {
            _knownHold.Reset();
            fix.State = CraftFixState.Positioned;
            fix.RefusalCode = null;
            fix.RefusalText = null;
            fix.World = world;
            fix.Body = body;
            fix.Live = false;
            _holdAbout = about;
            _subjectUt = placedUt;
            _subjectPlacedUt = placedUt;
            return fix;
        }

        /* How far before a conic's start the light history is read for the body the craft left: an SOI change's two entries share its UT. */
        private const double PathStartBeforeUt = 0.001;

        /* The body the light history has craft `pid` about at `ut`, by flightGlobalsIndex, or -1 with no record. */
        private static int HistoryBody(uint pid, double ut) =>
            LightHistoryHost.History.TryStateAt(pid, ut, out var state) ? state.Body : -1;

        /* Whether this solve's craft aim is the picture's place, drawn or kept, so its readings are the picture's and truth's forecast does not apply. */
        private bool AimFromPicture => _placed == TruthAimPlace.Drawn || _placed == TruthAimPlace.Kept;

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
            _subjectPlacedUt = tr;
            return Turned(_known.SubjectAlong(tr), path.Spin, tr, _knownDrawnAt);
        }

        /*
         * The instant the picture has turned the lens's own body to, which a
         * world model's aim must follow to stay on the craft drawn on or about
         * that body: `ut` less its spin back, or `ut` where it is not turned.
         * A source that knows what is drawn only comes here for a craft the
         * scene draws live, as it is now, so it stays at `ut`. The lens is
         * placed here because the aim is solved before LocateDrawn places it.
         */
        private double LensBodyDrawnAt(double ut, DVec3 station, CelestialBody lensBody)
        {
            if (_known.KnowsWhatIsDrawn) return ut;
            _cam.Picture.Place(LensAt(ut, station, lensBody));
            return _cam.Picture.TryDrawnBody(lensBody, out _, out double dt) ? ut : ut + dt;
        }

        /* The instant a known subject is placed at this solve, for the aim to meet the craft as drawn. */
        private double _knownDrawnAt;

        private InstantHold _knownHold;
        private uint _knownHoldPid;
        private BodySpin _lensSpin;

        /* Body `b`'s spin as PastInstant.Turn carries a craft about it, in world axes, its band ending at its sphere of influence as the ring's does. */
        internal static BodySpin SpinOf(CelestialBody b)
        {
            if (b == null) return default;
            bool root = b.referenceBody == null || b.referenceBody == b;
            double soi = root || double.IsNaN(b.sphereOfInfluence) ? double.PositiveInfinity : b.sphereOfInfluence;
            return BodySpin.Of(b.flightGlobalsIndex, CommandCameraOcclusion.Spin(b), b.Radius, b.inverseRotThresholdAltitude, soi);
        }

        /* A fix at `ut` placed as KnownPath solves it at `t`. */
        private static CraftFix Turned(CraftFix fix, in BodySpin spin, double ut, double t)
        {
            if (!fix.Positioned || fix.Body == null) return fix;
            var centre = ToD(fix.Body.position);
            fix.World = centre + PastInstant.KnownTurned(spin, fix.Body.flightGlobalsIndex, fix.World - centre, fix.Live, ut, t);
            return fix;
        }

        /* The subject's known positions about the frame body as a path back in
           time. A fix's World is its body's present position plus its offset at
           the UT asked, as Orbit.getPositionAtUT gives it. */
        private struct KnownPath : IPastPath
        {
            public IFixSource Source;
            public KeplerBodyTree Tree;
            public int Frame;
            /* Read along the subject's motion before the start its source vouches from. */
            public bool Along;
            /* About the lens's body the path is carried round with it to render UT `T`, as the picture's is. */
            public BodySpin Spin;
            public double T;

            public bool TryAt(double ut, out DVec3 position)
            {
                position = default;
                var fix = Along ? Source.SubjectAlong(ut) : Source.Subject(ut);
                if (!fix.Positioned || fix.Body == null) return false;
                int index = fix.Body.flightGlobalsIndex;
                if (!PastInstant.TryAbout(ref Tree, index, Frame, ut, out var body)) return false;
                position = body + PastInstant.KnownTurned(Spin, index, fix.World - ToD(fix.Body.position), fix.Live, ut, T);
                return true;
            }
        }

        /*
         * How the drawn craft looks from the lens. A craft drawn at the aimed
         * point is seen as the aim's readings say: `aimIsDrawn` when the fix
         * source reads the same truth the picture draws and the aim is the
         * craft, otherwise when the two points coincide. The aim's occlusion
         * and sunlight read the bodies as they are now, so they stand for the
         * picture's only while the picture draws every body there too, or
         * when `readDrawn` says they were read where the picture draws them.
         */
        private void SolveDrawn(DVec3 station, DVec3 aim, double distance, bool aimIsDrawn, bool readDrawn, CelestialBody aimBody)
        {
            if (_drawnOk)
            {
                if (aimIsDrawn || (aim - _drawnPos).Magnitude < 1e-3)
                {
                    _drawnDistance = distance;
                    bool moved = !readDrawn && _cam.Picture.MovesBodies();
                    _drawnOccluded = moved ? _occlusion.FindDrawnOccluder(_cam.Picture, station, _drawnPos, out _, _drawn.Radius) != null : Occluded;
                    _drawnSunlit = moved ? IsDrawnSunlit(_drawnPos) : Sunlit;
                }
                else
                {
                    _drawnDistance = (_drawnPos - station).Magnitude;
                    _drawnOccluded = _occlusion.FindDrawnOccluder(_cam.Picture, station, _drawnPos, out _, _drawn.Radius) != null;
                    _drawnSunlit = IsDrawnSunlit(_drawnPos);
                }
            }
            _frameBody = _drawnOk ? _drawnBody : aimBody;
        }

        private DVec3 DrawnOrAim(DVec3 aim) => _drawnOk ? _drawnPos : aim;

        /*
         * The point `range` metres along a compass direction from the lens. A
         * Direction aim passes the subject's range, so the view is zoomed for
         * the subject as it passes through.
         * North and east come from stepping the station's own latitude and
         * longitude, which holds for any body and any vantage.
         */
        private DVec3 DirectionPoint(CelestialBody body, DVec3 station, DVec3 localUp, double azimuth, double elevation, double range)
        {
            var st = new Vector3d(station.X, station.Y, station.Z);
            double lat = body.GetLatitude(st), lon = body.GetLongitude(st), alt = body.GetAltitude(st);
            const double Step = 0.01;
            double northSign = lat > 89.0 ? -1.0 : 1.0;
            var p0 = ToD(body.GetWorldSurfacePosition(lat, lon, alt));
            var towardNorth = (ToD(body.GetWorldSurfacePosition(lat + northSign * Step, lon, alt)) - p0) * northSign;
            var towardEast = ToD(body.GetWorldSurfacePosition(lat, lon + Step, alt)) - p0;
            var dir = CommandCameraMath.DirectionVector(localUp, towardNorth, towardEast, azimuth, elevation);
            if (!(range > 1.0)) range = DirectionDefaultRangeM;
            return station + dir * range;
        }

        /* The point a placeholder looks at. A craft's forward axis that cannot be read falls back to the horizon. */
        private DVec3 PlaceholderPoint(PlaceholderAim placeholder, CelestialBody body, DVec3 station, DVec3 localUp)
        {
            var site = _mount.Site;
            if (placeholder == PlaceholderAim.LaunchSite)
                return ToD(body.GetWorldSurfacePosition(site.LaunchLatitude, site.LaunchLongitude,
                    site.LaunchAltitude + CommandCameraMath.PlaceholderSiteHeightM));
            if (placeholder == PlaceholderAim.MountForward && site.TryVantageForward(out var forward))
                return station + ToD(forward).Normalized * DirectionDefaultRangeM;
            return DirectionPoint(body, station, localUp, CommandCameraMath.PlaceholderAzimuthDeg, 0, DirectionDefaultRangeM);
        }

        /* An Orbit, Surface or PositionAt aim's point in world space, on the
           same UT (and so the same physics tick) as the station and the
           scaled bodies. With a light speed it is where the picture draws that
           point for a lens at `station` on `lensBody`, as a craft aim is:
           a Surface aim on the body as drawn, an Orbit or PositionAt aim at its
           own light time. */
        private bool ResolveAim(CommandAim aim, Orbit aimOrbit, double ut, DVec3 station, CelestialBody lensBody, out DVec3 world, out CelestialBody body)
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
                    double? c = LightTime.LightSpeed;
                    if (c.HasValue) return PastAimPlace(new AimLightPath { Orbit = orbit, OnOrbit = true }, BodyMotion.OnOrbit(orbit), body, ut, station, lensBody, c.Value, out world);
                    return true;
                }
                case CommandAimKind.Surface:
                {
                    body = FlightGlobals.GetBodyByName(aim.Body);
                    if (body == null) return false;
                    world = ToD(body.GetWorldSurfacePosition(aim.Latitude, aim.Longitude, aim.Altitude));
                    return AimOnDrawnBody(body, world, out world);
                }
                case CommandAimKind.PositionAt:
                {
                    body = FlightGlobals.GetBodyByName(aim.Body);
                    if (body == null || !AimPath(aim, body, out var path) || !path.TryAt(ut, out var rel, out _)) return false;
                    world = KinematicsWorld.Now().ToWorld(body, rel);
                    double? c = LightTime.LightSpeed;
                    if (c.HasValue) return PastAimPlace(new AimLightPath { Kepler = path, Zup = KinematicsWorld.Now().Zup }, BodyMotion.OnPath(body, path, KinematicsWorld.Now().Zup), body, ut, station, lensBody, c.Value, out world);
                    return true;
                }
            }
            return false;
        }

        /* A point fixed to body `b`, at `live` now, where the picture draws it; false while the picture leaves the body out. */
        private bool AimOnDrawnBody(CelestialBody b, DVec3 live, out DVec3 world)
        {
            world = live;
            if (_cam.Picture.HidesBody(b)) return false;
            bool placed = _cam.Picture.TryDrawnBody(b, out var centre, out double dt);
            if (placed || dt != 0)
                world = CommandCameraMath.CarriedByBody(live - ToD(b.position), placed ? centre : ToD(b.position), CommandCameraOcclusion.Spin(b), dt);
            return true;
        }

        /*
         * An Orbit or PositionAt aim where the light arriving at the lens now
         * left it, solved in the frame of the lens's and the aim's common
         * body, and placed as the picture places a craft: against a body drawn
         * at its past instant, or left out, at its own past place in that
         * frame; otherwise on the body as drawn.
         */
        private bool PastAimPlace(AimLightPath path, BodyMotion motion, CelestialBody body, double ut, DVec3 station, CelestialBody lensBody, double c, out DVec3 world)
        {
            world = default;
            var tree = KeplerBodyTree.World;
            int bodyIndex = body.flightGlobalsIndex;
            int frame = PastInstant.CommonAncestor(ref tree, lensBody.flightGlobalsIndex, bodyIndex);
            var frameBody = tree.Body(frame);
            if (frameBody == null) return false;
            var spin = SpinOf(lensBody);
            path.Tree = tree;
            path.BodyIndex = bodyIndex;
            path.Frame = frame;
            path.Spin = spin;
            path.T = ut;
            double bound = CommandCameraForecast.SpeedBound(motion, frameBody);
            bound += PastInstant.TurnSpeedBoundAbout(spin, bodyIndex, bound, double.PositiveInfinity, double.NaN, (station - ToD(lensBody.position)).Magnitude, c);
            if (PastInstant.Solve(ref path, station - ToD(frameBody.position), ut, c, bound, double.NegativeInfinity, ut, out double at) != PastStatus.Found)
                return false;
            if (_cam.Picture.HidesBody(body) || _cam.Picture.TryDrawnBody(body, out _, out _))
            {
                if (!path.TryAt(at, out var about)) return false;
                world = ToD(frameBody.position) + about;
                return true;
            }
            if (!path.Offset(at, out var rel)) return false;
            return AimOnDrawnBody(body, ToD(body.position) + PastInstant.Turned(spin, bodyIndex, rel, false, at, ut), out world);
        }

        /* An aim's own path back through time about the frame body, for the light-time solve. */
        private struct AimLightPath : IPastPath
        {
            public Orbit Orbit;
            public bool OnOrbit;
            public KeplerPath Kepler;
            public ZupFrame Zup;
            public KeplerBodyTree Tree;
            public int BodyIndex;
            public int Frame;
            /* About the lens's body the path is carried round with it to render UT `T`, as a craft's is. */
            public BodySpin Spin;
            public double T;

            /* The aim's offset from its body's centre at `ut`, in world axes. */
            public bool Offset(double ut, out DVec3 offset)
            {
                offset = default;
                if (OnOrbit)
                {
                    var r = Orbit.getRelativePositionAtUT(ut).xzy;
                    offset = new DVec3(r.x, r.y, r.z);
                    return true;
                }
                if (!Kepler.TryAt(ut, out var rel, out _)) return false;
                offset = Zup.ToWorld(rel);
                return true;
            }

            public bool TryAt(double ut, out DVec3 position)
            {
                position = default;
                if (!Offset(ut, out var rel) || !PastInstant.TryAbout(ref Tree, BodyIndex, Frame, ut, out var centre)) return false;
                position = centre + PastInstant.Turned(Spin, BodyIndex, rel, false, ut, T);
                return true;
            }
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

        /* In pixels of the delivered frame: the field just rendered and the
           framing it was eased to, or, with nothing rendering, the field the
           next frame would snap to. */
        private void UpdateBracket(float now)
        {
            if (!HasFix)
            {
                BracketHalfPx = 0;
                TrueOffsetPx = null;
                return;
            }
            Framed(RangeM, SubjectShape(), out double radius, out double range);
            bool rendering = _lastPrepare >= 0 && now - _lastPrepare <= FovSnapAfter;
            double fov = rendering ? _fov : CommandCameraMath.FovForFraction(radius, range, _frameFraction, FovMinNow(), _fovMax);
            int rows = FrameRows();
            BracketHalfPx = rendering && _framing.Started
                ? _framing.BracketHalfPx(fov, rows)
                : CommandCameraMath.BracketHalfPx(radius, range, fov, rows);
            UpdateDrawnOffset(fov, rows);
        }

        /* Where the drawn craft's CoM sits against the frame's axis, which is the aim but for the craft's framing centre and a slew, in the frame `fov` gives. */
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

        /* IsSunlit with each body where this camera draws it. The Sun is the root, and never moves. */
        private bool IsDrawnSunlit(DVec3 point)
        {
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun == null) return false;
            var sunPos = ToD(sun.position);
            var bodies = FlightGlobals.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null || b == sun || _cam.Picture.HidesBody(b)) continue;
                var centre = _cam.Picture.TryDrawnBody(b, out var drawn, out _) ? drawn : ToD(b.position);
                if (!CommandCameraMath.SegmentClearOfSphere(point, sunPos, centre, b.Radius)) return false;
            }
            return true;
        }

        /* `drawn`: the target is the picture's place, lit past the bodies where the picture draws them. */
        private void UpdateSunlit(double ut, double d, DVec3 target, bool drawn)
        {
            bool lit = drawn ? IsDrawnSunlit(target) : IsSunlit(target);
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
            if (_modelFailedPid == pid && _modelFailedParts == parts && pid != 0)
            {
                /* Another craft's model must not stand in for this one. */
                _standIn?.Dispose();
                _standIn = null;
                return;
            }
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

        /* Hands the lens to `network`, which takes its first pass at once so the camera starts at the clearest vantage. */
        internal void AttachNetwork(CommandCameraNetwork network)
        {
            Network = network;
            network.Attach();
            network.Step(this, true);
        }

        /*
         * Moves the lens to another site in one step, for a tracking network's
         * cut. The site's own skyline, forecast, zoom limit and lens come with
         * it, as a camera standing there alone would have them. Nothing eased
         * on the old site carries over: the aim, the handover and the place a
         * light-delayed aim held start afresh, and the next frame snaps its
         * framing to the new range.
         */
        internal void Resite(CommandCameraSite site, CommandCameraOcclusion occlusion, CommandCameraForecast forecast,
            Func<double> fovMin, Func<OpticsLens> opticsLens, string label)
        {
            _mount.Site = site;
            _siteLabel = label;
            _siteGeneration++;
            _occlusion = occlusion;
            _forecast = forecast;
            _fovMinSource = fovMin;
            _fovMin = fovMin();
            _fovMinCheckedAt = Time.realtimeSinceStartup;
            _opticsFloor = double.NaN;
            if (_optics != null && opticsLens != null)
            {
                _opticsLens = opticsLens;
                _lensCheckedAt = float.MinValue;
            }
            _slew.Reset();
            _slewAt = double.NaN;
            _slewUt = double.NaN;
            ForgetHandover();
            _framing.Reset();
            _truthPlace.Reset();
            _knownHold.Reset();
            _snapNext = true;
            _first = true;
            /* An occlusion at the new site starts when it is first seen there, and its ground is drawn by its own test. */
            OccludedSinceUt = double.NaN;
            _groundDrawn = false;
            /* The frame state still names the old site until the next solve; a status read in between must not pair them. */
            _solvedAt = float.MinValue;
        }

        /* A label change with no move, when a host relabels the vantage the lens stands at. */
        internal void Relabel(string label) => _siteLabel = label;

        /* Keeps the status geometry and the occlusion log live while nobody is
           watching the feed (PrepareRender only runs for a granted capture). */
        public void Poll()
        {
            _optics?.Idle();
            float now = Time.unscaledTime;
            Network?.Step(this, false);
            TakeHeldFovWhileIdle(now);
            if (now - _solvedAt < PollStaleSeconds) return;
            if (_lastPrepare < 0 || now - _lastPrepare > FovSnapAfter) _frames.Learn(now - _solvedAt);
            Solve(Planetarium.GetUniversalTime(), now, out _, out _, out _, out _, out _);
        }

        /* Only a rendered frame moves the field, so a zoom set on a camera nobody displays is taken here: /cameras reports it and the first frame opens on it. */
        private void TakeHeldFovWhileIdle(float now)
        {
            double sinceFrame = _lastPrepare < 0 ? double.PositiveInfinity : now - _lastPrepare;
            double field = CommandCameraMath.IdleFov(_fov, _heldFov, FovMinNow(), _fovMax, sinceFrame, FovSnapAfter);
            if (field == _fov) return;
            _fov = field;
            _cam.SetRigFov((float)field);
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
                /* Truth's motion is the craft now: an aim at a place drawn further in the past than the forecast resolves has no forecast. */
                if (AimIsCraft && AimFromPicture && !CommandCameraMath.ForecastHoldsAtLightTime(TrackUt - _subjectUt, horizon, ForecastSamples))
                    return _trackEvents;
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
            if (a == null || a.Kind == CommandAimKind.Vessel) return _known.SubjectMotion(ut, out aim, out _, out validUntilUt);
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
            /* Before the solve, so a cut lands whole on this frame: the pose, the picture and the label all come from the new site. */
            Network?.Step(this, false);
            site = _mount.Site;
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
            CommandCameraMath.LookRotation(fwd, up, out double qx, out double qy, out double qz, out double qw);
            anchor.rotation = new Quaternion((float)qx, (float)qy, (float)qz, (float)qw);
            _poseValid = true;
            _poseEye = station;
            /* A lens riding a craft in the scene stands inside its parts, so that craft is hidden from this camera's own render. */
            var rides = KerbcastSettings.DebugShowOwnCraft ? null : LensCraft;
            if (rides != null && rides.loaded) cam.Picture.HideOwn(rides);
            _poseForward = fwd;
            _poseAimToward = aim + ViewOffset - station;
            _poseUp = up;
            SyncScaledBodies(cam, anchor.position);
            SyncScaledPose(cam);
            UpdateStationBodyMesh(cam, station, ut, _lastDistance);

            double distance = _lastDistance;
            var drawn = PlaceDrawn(cam, ut, now, station, aim, occluder, distance);

            /* A cut clears the floor, and a new camera has none yet: settled here, so the field chosen below already stops at this site's blur. */
            if (_optics != null && double.IsNaN(_opticsFloor))
                UpdateOpticsFloor(AssessOptics(station, localUp, distance, out _, out _, out _).BlurArcsec, now);

            var shape = SubjectShape();
            Framed(distance, shape, out double framedRadius, out double framedRange);
            double step = _frames.Next(dt);
            if (_snapNext)
            {
                /* A cut shows the new site at its own framing from its first frame. */
                _snapNext = false;
                step = double.MaxValue;
            }
            if (_zoomRate != 0f && _authority != null && _authority.Registered) _zoomRate = 0f;
            if (_zoomRate != 0f)
                _heldFov = CommandCameraMath.ZoomedFov(double.IsNaN(_heldFov) ? _fov : _heldFov, _zoomRate, ZoomFactorPerSecond,
                    Math.Min(dt, 0.25), FovMinNow(), _fovMax);
            double held = CommandCameraMath.HeldFov(_heldFov, FovMinNow(), _fovMax);
            if (_zoomRate != 0f && !double.IsNaN(held))
            {
                _framing.Reset();
                _fov = held;
            }
            else if (!double.IsNaN(held))
            {
                _framing.Reset();
                _fov = CommandCameraMath.SmoothFov(_fov, held, step, FovTau, FovSnapAfter);
            }
            else if (_mode != SubjectAimMode.Hold && _placeholder == PlaceholderAim.None && !AimIsPoint)
            {
                /* Not AimJumped, which also fires on every world model publish: the box would start over on each record and pump again. */
                uint subject = _known.SubjectPersistentId, vantage = site.VantagePersistentId;
                if (!CommandAim.SameAs(_aim, _framingAim) || subject != _framingSubject || vantage != _framingVantage
                    || _siteGeneration != _framingSite)
                    _framing.Reset();
                _framingAim = _aim;
                _framingSubject = subject;
                _framingVantage = vantage;
                _framingSite = _siteGeneration;
                double certain = framedRadius - (AimIsCraft ? _knownUncertainty : 0.0);
                _fov = _framing.Step(CommandCameraMath.FramingTan(framedRadius, framedRange),
                    CommandCameraMath.FramingTan(certain, framedRange), _fov, _frameFraction,
                    FovMinNow(), _fovMax, step, FovTau, FovSnapAfter);
            }
            else
            {
                _framing.Reset();
                double fovTarget = _mode == SubjectAimMode.Hold ? Math.Max(_fov, FovMinNow())
                    : _placeholder == PlaceholderAim.LaunchSite
                        ? CommandCameraMath.FovForFraction(CommandCameraMath.PlaceholderSiteRadiusM, distance,
                            CommandCameraMath.PlaceholderSiteFraction, FovMinNow(), _fovMax)
                    : AimIsPoint
                        ? CommandCameraMath.PointFov(_aim.Kind == CommandAimKind.Direction, FramingMargin, distance,
                            _frameFraction, FovMinNow(), _fovMax)
                        : _fovMax;
                _fov = CommandCameraMath.SmoothFov(_fov, fovTarget, step, FovTau, FovSnapAfter);
            }
            /* This eases on real time, between physics ticks too, so it is the one part of the aim the slew's per UT rate does not describe exactly. */
            _viewOffset = _mode == SubjectAimMode.Subject && AimIsCraft
                ? CommandCameraMath.EaseToward(_viewOffset, shape.Known ? shape.Centre : default, step, FovTau, FovSnapAfter)
                : default;
            _shape = shape;
            cam.SetRigFov((float)_fov);
            UpdateBracket(now);
            UpdateOptics(ut, station, localUp, distance, now);
            FinishDrawn(cam, ut, station, anchor.rotation, drawn);

            if (_first)
            {
                _first = false;
                var ksp = site.Body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Altitude);
                double err = (ToD(ksp) - station).Magnitude;
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={distance:F0} event=first-frame "
                    + $"station={station} aim={aim} kspSurfaceCheckErr={err:F4}m craftRadius={KnownRadius:F1} fov={_fov:G4}");
            }

            if (KerbcastSettings.DebugSkyProbe && now - _skyProbeAt >= SkyProbeSeconds)
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
            /* The stand-in is the model rather than the marker's point of light. */
            public bool Model;
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
               for an unloaded one, or one this camera hides beyond the boundary,
               and only where a real craft would be seen. The rig draws its
               subject's stand-in itself, so its clip plan covers it. */
            p.StandIn = drawnOk && (_drawn.Loaded == null || _drawn.Hidden);
            p.Toward = ((drawnOk ? _drawnPos : aim) - station).Normalized;
            p.PlanDistance = p.Distance;
            p.PlanScale = 1.0;
            /* The field is chosen before this frame decides between marker and model, so last frame's model sizes it. */
            if (p.StandIn && (_wantModel || _modelShown))
            {
                EnsureModel(cam);
                if (ModelMatches()) _drawn.Radius = _standIn.Radius;
            }
            return p;
        }

        /*
         * Marker or model, judged at the field this frame renders with, by a
         * size the rig knows (SubjectSizeM), then the model placed.
         */
        private void DecideStandIn(KerbcastCamera cam, double ut, DVec3 station, ref DrawnPlan p)
        {
            double size = SubjectSizeM(out bool guessed);
            p.Model = p.StandIn
                && CommandCameraMath.SubjectAsModel(_wantModel, _marker != null, _modelShown, size, p.Distance, _fov, RenderRows(cam));
            if (p.Model) EnsureModel(cam);
            _modelShown = CommandCameraMath.ShowsModel(p.Model, ModelMatches(), _wantModel, guessed);
            /* With no model to show the marker stands in, rather than nothing; the model setting has no marker. */
            if (!_wantModel) p.Model = _modelShown;
            if (!_modelShown && _standIn != null) _standIn.Place(false, default, Quaternion.identity, 1f);
            if (_modelShown)
            {
                _drawn.Radius = _standIn.Radius;
                /* Beyond ModelProxyDistance the model stands in at that
                   distance on the true ray, shrunk by the same ratio: the
                   same angular size and perspective, with local-space
                   precision and clip planes that stay simple. Under a space
                   lens, whose fields go narrower than any other, a lens far
                   from the floating origin stands it farther still, so the
                   float step of the lens's own position stays a fraction of a
                   pixel on it. Every other camera keeps the fixed distance,
                   which depth-based tints such as Scatterer's depend on. */
                p.PlanDistance = _optics != null && _lens.Space
                    ? CommandCameraMath.ImpostorDistance(p.Distance, ModelProxyDistance,
                        CommandCameraMath.PixelSizeAtUnit(_fov, RenderRows(cam)), station.Magnitude, ModelProxyMinUlps)
                    : Math.Min(p.Distance, ModelProxyDistance);
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
            /* The pass leaves the subject to the rig only when the rig has something to draw it with. */
            if (p.StandIn && (p.Model ? _modelShown : _marker != null)) _cam.Picture.RigDraws(new RigDrawn(_drawn.PersistentId, _drawn.ShownPast ? _drawn.ShownPid : 0));
        }

        /* The model built for the parts shown, else the history's size for them, else a generous guess. */
        private double SubjectSizeM(out bool guessed)
        {
            var parts = _drawn.ShownParts;
            double model = ModelMatches() ? _standIn.Radius : 0;
            double recorded = parts != null ? parts.SizeM : 0;
            guessed = !(model > 0) && !(recorded > 0);
            return CommandCameraMath.SubjectSizeM(model, recorded, PictureCull.UnknownSizeM);
        }

        /* Whether the model built is the subject's, for the parts it is drawn with. */
        private bool ModelMatches()
        {
            var parts = _drawn.ShownParts;
            uint pid = parts != null ? _drawn.ShownPid : _drawn.PersistentId;
            return _standIn != null && _standIn.PersistentId == pid && _standIn.Parts == parts;
        }

        private static int RenderRows(KerbcastCamera cam)
        {
            var rt = cam.NearLayer != null ? cam.NearLayer.targetTexture : null;
            return rt != null ? rt.height : cam.RenderHeight;
        }

        /* Marker or model, the sub-pixel camera, the clip plan and the marker, once the field is set. */
        private void FinishDrawn(KerbcastCamera cam, double ut, DVec3 station, Quaternion lensRotation, DrawnPlan p)
        {
            DecideStandIn(cam, ut, station, ref p);
            UpdateSubpixel(cam, ut, p.Distance, p.PlanDistance, p.PlanScale, p.Blocked, p.Lit, p.Model);

            _plan = CommandCameraMath.PlanClips(p.PlanDistance, _drawn.Radius * p.PlanScale, p.Blocked, _hasFar, _wasSplit,
                _nearNativeNear, _nearNativeFar, _farNativeNear, _farNativeFar);
            ApplyPlan(cam);

            if (_marker != null)
            {
                var rt = cam.NearLayer != null ? cam.NearLayer.targetTexture : null;
                var optics = _optics != null && _opticsFrame.Active ? _optics.Marker(_opticsFrame, CommandCameraMarker.DotSizePixels) : default;
                _marker.Place(p.Lit && p.StandIn && !p.Model && !p.Blocked, station, p.Toward, p.Distance, lensRotation,
                    (float)_fov, rt != null ? rt.height : cam.RenderHeight, optics);
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
                    occluded = _occlusion.FindDrawnOccluder(_cam.Picture, _poseEye, world, out _, radius) != null;
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
           while that terrain is within the layers' own reach the mesh is left
           as stock has it. Beyond it, or with the PQS inactive, the mesh is
           the only ground there is. That is the reach PicturePass tests before
           it hides terrain, so terrain it hides always has this mesh in its
           place. */
        private void UpdateStationBodyMesh(KerbcastCamera cam, DVec3 station, double ut, double d)
        {
            var body = _mount.Site.Body;
            bool stock = cam.Picture.TerrainLocal(body, station);
            var r = stock && body.scaledBody != null ? body.scaledBody.GetComponent<Renderer>() : null;
            cam.RigStockFaderRenderer = r;
            bool nowStock = r != null;
            if (nowStock != _stationMeshStock || !_stationMeshLogged)
            {
                _stationMeshLogged = true;
                _stationMeshStock = nowStock;
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={d:F0} event=station-body-mesh "
                    + (nowStock ? "stock (left to the fader, terrain is local)" : "forced on (no local terrain in reach)"));
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
        private static readonly int LightDirId = Shader.PropertyToID("_v4LightDir");
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
            public Vector3 LightDir;
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
                LightDir = afg.sunLightDirection,
            };
        }

        /*
         * AfgInputsFor for this lens, from its offset from the body's centre
         * in metres. With the origin far away the scaled positions are
         * rounded to hundreds of metres, more than the lens stands above the
         * ground, so its height read from them can land underwater. The
         * camera position is placed from the same offset so it agrees with
         * the height.
         */
        private static AfgInputs AfgInputsForLens(AtmosphereFromGround afg, DVec3 centreToLens)
        {
            double k = ScaledSpace.ScaleFactor;
            var centre = afg.planet.scaledBody.transform.position;
            var offset = centreToLens * (1.0 / k);
            var camPos = centre + ToF(offset);
            double h = offset.Magnitude;
            double depth = CommandCameraMath.LensDepthM(centreToLens.Magnitude, afg.pRadius, k);
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            float dot = sun != null
                ? Mathf.Clamp01(Vector3.Dot(ToF(offset * -1.0), camPos - sun.scaledBody.transform.position) * afg.dawnFactor)
                : afg.lightDot;
            return new AfgInputs
            {
                CameraPos = camPos,
                Height = (float)h,
                Underwater = depth >= 0 ? Mathf.Min((float)depth * afg.oceanDepthRecip, 1f) : 0f,
                LightDot = dot,
                LightDir = afg.sunLightDirection,
            };
        }

        private static void ApplyAfg(MaterialPropertyBlock mpb, AfgInputs v)
        {
            mpb.SetVector(CameraPosId, new Vector4(v.CameraPos.x, v.CameraPos.y, v.CameraPos.z, 0f));
            mpb.SetFloat(CameraHeightId, v.Height);
            mpb.SetFloat(CameraHeight2Id, v.Height * v.Height);
            mpb.SetFloat(CamHeightUnderwaterId, v.Underwater);
            mpb.SetFloat(LightDotId, v.LightDot);
            mpb.SetVector(LightDirId, new Vector4(v.LightDir.x, v.LightDir.y, v.LightDir.z, 0f));
        }

        private readonly ScaledSunForLens _sun = new ScaledSunForLens();
        private CommandCameraStationGround _ground;
        private bool _groundDrawn;
        private string _groundState;

        /* Draws the body's ground under a lens whose body has no local terrain,
           when the view reaches below the horizon. Runs for every scaled render,
           the probe's too, and draws nothing a view above the horizon can see. */
        private void DrawStationGround(KerbcastCamera cam)
        {
            var scaled = cam.ScaledLayer;
            var body = _mount.Site.Body;
            if (scaled == null || body == null || !_poseValid) return;
            var toCentre = ToD(body.position) - _poseEye;
            bool wasDrawn = _groundDrawn;
            _groundDrawn = false;
            var plan = CommandCameraMath.PlanStationGround(toCentre.Magnitude, body.Radius, ScaledSpace.ScaleFactor,
                _poseForward, _poseUp, toCentre.Normalized * -1.0, scaled.fieldOfView, scaled.aspect, wasDrawn);
            string state = _stationMeshStock ? "off (terrain is local)" : "off (no ground in view, or the lens is clear of the near plane)";
            if (plan.Draw && !_stationMeshStock)
            {
                if (_ground == null) _ground = new CommandCameraStationGround(_flightId);
                string failed = _ground.Show(body, scaled.transform.position, ToF(toCentre * plan.EyeToCentreScale), plan.SphereRadiusUnits);
                _groundDrawn = failed == null;
                state = failed == null ? "on (no local terrain, view reaches below the horizon)" : "unavailable (" + failed + ")";
            }
            if (state == _groundState) return;
            _groundState = state;
            Debug.Log($"{Tag} cam={_flightId} UT={F(_frameUt)} d={_lastDistance:F0} event=station-ground {state} "
                + $"body={body.bodyName} elev={ElevationDeg:F2} lowest={plan.LowestDeg:F2} dip={plan.HorizonDipDeg:F2} "
                + $"eyeHeight={toCentre.Magnitude - plan.GroundRadiusM:F0}m groundRadius={plan.GroundRadiusM:F0}m "
                + $"magnify={plan.Magnify:G4} sphere={plan.SphereRadiusUnits:G6}");
        }

        public void BeforeScaledRender(KerbcastCamera cam)
        {
            if (_optics != null && _opticsFrame.Active && !_opticsBegun)
            {
                var rt = cam.NearLayer != null ? cam.NearLayer.targetTexture : null;
                if (rt != null)
                {
                    /* Marked first, so a throw partway through still hands the layer back in AfterScaledRender. */
                    _opticsBegun = true;
                    _optics.BeginScaled(cam.ScaledLayer, rt.width, rt.height);
                }
            }
            /* When the picture lights each body itself, it has already aimed the Sun and given the shells their light. */
            if (!cam.Picture.BodiesLit) _sun.Begin(cam.ScaledLayer, $"{Tag} cam={_flightId}");
            DrawStationGround(cam);
            _afgOverridden = null;
            var body = _mount.Site.Body;
            var afg = body != null ? body.afg : null;
            var scaled = cam.ScaledLayer;
            if (afg == null || afg.planet == null || afg.planet.scaledBody == null || afg.mainCamera == null || scaled == null) return;
            if (_afgRenderer == null || _afgRenderer.gameObject != afg.gameObject) _afgRenderer = afg.GetComponent<Renderer>();
            if (_afgRenderer == null) return;
            _afgStock = AfgInputsFor(afg, afg.mainCamera.position);
            _afgOurs = _poseValid
                ? AfgInputsForLens(afg, _poseEye - ToD(body.position))
                : AfgInputsFor(afg, scaled.transform.position);
            _afgRenderer.GetPropertyBlock(_afgBlock);
            _afgOurs.LightDir = cam.Picture.BodiesLit ? (Vector3)_afgBlock.GetVector(LightDirId) : _sun.Turn * _afgOurs.LightDir;
            if (!_afgOverride) return;
            ApplyAfg(_afgBlock, _afgOurs);
            _afgRenderer.SetPropertyBlock(_afgBlock);
            _afgOverridden = afg;
        }

        /* Runs after any partial BeforeScaledRender too, and each restore runs even when one before it throws: the scaled target, the ground, the Sun and the atmosphere are shared with the main camera. */
        public void AfterScaledRender(KerbcastCamera cam)
        {
            try { EndOpticsLayer(cam); }
            finally
            {
                try { _ground?.Hide(); }
                finally
                {
                    try { _sun.End(); }
                    finally { RestoreAfg(); }
                }
            }
        }

        private void EndOpticsLayer(KerbcastCamera cam)
        {
            if (!_opticsBegun) return;
            _opticsBegun = false;
            var home = _poseValid ? _mount.Site.StationBody : null;
            var toHome = home != null ? ToD(home.position) - _poseEye : default;
            _optics.EndScaled(cam.ScaledLayer, cam.NearLayer != null ? cam.NearLayer.targetTexture : null, _opticsFrame, home, toHome, cam.Picture);
        }

        private void RestoreAfg()
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
            var lens = _poseEye - ToD(body.position);
            var up = _poseValid ? ToF(lens.Normalized) : (t.position - centre).normalized;
            double camAltM = _poseValid ? lens.Magnitude - body.Radius : ((t.position - centre).magnitude - afg.pRadius) * ScaledSpace.ScaleFactor;
            float scaledAltM = ((t.position - centre).magnitude - afg.pRadius) * ScaledSpace.ScaleFactor;
            float fwdElev = Mathf.Asin(Mathf.Clamp(Vector3.Dot(t.forward, up), -1f, 1f)) * Mathf.Rad2Deg;
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            var toSun = sun != null ? (sun.scaledBody.transform.position - t.position).normalized : Vector3.zero;
            float sunElev = Mathf.Asin(Mathf.Clamp(Vector3.Dot(toSun, up), -1f, 1f)) * Mathf.Rad2Deg;
            float sunAngle = Vector3.Angle(t.forward, toSun);
            float stockAltM = (_afgStock.Height - afg.pRadius) * ScaledSpace.ScaleFactor;
            Debug.Log($"{Tag} cam={_flightId} event=sky-inputs ours: camAlt={camAltM:F0}m scaledCamAlt={scaledAltM:F0}m height={_afgOurs.Height:F4} lightDot={_afgOurs.LightDot:F3} "
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
                _subpixel.Render(pose, (float)_fov, rt != null ? rt.height : cam.RenderHeight, _subpixelNear, _subpixelFar, _subpixelWindow);
                if (_optics != null && _opticsFrame.Active)
                {
                    if (_subpixel.HasLight) _optics.DrawCraft(rt, _subpixel.LastLight, _subpixel.LastCoverage, _subpixelAcross, _opticsFrame);
                }
                else _subpixel.Composite(rt);
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
            /* Under optics a craft the blur covers is drawn as the blur, through a window wide enough for its light. */
            double below = SubpixelBelowPx;
            int window = CommandCameraSubpixel.WindowPx;
            if (_opticsFrame.Active)
            {
                below = Math.Max(SubpixelBelowPx, Math.Min(_opticsFrame.BlurFwhmPx, CommandCameraSubpixel.MaxWindowPx * 0.75));
                if (across >= SubpixelBelowPx) window = Math.Min(CommandCameraSubpixel.MaxWindowPx, (int)Math.Ceiling(across * 1.3) + 4);
            }
            _subpixelWindow = window;
            _subpixelAcross = across;
            bool sub = across < below;
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
                    + $"across={across:G3}px fov={_fov:F4} rows={rows} clip=[{_subpixelNear:F1},{_subpixelFar:F1}]"
                    + (_opticsFrame.Active ? $" opticsBlurPx={_opticsFrame.BlurFwhmPx:F2} window={window}px" : ""));
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
            bool atFloor = _fov <= FovMinNow() * 1.0001;
            if (atFloor != _wasFovFloor)
            {
                _wasFovFloor = atFloor;
                Debug.Log($"{at} event={(atFloor ? "fov-floor-reached" : "fov-floor-left")} fov={_fov:F4} floor={FovMinNow():F4} opticsFloor={_opticsFloor:F4} craftRadius={KnownRadius:F1}");
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
                    + (_shape.Known ? $"shapeHalf={_shape.Half:F1} shapeCentreM={_shape.Centre.Magnitude:F1} viewOffsetM={_viewOffset.Magnitude:F1} " : "shape=none ")
                    + $"handoverM={_handover.Offset.Magnitude:F2} split={_plan.Split} occluded={Occluded} "
                    + (_hasFar ? $"far={(_plan.FarRender ? $"[{_plan.FarNear:F0},{_plan.FarFar:F0}]" : "skipped")} mid={(_plan.MidActive ? $"[{_plan.MidNear:F0},{_plan.MidFar:F0}]" : "off")} " : "")
                    + (_subpixelActive && _subpixel != null ? $"subpixelEnergy={_subpixel.LastEnergy:G3} subpixelCoverPx={_subpixel.LastCoverage:G3} subpixelPeak={_subpixel.LastPeak:G3} " : "")
                    + (_optics != null ? $"optics={(_opticsFrame.Active ? "on" : "off")} opticsBlurPx={_opticsBlurPx:F2} opticsLevel={_optics.LastLevel} "
                        + $"opticsBodies={_optics.LastBodies} opticsCraftGain={_optics.LastCraftGain:F2} opticsMs={_optics.CpuMs:F3} " : "")
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
            _ground?.Dispose();
            _ground = null;
            if (_midCam != null) UnityEngine.Object.Destroy(_midCam.gameObject);
            _midCam = null;
            _subpixel?.Dispose();
            _subpixel = null;
            _optics?.Dispose();
            Network?.Dispose();
            Network = null;
            _mount.DestroyAnchor();
            Debug.Log($"{Tag} cam={_flightId} rig down");
        }

        /*
         * Settles this frame's optics from the lens, the air at the lens and
         * the field: the blur in pixels, seeing's shift and flicker, the
         * colour the air leaves and the daylight. Under a pixel of blur the
         * frame renders exactly as without optics.
         */
        private void UpdateOptics(double ut, DVec3 station, DVec3 localUp, double rangeM, float now)
        {
            _opticsFrame = default;
            if (_optics == null) return;
            float real = Time.realtimeSinceStartup;
            var r = AssessOptics(station, localUp, rangeM, out var over, out var knobs, out double air);
            _opticsReading = r;
            UpdateOpticsFloor(r.BlurArcsec, now);
            int rows = FrameRows();
            double pixArc = OpticsModel.PixelArcsec(_fov, rows);
            double blurPx = r.BlurArcsec / pixArc;
            _opticsBlurPx = double.IsNaN(blurPx) ? 0 : blurPx;
            /* Whether the effect shows and how far it has faded in are judged on the rows the zoom floor counts, so the look is the same when adaptive quality halves the frame; the widths drawn stay in real pixels. */
            double lookPx = OpticsModel.BlurSpanPx(r.BlurArcsec, _fov, FullRows);
            bool active = OpticsModel.Worthwhile(lookPx);
            if (active)
            {
                /* The pyramid's deepest level caps the blur the picture can show, so the masks and the craft are capped with it. */
                var rt = _cam.NearLayer != null ? _cam.NearLayer.targetTexture : null;
                int cols = rt != null ? rt.width : _cam.RenderWidth;
                double capPx = OpticsModel.PyramidSigma(OpticsModel.MaxLevelFor(cols, rows)) * OpticsModel.FwhmPerSigma;
                double scale = blurPx > capPx ? capPx / blurPx : 1.0;
                double shownPx = blurPx * scale;
                double ramp = OpticsModel.Ramp(lookPx);
                double seeingPx = r.SeeingArcsec / pixArc * scale;
                OpticsModel.SeeingMotion(r.ResolutionArcsec, out double wobble, out double boil, out double twinkle);
                double amp = air > 0 ? 0.5 * seeingPx * Math.Sqrt(wobble * wobble + 0.31 * boil * boil) * ramp : 0;
                _opticsFrame = new OpticsFrame
                {
                    Active = true,
                    SigmaPx = shownPx / OpticsModel.FwhmPerSigma,
                    BlurFwhmPx = shownPx,
                    DiffFwhmPx = r.ResolutionArcsec / pixArc * scale,
                    SeeingFwhmPx = seeingPx,
                    OffsetX = (float)(amp * _seeingNoise.At(real, 0)),
                    OffsetY = (float)(amp * _seeingNoise.At(real, 1)),
                    Twinkle = air > 0 ? Mathf.Max(0f, (float)(1.0 + twinkle * ramp * _seeingNoise.At(real, 2))) : 1f,
                    Tint = new Color(Faded(OpticsModel.Transmission(r.Airmass, air, 0), ramp), Faded(OpticsModel.Transmission(r.Airmass, air, 1), ramp),
                        Faded(OpticsModel.Transmission(r.Airmass, air, 2), ramp), 1f),
                    BrightnessHold = knobs.BrightnessHold,
                    DaySky = r.DaySky,
                    DaySkyStrength = knobs.DaySkyStrength * ramp,
                    PixelRad = pixArc / OpticsModel.ArcsecPerRadian,
                    Ramp = ramp,
                };
            }
            else _optics.Idle();
            NoteOptics(ut, active, lookPx, over, air, now);
        }

        /* What this site's lens makes of the aim from where the solve just put it, with a feed's override over the lens's own knobs. */
        private OpticsReading AssessOptics(DVec3 station, DVec3 localUp, double rangeM, out OpticsKnobs over, out OpticsKnobs knobs, out double air)
        {
            float real = Time.realtimeSinceStartup;
            if (real - _lensCheckedAt >= 1f || _lens.Label == null)
            {
                _lensCheckedAt = real;
                _lens = _opticsLens();
            }
            over = _authority != null ? _authority.OpticsFor(_flightId) : OpticsKnobs.Unset;
            knobs = over.Over(_lens.Defaults);

            var body = _mount.Site.StationBody;
            air = body != null ? CommandCameraOptics.AirAt(body, (station - ToD(body.position)).Magnitude) : 0;
            double sunElev = double.NaN;
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (sun != null && sun != body)
            {
                var toSun = (ToD(sun.position) - station).Normalized;
                sunElev = Math.Asin(Math.Max(-1.0, Math.Min(1.0, DVec3.Dot(toSun, localUp)))) * 180.0 / Math.PI;
            }
            return OpticsModel.Assess(new OpticsSite
            {
                ResolutionArcsec = knobs.ResolutionArcsec,
                ZenithSeeingArcsec = knobs.SeeingArcsec,
                SeeingAirmassPower = knobs.SeeingAirmassPower,
                AirFactor = air,
                ElevationDeg = ElevationDeg,
                SunElevationDeg = sunElev,
            }, rangeM);
        }

        /* A transmission faded in from none (1) by `ramp`. */
        private static float Faded(double transmission, double ramp) => (float)(1.0 + (transmission - 1.0) * ramp);

        /* One line each time the feed's optics turn on or off, its lens or override changes, or the blur moves by half a stop. */
        private void NoteOptics(double ut, bool active, double lookPx, OpticsKnobs over, double air, float now)
        {
            int bucket = active ? (int)Math.Round(Math.Log(lookPx, 2.0) * 2.0) : 0;
            if (_opticsLogged && active == _opticsWasActive && _lens.Label == _opticsLoggedLens && over.SameAs(_opticsLoggedOver)
                && bucket == _opticsLoggedBucket && (air > 0) == _opticsLoggedAir) return;
            bool flip = !_opticsLogged || active != _opticsWasActive;
            if (!flip && now - _opticsLoggedAt < 1f) return;
            _opticsLogged = true;
            _opticsWasActive = active;
            _opticsLoggedLens = _lens.Label;
            _opticsLoggedOver = over;
            _opticsLoggedBucket = bucket;
            _opticsLoggedAir = air > 0;
            _opticsLoggedAt = now;
            var r = _opticsReading;
            Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={_lastDistance:F0} event=optics state={(active ? "on" : "off")} lens='{_lens.Label}' "
                + $"res={r.ResolutionArcsec:F2}arcsec seeing={r.SeeingArcsec:F2}arcsec air={air:F2} airmass={r.Airmass:F2} elev={ElevationDeg:F1} "
                + $"blur={r.BlurArcsec:F2}arcsec lookPx={lookPx:F2} onTargetM={r.BlurOnTargetM:G3} day={r.DaySky:F2} fov={_fov:F4} "
                + $"override={(over.IsUnset ? "none" : over.ToString())}");
        }

        private static string F(double ut) => ut.ToString("F2", CultureInfo.InvariantCulture);
        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
        private static DVec3 ToD(Vector3 v) => new DVec3(v.x, v.y, v.z);
        private static Vector3 ToF(DVec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
    }
}
