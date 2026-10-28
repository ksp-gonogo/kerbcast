using System;
using System.Collections.Generic;

namespace Kerbcast
{
    internal enum IdentityKind : byte
    {
        Begin,
        /* This craft came off Other (undock, decouple, EVA). */
        SplitFrom,
        /* Other came off this craft. */
        SplitOff,
        /* This craft docked or was grabbed into Other and ends here. */
        MergedInto,
        /* Other merged into this craft. */
        Absorbed,
        PartLoss,
        Destroyed,
        Recovered,
        /* Gone from the game outside play: a load whose save does not have it. */
        Removed,
    }

    internal struct HistoryEvent
    {
        public double Ut;
        public IdentityKind Kind;
        /* The other craft, or the lost part's id for PartLoss. */
        public uint Other;
    }

    /* One part as it sat in the craft: pose relative to the root part, as a
       ProtoPartSnapshot stores it, and a bounding radius about its own origin. */
    internal struct PartRecord
    {
        public uint Pid;
        public string Name;
        public float Px, Py, Pz;
        public float Qx, Qy, Qz, Qw;
        public float RadiusM;
    }

    /* The craft's parts from Ut until the next composition. ComX..ComZ is the
       centre of mass in the root's frame, the point the state entries follow. */
    internal sealed class Composition
    {
        public double Ut;
        public PartRecord[] Parts;
        public float ComX, ComY, ComZ;
        public float SizeM;

        public bool SameParts(Composition o)
        {
            if (o == null || o.Parts.Length != Parts.Length) return false;
            for (int i = 0; i < Parts.Length; i++)
                if (Parts[i].Pid != o.Parts[i].Pid) return false;
            return true;
        }

        /* Largest distance of any part's extent from the centre of mass. */
        public static float Size(PartRecord[] parts, float comX, float comY, float comZ)
        {
            double r = 0;
            foreach (var p in parts)
            {
                double dx = p.Px - comX, dy = p.Py - comY, dz = p.Pz - comZ;
                r = Math.Max(r, Math.Sqrt(dx * dx + dy * dy + dz * dz) + p.RadiusM);
            }
            return (float)r;
        }
    }

    /* Every body a craft occupied within the horizon, for the cull's first tier:
       the largest radius and the largest speed about that body. */
    internal struct BodySphere
    {
        public int Body;
        public double RadiusM;
        public double SpeedMs;
    }

    /* A run of entries with the maxima of the spans it holds. Trimming drops
       whole chunks, so it never moves entries or rescans them. */
    internal sealed class EntryChunk
    {
        public const int MaxSize = 256;

        public readonly HistoryEntry[] Items;
        public int Count;
        public readonly List<BodySphere> Spheres = new List<BodySphere>(2);

        public EntryChunk(int capacity) { Items = new HistoryEntry[capacity]; }

        public bool Full => Count == Items.Length;

        internal static void Fold(List<BodySphere> spheres, int body, double radius, double speed)
        {
            for (int i = 0; i < spheres.Count; i++)
            {
                if (spheres[i].Body != body) continue;
                var s = spheres[i];
                if (radius > s.RadiusM) s.RadiusM = radius;
                if (speed > s.SpeedMs) s.SpeedMs = speed;
                spheres[i] = s;
                return;
            }
            spheres.Add(new BodySphere { Body = body, RadiusM = radius, SpeedMs = speed });
        }
    }

    /* One craft's (or one body's, once it leaves its Kepler path) history. A craft whose id
       comes back after its end (a relaunch from the same .craft file) keeps
       this track as a ghost under a key of its own, and a fresh track starts. */
    internal sealed class HistoryTrack
    {
        public readonly long Key;
        public readonly uint Pid;
        public readonly bool IsBody;
        internal readonly List<EntryChunk> Chunks = new List<EntryChunk>();
        internal readonly List<Composition> Compositions = new List<Composition>();
        internal readonly List<HistoryEvent> Events = new List<HistoryEvent>();
        internal readonly List<BodySphere> Spheres = new List<BodySphere>();

        /* Set when a new life under the same id moved this one aside. */
        public bool IsGhost { get; internal set; }
        public double GhostedUt { get; internal set; }

        /* The craft this one split from, or 0. */
        public uint ParentPid { get; internal set; }
        /* When the craft stopped existing as itself; +inf while it lives. */
        public double EndUt { get; internal set; } = double.PositiveInfinity;
        public IdentityKind EndKind { get; internal set; }
        public bool Ended => !double.IsPositiveInfinity(EndUt);

        public double MaxSizeM { get; internal set; }
        public int EntryCount { get; internal set; }

        /* Gate state: the last tick's sample, for a coast's last entry. */
        internal bool HasPrev;
        internal HistoryState Prev;

        internal HistoryTrack(long key, uint pid, bool isBody)
        {
            Key = key;
            Pid = pid;
            IsBody = isBody;
        }

        public IReadOnlyList<HistoryEvent> Identity => Events;
        public IReadOnlyList<BodySphere> BodySpheres => Spheres;
        public bool HasEntries => EntryCount > 0;
        public HistoryEntry Last
        {
            get
            {
                var c = Chunks[Chunks.Count - 1];
                return c.Items[c.Count - 1];
            }
        }
        public HistoryEntry First => Chunks[0].Items[0];
        public double StartUt => EntryCount > 0 ? First.Ut : double.PositiveInfinity;
        public int PartRecordCount
        {
            get
            {
                int n = 0;
                foreach (var c in Compositions) n += c.Parts.Length;
                return n;
            }
        }

