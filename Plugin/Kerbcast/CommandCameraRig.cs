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

        private const string Tag = "[Kerbcast][CMDCAM]";
        private const double FovTau = 0.4;
        private const double FovSnapAfter = 1.0;
        private const float RadiusRefreshSeconds = 0.5f;
        private const double PartPad = 1.5;
        private const double MinCraftRadius = 2.0;
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
        private readonly CommandCameraTarget _target;
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
        private double _craftRadius = MinCraftRadius;
        private float _radiusAt = float.MinValue;
        private uint _radiusVesselId;
        private readonly List<DVec3> _partScratch = new List<DVec3>();

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
        public uint SubjectPersistentId => _target.PersistentId;
        public string SubjectName => _target.Name ?? "";
        /* Nothing between the target and the Sun but empty space. */
        public bool Sunlit { get; private set; }
        private bool _sunlitLogged;

        /* Under an authority the readings above are taken at the aimed point.
           The true craft is still drawn where it is, so its own visibility is
           kept apart for the render. */
        private bool _trueOk;
        private DVec3 _truePos;
        private CelestialBody _trueBody;
        private double _trueDistance;
        private bool _trueOccluded;
        private bool _trueSunlit;
        private double _trueOffsetRad;

        /* The lens pose of the frame last rendered, for the anchor projector. */
        private bool _poseValid;
        private DVec3 _poseEye, _poseForward, _poseUp;
        private readonly AnchorSpec _subjectAnchor = new AnchorSpec { Id = SubjectAnchorId, Kind = AnchorKind.Vessel, Occlusion = true };

        /* The bracket contract (see AppendCommandTrack in KerbcastCore). Kept
           for clients older than 1.12; newer ones draw the subject's anchor. */
        public double TrackUt { get; private set; }
        public double BracketHalfPx { get; private set; }
        public int FrameHeightPx => FrameRows();
        /* Angle between the aim and the true craft as seen from the station, in
           capture pixels; null without a true fix. */
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
        public string StationDisplayName =>
            string.IsNullOrEmpty(_mount.Site.DisplayName) ? _mount.Site.NodeName : _mount.Site.DisplayName;

        /* Where the camera points; null for its default craft. */
        public CommandAim Aim => _aim;
        internal Orbit AimOrbit => _aimOrbit;

        /* The craft this lens rides no longer exists (crashed, recovered, or
           gone some way other than a merge, which the lens follows). */
        public bool VantageLost => _mount.Site.VantageGone;

        /* A merge (dock or claw grab) moves both ends: the watched craft and
           the craft the lens rides each follow into the survivor. */
        private void OnMerged(uint oldPid, uint newPid)
        {
            _target.Merged(oldPid, newPid);
            if (_mount.Site.FollowMerge(oldPid, newPid))
                Debug.Log($"{Tag} cam={_flightId} vantage merged: persistentId={oldPid} -> {newPid} ('{_mount.Site.Vantage.vesselName}')");
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
            if (subject != _target.ChosenPersistentId) _target.Choose(subject);
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
        private bool _blankLogged;
        private string _loggedBlank;
        private string _loggedAim;

        private CommandCameraMarker _marker;
        private CommandCameraUtStripe _utStripe;
        private double _frameUt;
        /* The model wins over the marker when both are set. */
        private readonly bool _wantModel;
        private CommandCameraTargetModel _model;
        private uint _modelFailedPid;
        private CelestialBody _lastTargetBody;

        public CommandCameraRig(KerbcastCamera cam, CommandCameraMount mount, CommandCameraTarget target,
            CommandAuthority authority, double frameFraction, Func<double> fovMin, double fovMax, bool targetMarker, bool targetModel,
            bool utStripe)
        {
            _cam = cam;
            _mount = mount;
            _target = target;
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
            bool governed = _authority != null && _authority.Registered;
            bool timedOut = governed && _authority.TimedOut(Time.realtimeSinceStartup);
            var orders = governed ? _authority.Orders(_flightId) : null;

            bool ok = SolveGeometry(ut, out station, out aim, out localUp, out north, out occluder);
            if (!ok) HasFix = false;
            else
            {
                CommandCameraMath.AimBasis(station, aim, localUp, north, out _solveForward, out _solveUp);
                _solveEye = station;
            }
            FramingFits = !ok || CommandCameraMath.FramingFits(_craftRadius + FramingMargin, RangeM, _fovMax);
            Blank = CommandCameraMath.BlankReason(governed, timedOut, orders?.Blank, VantageLost);
            UpdateBracket(now);
            NoteGovernance(ut, governed);
            _events.Record(new CommandTrackEvent(ut, Occluded, Blocker, Sunlit, Blank));
            return ok;
        }

        /* Station, aim point and true craft positions, and the camera-axis
           readings at the aim point: the true craft for the default or a
           Vessel aim, otherwise the point the aim resolves to. */
        private bool SolveGeometry(double ut,
            out DVec3 station, out DVec3 aim, out DVec3 localUp, out DVec3 north, out string occluder)
        {
            station = aim = localUp = north = default;
            occluder = null;
            _trueOk = false;
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

            _trueOk = _target.TryLocate(ut, out _truePos, out _trueBody);
            CelestialBody aimBody;
            bool aimed = _aim != null && _aim.Kind != CommandAimKind.Vessel;
            if (!aimed)
            {
                if (!_trueOk) return false;
                aim = _truePos;
                aimBody = _trueBody;
            }
            else if (_aim.Kind == CommandAimKind.Direction)
            {
                aim = DirectionPoint(body, station, localUp);
                aimBody = body;
            }
            else if (!ResolveAim(_aim, _aimOrbit, ut, out aim, out aimBody))
            {
                return false;
            }
            _lastAimPoint = aim;

            long occT0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _occlusion.UseSite(site);
            occluder = _occlusion.FindOccluder(station, aim, out string occluderSource);
            double distance = (aim - station).Magnitude;
            _lastDistance = distance;
            HasFix = true;
            RangeM = distance;
            ElevationDeg = CommandCameraMath.ElevationDeg(aim - station, localUp);
            Occluded = occluder != null;
            Blocker = occluder ?? "";
            UpdateSunlit(ut, distance, aim);

            _trueOffsetRad = _trueOk ? CommandCameraMath.AngleBetween(aim - station, _truePos - station) : 0.0;
            if (_trueOk)
            {
                if (!aimed || (aim - _truePos).Magnitude < 1e-3)
                {
                    _trueDistance = distance;
                    _trueOccluded = Occluded;
                    _trueSunlit = Sunlit;
                }
                else
                {
                    _trueDistance = (_truePos - station).Magnitude;
                    _trueOccluded = _occlusion.FindOccluder(station, _truePos, out _) != null;
                    _trueSunlit = IsSunlit(_truePos);
                }
            }
            _lastTargetBody = _trueOk ? _trueBody : aimBody;
            double occMs = (System.Diagnostics.Stopwatch.GetTimestamp() - occT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _occlusionMs = _occlusionMs > 0 ? _occlusionMs + OcclusionMsAlpha * (occMs - _occlusionMs) : occMs;

            NoteTargetAndOcclusion(ut, distance, aimBody, _trueOk ? _truePos : aim, occluder, occluderSource);
            return true;
        }

        /*
         * A Direction aim's point: along the held direction at the subject's
         * range, so the view is zoomed for the subject as it passes through.
         * North and east come from stepping the station's own latitude and
         * longitude, which holds for any body and any vantage.
         */
        private DVec3 DirectionPoint(CelestialBody body, DVec3 station, DVec3 localUp)
        {
            var st = new Vector3d(station.X, station.Y, station.Z);
            double lat = body.GetLatitude(st), lon = body.GetLongitude(st), alt = body.GetAltitude(st);
            const double Step = 0.01;
            double northSign = lat > 89.0 ? -1.0 : 1.0;
            var p0 = ToD(body.GetWorldSurfacePosition(lat, lon, alt));
            var towardNorth = (ToD(body.GetWorldSurfacePosition(lat + northSign * Step, lon, alt)) - p0) * northSign;
            var towardEast = ToD(body.GetWorldSurfacePosition(lat, lon + Step, alt)) - p0;
            var dir = CommandCameraMath.DirectionVector(localUp, towardNorth, towardEast, _aim.Azimuth, _aim.Elevation);
            double range = _trueOk ? (_truePos - station).Magnitude : DirectionDefaultRangeM;
            if (!(range > 1.0)) range = DirectionDefaultRangeM;
            return station + dir * range;
        }

        /* An Orbit, Surface or PositionAt aim's point in world space, on the
           same UT (and so the same physics tick) as the station and the
           scaled bodies. */
        private static bool ResolveAim(CommandAim aim, Orbit aimOrbit, double ut, out DVec3 world, out CelestialBody body)
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
                    if (body == null) return false;
                    /* Orbit.getPositionAtUT's conversion: body-relative z-up to
                       Unity's y-up by the xzy swizzle, offset by the body centre. */
                    var rel = aim.BodyRelativePosition + aim.BodyRelativeVelocity * (ut - aim.Ut);
                    world = ToD(body.position + rel.xzy);
                    return true;
                }
            }
            return false;
        }

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
            double radius = _craftRadius + FramingMargin;
            bool rendering = _lastPrepare >= 0 && now - _lastPrepare <= FovSnapAfter;
            double fov = rendering
                ? _fov
                : CommandCameraMath.FovForFraction(radius, RangeM, _frameFraction, FovMinNow(), _fovMax);
            int rows = FrameRows();
            BracketHalfPx = CommandCameraMath.BracketHalfPx(radius, RangeM, fov, rows);
            TrueOffsetPx = _trueOk ? CommandCameraMath.AngleInPixels(_trueOffsetRad, fov, rows) : (double?)null;
            TrueVisible = _trueOk && !_trueOccluded && TrueInFrame(fov, rows);
        }

        /* Whether the true craft lands inside the frame the aim gives. */
        private bool TrueInFrame(double fov, int rows)
        {
            var rt = _cam.NearLayer != null ? _cam.NearLayer.targetTexture : null;
            int cols = rt != null ? rt.width : _cam.RenderWidth;
            if (cols <= 0 || rows <= 0) return false;
            CommandCameraMath.PerspectiveTerms(fov, (double)cols / rows, out double m00, out double m11);
            return CommandCameraMath.Project(_solveEye, _solveForward, _solveUp, m00, m11, 0, 0, cols, rows, _truePos).InFrame;
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
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={d:F0} event={(lit ? "sunlit" : "eclipsed")} target='{_target.Name}'");
            }
            Sunlit = lit;
        }

        /* Built once per target; a craft that cannot be built is not retried. */
        private void EnsureModel(KerbcastCamera cam)
        {
            uint pid = _target.PersistentId;
            if (_model != null && _model.PersistentId == pid) return;
            if (_modelFailedPid == pid && pid != 0) return;
            _model?.Dispose();
            _model = null;
            try
            {
                _model = CommandCameraTargetModel.Build(_target.Proto, _flightId,
                    new[] { cam.NearLayer, cam.FarLayer, _midCam });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} cam={_flightId} target model build threw: {ex}");
            }
            if (_model == null) _modelFailedPid = pid;
            else if (_subpixel != null) _model.AttachSubpixel(_subpixel.Camera);
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
                if (TryMotions(now, out var station, out var aim) && _forecast.Begin(now, horizon, station, aim))
                {
                    CommandCameraMath.FindEdges(now, now + horizon, ForecastSamples, ForecastBisectSteps, ForecastToleranceUt,
                        _forecast.StateAt, (a, b) => a.SameCameraAxisAs(b), _edges);
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
           both are in this frame's world. Mirrors SolveGeometry and ResolveAim. */
        private bool TryMotions(double ut, out BodyMotion station, out BodyMotion aim)
        {
            station = aim = default;
            var site = _mount.Site;
            var v = site.Vantage;
            if (site.HasVantage)
            {
                if (!site.TryVantagePosition(ut, out var vantage, out _)) return false;
                if (v.LandedOrSplashed) station = BodyMotion.OnSurface(v.mainBody, vantage);
                else if (v.orbit != null) station = BodyMotion.OnOrbit(v.orbit);
                else return false;
            }
            else
            {
                var body = site.Body;
                if (body == null) return false;
                station = BodyMotion.OnSurface(body, body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Altitude));
            }

            var a = _aim;
            if (a == null || a.Kind == CommandAimKind.Vessel) return TargetMotion(ut, out aim);
            switch (a.Kind)
            {
                case CommandAimKind.Direction:
                {
                    /* Held still against the ground, as near as the forecast needs. */
                    var body = site.HasVantage ? (site.Vantage != null ? site.Vantage.mainBody : null) : site.Body;
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
                    if (body == null) return false;
                    aim = BodyMotion.Linear(body, a.BodyRelativePosition, a.BodyRelativeVelocity, a.Ut);
                    return true;
                }
            }
            return false;
        }

        private bool TargetMotion(double ut, out BodyMotion aim)
        {
            aim = default;
            if (!_target.TryMotion(out var orbit, out bool onSurface)) return false;
            if (orbit != null)
            {
                if (orbit.referenceBody == null) return false;
                aim = BodyMotion.OnOrbit(orbit);
                return true;
            }
            if (!onSurface || !_target.TryLocate(ut, out var world, out var body) || body == null) return false;
            aim = BodyMotion.OnSurface(body, new Vector3d(world.X, world.Y, world.Z));
            return true;
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
            if (Blank != null) return RigFrame.Blank;
            if (!solved) return RigFrame.Skip;

            CommandCameraMath.AimBasis(station, aim, localUp, north, out var fwd, out var up);
            anchor.position = new Vector3((float)station.X, (float)station.Y, (float)station.Z);
            anchor.rotation = Quaternion.LookRotation(ToF(fwd), ToF(up));
            _poseValid = true;
            _poseEye = station;
            _poseForward = fwd;
            _poseUp = up;
            SyncScaledBodies(cam, anchor.position);
            SyncScaledPose(cam);
            UpdateStationBodyMesh(cam, ut, _lastDistance);

            double distance = _lastDistance;
            /* The aim frames the view; the true craft, wherever it really is,
               decides what is drawn and how the layers are clipped around it. */
            bool trueOk = _trueOk;
            double trueDistance = trueOk ? _trueDistance : distance;
            bool trueOccluded = trueOk ? _trueOccluded : occluder != null;
            bool trueLit = trueOk ? _trueSunlit : Sunlit;
            RefreshCraftRadius(trueOk ? _target.Loaded : null, _truePos, now);

            /* A loaded craft draws itself; the model and the marker stand in only
               for an unloaded one, and only where a real craft would be seen. */
            bool standIn = trueOk && _target.Loaded == null;
            var towardTrue = ((trueOk ? _truePos : aim) - station).Normalized;
            double planDistance = trueDistance;
            double planScale = 1.0;
            if (_wantModel && standIn)
            {
                EnsureModel(cam);
                if (_model != null)
                {
                    _craftRadius = _model.Radius;
                    /* Beyond ModelProxyDistance the model stands in at that
                       distance on the true ray, shrunk by the same ratio: the
                       same angular size and perspective, with local-space
                       precision and clip planes that stay simple. */
                    planDistance = Math.Min(trueDistance, ModelProxyDistance);
                    planScale = planDistance / trueDistance;
                    var at = station + towardTrue * planDistance;
                    _model.Place(trueLit && !trueOccluded, at, _target.SavedRotation(_lastTargetBody), (float)planScale);
                    NoteModelMode(ut, trueDistance, planDistance, planScale);
                }
            }

            double fovTarget = CommandCameraMath.FovForFraction(_craftRadius + FramingMargin, distance, _frameFraction, FovMinNow(), _fovMax);
            _fov = CommandCameraMath.SmoothFov(_fov, fovTarget, dt, FovTau, FovSnapAfter);
            cam.SetRigFov((float)_fov);
            UpdateBracket(now);
            UpdateSubpixel(cam, ut, trueDistance, planDistance, planScale, trueOccluded, trueLit, standIn);

            _plan = CommandCameraMath.PlanClips(planDistance, _craftRadius * planScale, trueOccluded, _hasFar, _wasSplit,
                _nearNativeNear, _nearNativeFar, _farNativeNear, _farNativeFar);
            ApplyPlan(cam);

            if (_marker != null)
            {
                var rt = cam.NearLayer != null ? cam.NearLayer.targetTexture : null;
                _marker.Place(trueLit && standIn && !trueOccluded, station, towardTrue, trueDistance, anchor.rotation,
                    (float)_fov, rt != null ? rt.height : cam.RenderHeight);
            }

            if (_first)
            {
                _first = false;
                var ksp = site.Body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Altitude);
                double err = (ToD(ksp) - station).Magnitude;
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={distance:F0} event=first-frame "
                    + $"station={station} aim={aim} kspSurfaceCheckErr={err:F4}m craftRadius={_craftRadius:F1} fov={_fov:F3}");
            }

            if (now - _skyProbeAt >= SkyProbeSeconds)
            {
                _skyProbeAt = now;
                cam.RigScaledProbe = true;
            }

            LogTransitions(ut, distance, aim, fwd, localUp, now);
            return RigFrame.Render;
        }

        public bool TryRenderPose(out DVec3 eye, out DVec3 forward, out DVec3 up)
        {
            eye = _poseEye;
            forward = _poseForward;
            up = _poseUp;
            return _poseValid;
        }

        /* The subject as a vessel anchor with occlusion on: where the craft
           really is in each frame, wherever the camera aims, sized by its
           radius plus the framing margin. Kerbcast's own anchors are always
           observed-only (AnchorRules), so it carries only what the picture
           shows. Under a held lock the owner can withhold it. */
        public void AddOwnAnchors(List<KeyValuePair<string, AnchorSpec>> into)
        {
            uint pid = _target.PersistentId;
            if (pid == 0 || !_trueOk) return;
            if (_authority != null && _authority.Registered && !_authority.SubjectAnchorVisible) return;
            _subjectAnchor.Vessel = pid;
            _subjectAnchor.MarginM = FramingMargin;
            into.Add(new KeyValuePair<string, AnchorSpec>(AnchorRegistry.KerbcastOwner, _subjectAnchor));
        }

        /* The subject where this rig solved it for the frame, with the radius
           it frames and the terrain-aware occlusion it already ran. Unloaded
           craft (the Space Center, or beyond physics range) are located from
           their orbit, which the generic projector can not do. */
        public bool TryLocateVessel(uint pid, out DVec3 world, out double radius, out bool occluded)
        {
            world = _truePos;
            radius = _craftRadius;
            occluded = _trueOccluded;
            return _trueOk && pid != 0 && pid == _target.PersistentId;
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
                if (b == _lastTargetBody && perPx > 0)
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
            if (_model == null || _subpixel == null || !standIn)
            {
                if (_model != null) _model.SubpixelMode = false;
                return;
            }
            var rt = cam.NearLayer != null ? cam.NearLayer.targetTexture : null;
            int rows = rt != null ? rt.height : cam.RenderHeight;
            double perPx = CommandCameraMath.PixelSizeAtUnit(_fov, rows);
            double across = perPx > 0 && distance > 0 ? 2.0 * _model.Radius / (distance * perPx) : 0;
            bool sub = across < SubpixelBelowPx;
            _model.SubpixelMode = sub;
            _subpixelActive = sub && across >= SubpixelFloorPx && lit && !occluded;
            double span = CommandCameraMath.ClipSpan(_model.Radius * planScale);
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

        /* Bounding radius about the CoM from part origins, refreshed twice a
           second (staging changes it) and immediately on a vessel switch. An
           unloaded craft has no parts, so it gets the minimum. */
        private void RefreshCraftRadius(Vessel v, DVec3 com, float now)
        {
            if (v == null)
            {
                _craftRadius = MinCraftRadius;
                _radiusVesselId = 0;
                return;
            }
            if (v.persistentId == _radiusVesselId && now - _radiusAt < RadiusRefreshSeconds) return;
            _radiusVesselId = v.persistentId;
            _radiusAt = now;
            _partScratch.Clear();
            var parts = v.parts;
            if (parts != null)
            {
                for (int i = 0; i < parts.Count; i++)
                {
                    var p = parts[i];
                    if (p != null && p.transform != null) _partScratch.Add(ToD(p.transform.position));
                }
            }
            _craftRadius = CommandCameraMath.BoundingRadius(_partScratch, com, PartPad, MinCraftRadius);
        }

        private void NoteTargetAndOcclusion(double ut, double d, CelestialBody targetBody, DVec3 target, string occluder,
            string source)
        {
            string at = $"{Tag} cam={_flightId} UT={F(ut)} d={d:F0}";

            if (!_targetLogged || _target.PersistentId != _targetPid)
            {
                _targetLogged = true;
                _targetPid = _target.PersistentId;
                Debug.Log($"{at} event=target vessel='{_target.Name}' persistentId={_target.PersistentId} "
                    + $"situation={_target.Situation} loaded={_target.Loaded != null} elev={ElevationDeg:F2}");
                /* Once per rig: the orbit-derived position against stock's own,
                   to show the floating-origin conversion is right. */
                if (!_stockCheckLogged && _target.Loaded == null)
                {
                    var stock = _target.StockWorldPosition();
                    if (stock.HasValue)
                    {
                        _stockCheckLogged = true;
                        Debug.Log($"{at} event=target-position-check orbit={target} stock={stock.Value} err={(stock.Value - target).Magnitude:F1}m");
                    }
                }
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
                    ? $"{at} event=occluded-on by={occluder} occluderSource={source} elev={ElevationDeg:F2} target='{_target.Name}' occlusionMs={_occlusionMs:F3}"
                    : $"{at} event=occluded-off was={_occluderName} occluderSource={_occluderSource} elev={ElevationDeg:F2} target='{_target.Name}' occlusionMs={_occlusionMs:F3}");
                _wasOccluded = occluded;
                _occluderName = occluder;
                _occluderSource = source;
            }
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
                Debug.Log($"{at} event={(atFloor ? "fov-floor-reached" : "fov-floor-left")} fov={_fov:F4} craftRadius={_craftRadius:F1}");
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
                Debug.Log($"{at} event=status elev={elev:F2} fov={_fov:F4} craftRadius={_craftRadius:F1} "
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
            _model?.Dispose();
            _model = null;
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
