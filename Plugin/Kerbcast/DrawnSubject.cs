using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /*
     * The feed's subject as the picture draws it: where the craft is
     * rendered, whether its own model is in the scene, the saved record a
     * stand-in is built from, and its measured size for clip planning. It
     * reads KSP truth at the render UT, or the camera's picture pass at the
     * craft's past instant, for whichever craft the feed's fix source
     * resolved its selection to. Nothing here decides what the camera knows;
     * that goes through IFixSource.
     */
    internal sealed class DrawnSubject
    {
        private const float RadiusRefreshSeconds = 0.5f;
        private const double PartPad = 1.5;
        public const double MinRadius = 2.0;

        private readonly SubjectSelection _selection;
        private readonly SubjectTrack _truth;
        private readonly PicturePass _picture;
        /* A craft the picture could not find, not looked for again until the request changes. */
        private uint _missingPid;
        private int _missingVersion;
        private float _radiusAt = float.MinValue;
        private uint _radiusVesselId;
        private readonly List<DVec3> _partScratch = new List<DVec3>();

        public DrawnSubject(SubjectSelection selection, SubjectTrack truth, PicturePass picture)
        {
            _selection = selection;
            _truth = truth;
            _picture = picture;
        }

        public uint PersistentId => _truth.PersistentId;
        public string Situation => _truth.Situation;
        /* The craft's model is in the scene, so it draws itself and its parts size it. */
        public Vessel Loaded => _truth.Loaded;
        /* The saved record a stand-in model is built from. */
        public ProtoVessel Proto => _truth.Proto;
        public Quaternion SavedRotation(CelestialBody body) => _truth.SavedRotation(body);

        /* Where the craft is drawn from `lens` at its UT. Under truth the fix
           source has already adopted the resolved craft; any other source
           leaves the track to the picture, which adopts the craft it resolved.
           Under a light speed an unloaded craft is drawn where its arriving
           light left it, or not at all; so is one truth has lost (destroyed,
           recovered), by its last id, until its last light arrives. A loaded
           one is still live. */
        public bool TryLocate(PictureLens lens, out DVec3 world, out CelestialBody body)
        {
            uint pid = _selection.ResolvedPersistentId;
            if (pid == 0 && _selection.Locked)
            {
                /* Under the lock a feed with no subject draws no craft, whatever the track last held. */
                ShownUt = lens.Ut;
                ShownPast = false;
                world = default;
                body = null;
                return false;
            }
            bool missing = pid == _missingPid && _selection.Version == _missingVersion;
            if (pid != 0 && !_truth.Following && pid != _truth.PersistentId && !missing && !_truth.AdoptById(pid))
            {
                _missingPid = pid;
                _missingVersion = _selection.Version;
            }
            ShownUt = lens.Ut;
            ShownPast = false;
            ShownParts = null;
            Hidden = false;
            bool located = _truth.TryLocate(lens.Ut, out world, out body);
            if (LightTime.LightSpeed.HasValue && Loaded != null) Hidden = _picture.Hides(Loaded);
            if (!LightTime.LightSpeed.HasValue || (located && Loaded != null && !Hidden))
            {
                _lineage = _truth.PersistentId;
                return located;
            }
            bool seen = _picture.TrySeeSubject(_truth.PersistentId, ref _lineage, lens, out var past);
            if (!seen) return false;
            world = past.World;
            body = past.Body;
            ShownUt = past.Ut;
            ShownPast = true;
            ShownRotation = past.Rotation;
            ShownPid = past.Pid;
            ShownParts = past.Parts;
            PastSizeM = past.SizeM;
            return true;
        }

        /* Loaded, but hidden from this camera beyond the boundary: a stand-in built from the history draws it. */
        public bool Hidden { get; private set; }

        /* The craft id the picture last drew for the subject, which a merge at the present leaves in place until its light arrives. */
        private uint _lineage;

        /* The craft's root orientation and parts at its past instant while
           ShownPast; parts null when the ring has none, or when drawn live. */
        public Quaternion ShownRotation { get; private set; }
        /* The craft id drawn: the subject, or the craft it was part of or not yet merged from. */
        public uint ShownPid { get; private set; }
        public Composition ShownParts { get; private set; }

        /* The instant the craft is drawn at: the render UT, or its past instant. */
        public double ShownUt { get; private set; }
        /* Drawn from the ring at its past instant, sized by its parts then. */
        public bool ShownPast { get; private set; }
        private double PastSizeM { get; set; }

        /* The size an anchor on the drawn craft takes: its parts at the past
           instant when drawn from the ring, otherwise the drawn radius. */
        public double AnchorRadius => ShownPast && PastSizeM > 0 ? Math.Max(MinRadius, PastSizeM) : Radius;

        /* Stock's own world position for the live craft, for a one-off check
           against the orbit-derived one. */
        public DVec3? StockWorldPosition() => _truth.StockWorldPosition();

        /* The drawn craft's radius in metres: measured from its parts, or set
           from the stand-in model that replaces it. */
        public double Radius { get; set; } = MinRadius;

        /* Bounding radius about the CoM from part origins, refreshed twice a
           second (staging changes it) and immediately on a vessel switch. An
           unloaded or unlocated craft has no parts, so it gets the minimum. A
           hidden craft is measured about its own live CoM, not where it is drawn. */
        public void Measure(bool located, DVec3 com, float now)
        {
            var v = located ? Loaded : null;
            if (v != null && Hidden) com = new DVec3(v.CoMD.x, v.CoMD.y, v.CoMD.z);
            if (v == null)
            {
                Radius = MinRadius;
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
                    if (p != null && p.transform != null)
                    {
                        var at = p.transform.position;
                        _partScratch.Add(new DVec3(at.x, at.y, at.z));
                    }
                }
            }
            Radius = CommandCameraMath.BoundingRadius(_partScratch, com, PartPad, MinRadius);
        }
    }
}
