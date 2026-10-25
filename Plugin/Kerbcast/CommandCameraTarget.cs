using System;
using UnityEngine;

namespace Kerbcast
{
    /* What a command camera watches. In flight it follows the active craft. At
       the Space Center it watches one chosen craft, which is unloaded there, so
       its position comes from its orbit (or its surface coordinates when
       landed) rather than from any model. Choose swaps either default for a
       specific craft at runtime. Identified by Vessel.persistentId. */
    internal sealed class CommandCameraTarget
    {
        private const string Tag = "[Kerbcast][CMDCAM]";
        private const float RepickSeconds = 5f;

        private readonly bool _defaultFollowActive;
        private bool _followActive;
        private readonly string _wantedName;
        private uint _wantedPid;
        private float _repickAt = float.MinValue;

        private Vessel _live;
        private Orbit _orbit;
        private CelestialBody _surfaceBody;
        private double _lat, _lon, _alt;
        private bool _onSurface;

        public uint PersistentId { get; private set; }
        /* The craft last chosen by Choose (or followed through a merge); 0 for the default. */
        public uint ChosenPersistentId => _wantedPid;
        public string Name { get; private set; } = "";
        public string Situation { get; private set; } = "";
        public string LaunchedFrom { get; private set; } = "";
        /* The craft's model is in the scene, so its parts can size the framing. */
        public Vessel Loaded { get; private set; }
        /* The saved record of a chosen craft; null when following the active one. */
        public ProtoVessel Proto { get; private set; }

        /* The craft's saved orientation, which is kept relative to its body. */
        public Quaternion SavedRotation(CelestialBody body)
        {
            if (Proto == null || body == null || body.bodyTransform == null) return Quaternion.identity;
            return body.bodyTransform.rotation * Proto.rotation;
        }

        private CommandCameraTarget(bool followActive, string wantedName)
        {
            _defaultFollowActive = followActive;
            _followActive = followActive;
            _wantedName = wantedName ?? "";
        }

        public static CommandCameraTarget ActiveVessel() => new CommandCameraTarget(true, null);

        /* Empty name: the most recently launched craft that is not debris. */
        public static CommandCameraTarget Chosen(string name)
        {
            var t = new CommandCameraTarget(false, name);
            t.Pick();
            return t;
        }

        /* Watch the craft with this persistentId from now on, whether or not
           it is loaded. 0 returns to the default: the active craft, or the
           configured name at the Space Center. */
        public void Choose(uint persistentId)
        {
            _wantedPid = persistentId;
            _followActive = persistentId == 0 && _defaultFollowActive;
            _live = null;
            _orbit = null;
            _onSurface = false;
            Proto = null;
            Loaded = null;
            _repickAt = float.MinValue;
            Debug.Log($"{Tag} target choice: {(persistentId == 0 ? "default" : "persistentId=" + persistentId)}");
            if (!_followActive) Pick();
        }

        /* Docking or a claw grab merges one craft into another, and only the
           survivor keeps its persistentId. A watched craft that is absorbed is
           followed into the craft it joined instead of being dropped. KSP
           announces each merge twice; the second is a no-op. */
        public void Merged(uint oldPid, uint newPid)
        {
            if (_followActive || oldPid == newPid) return;
            if (_wantedPid != oldPid && PersistentId != oldPid) return;
            Debug.Log($"{Tag} target merged: persistentId={oldPid} -> {newPid}");
            _wantedPid = newPid;
            _live = null;
            _orbit = null;
            _onSurface = false;
            Proto = null;
            Loaded = null;
            Pick();
        }