        /* The last entry at or before ut, as chunk and index; false if none. */
        internal bool Find(double ut, out int chunk, out int index)
        {
            chunk = -1;
            index = -1;
            int lo = 0, hi = Chunks.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (Chunks[mid].Items[0].Ut <= ut) { chunk = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (chunk < 0) return false;
            var items = Chunks[chunk].Items;
            lo = 0;
            hi = Chunks[chunk].Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (items[mid].Ut <= ut) { index = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return true;
        }

        internal bool Next(int chunk, int index, out HistoryEntry next)
        {
            if (index + 1 < Chunks[chunk].Count) { next = Chunks[chunk].Items[index + 1]; return true; }
            if (chunk + 1 < Chunks.Count) { next = Chunks[chunk + 1].Items[0]; return true; }
            next = default;
            return false;
        }

        internal void MergeSpheres()
        {
            Spheres.Clear();
            foreach (var c in Chunks)
                foreach (var s in c.Spheres) EntryChunk.Fold(Spheres, s.Body, s.RadiusM, s.SpeedMs);
        }
    }

    /* The light-time history: truth about every craft over the horizon, kept
       outside any scene. Nothing of it is exposed through a public API; the
       picture reads it to draw what a lens's arriving light shows.

       It runs only while active: a lock holder has a reservation or a light
       speed is set. A player with no holder pays nothing, and each time it
       turns on it starts empty, like a save from before the ring existed,
       unless it holds a save's history and no game time has passed since.

       Main-thread only. */
    internal sealed partial class LightHistory
    {
        public const double SpeedOfLight = 299792458.0;
        public const long DefaultMemoryCapBytes = 64L * 1024 * 1024;

        /* Accounting sizes. An entry is the HistoryEntry struct, counted by the
           chunk capacity it occupies; a part record is the PartRecord struct with
           its interned name shared. */
        public const int EntryBytes = 96;
        public const int PartBytes = 48;
        public const int CompositionBytes = 48;
        public const int EventBytes = 24;
        public const int TrackBytes = 256;
        public const int ChunkBytes = 96;

        private const long BodyKeyBase = 1L << 32;
        private const long GhostKeyBase = 1L << 40;
        /* Below this a change of UT is jitter, not a load. */
        private const double RewindEpsilonS = 1e-6;
        /* The floor never reaches the newest instant; reads there must still work. */
        private const double FloorMarginS = 1e-3;
        private const int ChunkPoolMax = 64;
        /* A centre of mass that moves less than this keeps its record. */
        private const float ComToleranceM = 0.1f;

        private readonly Dictionary<long, HistoryTrack> _tracks = new Dictionary<long, HistoryTrack>();
        private readonly Dictionary<uint, List<HistoryTrack>> _ghosts = new Dictionary<uint, List<HistoryTrack>>();
        /* Latched for the game, through a rewind or a load, so a body's track stays continuous once it has one. */
        private readonly HashSet<int> _offKepler = new HashSet<int>();
        private readonly List<long> _scratch = new List<long>();
        private readonly Stack<EntryChunk> _pool = new Stack<EntryChunk>();
        private HistoryBody[] _bodies = new HistoryBody[0];
        private long _bytes;
        private long _ghostSeq;
        private double _latestUt = double.NegativeInfinity;
        /* Bytes after the last cap trim that could not get under the cap; no
           second trim until the ring has grown well past it. */
        private long _capBlockedAt = -1;
        private bool _reservation;
        private double? _lightSpeed;

        /* Raised when time went back and the ring dropped what came after. */
        public event Action<double> Rewound;

        /* The holder's light speed in m/s, or null when none is set. */
        public double? LightSpeed => _lightSpeed;
        /* The slowest speed set in this game; the horizon under a reservation
           with no speed. The save carries it across loads. */
        public double? SlowestLightSpeed { get; private set; }
        public bool Active => _reservation || _lightSpeed.HasValue;

        /* The longest distance between a lens and a craft in this system. */
        public double SystemDiameterM { get; set; }
        public long MemoryCapBytes { get; set; } = DefaultMemoryCapBytes;
        /* Bodies seen leaving their Kepler paths this session: each keeps a track from then on. */
        public int BodiesOffKepler => _offKepler.Count;

        /* Switching a slow (scaled) light speed on mid-game lengthens the horizon at
           once, but the ring only holds what it sampled since it turned on, so
           the first stretch may have less history than the new horizon. */
        public double HorizonS => LightHistoryMath.HorizonS(SystemDiameterM, _lightSpeed ?? SlowestLightSpeed ?? SpeedOfLight);
        /* The oldest instant the ring can answer for; raised by the horizon and the memory cap. */
        public double FloorUt { get; private set; } = double.NegativeInfinity;
        public double LatestUt => _latestUt;
        public long Bytes => _bytes;
        public int TrackCount => _tracks.Count;

        public static long CraftKey(uint pid) => pid;
        public static long BodyKey(int index) => BodyKeyBase + index;
        public static bool IsBodyKey(long key) => key >= BodyKeyBase && key < GhostKeyBase;

        /* Every track the cull walks: live craft, ghosts and merged-away craft,
           and the bodies that left their Kepler paths (HistoryTrack.IsBody). */
        public Dictionary<long, HistoryTrack>.ValueCollection Tracks => _tracks.Values;

        /* For KC5: the lock holder has a reservation. */
        internal void SetReservationHeld(bool held)
        {
            bool was = Active;
            _reservation = held;
            OnActiveChanged(was);
        }

        /* For ICommand.SetLightSpeed: null clears it. */
        internal void SetLightSpeed(double? mps)
        {
            if (mps.HasValue && !(mps.Value > 0)) mps = null;
            bool was = Active;
            _lightSpeed = mps;
            if (mps.HasValue && (!SlowestLightSpeed.HasValue || mps.Value < SlowestLightSpeed.Value))
                SlowestLightSpeed = mps;
            OnActiveChanged(was);
        }

        private void OnActiveChanged(bool was)
        {
            /* History across a stretch nobody sampled would read a burn or a dock
               in it as a coast, so each start begins empty, except with a save's
               history that no game time has passed since; and an idle ring holds
               nothing. */
            if (Active == was || (Active && RestoredUt.HasValue)) return;
            ClearHistory();
        }

        /* A save's history can arrive before the bodies, so its bounds are worked out again once they do. */
        public void SetBodies(HistoryBody[] bodies)
        {
            _bodies = bodies ?? new HistoryBody[0];
            if (_tracks.Count > 0) RebuildSpheres();
        }

        public bool TryBody(int index, out HistoryBody body)
        {
            if (index >= 0 && index < _bodies.Length && _bodies[index].Index == index)
            {
                body = _bodies[index];
                return true;
            }
            body = default;
            return false;
        }

        private double Mu(int body) => TryBody(body, out var b) ? b.Mu : 0;

        public HistoryTrack Track(long key) => _tracks.TryGetValue(key, out var t) ? t : null;

        /* The track for a craft id at a past instant: the live one, or the ghost
           of an earlier life under the same id. */
        public HistoryTrack TrackFor(uint pid, double ut)
        {
            var live = Track(CraftKey(pid));
            if (live != null && (live.StartUt <= ut || !_ghosts.ContainsKey(pid))) return live;
            if (_ghosts.TryGetValue(pid, out var list))
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i].StartUt <= ut && ut <= list[i].EndUt) return list[i];
            return live;
        }

