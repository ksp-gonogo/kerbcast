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
        private const string Tag = "[Kerbcast][CMDCAM]";
        private const float RadiusRefreshSeconds = 0.5f;
        /* While a canopy is open, so the zoom eases out with it as it unfolds rather than in half second steps, and the framing follows it as the craft swings beneath. */
        private const float CanopyRefreshSeconds = 0.1f;
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
        private readonly CommandCameraCanopies _canopies = new CommandCameraCanopies();
        private double _partsRadius = MinRadius;
        /* Part origins about the CoM in the vessel's own frame, and the sphere they fit in, so the framing turns with the craft between measures. */
        private readonly CraftExtent _partsLocal = new CraftExtent();
        private DVec3 _partsCentreLocal;
        private double _partsHalf;
        /* Open canopies about the CoM in world axes: they hang in the airflow rather than turn with the craft. */
        private readonly CraftExtent _canopyExtent = new CraftExtent();
        private readonly CraftExtent _framing = new CraftExtent();
        private int _framingFrame = -1;
        private DVec3 _framingCentre;
        private double _framingHalf;
        private float _canopyAt = float.MinValue;
        private int _canopiesLogged;
        private bool _unfoldingLogged;

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

        /*
         * The sphere the measured parts and open canopies fit in, as its
         * centre's offset from the CoM in world axes and its radius, with
         * the parts' sphere turned to the craft's attitude this frame and
         * never larger than Radius. False unless the craft is drawn live in
         * this camera's scene and was measured.
         */
        public bool FramingShape(out DVec3 centre, out double half)
        {
            centre = default;
            half = 0;
            var v = Loaded;
            if (v == null || Hidden || ShownPast || v.persistentId != _radiusVesselId || v.transform == null) return false;
            if (_partsLocal.Count + _canopyExtent.Count == 0) return false;
            if (_framingFrame != Time.frameCount)
            {
                _framingFrame = Time.frameCount;
                _framing.Clear();
                if (_partsLocal.Count > 0)
                {
                    var l = _partsCentreLocal;
                    var w = v.transform.rotation * new Vector3((float)l.X, (float)l.Y, (float)l.Z);
                    _framing.Add(new DVec3(w.x, w.y, w.z), _partsHalf);
                }
                _framing.AddFrom(_canopyExtent);
                _framing.Enclose(MinRadius, out _framingCentre, out _framingHalf);
                if (_framingHalf > Radius)
                {
                    _framingCentre = default;
                    _framingHalf = Radius;
                }
            }
            centre = _framingCentre;
            half = _framingHalf;
            return true;
        }

        /* Bounding radius about the CoM from part origins, refreshed twice a
           second (staging changes it) and immediately on a vessel switch, or
           the farthest reach of an open parachute canopy when that is larger,
           refreshed faster while one unfolds. An unloaded or unlocated craft
           has no parts, so it gets the minimum. A hidden craft is measured
           about its own live CoM, not where it is drawn. FramingShape reads
           the same parts and canopies. */
        public void Measure(bool located, DVec3 com, float now)
        {
            var v = located ? Loaded : null;
            if (v != null && Hidden) com = new DVec3(v.CoMD.x, v.CoMD.y, v.CoMD.z);
            if (v == null)
            {
                Radius = MinRadius;
                _partsLocal.Clear();
                _canopyExtent.Clear();
                _radiusVesselId = 0;
                _canopiesLogged = 0;
                _unfoldingLogged = false;
                return;
            }
            bool partsDue = v.persistentId != _radiusVesselId || now - _radiusAt >= RadiusRefreshSeconds;
            if (!partsDue && !(_canopies.Open > 0 && now - _canopyAt >= CanopyRefreshSeconds)) return;
            if (partsDue)
            {
                _radiusVesselId = v.persistentId;
                _radiusAt = now;
                _partScratch.Clear();
                _partsLocal.Clear();
                var untwist = v.transform != null ? Quaternion.Inverse(v.transform.rotation) : Quaternion.identity;
                var parts = v.parts;
                if (parts != null)
                {
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var p = parts[i];
                        if (p != null && p.transform != null)
                        {
                            var at = p.transform.position;
                            var point = new DVec3(at.x, at.y, at.z);
                            _partScratch.Add(point);
                            var local = untwist * new Vector3((float)(point.X - com.X), (float)(point.Y - com.Y), (float)(point.Z - com.Z));
                            _partsLocal.Add(new DVec3(local.x, local.y, local.z), PartPad);
                        }
                    }
                }
                _partsRadius = CommandCameraMath.BoundingRadius(_partScratch, com, PartPad, MinRadius);
                _partsLocal.Enclose(0, out _partsCentreLocal, out _partsHalf);
            }
            _canopyAt = now;
            double canopy = _canopies.Reach(v, com, _canopyExtent);
            Radius = Math.Max(_partsRadius, canopy);
            _framingFrame = -1;
            if (_canopies.Open != _canopiesLogged || _canopies.Unfolding != _unfoldingLogged)
            {
                _canopiesLogged = _canopies.Open;
                _unfoldingLogged = _canopies.Unfolding;
                Debug.Log($"{Tag} drawn pid={v.persistentId} open canopies={_canopies.Open}{(_canopies.Unfolding ? " (unfolding)" : "")} "
                    + $"reach={canopy:F1}m parts={_partsRadius:F1}m radius={Radius:F1}m");
            }
        }
    }
}