        public bool TryLocate(double ut, out DVec3 world, out CelestialBody body)
        {
            world = default;
            body = null;
            if (_followActive)
            {
                var v = FlightGlobals.ActiveVessel;
                Loaded = v;
                if (v == null) return false;
                PersistentId = v.persistentId;
                Name = v.vesselName;
                Situation = v.situation.ToString();
                /* CoMD is only maintained while the craft is off rails; on rails
                   (time warp) its orbit position is the live one. */
                world = ToD(v.packed ? v.GetWorldPos3D() : v.CoMD);
                body = v.mainBody;
                return true;
            }

            if (_orbit == null && !_onSurface)
            {
                if (Time.unscaledTime < _repickAt) return false;
                Pick();
                if (_orbit == null && !_onSurface) return false;
            }
            Loaded = _live != null && _live.loaded ? _live : null;
            if (Loaded != null && Loaded.mainBody != null)
            {
                /* A chosen craft in physics range moves under its own power. */
                Situation = Loaded.situation.ToString();
                world = ToD(Loaded.packed ? Loaded.GetWorldPos3D() : Loaded.CoMD);
                body = Loaded.mainBody;
                return true;
            }
            if (_onSurface)
            {
                body = _surfaceBody;
                world = ToD(body.GetWorldSurfacePosition(_lat, _lon, _alt));
            }
            else
            {
                body = _orbit.referenceBody;
                world = ToD(_orbit.getPositionAtUT(ut));
            }
            return body != null;
        }

        /* How to carry the craft forward in time: the conic it is on, or null
           with `onSurface` when it sits on its body and turns with it. False
           when there is nothing to carry. A thrusting craft leaves its conic. */
        public bool TryMotion(out Orbit orbit, out bool onSurface)
        {
            orbit = null;
            onSurface = false;
            if (_followActive)
            {
                var v = FlightGlobals.ActiveVessel;
                if (v == null || v.mainBody == null) return false;
                if (IsSurface(v.situation)) onSurface = true;
                else orbit = v.orbit;
                return onSurface || orbit != null;
            }
            if (_live != null && _live.loaded && _live.mainBody != null)
            {
                if (IsSurface(_live.situation)) onSurface = true;
                else orbit = _live.orbit;
                return onSurface || orbit != null;
            }
            onSurface = _onSurface;
            orbit = _onSurface ? null : _orbit;
            return onSurface || orbit != null;
        }

        /* Stock's own world position for the live craft, for a one-off check
           against the orbit-derived one. Null when there is no live craft. */
        public DVec3? StockWorldPosition() => _live != null ? ToD(_live.GetWorldPos3D()) : (DVec3?)null;

        private void Pick()
        {
            _repickAt = Time.unscaledTime + RepickSeconds;
            _live = null;
            _orbit = null;
            _onSurface = false;
            Proto = null;

            if (_wantedPid != 0 && PickById()) return;

            Vessel bestLive = null;
            ProtoVessel bestProto = null;
            double bestLaunch = double.MinValue;
            bool named = _wantedName.Length > 0;
            bool nameFound = false;

            /* Live Vessel objects first; the save's proto records cover a scene
               that has not built them. */
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                foreach (var v in vessels)
                {
                    if (v == null || !Eligible(v.vesselType)) continue;
                    Consider(v.vesselName, v.launchTime, named, ref nameFound, ref bestLaunch,
                        () => { bestLive = v; bestProto = null; });
                }
            }
            if (bestLive == null)
            {
                var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protos != null)
                {
                    foreach (var pv in protos)
                    {
                        if (!CommandCameraTargets.RecordCounts(pv) || !Eligible(pv.vesselType)) continue;
                        Consider(pv.vesselName, pv.launchTime, named, ref nameFound, ref bestLaunch,
                            () => { bestProto = pv; bestLive = null; });
                    }
                }
            }

            if (named && !nameFound)
                Debug.LogWarning($"{Tag} no craft named '{_wantedName}' (CommandCameraTargetName); falling back to the most recently launched craft");

