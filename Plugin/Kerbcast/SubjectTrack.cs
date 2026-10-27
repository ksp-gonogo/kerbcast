using UnityEngine;

namespace Kerbcast
{
    /*
     * KSP's handles for a command feed's subject: the live active craft, or a
     * chosen craft's Vessel, conic or surface coordinates as captured when it
     * was adopted, and its saved record. A chosen craft at the Space Center is
     * unloaded, so its position comes from its orbit (or its surface
     * coordinates when landed) rather than from any model.
     *
     * Truth only. TruthFixSource decides which craft is adopted and reads it
     * as knowledge; DrawnSubject reads it as the picture. Neither reads the
     * other's results.
     */
    internal sealed class SubjectTrack
    {
        private bool _following;
        private Vessel _live;
        private Orbit _orbit;
        private CelestialBody _surfaceBody;
        private double _lat, _lon, _alt;
        private bool _onSurface;

        public uint PersistentId { get; private set; }
        public string Name { get; private set; } = "";
        public string Situation { get; private set; } = "";
        public string LaunchedFrom { get; private set; } = "";
        /* The craft's model is in the scene, so its parts can size it. */
        public Vessel Loaded { get; private set; }
        /* The saved record of a chosen craft; null when following the active one. */
        public ProtoVessel Proto { get; private set; }

        /* Reading the active craft live rather than adopted handles. */
        public bool Following => _following;
        /* A chosen craft has a conic or surface coordinates to place it by. */
        public bool HasPosition => _orbit != null || _onSurface;
        /* Placed by surface coordinates rather than a conic. */
        public bool OnSurface => _onSurface;

        /* Situation's text, rebuilt only when it changes: the light-time solve
           reads the subject many times a frame, and an enum's ToString allocates. */
        private Vessel.Situations _situation;
        private void SetSituation(Vessel.Situations s)
        {
            if (s == _situation && Situation.Length > 0) return;
            _situation = s;
            Situation = s.ToString();
        }

        /* The craft's saved orientation, which is kept relative to its body. */
        public Quaternion SavedRotation(CelestialBody body)
        {
            if (Proto == null || body == null || body.bodyTransform == null) return Quaternion.identity;
            return body.bodyTransform.rotation * Proto.rotation;
        }

        /* Drops every handle, keeping the last identity, and sets whether the active craft is followed. */
        public void Clear(bool follow)
        {
            _following = follow;
            Release();
            Loaded = null;
        }

        public void Follow(bool follow) => _following = follow;

        /* No craft at all: no handles and no identity, so nothing is drawn. */
        public void Forget()
        {
            Clear(false);
            PersistentId = 0;
            Name = "";
            Situation = "";
            LaunchedFrom = "";
        }

        /* Drops the handles before a new craft is adopted; the last loaded craft stays until the next locate. */
        public void Release()
        {
            _live = null;
            _orbit = null;
            _onSurface = false;
            Proto = null;
        }

        /* The craft could not be placed after all. */
        public void DropPosition()
        {
            _orbit = null;
            _onSurface = false;
        }

        public void Adopt(Vessel v)
        {
            _live = v;
            Proto = v.protoVessel;
            PersistentId = v.persistentId;
            Name = v.vesselName;
            SetSituation(v.situation);
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

        public void Adopt(ProtoVessel pv)
        {
            Proto = pv;
            PersistentId = pv.persistentId;
            Name = pv.vesselName;
            SetSituation(pv.situation);
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

        /* Adopts the craft with this persistentId, live first, for a reader
           whose subject was resolved by something other than KSP. When no
           such craft exists the handles are dropped, so nothing old is drawn,
           and false is returned. */
        public bool AdoptById(uint persistentId)
        {
            Vessel live = null;
            ProtoVessel proto = null;
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
                foreach (var v in vessels)
                    if (v != null && v.persistentId == persistentId) { live = v; break; }
            if (live == null)
            {
                var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protos != null)
                    foreach (var pv in protos)
                        if (RecordCounts(pv) && pv.persistentId == persistentId) { proto = pv; break; }
            }
            if (live == null && proto == null)
            {
                Clear(false);
                return false;
            }
            _following = false;
            Release();
            try
            {
                if (live != null) Adopt(live);
                else Adopt(proto);
            }
            catch (System.Exception) { DropPosition(); }
            return true;
        }

        /* The craft at `ut`: the active craft live, a chosen craft in physics
           range live, otherwise from its conic or surface coordinates. False
           with nothing adopted. */
        public bool TryLocate(double ut, out DVec3 world, out CelestialBody body)
        {
            world = default;
            body = null;
            if (_following)
            {
                var v = FlightGlobals.ActiveVessel;
                Loaded = v;
                if (v == null) return false;
                PersistentId = v.persistentId;
                Name = v.vesselName;
                SetSituation(v.situation);
                /* CoMD is only maintained while the craft is off rails; on rails
                   (time warp) its orbit position is the live one. */
                world = ToD(v.packed ? v.GetWorldPos3D() : v.CoMD);
                body = v.mainBody;
                return true;
            }
            if (!HasPosition) return false;
            Loaded = _live != null && _live.loaded ? _live : null;
            if (Loaded != null && Loaded.mainBody != null)
            {
                /* A chosen craft in physics range moves under its own power. */
                SetSituation(Loaded.situation);
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
            if (_following)
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

        /* Whether a saved record still names a real craft (see
           CommandCameraMath.SavedRecordCounts). A Vessel built from the record
           and since destroyed reads as Unity-null while still referenced. */
        public static bool RecordCounts(ProtoVessel pv)
        {
            if (pv == null) return false;
            var vessels = FlightGlobals.Vessels;
            bool authoritative = HighLogic.LoadedSceneIsFlight && vessels != null && vessels.Count > 0;
            bool built = !ReferenceEquals(pv.vesselRef, null);
            return CommandCameraMath.SavedRecordCounts(authoritative, built, built && pv.vesselRef == null);
        }

        public static bool IsSurface(Vessel.Situations s) =>
            s == Vessel.Situations.LANDED || s == Vessel.Situations.SPLASHED || s == Vessel.Situations.PRELAUNCH;

        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
    }
}
