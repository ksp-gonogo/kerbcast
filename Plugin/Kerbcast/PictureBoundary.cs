using System;
using System.Collections.Generic;

namespace Kerbcast
{
    /*
     * The boundary between craft drawn live and craft drawn at their past
     * instant: one frame of light time τ from the lens. A loaded craft is
     * hidden, and a stand-in drawn for it, once any of it may be beyond c·τ,
     * and drawn live again only once all of it is inside 0.9 c·τ, so a craft
     * drawn live is never farther than c·τ and a frame's live light time of
     * τ covers it.
     */
    internal static class PictureBoundary
    {
        /* τ, the same one a frame's live light time is bounded by. */
        public const double FrameS = CommandCameraMath.BoundaryLightTimeS;
        public const double ReturnFraction = 0.9;
        /* A stand-in nearer than this is drawn at its true place; beyond it, at
           this distance on the true ray and shrunk by the same ratio. */
        public const double ProxyM = 50000;
        public static double BoundaryM(double lightSpeed) => lightSpeed * FrameS;

        /*
         * Whether a loaded craft whose farthest point may be `farthestM` from
         * the lens is hidden this frame, given whether it was hidden last. A
         * distance that cannot be read hides it.
         */
        public static bool Hidden(bool wasHidden, double farthestM, double lightSpeed)
        {
            if (double.IsNaN(farthestM) || double.IsInfinity(farthestM) || farthestM < 0) return true;
            double boundary = BoundaryM(lightSpeed);
            if (farthestM > boundary) return true;
            return wasHidden && farthestM >= ReturnFraction * boundary;
        }

        /* A point on a live craft (pivot `livePivot`, turned `liveRotation`) carried to where the craft is drawn. */
        public static DVec3 Carry(DVec3 point, DVec3 livePivot, DQuat liveRotation, DVec3 drawnPivot, DQuat drawnRotation) =>
            drawnPivot + Turn(liveRotation, drawnRotation).Rotate(point - livePivot);

        /* The rotation that takes the live craft's attitude to the drawn one. */
        public static DQuat Turn(DQuat liveRotation, DQuat drawnRotation) => (drawnRotation * liveRotation.Conjugate).Normalized;

        /* A part in the drawn craft: its pose relative to the root, the root
           placed so the craft's centre of mass `comLocal` sits at `drawnCom`. */
        public static DVec3 PartAt(DVec3 partLocal, DVec3 comLocal, DVec3 drawnCom, DQuat drawnRotation) =>
            drawnCom + drawnRotation.Rotate(partLocal - comLocal);

        /*
         * Where a stand-in for a craft at `at` is put for a lens at `eye`: at
         * its true place within ProxyM, otherwise on the same ray at ProxyM
         * and shrunk by `scale`, the same angular size.
         */
        public static DVec3 Proxy(DVec3 eye, DVec3 at, out double scale)
        {
            var d = at - eye;
            double distance = d.Magnitude;
            scale = 1.0;
            if (!(distance > ProxyM)) return at;
            scale = ProxyM / distance;
            return eye + d * scale;
        }
    }

    /*
     * The craft one camera has hidden that are no longer loaded (docked into
     * another, destroyed, recovered, unloaded). Each is still drawn where its
     * light shows it, until its past instant no longer resolves: vanishing
     * when it leaves the scene would show that event before its light could.
     * A craft is dropped only after MissesToDrop frames in a row whose solve,
     * from a valid lens, found nothing, so one unsettled solve never drops it.
     */
    internal sealed class Lingering
    {
        public const int MissesToDrop = 3;

        private readonly HashSet<uint> _pids = new HashSet<uint>();
        private readonly Dictionary<uint, int> _misses = new Dictionary<uint, int>();

        public int Count => _pids.Count;
        public bool Contains(uint pid) => _pids.Contains(pid);
        public HashSet<uint> Pids => _pids;

        /* The craft hidden this frame join the set. */
        public void Note(List<uint> hidden)
        {
            for (int i = 0; i < hidden.Count; i++) _pids.Add(hidden[i]);
        }

        /*
         * This frame's craft to solve besides the hidden ones, into `into`:
         * every craft in the set the scene does not draw itself (`sceneDraws`:
         * loaded, live or hidden) nor the rig (`rig`). One the scene
         * draws stays in the set, so it is drawn again the moment it leaves.
         */
        public void Collect(HashSet<uint> sceneDraws, RigDrawn rig, List<uint> into)
        {
            foreach (uint pid in _pids)
                if (!sceneDraws.Contains(pid) && !rig.Draws(pid)) into.Add(pid);
        }

        /* Craft `pid` left the scene but flies on: the ring's own track draws it from now on, through the cull like any unloaded craft. */
        public void LeaveToCull(uint pid) => Drop(pid);

        /* Craft `pid` was solved and found. */
        public void Seen(uint pid) => _misses.Remove(pid);

        /* Craft `pid`'s solve from a valid lens found nothing: dropped once that has held MissesToDrop frames, unless the scene still draws it. */
        public void Missed(uint pid, HashSet<uint> sceneDraws)
        {
            if (sceneDraws.Contains(pid) || !_pids.Contains(pid)) return;
            _misses.TryGetValue(pid, out int n);
            if (++n >= MissesToDrop) Drop(pid);
            else _misses[pid] = n;
        }

        private void Drop(uint pid)
        {
            _pids.Remove(pid);
            _misses.Remove(pid);
        }

        public void Clear()
        {
            _pids.Clear();
            _misses.Clear();
        }
    }

    /* The craft a command rig draws its subject's stand-in for this frame: the
       subject, and the craft it is drawn as while its light still shows it part
       of another. The pass draws neither, so nothing is drawn twice. */
    internal struct RigDrawn
    {
        public uint Subject;
        public uint DrawnAs;

        public RigDrawn(uint subject, uint drawnAs)
        {
            Subject = subject;
            DrawnAs = drawnAs;
        }

        public bool Draws(uint pid) => pid != 0 && (pid == Subject || pid == DrawnAs);

        /* A candidate `pid` drawn as `seenPid`: a sibling split from the same craft within the light delay is drawn as it too. */
        public bool Draws(uint pid, uint seenPid) => Draws(pid) || Draws(seenPid);
    }

    /* Which drawn craft carried each part at that craft's past instant, built once a frame for part anchors. */
    internal sealed class PartOwners
    {
        private readonly Dictionary<uint, uint> _owner = new Dictionary<uint, uint>();

        public void Clear() => _owner.Clear();

        /* The first craft to claim a part keeps it. */
        public void Add(uint part, uint craft)
        {
            if (!_owner.ContainsKey(part)) _owner[part] = craft;
        }

        public bool TryOwner(uint part, out uint craft) => _owner.TryGetValue(part, out craft);
    }
}