        /* The life the same craft id named before `t`, or null: the ghosts are
           kept oldest first, and the live track comes after all of them. */
        public HistoryTrack EarlierLife(HistoryTrack t)
        {
            if (t == null || t.IsBody || !_ghosts.TryGetValue(t.Pid, out var list)) return null;
            int at = t.IsGhost ? list.IndexOf(t) : list.Count;
            return at > 0 ? list[at - 1] : null;
        }

        private HistoryTrack GetOrCreate(long key, double ut)
        {
            if (_tracks.TryGetValue(key, out var t)) return t;
            bool body = IsBodyKey(key);
            t = new HistoryTrack(key, body ? 0u : (uint)key, body);
            t.Events.Add(new HistoryEvent { Ut = ut, Kind = IdentityKind.Begin });
            _tracks[key] = t;
            _bytes += TrackBytes + EventBytes;
            return t;
        }

        /* A live craft under an id whose track has ended: the old life becomes a
           ghost, kept whole for the picture, and a new track starts. */
        private HistoryTrack Live(long key, double ut)
        {
            var t = GetOrCreate(key, ut);
            if (!t.Ended) return t;
            _tracks.Remove(key);
            long ghostKey = GhostKeyBase + ++_ghostSeq;
            var ghost = Rekey(t, ghostKey);
            ghost.IsGhost = true;
            ghost.GhostedUt = ut;
            _tracks[ghostKey] = ghost;
            if (!_ghosts.TryGetValue(t.Pid, out var list)) _ghosts[t.Pid] = list = new List<HistoryTrack>();
            list.Add(ghost);
            return GetOrCreate(key, ut);
        }

        private static HistoryTrack Rekey(HistoryTrack t, long key)
        {
            var n = new HistoryTrack(key, t.Pid, t.IsBody)
            {
                ParentPid = t.ParentPid, EndUt = t.EndUt, EndKind = t.EndKind,
                MaxSizeM = t.MaxSizeM, EntryCount = t.EntryCount,
                IsGhost = t.IsGhost, GhostedUt = t.GhostedUt,
            };
            n.Chunks.AddRange(t.Chunks);
            n.Compositions.AddRange(t.Compositions);
            n.Events.AddRange(t.Events);
            n.Spheres.AddRange(t.Spheres);
            return n;
        }

        /* Entries ever written, trims and rewinds aside, for an entries-per-second rate. */
        public long EntriesWritten { get; private set; }

        public long EntryTotal
        {
            get
            {
                long n = 0;
                foreach (var t in _tracks.Values) n += t.EntryCount;
                return n;
            }
        }

        /* Time went back (a quickload or revert) when a mutation arrives earlier
           than anything seen: drop what came after before taking it. */
        private void Arrive(double ut)
        {
            if (ut < _latestUt - RewindEpsilonS) Rewind(ut);
            else if (ut > _latestUt) _latestUt = ut;
            RestoredUt = null;
        }

        /* One tick's sample through the gate. `gated` is false for a craft on
           rails between its checks: then only a change of body adds an entry, and
           everything else comes from Record. Returns whether an entry was added. */
        public bool Sample(long key, in HistoryState now, bool gated, bool checkAttitude)
        {
            Arrive(now.Ut);
            return SampleInto(Live(key, now.Ut), now, gated, checkAttitude);
        }

