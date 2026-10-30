using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * Knowledge read only from the holder's published world model: the scope
     * the site is bound to, as captured for this frame. Nothing here reads
     * KSP craft state. A craft with no record in that scope is unknown, an
     * unbound site knows no craft, and identity is the model's: an aim's
     * craft follows the model's aliases, not KSP's merges.
     *
     * A positioned record is placed at the asked UT by the kinematics on the
     * snapshot's own arrays: its conic, its state vector propagated
     * ballistically, or its surface point carried by the body's rotation,
     * in KSP's fixed frame and turned into this frame's world axes. No KSP
     * Orbit is built. Bodies are flightGlobalsIndex. A read that throws
     * refuses for the frame and is logged once; nothing throws out of here.
     */
    internal sealed class ProviderFixSource : IFixSource
    {
        private const string Tag = "[Kerbcast][WORLD]";
        private const string SourcesMemoKey = "provider.sources";
        private const string ErrorCode = "error";

        private static readonly Func<int, bool> BodyKnownFn = BodyKnown;

        private readonly WorldModelStore _store;
        private readonly SiteBindings _sites;
        private readonly Func<CommandCameraSite> _site;
        private readonly string _siteKey;
        private readonly SubjectSelection _selection;
        private readonly uint _flightId;

        private long _scopeFrame = long.MinValue;
        private long _scopeGeneration = -1;
        private string _scopeSiteKey;
        private int _scope = -1;

        private long _subjectFrame = long.MinValue;
        private long _subjectGeneration = -1;
        private uint _subjectWanted;
        private double _subjectUt = double.NaN;
        private CraftFix _subjectFix;

        private uint _subjectPid;
        private string _subjectName = "";
        private bool _threwLogged;

        /* A feed's source: its site as it is now, its subject request, and its flightId for the log. */
        public ProviderFixSource(WorldModelStore store, SiteBindings sites, Func<CommandCameraSite> site,
            SubjectSelection selection, uint flightId)
        {
            _store = store;
            _sites = sites;
            _site = site;
            _selection = selection;
            _flightId = flightId;
        }

        private ProviderFixSource(WorldModelStore store, string siteKey)
        {
            _store = store;
            _siteKey = siteKey;
        }

        /* The shared source for questions from one site key (null for none) that no feed asks. Main thread. */
        public static ProviderFixSource ForKey(WorldModelStore store, string siteKey)
        {
            if (!(store.Memos.TryGetValue(SourcesMemoKey, out var o) && o is Dictionary<string, ProviderFixSource> sources))
            {
                sources = new Dictionary<string, ProviderFixSource>(StringComparer.Ordinal);
                store.Memos[SourcesMemoKey] = sources;
            }
            string key = siteKey ?? "";
            if (!sources.TryGetValue(key, out var source))
            {
                source = new ProviderFixSource(store, siteKey);
                sources[key] = source;
            }
            return source;
        }

        public bool KnowsWhatIsDrawn => false;
        public bool ListsUnplacedByCareerRules => false;
        public bool IdentityIsKsp => false;

        public uint SubjectPersistentId => _subjectPid;
        public string SubjectName => _subjectName;

        public CraftFix Subject(double ut)
        {
            if (_selection == null) return CraftFix.Unknown(0);
            uint wanted = _selection.FollowActive ? 0 : _selection.ChosenPersistentId;
            CraftFix fix;
            try
            {
                var view = Capture(ut);
                long generation = view.Snapshot != null ? view.Snapshot.Generation : 0;
                if (_subjectFrame == Time.frameCount && _subjectGeneration == generation && _subjectWanted == wanted && _subjectUt == ut)
                    fix = _subjectFix;
                else
                {
                    fix = wanted == 0 ? CraftFix.Unknown(0) : Placed(view, ModelLookup.Find(view, ScopeIndex(view), wanted, ut, BodyKnownFn), ut, true);
                    _subjectFrame = Time.frameCount;
                    _subjectGeneration = generation;
                    _subjectWanted = wanted;
                    _subjectUt = ut;
                    _subjectFix = fix;
                }
            }
            catch (Exception ex)
            {
                Threw(ex);
                fix = CraftFix.Refused(wanted, ErrorCode, null);
            }
            _subjectPid = fix.State == CraftFixState.Unknown ? 0 : fix.PersistentId;
            _subjectName = _subjectPid == 0 ? "" : fix.Name ?? "";
            _selection.ResolvedPersistentId = _subjectPid;
            return fix;
        }

        public bool SubjectMotion(double ut, out BodyMotion motion, out double validFromUt, out double validUntilUt)
        {
            motion = default;
            validFromUt = double.NegativeInfinity;
            validUntilUt = double.NegativeInfinity;
            if (_subjectPid == 0) return false;
            try
            {
                var view = Capture(ut);
                var fix = ModelLookup.Find(view, ScopeIndex(view), _subjectPid, ut, BodyKnownFn);
                if (fix.State != CraftFixState.Positioned) return false;
                var r = fix.Record;
                validFromUt = r.ValidFromUt;
                var world = KinematicsWorld.Now();
                var body = world.Body(r.BodyIndex);
                if (body == null) return false;
                var kb = world.Of(body);
                if (r.Kinematics == Api.KinematicsKind.Surface)
                {
                    if (!view.Snapshot.TryStateAt(r, kb, ut, out var rel, out _)) return false;
                    var at = world.ToWorld(body, rel);
                    if (!Finite(at)) return false;
                    motion = BodyMotion.OnSurface(body, new Vector3d(at.X, at.Y, at.Z));
                }
                else
                {
                    if (!view.Snapshot.TryPath(r, kb, out var path) || !path.TryAt(ut, out _, out _)) return false;
                    motion = BodyMotion.OnPath(body, path, world.Zup);
                }
                validUntilUt = r.ValidUntilUt;
                return true;
            }
            catch (Exception ex)
            {
                Threw(ex);
                return false;
            }
        }

        public double SubjectRadiusM
        {
            get
            {
                if (_subjectPid == 0) return double.NaN;
                try
                {
                    var view = Capture(double.NaN);
                    return ModelLookup.Find(view, ScopeIndex(view), _subjectPid, double.NaN, BodyKnownFn).RadiusM;
                }
                catch (Exception ex)
                {
                    Threw(ex);
                    return double.NaN;
                }
            }
        }

        /* A world model gives a radius, not a shape. */
        public bool SubjectShape(out DVec3 centreOffset, out double radiusM)
        {
            centreOffset = default;
            radiusM = double.NaN;
            return false;
        }

        public bool Knows(uint persistentId)
        {
            try
            {
                var view = Capture(double.NaN);
                return ModelLookup.Find(view, ScopeIndex(view), persistentId, double.NaN, BodyKnownFn).State != CraftFixState.Unknown;
            }
            catch (Exception ex)
            {
                Threw(ex);
                return false;
            }
        }

        public CraftFix Find(uint persistentId, double ut)
        {
            try
            {
                var view = Capture(ut);
                return Placed(view, ModelLookup.Find(view, ScopeIndex(view), persistentId, ut, BodyKnownFn), ut, true);
            }
            catch (Exception ex)
            {
                Threw(ex);
                return CraftFix.Refused(persistentId, ErrorCode, null);
            }
        }

        public void List(double ut, uint exceptPersistentId, bool place, List<CraftFix> into)
        {
            int start = into.Count;
            try
            {
                var view = Capture(ut);
                int scope = ScopeIndex(view);
                if (scope < 0) return;
                var snap = view.Snapshot;
                for (int i = 0, n = snap.CountIn(scope); i < n; i++)
                {
                    var r = snap.RecordAt(scope, i);
                    if (r.PersistentId == exceptPersistentId) continue;
                    into.Add(Placed(view, ModelLookup.Of(view, scope, r, ut, BodyKnownFn), ut, place));
                }
            }
            catch (Exception ex)
            {
                Threw(ex);
                into.RemoveRange(start, into.Count - start);
            }
        }

        /* The lookup as a CraftFix, positioned at `ut` when it is Positioned and `place` asks. */
        private CraftFix Placed(WorldModelView view, ModelFix m, double ut, bool place)
        {
            var fix = CraftFix.Unknown(m.PersistentId);
            fix.State = m.State;
            fix.Name = m.Name;
            fix.Kind = m.Kind;
            fix.Discovery = null;
            fix.RadiusM = m.RadiusM;
            fix.RefusalCode = m.RefusalCode;
            fix.RefusalText = m.RefusalText;
            if (fix.State != CraftFixState.Positioned) return fix;
            fix.UncertaintyM = ModelRules.UncertaintyAt(m.Record, ut);
            if (!place) return fix;
            if (!TryPlace(view.Snapshot, m.Record, ut, out var world, out var body))
            {
                fix.State = CraftFixState.Refused;
                fix.RefusalCode = CommandCameraMath.RefusalStale;
                fix.UncertaintyM = double.NaN;
                return fix;
            }
            fix.World = world;
            fix.Body = body;
            return fix;
        }

        private WorldModelView Capture(double ut) =>
            _store.Capture(Time.frameCount, double.IsNaN(ut) ? Planetarium.GetUniversalTime() : ut);

        /* The scope this source reads, -1 for none, worked out once per frame and snapshot. An unbound feed is logged once. */
        private int ScopeIndex(WorldModelView view)
        {
            long generation = view.Snapshot != null ? view.Snapshot.Generation : 0;
            string key = SiteKey(out var kind);
            if (_scopeFrame == Time.frameCount && _scopeGeneration == generation && string.Equals(_scopeSiteKey, key, StringComparison.Ordinal))
                return _scope;
            _scopeFrame = Time.frameCount;
            _scopeGeneration = generation;
            _scopeSiteKey = key;
            if (kind.HasValue) _store.NoteSite(key, kind.Value);
            _scope = ModelLookup.ScopeFor(view, key, out bool bound);
            if (!bound && view.Snapshot != null && _sites != null && key != null && _sites.FirstUnbound(_flightId, key))
                Debug.Log($"{Tag} cam={_flightId} site '{key}' is not bound to any scope; every craft is unknown to it");
            return _scope;
        }

        private string SiteKey(out Api.SiteKind? kind)
        {
            kind = null;
            if (_site == null) return _siteKey;
            var site = _site();
            if (site == null) return null;
            kind = site.Kind;
            return site.NodeName;
        }

        private static bool TryPlace(WorldModelSnapshot snap, in ModelRecord r, double ut, out DVec3 world, out CelestialBody body)
        {
            world = default;
            var kinematics = KinematicsWorld.Now();
            body = kinematics.Body(r.BodyIndex);
            if (body == null || !snap.TryStateAt(r, kinematics.Of(body), ut, out var rel, out _)) return false;
            world = kinematics.ToWorld(body, rel);
            if (!Finite(world)) return false;
            float now = Time.realtimeSinceStartup;
            if (now >= _nextCheckAt)
            {
                _nextCheckAt = now + CheckIntervalS;
                CheckAgainstKsp(r, body, ut, world);
            }
            return true;
        }

        private const float CheckIntervalS = 60f;
        private static float _nextCheckAt;

        /*
         * Logs how far one placement is from where KSP's own maths put the same
         * record, at most once a minute, as the rig's evidence that the frames
         * agree: a conic through new Orbit, a state vector through
         * UpdateFromFixedVectors (the fixed frame the record is in), a surface
         * point through GetWorldSurfacePosition at the present UT only.
         */
        private static void CheckAgainstKsp(in ModelRecord r, CelestialBody body, double ut, DVec3 world)
        {
            try
            {
                Vector3d ksp;
                string kind;
                if (r.Kinematics == Api.KinematicsKind.Conic)
                {
                    kind = "conic";
                    ksp = new Orbit(r.Inclination, r.Eccentricity, r.SemiMajorAxis, r.Lan, r.ArgumentOfPeriapsis,
                        r.MeanAnomalyAtEpoch, r.Epoch, body).getPositionAtUT(ut);
                }
                else if (r.Kinematics == Api.KinematicsKind.StateVector)
                {
                    kind = "stateVector";
                    var o = new Orbit();
                    o.UpdateFromFixedVectors(new Vector3d(r.Position.X, r.Position.Y, r.Position.Z),
                        new Vector3d(r.Velocity.X, r.Velocity.Y, r.Velocity.Z), body, r.StateUt);
                    ksp = o.getPositionAtUT(ut);
                }
                else
                {
                    if (ut != Planetarium.GetUniversalTime()) return;
                    kind = "surface";
                    ksp = body.GetWorldSurfacePosition(r.Latitude, r.Longitude, r.Altitude);
                }
                double err = (new DVec3(ksp.x, ksp.y, ksp.z) - world).Magnitude;
                Debug.Log($"{Tag} event=kinematics-check pid={r.PersistentId} kind={kind} body={body.bodyName} "
                    + $"inverseRotation={Planetarium.InverseRotAngle:F3} errM={err:G4}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Tag} event=kinematics-check threw: {ex.Message}");
            }
        }

        private void Threw(Exception ex)
        {
            if (_threwLogged) return;
            _threwLogged = true;
            Debug.LogWarning($"{Tag} cam={_flightId} reading the world model threw; that read refuses (logged once): {ex}");
        }

        private static bool BodyKnown(int index) => KinematicsWorld.Now().Body(index) != null;

        private static bool Finite(DVec3 v) => Kinematics.Finite(v.X) && Kinematics.Finite(v.Y) && Kinematics.Finite(v.Z);
    }
}
