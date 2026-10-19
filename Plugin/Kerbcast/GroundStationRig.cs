using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Kerbcast
{
    /* Points a KerbcastCamera's layer stack from a fixed ground station at the
       active craft. Each rendered tick it recomputes the station's world
       position from body + lat/lon/alt (so floating-origin shifts, Krakensbane
       and body rotation are absorbed), aims at the craft's CoM with the horizon
       level, zooms so the craft spans a set fraction of the frame, and replans
       the local layers' clip planes for the range (see GroundStationMath.PlanClips).

       Every layer, clip, occlusion and frame transition is logged once with UT
       and range under [Kerbcast][GROUNDCAM], so a pop or blank frame in a
       capture can be pinned to its cause. */
    internal sealed class GroundStationRig : ICameraRig
    {
        private const string Tag = "[Kerbcast][GROUNDCAM]";
        private const double FovTau = 0.4;
        private const double FovSnapAfter = 1.0;
        private const float RadiusRefreshSeconds = 0.5f;
        private const double PartPad = 1.5;
        private const double MinCraftRadius = 2.0;
        private const float StatusLogSeconds = 1.0f;
        private const int MaxOriginShiftLogsPerSecond = 4;

        private readonly GroundStationMount _mount;
        private readonly uint _flightId;
        private readonly double _frameFraction;
        private readonly double _fovMin;
        private readonly double _fovMax;

        private readonly float _nearNativeNear;
        private readonly float _nearNativeFar;
        private readonly bool _hasFar;
        private readonly float _farNativeNear;
        private readonly float _farNativeFar;
        private Camera _midCam;

        private GroundClipPlan _plan;
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
        private Guid _targetId;
        private string _targetBody;
        private double _lastDistance;
        private float _statusAt = float.MinValue;
        private int _originShifts;
        private int _originShiftLogsThisSecond;
        private float _originShiftWindowAt;

        public GroundStationRig(KerbcastCamera cam, GroundStationMount mount,
            double frameFraction, double fovMin, double fovMax)
        {
            _mount = mount;
            _flightId = cam.FlightId;
            _frameFraction = frameFraction;
            _fovMin = fovMin;
            _fovMax = fovMax;
            _fov = fovMax;

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

            GameEvents.onFloatingOriginShift.Add(OnOriginShift);
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

        public bool PrepareRender(KerbcastCamera cam)
        {
            var v = FlightGlobals.ActiveVessel;
            var site = _mount.Site;
            var body = site.Body;
            var anchor = _mount.PartTransform;
            if (v == null || body == null || anchor == null) return false;

            float now = Time.unscaledTime;
            double dt = _lastPrepare < 0 ? double.MaxValue : now - _lastPrepare;
            _lastPrepare = now;
            double ut = Planetarium.GetUniversalTime();

            var frame = body.BodyFrame;
            var bodyPos = ToD(body.position);
            var station = GroundStationMath.SurfacePosition(
                bodyPos, ToD(frame.X), ToD(frame.Y), ToD(frame.Z),
                body.Radius, site.Latitude, site.Longitude, site.Altitude);
            /* CoMD is only maintained while the craft is off rails; on rails
               (time warp) its orbit position is the live one. */
            var target = ToD(v.packed ? v.GetWorldPos3D() : v.CoMD);
            var localUp = (station - bodyPos).Normalized;
            var z = ToD(frame.Z);
            var north = new DVec3(z.X, z.Z, z.Y);

            GroundStationMath.AimBasis(station, target, localUp, north, out var fwd, out var up);
            anchor.position = new Vector3((float)station.X, (float)station.Y, (float)station.Z);
            anchor.rotation = Quaternion.LookRotation(ToF(fwd), ToF(up));

            double distance = (target - station).Magnitude;
            _lastDistance = distance;

            RefreshCraftRadius(v, target, now);

            string occluder = null;
            var bodies = FlightGlobals.Bodies;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b == null) continue;
                if (!GroundStationMath.SegmentClearOfSphere(station, target, ToD(b.position), b.Radius))
                {
                    occluder = b.bodyName;
                    break;
                }
            }
            bool occluded = occluder != null;

            double fovTarget = GroundStationMath.FovForFraction(_craftRadius, distance, _frameFraction, _fovMin, _fovMax);
            _fov = GroundStationMath.SmoothFov(_fov, fovTarget, dt, FovTau, FovSnapAfter);
            cam.SetRigFov((float)_fov);

            _plan = GroundStationMath.PlanClips(distance, _craftRadius, occluded, _hasFar, _wasSplit,
                _nearNativeNear, _nearNativeFar, _farNativeNear, _farNativeFar);
            ApplyPlan(cam);

            if (_first)
            {
                _first = false;
                var ksp = body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Altitude);
                double err = (ToD(ksp) - station).Magnitude;
                Debug.Log($"{Tag} cam={_flightId} UT={F(ut)} d={distance:F0} event=first-frame "
                    + $"station={station} target={target} kspSurfaceCheckErr={err:F4}m craftRadius={_craftRadius:F1} fov={_fov:F3}");
            }

            LogTransitions(ut, distance, v, occluder, fwd, localUp, now);
            return true;
        }

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
            if (_midCam == null || !_plan.MidActive) return;
            if ((cam.Layers & CameraLayers.Far) == 0) return;
            var near = cam.NearLayer;
            if (near == null) return;
            _midCam.targetTexture = near.targetTexture;
            _midCam.fieldOfView = (float)_fov;
            _midCam.Render();
        }

        /* Bounding radius about the CoM from part origins, refreshed twice a
           second (staging changes it) and immediately on a vessel switch. */
        private void RefreshCraftRadius(Vessel v, DVec3 com, float now)
        {
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
            _craftRadius = GroundStationMath.BoundingRadius(_partScratch, com, PartPad, MinCraftRadius);
        }

        private void LogTransitions(double ut, double d, Vessel v, string occluder, DVec3 fwd, DVec3 localUp, float now)
        {
            string at = $"{Tag} cam={_flightId} UT={F(ut)} d={d:F0}";

            if (v.id != _targetId)
            {
                _targetId = v.id;
                Debug.Log($"{at} event=target vessel='{v.vesselName}' id={v.id} craftRadius={_craftRadius:F1}");
            }
            string tb = v.mainBody != null ? v.mainBody.bodyName : "?";
            if (tb != _targetBody)
            {
                if (_targetBody != null) Debug.Log($"{at} event=target-soi {_targetBody}->{tb}");
                _targetBody = tb;
            }
            if (_plan.Split != _wasSplit)
            {
                _wasSplit = _plan.Split;
                Debug.Log(_plan.Split
                    ? $"{at} event=split-on mid=[{_plan.MidNear:F0},{_plan.MidFar:F0}] far=[{_plan.FarNear:F0},{_plan.FarFar:F0}]"
                    : $"{at} event=split-off far=[{_plan.FarNear:F0},{_plan.FarFar:F0}]");
            }
            bool occluded = occluder != null;
            if (occluded != _wasOccluded || (occluded && occluder != _occluderName))
            {
                _wasOccluded = occluded;
                _occluderName = occluder;
                Debug.Log(occluded
                    ? $"{at} event=occluded-on by={occluder} farRender={_plan.FarRender} mid={(_plan.MidActive ? $"[{_plan.MidNear:F0},{_plan.MidFar:F0}]" : "off")} nearFar={_plan.NearFar:F0}"
                    : $"{at} event=occluded-off");
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
                double elev = GroundStationMath.ElevationDeg(fwd, localUp);
                Debug.Log($"{at} event=status elev={elev:F2} fov={_fov:F4} craftRadius={_craftRadius:F1} "
                    + $"split={_plan.Split} occluded={occluder != null} "
                    + (_hasFar ? $"far={(_plan.FarRender ? $"[{_plan.FarNear:F0},{_plan.FarFar:F0}]" : "skipped")} mid={(_plan.MidActive ? $"[{_plan.MidNear:F0},{_plan.MidFar:F0}]" : "off")} " : "")
                    + $"nearFar={_plan.NearFar:F0} originShifts={_originShifts} alt={v.altitude:F0} body={tb}");
                _originShifts = 0;
            }
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
            if (_originShiftLogsThisSecond >= MaxOriginShiftLogsPerSecond) return;
            _originShiftLogsThisSecond++;
            Debug.Log($"{Tag} cam={_flightId} UT={F(Planetarium.GetUniversalTime())} d={_lastDistance:F0} "
                + $"event=origin-shift offset={offset.magnitude:F1}");
        }

        public void Dispose()
        {
            GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
            if (_midCam != null) UnityEngine.Object.Destroy(_midCam.gameObject);
            _midCam = null;
            _mount.DestroyAnchor();
            Debug.Log($"{Tag} cam={_flightId} rig down");
        }

        private static string F(double ut) => ut.ToString("F2", CultureInfo.InvariantCulture);
        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
        private static DVec3 ToD(Vector3 v) => new DVec3(v.x, v.y, v.z);
        private static Vector3 ToF(DVec3 v) => new Vector3((float)v.X, (float)v.Y, (float)v.Z);
    }
}