        /*
         * Body `index` where the game has put it, `s`, and how far, in metres,
         * it was last seen from where its Kepler orbit as first seen puts it. A
         * body that departs by more than the position tolerance is latched off
         * Kepler for the session and sampled through the gate from then on; one
         * that never departs gets no track and is read from its orbit. Returns
         * whether an entry was added.
         */
        public bool ObserveBody(int index, in HistoryState s, double offKeplerM)
        {
            if (!_offKepler.Contains(index))
            {
                if (!(offKeplerM > LightHistoryMath.PositionToleranceM)) return false;
                _offKepler.Add(index);
            }
            return SampleBody(index, s);
        }

        public bool IsOffKepler(int index) => _offKepler.Contains(index);

        /* A body off its Kepler path, sampled a tick back for its centred velocity.
           That UT may be older than the latest event, which is not a rewind. */
        public bool SampleBody(int index, in HistoryState s)
        {
            var t = GetOrCreate(BodyKey(index), s.Ut);
            if (t.HasEntries && s.Ut <= t.Last.Ut) return false;
            if (s.Ut > _latestUt) _latestUt = s.Ut;
            return SampleInto(t, s, gated: true, checkAttitude: false);
        }

        private bool SampleInto(HistoryTrack t, in HistoryState now, bool gated, bool checkAttitude)
        {
            bool added = false;
            if (!t.HasEntries)
            {
                Append(t, now.ToEntry(HistoryReason.Start));
                added = true;
            }
            else
            {
                var last = t.Last;
                if (now.Body != last.Body)
                {
                    ChangeBody(t, now);
                    added = true;
                }
                else if (gated && now.Ut - last.Ut >= LightHistoryMath.MinIntervalS
                    && LightHistoryMath.Departs(last, Mu(last.Body), now, checkAttitude))
                {
                    if (now.Ut - last.Ut > LightHistoryMath.CoastWindowS && t.HasPrev && t.Prev.Ut > last.Ut
                        && t.Prev.Body == last.Body && t.Prev.Ut - last.Ut >= LightHistoryMath.MinIntervalS)
                        Append(t, t.Prev.ToEntry(HistoryReason.CoastEnd));
                    else
                        Append(t, now.ToEntry(HistoryReason.Departure));
                    added = true;
                }
            }
            t.Prev = now;
            t.HasPrev = gated;
            return added;
        }

        /* A change of body stamped when it happened, which on rails under warp
           can be long before this tick: the state at the change, after the old
           body's last state at the same UT, so no read carries the old conic
           past the SOI. */
        public void BodyChanged(long key, in HistoryState atChange, double nowUt)
        {
            Arrive(nowUt);
            var t = Live(key, nowUt);
            if (!t.HasEntries) { Append(t, atChange.ToEntry(HistoryReason.Start)); return; }
            if (t.Last.Body == atChange.Body) return;
            var s = atChange;
            s.Ut = Math.Min(Math.Max(s.Ut, t.Last.Ut), nowUt);
            ChangeBody(t, s);
            t.HasPrev = false;
        }

        private void ChangeBody(HistoryTrack t, in HistoryState s)
        {
            var last = t.Last;
            if (s.Ut > last.Ut)
                Append(t, LightHistoryMath.Predict(last, Mu(last.Body), s.Ut).ToEntry(HistoryReason.SoiExit));
            Append(t, s.ToEntry(HistoryReason.SoiChange));
        }

        /* An entry the game's own events call for (load, unload, rails, split),
           outside the gate and its rate cap. */
        public void Record(long key, in HistoryState now, HistoryReason reason)
        {
            Arrive(now.Ut);
            var t = Live(key, now.Ut);
            /* Same instant: the later word wins, keeping one entry per UT. */
            if (t.HasEntries && t.Last.Ut >= now.Ut && t.Last.Body == now.Body) RemoveLastEntry(t);
            if (t.HasEntries && t.Last.Body != now.Body) ChangeBody(t, now);
            else Append(t, now.ToEntry(t.HasEntries ? reason : HistoryReason.Start));
            t.HasPrev = false;
        }

        public bool LastBody(long key, out int body)
        {
            var t = Track(key);
            if (t != null && t.HasEntries && !t.Ended) { body = t.Last.Body; return true; }
            body = -1;
            return false;
        }

        /* A new composition, kept only when the part set changed; the same parts may move the centre of mass (MoveCom). */
        public bool SetComposition(uint pid, double ut, PartRecord[] parts, float comX, float comY, float comZ)
        {
            Arrive(ut);
            var t = Live(CraftKey(pid), ut);
            var c = new Composition { Ut = ut, Parts = parts ?? new PartRecord[0], ComX = comX, ComY = comY, ComZ = comZ };
            c.SizeM = Composition.Size(c.Parts, comX, comY, comZ);
            if (t.Compositions.Count > 0)
            {
                var last = t.Compositions[t.Compositions.Count - 1];
                if (c.SameParts(last))
                {
                    MoveCom(pid, ut, comX, comY, comZ);
                    return false;
                }
                if (last.Ut >= ut)
                {
                    _bytes -= CompositionCost(t, t.Compositions.Count - 1);
                    t.Compositions.RemoveAt(t.Compositions.Count - 1);
                }
            }
            t.Compositions.Add(c);
            _bytes += CompositionBytes + (long)c.Parts.Length * PartBytes;
            if (c.SizeM > t.MaxSizeM) t.MaxSizeM = c.SizeM;
            CheckCap();
            return true;
        }

