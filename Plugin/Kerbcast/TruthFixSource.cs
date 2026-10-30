using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * Knowledge read straight from KSP: live Vessels and the save's
     * ProtoVessel records. It resolves a feed's subject request itself (the
     * active craft, a chosen craft, or the configured name or latest launch)
     * and adopts that craft's handles into the feed's SubjectTrack. This is
     * 1.11's behaviour, with every craft Positioned or Listed.
     *
     * A craft is framed by the radius the picture measured for it
     * (DrawnSubject.Radius), or by the extent of its parts and canopies
     * when it is drawn live: KSP's own size for the craft is the truth here.
     */
    internal sealed class TruthFixSource : IFixSource
    {
        private const string Tag = "[Kerbcast][CMDCAM]";
        private const float RepickSeconds = 5f;

        /* A source with no feed subject, for questions no feed asks. */
        public static readonly TruthFixSource Scope = new TruthFixSource(null, null);

        /* Scratch for List; every caller is on the main thread. */
        private static readonly HashSet<uint> Seen = new HashSet<uint>();

        private readonly SubjectSelection _selection;
        private readonly SubjectTrack _track;
        private readonly TruthResolveGate _gate;
        private float _repickAt = float.MinValue;

        /* The picture whose measured radius frames the subject, set per rig. */
        private DrawnSubject _drawn;

        /* `truthActive` says whether feeds read truth now (null: always); while
           they read a world model, requests wait for truth to come back. */
        public TruthFixSource(SubjectSelection selection, SubjectTrack track, Func<bool> truthActive = null)
        {
            _selection = selection;
            _track = track;
            if (_selection == null) return;
            _gate = new TruthResolveGate(truthActive, Resolve);
            _selection.Changed = _gate.Changed;
            _track.Clear(_selection.FollowActive);
            _gate.Changed();
            LockChanged();
        }

        /* The lock was taken or released (or the source was built under it).
           Whatever reads the feed now, a default request under the lock names
           no craft, so the shared track forgets it and the picture draws none. */
        public void LockChanged()
        {
            if (_selection == null || !NoDefault()) return;
            _track.Forget();
            _selection.ResolvedPersistentId = 0;
        }

        public bool KnowsWhatIsDrawn => true;
        public bool ListsUnplacedByCareerRules => true;
        public bool IdentityIsKsp => true;

        public uint SubjectPersistentId => _track != null ? _track.PersistentId : 0;
        public string SubjectName => _track != null ? _track.Name ?? "" : "";

        public CraftFix Subject(double ut)
        {
            if (_selection == null) return CraftFix.Unknown(0);
            _gate.BeforeRead();
            var fix = CraftFix.Unknown(0);
            bool located = LocateSubject(ut, out fix.World, out fix.Body);
            fix.PersistentId = _track.PersistentId;
            fix.Name = _track.Name ?? "";
            fix.State = located ? CraftFixState.Positioned
                : fix.PersistentId != 0 ? CraftFixState.Listed : CraftFixState.Unknown;
            if (!located) fix.Body = null;
            return fix;
        }

        public bool SubjectMotion(double ut, out BodyMotion motion, out double validFromUt, out double validUntilUt)
        {
            motion = default;
            validFromUt = double.NegativeInfinity;
            validUntilUt = double.PositiveInfinity;
            if (_selection == null) return false;
            _gate.BeforeRead();
            if (!_track.TryMotion(out var orbit, out bool onSurface)) return false;
            if (orbit != null)
            {
                if (orbit.referenceBody == null) return false;
                motion = BodyMotion.OnOrbit(orbit);
                return true;
            }
            if (!onSurface || !LocateSubject(ut, out var world, out var body) || body == null) return false;
            motion = BodyMotion.OnSurface(body, new Vector3d(world.X, world.Y, world.Z));
            return true;
        }

        /* Truth frames a craft by the size the picture measured: the one place knowledge reads the picture. */
        public double SubjectRadiusM => _drawn != null ? _drawn.Radius : double.NaN;

        public bool SubjectShape(out DVec3 centreOffset, out double radiusM)
        {
            centreOffset = default;
            radiusM = double.NaN;
            return _drawn != null && _drawn.FramingShape(out centreOffset, out radiusM);
        }

        public void MeasureWith(DrawnSubject drawn) => _drawn = drawn;

        /* A live Vessel that is not dead, or, in a scene that has not built
           it, a saved record. */
        public bool Knows(uint persistentId)
        {
            if (persistentId == 0) return false;
            if (FlightGlobals.FindVessel(persistentId, out Vessel v) && v != null && v.state != Vessel.State.DEAD) return true;
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos == null) return false;
            for (int i = 0; i < protos.Count; i++)
                if (SubjectTrack.RecordCounts(protos[i]) && protos[i].persistentId == persistentId) return true;
            return false;
        }

        public CraftFix Find(uint persistentId, double ut)
        {
            if (persistentId == 0) return CraftFix.Unknown(0);
            if (FlightGlobals.FindVessel(persistentId, out Vessel v) && v != null)
                return Fix(v, ut, true);
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos != null)
                foreach (var pv in protos)
                    if (SubjectTrack.RecordCounts(pv) && pv.persistentId == persistentId)
                        return Fix(pv, ut, true);
            return CraftFix.Unknown(persistentId);
        }

        /* Every vessel except flags, Unknown, dropped parts and the parts of
           deployed ground experiments (their controller is listed): live
           Vessels first, then the save's records for any a scene has not
           built. */
        public void List(double ut, uint exceptPersistentId, bool place, List<CraftFix> into)
        {
            Seen.Clear();
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                foreach (var v in vessels)
                {
                    if (v == null || v.persistentId == exceptPersistentId || !Listed(v.vesselType)) continue;
                    if (!Seen.Add(v.persistentId)) continue;
                    into.Add(Fix(v, ut, place));
                }
            }
            var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
            if (protos != null)
            {
                foreach (var pv in protos)
                {
                    if (!SubjectTrack.RecordCounts(pv) || !Listed(pv.vesselType) || Seen.Contains(pv.persistentId)) continue;
                    if (pv.persistentId == exceptPersistentId) continue;
                    Seen.Add(pv.persistentId);
                    into.Add(Fix(pv, ut, place));
                }
            }
        }

        /* A new request: drop the old craft and resolve the new one at once. */
        private void Resolve()
        {
            if (NoDefault())
            {
                _track.Forget();
                _selection.ResolvedPersistentId = 0;
                return;
            }
            _track.Clear(_selection.FollowActive);
            _repickAt = float.MinValue;
            if (!_selection.FollowActive) Pick();
            _selection.ResolvedPersistentId = _track.PersistentId;
        }

        /* Under the lock a feed with no aim has no subject: it holds pose, and
           the picture draws no craft for it either. */
        private bool NoDefault() => _selection.Locked && _selection.ChosenPersistentId == 0;

        /* The subject's position, choosing a craft again every RepickSeconds
           while a chosen or default one cannot be placed. */
        private bool LocateSubject(double ut, out DVec3 world, out CelestialBody body)
        {
            world = default;
            body = null;
            if (NoDefault())
            {
                if (_track.PersistentId != 0 || _track.Following) _track.Forget();
                _selection.ResolvedPersistentId = 0;
                return false;
            }
            bool located;
            if (!_track.Following && !_track.HasPosition)
            {
                if (Time.unscaledTime < _repickAt) located = false;
                else
                {
                    Pick();
                    located = _track.HasPosition && _track.TryLocate(ut, out world, out body);
                }
            }
            else located = _track.TryLocate(ut, out world, out body);
            _selection.ResolvedPersistentId = _track.PersistentId;
            return located;
        }

        private void Pick()
        {
            _repickAt = Time.unscaledTime + RepickSeconds;
            _track.Release();

            if (_selection.ChosenPersistentId != 0 && PickById()) return;

            Vessel bestLive = null;
            ProtoVessel bestProto = null;
            double bestLaunch = double.MinValue;
            string wantedName = _selection.WantedName;
            bool named = wantedName.Length > 0;
            bool nameFound = false;

            /* Live Vessel objects first; the save's proto records cover a scene
               that has not built them. */
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
            {
                foreach (var v in vessels)
                {
                    if (v == null || !Eligible(v.vesselType)) continue;
                    Consider(v.vesselName, v.launchTime, wantedName, named, ref nameFound, ref bestLaunch,
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
                        if (!SubjectTrack.RecordCounts(pv) || !Eligible(pv.vesselType)) continue;
                        Consider(pv.vesselName, pv.launchTime, wantedName, named, ref nameFound, ref bestLaunch,
                            () => { bestProto = pv; bestLive = null; });
                    }
                }
            }

            if (named && !nameFound)
                Debug.LogWarning($"{Tag} no craft named '{wantedName}' (CommandCameraTargetName); falling back to the most recently launched craft");

            try
            {
                if (bestLive != null) _track.Adopt(bestLive);
                else if (bestProto != null) _track.Adopt(bestProto);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} target could not be located: {ex.Message}");
                _track.DropPosition();
            }

            if (!_track.HasPosition)
            {
                Debug.LogWarning($"{Tag} no launched craft to watch; retrying every {RepickSeconds:F0}s");
                return;
            }
            Debug.Log($"{Tag} target chosen: vessel='{_track.Name}' persistentId={_track.PersistentId} situation={_track.Situation} "
                + $"source={(bestLive != null ? "vessel" : "protovessel")} position={(_track.OnSurface ? "surface" : "orbit")} "
                + $"launchedFrom={(string.IsNullOrEmpty(_track.LaunchedFrom) ? "?" : _track.LaunchedFrom)} "
                + (named ? $"by name '{wantedName}'" : "most recently launched"));
        }

        /* The chosen craft by persistentId, live first. A craft that no longer
           exists drops the choice and returns to the default. */
        private bool PickById()
        {
            uint wanted = _selection.ChosenPersistentId;
            Vessel live = null;
            ProtoVessel proto = null;
            var vessels = FlightGlobals.Vessels;
            if (vessels != null)
                foreach (var v in vessels)
                    if (v != null && v.persistentId == wanted) { live = v; break; }
            if (live == null)
            {
                var protos = HighLogic.CurrentGame?.flightState?.protoVessels;
                if (protos != null)
                    foreach (var pv in protos)
                        if (SubjectTrack.RecordCounts(pv) && pv.persistentId == wanted) { proto = pv; break; }
            }
            if (live == null && proto == null)
            {
                Debug.LogWarning($"{Tag} no craft with persistentId={wanted}; back to the default target");
                _selection.DropChoice();
                if (NoDefault())
                {
                    /* Back to a default the lock does not allow: no craft, not even for one solve. */
                    _track.Forget();
                    return true;
                }
                _track.Follow(_selection.FollowActive);
                return _selection.FollowActive;
            }
            try
            {
                if (live != null) _track.Adopt(live);
                else _track.Adopt(proto);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} chosen target could not be located: {ex.Message}");
                _track.DropPosition();
                return true;
            }
            Debug.Log($"{Tag} target chosen: vessel='{_track.Name}' persistentId={_track.PersistentId} situation={_track.Situation} "
                + $"source={(live != null ? "vessel" : "protovessel")} position={(_track.OnSurface ? "surface" : "orbit")} by persistentId");
            return true;
        }

        /* With a name, only matches count (latest launch wins among duplicates)
           until one is found; without one, or with no match at all, the latest
           launch overall. */
        private static void Consider(string name, double launchTime, string wantedName, bool named, ref bool nameFound,
            ref double bestLaunch, Action take)
        {
            bool match = named && string.Equals((name ?? "").Trim(), wantedName, StringComparison.OrdinalIgnoreCase);
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

        private static bool Eligible(VesselType t) =>
            t != VesselType.Debris && t != VesselType.SpaceObject && t != VesselType.Unknown
            && t != VesselType.Flag && t != VesselType.EVA && t != VesselType.DroppedPart
            && t != VesselType.DeployedScienceController && t != VesselType.DeployedSciencePart
            && t != VesselType.DeployedGroundPart;

        private static bool Listed(VesselType t) =>
            t != VesselType.Flag && t != VesselType.Unknown && t != VesselType.DroppedPart
            && t != VesselType.DeployedSciencePart && t != VesselType.DeployedGroundPart;

        /* One craft's row; unplaced when `place` is false. */
        private static CraftFix Fix(Vessel v, double ut, bool place)
        {
            var fix = CraftFix.Unknown(v.persistentId);
            fix.Name = v.GetDisplayName() ?? "";
            fix.Kind = v.vesselType.ToString();
            fix.Discovery = v.DiscoveryInfo != null ? v.DiscoveryInfo.Level : DiscoveryLevels.Owned;
            fix.State = CraftFixState.Listed;
            if (place && TryPosition(v, ut, out Vector3d at))
            {
                fix.State = CraftFixState.Positioned;
                fix.World = new DVec3(at.x, at.y, at.z);
                fix.Body = v.mainBody;
            }
            return fix;
        }

        private static CraftFix Fix(ProtoVessel pv, double ut, bool place)
        {
            var fix = CraftFix.Unknown(pv.persistentId);
            fix.Name = Localised(pv.vesselName);
            fix.Kind = pv.vesselType.ToString();
            fix.Discovery = ProtoLevel(pv);
            fix.State = CraftFixState.Listed;
            if (place && TryPosition(pv, ut, out Vector3d at, out CelestialBody body))
            {
                fix.State = CraftFixState.Positioned;
                fix.World = new DVec3(at.x, at.y, at.z);
                fix.Body = body;
            }
            return fix;
        }

        private static bool TryPosition(Vessel v, double ut, out Vector3d world)
        {
            world = default;
            if (v.mainBody == null) return false;
            if (v.loaded) world = v.packed ? v.GetWorldPos3D() : v.CoMD;
            else if (SubjectTrack.IsSurface(v.situation)) world = v.mainBody.GetWorldSurfacePosition(v.latitude, v.longitude, v.altitude);
            else if (v.orbit != null) world = v.orbit.getPositionAtUT(ut);
            else return false;
            return true;
        }

        private static bool TryPosition(ProtoVessel pv, double ut, out Vector3d world, out CelestialBody body)
        {
            world = default;
            body = null;
            var snap = pv.orbitSnapShot;
            if (snap == null) return false;
            var bodies = FlightGlobals.Bodies;
            int idx = snap.ReferenceBodyIndex;
            if (bodies == null || idx < 0 || idx >= bodies.Count || bodies[idx] == null) return false;
            try
            {
                if (SubjectTrack.IsSurface(pv.situation))
                    world = bodies[idx].GetWorldSurfacePosition(pv.latitude, pv.longitude, pv.altitude);
                else
                    world = snap.Load().getPositionAtUT(ut);
                body = bodies[idx];
                return true;
            }
            catch (Exception) { return false; }
        }

        /* A saved craft's discovery state; Owned when the record has none. */
        private static DiscoveryLevels ProtoLevel(ProtoVessel pv)
        {
            var state = pv.discoveryInfo?.GetValue("state");
            return int.TryParse(state, out int level) ? (DiscoveryLevels)level : DiscoveryLevels.Owned;
        }

        private static string Localised(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith("#", StringComparison.Ordinal)) return name ?? "";
            try { return KSP.Localization.Localizer.Format(name); }
            catch (Exception) { return name; }
        }
    }
}