            try
            {
                if (bestLive != null) Adopt(bestLive);
                else if (bestProto != null) Adopt(bestProto);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} target could not be located: {ex.Message}");
                _orbit = null;
                _onSurface = false;
            }

            if (_orbit == null && !_onSurface)
            {
                Debug.LogWarning($"{Tag} no launched craft to watch; retrying every {RepickSeconds:F0}s");
                return;
            }
            Debug.Log($"{Tag} target chosen: vessel='{Name}' persistentId={PersistentId} situation={Situation} "
                + $"source={(bestLive != null ? "vessel" : "protovessel")} position={(_onSurface ? "surface" : "orbit")} "
                + $"launchedFrom={(string.IsNullOrEmpty(LaunchedFrom) ? "?" : LaunchedFrom)} "
                + (named ? $"by name '{_wantedName}'" : "most recently launched"));
        }

        /* The chosen craft by persistentId, live first. A craft that no longer
           exists drops the choice and returns to the default. */
        private bool PickById()
        {
            Vessel live = null;
            ProtoVessel proto = null;
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
                foreach (var v in vessels)
                    if (v != null && v.persistentId == _wantedPid) { live = v; break; }
            if (live == null)
            {
                var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protos != null)
                    foreach (var pv in protos)
                        if (CommandCameraTargets.RecordCounts(pv) && pv.persistentId == _wantedPid) { proto = pv; break; }
            }
            if (live == null && proto == null)
            {
                Debug.LogWarning($"{Tag} no craft with persistentId={_wantedPid}; back to the default target");
                _wantedPid = 0;
                _followActive = _defaultFollowActive;
                return _followActive;
            }
            try
            {
                if (live != null) Adopt(live);
                else Adopt(proto);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} chosen target could not be located: {ex.Message}");
                _orbit = null;
                _onSurface = false;
                return true;
            }
            Debug.Log($"{Tag} target chosen: vessel='{Name}' persistentId={PersistentId} situation={Situation} "
                + $"source={(live != null ? "vessel" : "protovessel")} position={(_onSurface ? "surface" : "orbit")} by persistentId");
            return true;
        }

        /* With a name, only matches count (latest launch wins among duplicates)
           until one is found; without one, or with no match at all, the latest
           launch overall. */
        private void Consider(string name, double launchTime, bool named, ref bool nameFound,
            ref double bestLaunch, Action take)
        {
            bool match = named && string.Equals((name ?? "").Trim(), _wantedName, StringComparison.OrdinalIgnoreCase);
            if (match && !nameFound)
            {
                nameFound = true;
                bestLaunch = double.MinValue;
            }
            if (nameFound && !match) return;
            if (launchTime <= bestLaunch) return;
            bestLaunch = launchTime;
            take();
        }

        private void Adopt(Vessel v)
        {
            _live = v;
            Proto = v.protoVessel;
            PersistentId = v.persistentId;
            Name = v.vesselName;
            Situation = v.situation.ToString();
            LaunchedFrom = v.launchedFrom;
            if (IsSurface(v.situation))
            {
                _onSurface = true;
                _surfaceBody = v.mainBody;
                _lat = v.latitude; _lon = v.longitude; _alt = v.altitude;
                if (_surfaceBody == null) _onSurface = false;
            }
            else
            {
                _orbit = v.orbit;
            }
        }

        private void Adopt(ProtoVessel pv)
        {
            Proto = pv;
            PersistentId = pv.persistentId;
            Name = pv.vesselName;
            Situation = pv.situation.ToString();
            LaunchedFrom = pv.launchedFrom;
            var orbit = pv.orbitSnapShot != null ? pv.orbitSnapShot.Load() : null;
            if (IsSurface(pv.situation))
            {
                var bodies = FlightGlobals.Bodies;
                int idx = pv.orbitSnapShot != null ? pv.orbitSnapShot.ReferenceBodyIndex : -1;
                _surfaceBody = idx >= 0 && bodies != null && idx < bodies.Count ? bodies[idx] : FlightGlobals.GetHomeBody();
                _lat = pv.latitude; _lon = pv.longitude; _alt = pv.altitude;
                _onSurface = _surfaceBody != null;
            }
            else
            {
                _orbit = orbit;
            }
        }

        private static bool IsSurface(Vessel.Situations s) =>
            s == Vessel.Situations.LANDED || s == Vessel.Situations.SPLASHED || s == Vessel.Situations.PRELAUNCH;

        private static bool Eligible(VesselType t) =>
            t != VesselType.Debris && t != VesselType.SpaceObject && t != VesselType.Unknown
            && t != VesselType.Flag && t != VesselType.EVA && t != VesselType.DroppedPart
            && t != VesselType.DeployedScienceController && t != VesselType.DeployedSciencePart
            && t != VesselType.DeployedGroundPart;

        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
    }
}