        /*
         * The craft's centre of mass moved with the same parts (fuel drawn
         * down): from `ut` on its parts hang from the new point, the one its
         * states follow, in a record that shares the parts of the one before.
         * Earlier instants keep theirs. Under the tolerance nothing is kept.
         */
        public bool MoveCom(uint pid, double ut, float comX, float comY, float comZ)
        {
            Arrive(ut);
            var t = Track(CraftKey(pid));
            if (t == null || t.Ended || t.Compositions.Count == 0) return false;
            var last = t.Compositions[t.Compositions.Count - 1];
            float dx = comX - last.ComX, dy = comY - last.ComY, dz = comZ - last.ComZ;
            if (dx * dx + dy * dy + dz * dz <= ComToleranceM * ComToleranceM) return false;
            float size = Composition.Size(last.Parts, comX, comY, comZ);
            if (last.Ut >= ut)
            {
                last.ComX = comX;
                last.ComY = comY;
                last.ComZ = comZ;
                last.SizeM = size;
                float max = 0;
                foreach (var each in t.Compositions) max = Math.Max(max, each.SizeM);
                t.MaxSizeM = max;
                return true;
            }
            t.Compositions.Add(new Composition { Ut = ut, Parts = last.Parts, ComX = comX, ComY = comY, ComZ = comZ, SizeM = size });
            _bytes += CompositionBytes;
            if (size > t.MaxSizeM) t.MaxSizeM = size;
            CheckCap();
            return true;
        }

        /* A record's memory, its parts counted once while they are shared with the record before. */
        private static long CompositionCost(HistoryTrack t, int index)
        {
            var c = t.Compositions[index];
            bool shared = index > 0 && ReferenceEquals(t.Compositions[index - 1].Parts, c.Parts);
            return CompositionBytes + (shared ? 0 : (long)c.Parts.Length * PartBytes);
        }

        public void PartLost(uint pid, uint partPid, double ut)
        {
            Arrive(ut);
            var t = Track(CraftKey(pid));
            if (t == null || t.Ended) return;
            AddEvent(t, ut, IdentityKind.PartLoss, partPid);
        }

        /* Child came off parent at ut: the child's history starts with a link to its parent. */
        public void Split(uint parent, uint child, double ut)
        {
            if (parent == child) return;
            Arrive(ut);
            var c = Live(CraftKey(child), ut);
            c.ParentPid = parent;
            AddEvent(c, ut, IdentityKind.SplitFrom, parent);
            AddEvent(Live(CraftKey(parent), ut), ut, IdentityKind.SplitOff, child);
        }

        /* Absorbed docked into, or was grabbed by, survivor at ut and ends there. */
        public void Merge(uint absorbed, uint survivor, double ut)
        {
            if (absorbed == survivor) return;
            Arrive(ut);
            var a = Track(CraftKey(absorbed));
            if (a != null && a.Ended) return;
            a = GetOrCreate(CraftKey(absorbed), ut);
            AddEvent(a, ut, IdentityKind.MergedInto, survivor);
            End(a, ut, IdentityKind.MergedInto);
            AddEvent(Live(CraftKey(survivor), ut), ut, IdentityKind.Absorbed, absorbed);
        }

        /* Bumped by every rewind and clear. A caller that holds an event to
           deliver later stamps it with this and the ring drops it if the
           timeline changed in between. */
        public long Generation { get; private set; }

        /* The game's destroy reaches the ring a frame late, so its UT may be
           older than the latest sample; that is not a rewind. A destroy from
           before a rewind or clear (an older generation) is dropped. */
        public void Destroyed(uint pid, double ut, long generation)
        {
            if (generation != Generation) return;
            if (ut > _latestUt) _latestUt = ut;
            Finish(pid, ut, IdentityKind.Destroyed);
        }

        public void Recovered(uint pid, double ut)
        {
            Arrive(ut);
            Finish(pid, ut, IdentityKind.Recovered);
        }

        /* A recovery queued at `generation`, dropped when the ring has been cleared or rewound since, so it is never
           stamped on the timeline a quickload went back to. */
        public void Recovered(uint pid, double ut, long generation)
        {
            if (generation != Generation) return;
            Recovered(pid, ut);
        }

        /* After a load: every live craft the loaded game does not have ends here. */
        public int EndMissing(HashSet<uint> present, double ut)
        {
            Arrive(ut);
            _scratch.Clear();
            foreach (var t in _tracks.Values)
                if (!t.IsBody && !t.IsGhost && !t.Ended && !present.Contains(t.Pid)) _scratch.Add(t.Key);
            foreach (var k in _scratch) Finish((uint)k, ut, IdentityKind.Removed);
            return _scratch.Count;
        }

        private void Finish(uint pid, double ut, IdentityKind kind)
        {
            var t = Track(CraftKey(pid));
            if (t == null || t.Ended) return;
            AddEvent(t, ut, kind, 0);
            End(t, ut, kind);
        }

        private static void End(HistoryTrack t, double ut, IdentityKind kind)
        {
            t.EndUt = ut;
            t.EndKind = kind;
            t.HasPrev = false;
        }

        private void AddEvent(HistoryTrack t, double ut, IdentityKind kind, uint other)
        {
            if (t.Events.Count > 0)
            {
                var last = t.Events[t.Events.Count - 1];
                if (last.Kind == kind && last.Other == other && (kind == IdentityKind.PartLoss || last.Ut == ut)) return;
            }
            t.Events.Add(new HistoryEvent { Ut = ut, Kind = kind, Other = other });
            _bytes += EventBytes;
        }

        private void Append(HistoryTrack t, HistoryEntry e)
        {
            HistoryEntry prev = default;
            bool hasPrev = t.HasEntries;
            if (hasPrev) prev = t.Last;
            var chunk = t.Chunks.Count > 0 ? t.Chunks[t.Chunks.Count - 1] : null;
            if (chunk == null || chunk.Full)
            {
                chunk = NewChunk(t.EntryCount);
                t.Chunks.Add(chunk);
                _bytes += ChunkBytes + (long)chunk.Items.Length * EntryBytes;
            }
            chunk.Items[chunk.Count++] = e;
            t.EntryCount++;
            EntriesWritten++;
            if (hasPrev) FoldSpan(t, chunk, prev, e);
            FoldEntry(t, chunk, e);
            CheckCap();
        }

        /* Small first chunks keep a coasting craft's few entries cheap; busy
           tracks reach full chunks, which are pooled for reuse. */
        private EntryChunk NewChunk(int entriesSoFar)
        {
            int size = Math.Min(EntryChunk.MaxSize, Math.Max(4, entriesSoFar));
            if (size == EntryChunk.MaxSize && _pool.Count > 0)
            {
                var c = _pool.Pop();
                c.Count = 0;
                c.Spheres.Clear();
                return c;
            }
            return new EntryChunk(size);
        }

        private void Release(EntryChunk c)
        {
            if (c.Items.Length == EntryChunk.MaxSize && _pool.Count < ChunkPoolMax) _pool.Push(c);
        }

        private void RemoveLastEntry(HistoryTrack t)
        {
            var c = t.Chunks[t.Chunks.Count - 1];
            c.Count--;
            t.EntryCount--;
            if (c.Count == 0)
            {
                t.Chunks.RemoveAt(t.Chunks.Count - 1);
                _bytes -= ChunkBytes + (long)c.Items.Length * EntryBytes;
                Release(c);
            }
        }

        private void FoldEntry(HistoryTrack t, EntryChunk chunk, in HistoryEntry e)
        {
            FoldEntryInto(chunk.Spheres, e);
            FoldEntryInto(t.Spheres, e);
        }

        private static void FoldSpan(HistoryTrack t, EntryChunk chunk, in HistoryEntry a, in HistoryEntry b)
        {
            FoldSpanInto(chunk.Spheres, a, b);
            FoldSpanInto(t.Spheres, a, b);
        }

        /* The craft's state at a past instant: Kepler from the entry while it
           coasts, Hermite between entries under thrust. False before its history
           starts, before the ring's floor, or after it ended. */
        public bool TryStateAt(uint pid, double ut, out HistoryState state) => TryStateAt(TrackFor(pid, ut), ut, out state);

        /* A body about its parent, while its track covers `ut`: false for a body on its Kepler path. */
        public bool TryBodyStateAt(int body, double ut, out HistoryState state) => TryStateAt(Track(BodyKey(body)), ut, out state);

        public bool TryStateAt(HistoryTrack t, double ut, out HistoryState state)
        {
            state = default;
            if (t == null || ut < FloorUt || ut > t.EndUt) return false;
            if (!t.Find(ut, out int c, out int i)) return false;
            var e = t.Chunks[c].Items[i];
            if (t.Next(c, i, out var next) && LightHistoryMath.Hermite(e, next))
                state = LightHistoryMath.Interpolate(e, next, ut);
            else
                state = LightHistoryMath.Predict(e, Mu(e.Body), ut);
            return true;
        }

        public Composition CompositionAt(HistoryTrack t, double ut)
        {
            if (t == null || ut < FloorUt) return null;
            Composition found = null;
            foreach (var c in t.Compositions)
            {
                if (c.Ut > ut) break;
                found = c;
            }
            return found;
        }

        public Composition CompositionAt(uint pid, double ut) => CompositionAt(TrackFor(pid, ut), ut);

        /* The identity events of the life that pid named at ut. */
        public IReadOnlyList<HistoryEvent> IdentityOf(uint pid, double ut) => TrackFor(pid, ut)?.Events;

        /* Largest speed over the horizon in the heliocentric frame: the craft's
           speed about each body it occupied plus that body's own bound to the root. */
        public double MaxInertialSpeed(HistoryTrack t)
        {
            double best = 0;
            foreach (var s in t.Spheres) best = Math.Max(best, s.SpeedMs + BodySpeedBound(s.Body));
            return best;
        }

        /* Largest speed over the horizon about body `frame`, for a light time solved in its frame. */
        public double MaxSpeedAbout(HistoryTrack t, int frame)
        {
            double best = 0;
            foreach (var s in t.Spheres) best = Math.Max(best, s.SpeedMs + BodySpeedBound(s.Body, frame));
            return best;
        }

        /* A body's speed bound relative to the root, through its parents. A
           body's own track, once it has one, widens its Kepler bound. */
        public double BodySpeedBound(int index) => BodySpeedBound(index, -1);

        /* A body's speed bound relative to body `frame`: its parents' bounds up
           to `frame`, or to the root plus frame's own when `frame` is not above it. */
        public double BodySpeedBound(int index, int frame)
        {
            double sum = 0;
            for (int guard = 0; guard < 16 && index != frame; guard++)
            {
                if (!TryBody(index, out var b) || b.Parent < 0)
                    return frame >= 0 ? sum + BodySpeedBound(frame, -1) : sum;
                double own = b.MaxOrbitalSpeed;
                var bt = Track(BodyKey(index));
                if (bt != null)
                    foreach (var s in bt.Spheres) own = Math.Max(own, s.SpeedMs);
                sum += own;
                index = b.Parent;
            }
            return sum;
        }

        /*
         * A body's acceleration bound relative to body `frame`, through the
         * same parents as BodySpeedBound: each body's parent pulls it with at
         * most Mu / periapsis squared on its Kepler path. Infinite for a body
         * with no known periapsis or one that has left its Kepler path, whose
         * pull is not bounded.
         */
        public double BodyAccelBound(int index, int frame)
        {
            double sum = 0;
            for (int guard = 0; guard < 16 && index != frame; guard++)
            {
                if (!TryBody(index, out var b) || b.Parent < 0)
                    return frame >= 0 ? sum + BodyAccelBound(frame, -1) : sum;
                if (!(b.PeriapsisM > 0) || Track(BodyKey(index)) != null) return double.PositiveInfinity;
                sum += Mu(b.Parent) / (b.PeriapsisM * b.PeriapsisM);
                index = b.Parent;
            }
            return sum;
        }

        /* Drop what no lens can need: everything before now minus the horizon,
           and more, oldest first, while the ring is over its memory cap. */
        public void Trim(double nowUt)
        {
            Arrive(nowUt);
            TrimTo(Math.Max(FloorUt, nowUt - HorizonS));
            if (_bytes > MemoryCapBytes) TrimToCap();
        }

        private void CheckCap()
        {
            if (_bytes <= MemoryCapBytes) return;
            if (_capBlockedAt >= 0 && _bytes < _capBlockedAt + MemoryCapBytes / 8) return;
            TrimToCap();
        }

        /* Raise the floor a tenth of the remaining span at a time. What no floor
           can drop (each track's last two chunks, its last composition) is
           weighed first: if that alone is over the cap, trimming goes straight
           to the newest instant rather than spinning, and waits for the ring to
           grow. The cap is meant to be far above a chunk per track. */
        private void TrimToCap()
        {
            long target = MemoryCapBytes - MemoryCapBytes / 10;
            long fixedBytes = FixedBytes();
            double ceiling = _latestUt - FloorMarginS;
            if (fixedBytes >= target)
                TrimTo(ceiling);
            else
                for (int guard = 0; guard < 64 && _bytes > target; guard++)
                {
                    double from = double.IsNegativeInfinity(FloorUt) ? OldestUt() : FloorUt;
                    if (!(from < ceiling)) break;
                    TrimTo(Math.Min(from + Math.Max((ceiling - from) * 0.1, FloorMarginS), ceiling));
                }
            _capBlockedAt = _bytes > MemoryCapBytes ? _bytes : -1;
        }

        private long FixedBytes()
        {
            long b = 0;
            foreach (var t in _tracks.Values)
            {
                b += TrackBytes;
                for (int i = Math.Max(0, t.Chunks.Count - 2); i < t.Chunks.Count; i++)
                    b += ChunkBytes + (long)t.Chunks[i].Items.Length * EntryBytes;
                if (t.Compositions.Count > 0) b += CompositionBytes + (long)t.Compositions[t.Compositions.Count - 1].Parts.Length * PartBytes;
            }
            return b;
        }

        private double OldestUt()
        {
            double oldest = _latestUt;
            foreach (var t in _tracks.Values)
                if (t.HasEntries && t.First.Ut < oldest) oldest = t.First.Ut;
            return oldest;
        }

        private void TrimTo(double floor)
        {
            if (!(floor > FloorUt)) return;
            FloorUt = Math.Min(floor, _latestUt);
            _scratch.Clear();
            foreach (var kv in _tracks)
            {
                var t = kv.Value;
                if (t.EndUt < FloorUt) { _scratch.Add(kv.Key); continue; }
                /* A chunk goes once the next one starts at or before the floor, so
                   the entry reads at the floor start from is always kept. */
                bool dropped = false;
                while (t.Chunks.Count >= 2 && t.Chunks[1].Items[0].Ut <= FloorUt)
                {
                    var c = t.Chunks[0];
                    t.Chunks.RemoveAt(0);
                    t.EntryCount -= c.Count;
                    Release(c);
                    dropped = true;
                }
                int n = 0;
                while (n + 1 < t.Compositions.Count && t.Compositions[n + 1].Ut <= FloorUt) n++;
                if (n > 0)
                {
                    t.Compositions.RemoveRange(0, n);
                    float size = 0;
                    foreach (var c in t.Compositions) size = Math.Max(size, c.SizeM);
                    t.MaxSizeM = size;
                }
                int ev = 0;
                while (ev < t.Events.Count && t.Events[ev].Ut < FloorUt) ev++;
                if (ev > 0) t.Events.RemoveRange(0, ev);
                /* A split's shell for a craft never sampled, now older than the floor. */
                if (!t.HasEntries && t.Events.Count == 0) { _scratch.Add(kv.Key); continue; }
                if (dropped) t.MergeSpheres();
            }
            foreach (var k in _scratch) RemoveTrack(k);
            Recount();
        }

        private void RemoveTrack(long key)
        {
            var t = Detach(key);
            if (t != null) foreach (var c in t.Chunks) Release(c);
        }

        /* Out of the ring but with its chunks still its own. */
        private HistoryTrack Detach(long key)
        {
            if (!_tracks.TryGetValue(key, out var t)) return null;
            _tracks.Remove(key);
            if (t.IsGhost && _ghosts.TryGetValue(t.Pid, out var list))
            {
                list.Remove(t);
                if (list.Count == 0) _ghosts.Remove(t.Pid);
            }
            return t;
        }

        private void Recount()
        {
            long b = 0;
            foreach (var t in _tracks.Values)
            {
                b += TrackBytes + (long)t.Events.Count * EventBytes;
                foreach (var c in t.Chunks) b += ChunkBytes + (long)c.Items.Length * EntryBytes;
                for (int i = 0; i < t.Compositions.Count; i++) b += CompositionCost(t, i);
            }
            _bytes = b;
        }

        /* Time went back (a quickload or revert): nothing after ut happened. */
        public void Rewind(double ut)
        {
            if (!(ut < _latestUt)) return;
            Generation++;
            _scratch.Clear();
            foreach (var kv in _tracks)
            {
                var t = kv.Value;
                Truncate(t, ut);
                int c = t.Compositions.Count;
                while (c > 0 && t.Compositions[c - 1].Ut > ut) c--;
                if (c < t.Compositions.Count) t.Compositions.RemoveRange(c, t.Compositions.Count - c);
                float size = 0;
                foreach (var comp in t.Compositions) size = Math.Max(size, comp.SizeM);
                t.MaxSizeM = size;
                int ev = t.Events.Count;
                while (ev > 0 && t.Events[ev - 1].Ut > ut)
                {
                    ev--;
                    if (t.Events[ev].Kind == IdentityKind.SplitFrom) t.ParentPid = 0;
                }
                if (ev < t.Events.Count) t.Events.RemoveRange(ev, t.Events.Count - ev);
                if (t.EndUt > ut) t.EndUt = double.PositiveInfinity;
                t.HasPrev = false;
                if (!t.HasEntries && t.Events.Count == 0) _scratch.Add(kv.Key);
            }
            foreach (var k in _scratch) RemoveTrack(k);

            /* A ghost moved aside after ut is the live craft again. */
            _scratch.Clear();
            foreach (var t in _tracks.Values)
                if (t.IsGhost && t.GhostedUt > ut && !_tracks.ContainsKey(CraftKey(t.Pid))) _scratch.Add(t.Key);
            foreach (var k in _scratch)
            {
                /* Its chunks move to the restored track, so they must not go back to the pool. */
                var g = Detach(k);
                var back = Rekey(g, CraftKey(g.Pid));
                back.IsGhost = false;
                back.GhostedUt = 0;
                _tracks[back.Key] = back;
            }

            _latestUt = ut;
            if (FloorUt > ut) FloorUt = ut;
            _capBlockedAt = -1;
            Recount();
            Rewound?.Invoke(ut);
        }

        private void Truncate(HistoryTrack t, double ut)
        {
            if (!t.HasEntries) return;
            if (!t.Find(ut, out int c, out int i)) { c = 0; i = -1; }
            if (c == t.Chunks.Count - 1 && i == t.Chunks[c].Count - 1) return;
            for (int k = t.Chunks.Count - 1; k > c; k--)
            {
                t.EntryCount -= t.Chunks[k].Count;
                Release(t.Chunks[k]);
                t.Chunks.RemoveAt(k);
            }
            var chunk = t.Chunks[c];
            t.EntryCount -= chunk.Count - (i + 1);
            chunk.Count = i + 1;
            if (chunk.Count == 0)
            {
                t.Chunks.RemoveAt(c);
                Release(chunk);
            }
            else
            {
                chunk.Spheres.Clear();
                for (int k = 0; k < chunk.Count; k++)
                {
                    if (k > 0) FoldSpanInto(chunk.Spheres, chunk.Items[k - 1], chunk.Items[k]);
                    else if (c > 0) FoldSpanInto(chunk.Spheres, t.Chunks[c - 1].Items[t.Chunks[c - 1].Count - 1], chunk.Items[0]);
                    FoldEntryInto(chunk.Spheres, chunk.Items[k]);
                }
            }
            t.MergeSpheres();
        }

        private void FoldEntryInto(List<BodySphere> spheres, in HistoryEntry e)
        {
            if (!TryBody(e.Body, out var body))
                body = new HistoryBody { Index = e.Body, SoiRadius = double.PositiveInfinity };
            LightHistoryMath.EntryBound(e, body, out double r, out double v);
            EntryChunk.Fold(spheres, e.Body, r, v);
        }

        private static void FoldSpanInto(List<BodySphere> spheres, in HistoryEntry a, in HistoryEntry b)
        {
            if (!LightHistoryMath.Hermite(a, b)) return;
            LightHistoryMath.HermiteBound(a, b, out double r, out double v);
            EntryChunk.Fold(spheres, a.Body, r, v);
        }

        /* A different game (another save): nothing carries over, not even the slowest speed. */
        /* For another game: what this one saw of the bodies leaving their Kepler paths goes too. */
        public void Clear()
        {
            ClearHistory();
            _offKepler.Clear();
            SlowestLightSpeed = _lightSpeed;
        }

        private void ClearHistory()
        {
            Generation++;
            foreach (var t in _tracks.Values)
                foreach (var c in t.Chunks) Release(c);
            _tracks.Clear();
            _ghosts.Clear();
            _bytes = 0;
            _capBlockedAt = -1;
            _latestUt = double.NegativeInfinity;
            FloorUt = double.NegativeInfinity;
            RestoredUt = null;
        }
    }
}
