using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kerbcast
{
    /*
     * The ring in a save file: what a lens could still need, so after a load
     * every lens draws what its arriving light shows at once, ghosts of craft
     * destroyed or recovered within the horizon included.
     *
     * A save holds the horizon back from its UT, and less when that is over
     * the size cap: the oldest instant kept is raised a tenth of the span at a
     * time, by the size each floor would write, until it fits, and the ring is
     * written once. When even the highest floor is over the cap, the save
     * keeps as many tracks as fit, the bodies first, then craft still flying,
     * then those that have ended, the smallest first within each. A load replaces the ring with the save's history and drops
     * anything after the load UT; anything it cannot trust leaves the ring
     * empty instead. Pure, so the format is tested headless.
     */
    internal sealed partial class LightHistory
    {
        public const int SaveFormat = 1;
        private static readonly byte[] SaveMagic = { (byte)'K', (byte)'C', (byte)'L', (byte)'H' };

        /* Bytes each record takes in the format, for sizing a save before writing it and bounding counts when reading one. */
        private const int HeaderBytes = 4 + 4 + 8 + 8 + 8 + 4;
        private const int TrackHeaderBytes = 8 + 4 + 1 + 1 + 8 + 4 + 8 + 1;
        private const int SavedEntryBytes = 8 + 6 * 8 + 7 * 4 + 4 + 1 + 1;
        private const int CompositionHeaderBytes = 8 + 3 * 4 + 1;
        private const int PartFixedBytes = 4 + 8 * 4;
        private const int SavedEventBytes = 8 + 1 + 4;
        /* A body index past this is damage, not a body, while the bodies are not yet known. */
        private const int MaxBodyIndex = 4096;

        /* The UT of the save the ring was loaded from, until the ring next takes
           a sample; while set, the ring turning on keeps it. */
        public double? RestoredUt { get; private set; }

        /* How many times a save has been serialised, for the single-pass test. */
        public int SaveWrites { get; private set; }

        public byte[] Save(double nowUt, int capBytes, out double floorUt) => Save(nowUt, capBytes, out floorUt, out _);

        /*
         * The ring as a save holds it at `nowUt`, at most `capBytes` long,
         * leaving out `dropped` tracks when every track's newest instant
         * together is over the cap, or null when not one track fits.
         * `floorUt` is the oldest instant the save can answer for. Before the
         * bodies are known there is no horizon, and the ring's own floor stands.
         */
        public byte[] Save(double nowUt, int capBytes, out double floorUt, out int dropped)
        {
            dropped = 0;
            double latest = Math.Max(_latestUt, nowUt);
            floorUt = SystemDiameterM > 0 ? Math.Max(FloorUt, nowUt - HorizonS) : FloorUt;
            if (double.IsNaN(floorUt) || double.IsPositiveInfinity(floorUt)) floorUt = FloorUt;
            var partBytes = new Dictionary<PartRecord[], long>();
            long size = SizeAt(floorUt, partBytes);
            double from = double.IsNegativeInfinity(floorUt) ? OldestUt() : floorUt;
            for (int guard = 0; guard < 64 && size > capBytes; guard++)
            {
                if (!(from < latest)) return null;
                from = Math.Min(from + Math.Max((latest - from) * 0.1, FloorMarginS), latest);
                floorUt = from;
                size = SizeAt(floorUt, partBytes);
            }
            List<HistoryTrack> keep = null;
            if (size > capBytes)
            {
                keep = Fitting(floorUt, capBytes, partBytes, out dropped);
                if (keep.Count == 0) return null;
            }
            SaveWrites++;
            return Write(floorUt, latest, keep);
        }

        /* The tracks a save at `floor` keeps under `capBytes`, in the order they are worth keeping, and how many it leaves out. */
        private List<HistoryTrack> Fitting(double floor, long capBytes, Dictionary<PartRecord[], long> partBytes, out int dropped)
        {
            var sized = new List<KeyValuePair<long, HistoryTrack>>();
            foreach (var t in _tracks.Values)
                if (t.EndUt >= floor) sized.Add(new KeyValuePair<long, HistoryTrack>(TrackSize(t, floor, partBytes), t));
            sized.Sort((a, b) =>
            {
                int byRank = SaveRank(a.Value).CompareTo(SaveRank(b.Value));
                if (byRank != 0) return byRank;
                int bySize = a.Key.CompareTo(b.Key);
                return bySize != 0 ? bySize : a.Value.Key.CompareTo(b.Value.Key);
            });
            var keep = new List<HistoryTrack>();
            long size = HeaderBytes;
            foreach (var kv in sized)
            {
                if (size + kv.Key > capBytes) continue;
                size += kv.Key;
                keep.Add(kv.Value);
            }
            dropped = sized.Count - keep.Count;
            return keep;
        }

        /* Bodies first, every craft is drawn against them; then craft still flying; then ghosts and ended craft. */
        private static int SaveRank(HistoryTrack t) => t.IsBody ? 0 : !t.IsGhost && !t.Ended ? 1 : 2;

        /* The length Write would produce at `floor`, without writing it. */
        internal long SizeAt(double floor, Dictionary<PartRecord[], long> partBytes)
        {
            long size = HeaderBytes;
            foreach (var t in _tracks.Values)
                if (t.EndUt >= floor) size += TrackSize(t, floor, partBytes);
            return size;
        }

        /* The bytes track `t` takes in a save at `floor`. */
        private static long TrackSize(HistoryTrack t, double floor, Dictionary<PartRecord[], long> partBytes)
        {
            long size = TrackHeaderBytes + 4 + 4 + 4;
            FirstEntry(t, floor, out int chunk, out int index);
            for (int k = chunk; k < t.Chunks.Count; k++) size += (long)(t.Chunks[k].Count - (k == chunk ? index : 0)) * SavedEntryBytes;
            int firstComp = FirstComposition(t, floor);
            for (int k = firstComp; k < t.Compositions.Count; k++)
            {
                size += CompositionHeaderBytes;
                var parts = t.Compositions[k].Parts;
                if (k > firstComp && ReferenceEquals(t.Compositions[k - 1].Parts, parts)) continue;
                if (!partBytes.TryGetValue(parts, out long bytes))
                {
                    bytes = 4;
                    foreach (var p in parts) bytes += PartFixedBytes + StringBytes(p.Name ?? "");
                    partBytes[parts] = bytes;
                }
                size += bytes;
            }
            size += (long)(t.Events.Count - FirstEvent(t, floor)) * SavedEventBytes;
            return size;
        }

        /* A string as BinaryWriter writes it: a 7-bit encoded length, then UTF-8. */
        private static int StringBytes(string s)
        {
            int n = Encoding.UTF8.GetByteCount(s);
            int prefix = 1;
            for (int v = n >> 7; v != 0; v >>= 7) prefix++;
            return prefix + n;
        }

        private static void FirstEntry(HistoryTrack t, double floor, out int chunk, out int index)
        {
            chunk = 0;
            index = 0;
            if (t.HasEntries && t.Find(floor, out int c, out int i)) { chunk = c; index = i; }
        }

        private static int FirstComposition(HistoryTrack t, double floor)
        {
            int k = 0;
            while (k + 1 < t.Compositions.Count && t.Compositions[k + 1].Ut <= floor) k++;
            return k;
        }

        private static int FirstEvent(HistoryTrack t, double floor)
        {
            int k = 0;
            while (k < t.Events.Count && t.Events[k].Ut < floor) k++;
            return k;
        }

        /* The ring from `floor`, or only the tracks in `keep` when it is given. */
        internal byte[] Write(double floor, double latest, List<HistoryTrack> keep = null)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                w.Write(SaveMagic);
                w.Write(SaveFormat);
                w.Write(floor);
                w.Write(latest);
                w.Write(_ghostSeq);
                if (keep == null)
                {
                    keep = new List<HistoryTrack>();
                    foreach (var t in _tracks.Values)
                        if (t.EndUt >= floor) keep.Add(t);
                }
                else keep = new List<HistoryTrack>(keep);
                keep.Sort((a, b) => a.Key.CompareTo(b.Key));
                w.Write(keep.Count);
                foreach (var t in keep) WriteTrack(w, t, floor);
                w.Flush();
                return ms.ToArray();
            }
        }

        /* From the entry and the parts a read at `floor` starts from. */
        private static void WriteTrack(BinaryWriter w, HistoryTrack t, double floor)
        {
            w.Write(t.Key);
            w.Write(t.Pid);
            w.Write(t.IsBody);
            w.Write(t.IsGhost);
            w.Write(t.GhostedUt);
            w.Write(t.ParentPid);
            w.Write(t.EndUt);
            w.Write((byte)t.EndKind);

            FirstEntry(t, floor, out int firstChunk, out int firstIndex);
            int entries = 0;
            for (int k = firstChunk; k < t.Chunks.Count; k++) entries += t.Chunks[k].Count - (k == firstChunk ? firstIndex : 0);
            w.Write(entries);
            for (int k = firstChunk; k < t.Chunks.Count; k++)
            {
                var chunk = t.Chunks[k];
                for (int j = k == firstChunk ? firstIndex : 0; j < chunk.Count; j++) WriteEntry(w, chunk.Items[j]);
            }

            int firstComp = FirstComposition(t, floor);
            w.Write(t.Compositions.Count - firstComp);
            for (int k = firstComp; k < t.Compositions.Count; k++)
            {
                var comp = t.Compositions[k];
                w.Write(comp.Ut);
                w.Write(comp.ComX); w.Write(comp.ComY); w.Write(comp.ComZ);
                /* A record that only moved the centre of mass shares the parts of the one before. */
                bool shared = k > firstComp && ReferenceEquals(t.Compositions[k - 1].Parts, comp.Parts);
                w.Write(shared);
                if (shared) continue;
                w.Write(comp.Parts.Length);
                foreach (var p in comp.Parts)
                {
                    w.Write(p.Pid);
                    w.Write(p.Name ?? "");
                    w.Write(p.Px); w.Write(p.Py); w.Write(p.Pz);
                    w.Write(p.Qx); w.Write(p.Qy); w.Write(p.Qz); w.Write(p.Qw);
                    w.Write(p.RadiusM);
                }
            }

            int firstEvent = FirstEvent(t, floor);
            w.Write(t.Events.Count - firstEvent);
            for (int k = firstEvent; k < t.Events.Count; k++)
            {
                var e = t.Events[k];
                w.Write(e.Ut);
                w.Write((byte)e.Kind);
                w.Write(e.Other);
            }
        }

        private static void WriteEntry(BinaryWriter w, in HistoryEntry e)
        {
            w.Write(e.Ut);
            w.Write(e.Rx); w.Write(e.Ry); w.Write(e.Rz);
            w.Write(e.Vx); w.Write(e.Vy); w.Write(e.Vz);
            w.Write(e.Qx); w.Write(e.Qy); w.Write(e.Qz); w.Write(e.Qw);
            w.Write(e.Wx); w.Write(e.Wy); w.Write(e.Wz);
            w.Write(e.Body);
            w.Write((byte)e.Kind);
            w.Write((byte)e.Reason);
        }

        /*
         * Replaces the ring with a save's history, as of `loadUt`: anything
         * after it is dropped. Null or empty `data` is a save from before the
         * ring was saved: the ring starts empty, and a distant craft is not
         * drawn until its past instant passes the load. False, with the ring
         * empty, when the data cannot be read or trusted. Never throws.
         */
        public bool Load(byte[] data, double loadUt, out string error)
        {
            error = null;
            ClearHistory();
            if (data != null && data.Length > 0)
            {
                try
                {
                    Read(data);
                    double latest = NewestUt();
                    if (latest > _latestUt) _latestUt = latest;
                    if (loadUt < _latestUt) Rewind(loadUt);
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }
                if (error != null) ClearHistory();
            }
            if (!double.IsNaN(loadUt) && !(_latestUt >= loadUt)) _latestUt = loadUt;
            RestoredUt = double.IsNaN(loadUt) ? (double?)null : loadUt;
            return error == null;
        }

        /* Whether the ring still holds everything a save it wrote at generation
           `savedGeneration`, from floor `savedFloor`, carries: nothing cleared
           or rewound since, and nothing it holds trimmed away. */
        public bool HoldsSave(long savedGeneration, double savedFloor) =>
            savedGeneration == Generation && FloorUt <= savedFloor;

        /* The latest instant anything in the ring is stamped with. */
        private double NewestUt()
        {
            double latest = double.NegativeInfinity;
            foreach (var t in _tracks.Values)
            {
                if (t.HasEntries) latest = Math.Max(latest, t.Last.Ut);
                if (t.Compositions.Count > 0) latest = Math.Max(latest, t.Compositions[t.Compositions.Count - 1].Ut);
                if (t.Events.Count > 0) latest = Math.Max(latest, t.Events[t.Events.Count - 1].Ut);
                if (t.Ended) latest = Math.Max(latest, t.EndUt);
                if (t.IsGhost) latest = Math.Max(latest, t.GhostedUt);
            }
            return latest;
        }

        private void Read(byte[] data)
        {
            using (var r = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8))
            {
                var magic = r.ReadBytes(SaveMagic.Length);
                for (int i = 0; i < SaveMagic.Length; i++)
                    if (magic.Length != SaveMagic.Length || magic[i] != SaveMagic[i]) throw new InvalidDataException("not a light-time history");
                int format = r.ReadInt32();
                if (format != SaveFormat) throw new InvalidDataException($"history format {format}, expected {SaveFormat}");
                double floor = r.ReadDouble();
                double latest = r.ReadDouble();
                if (double.IsNaN(floor) || double.IsPositiveInfinity(floor) || !Finite(latest) || floor > latest)
                    throw new InvalidDataException($"history span {floor} to {latest}");
                long ghostSeq = r.ReadInt64();
                if (ghostSeq < 0) throw new InvalidDataException($"ghost sequence {ghostSeq}");
                int count = Count(r, TrackHeaderBytes + 12);
                var names = new Dictionary<string, string>();
                for (int n = 0; n < count; n++) ReadTrack(r, names);
                FloorUt = floor;
                _latestUt = latest;
                _ghostSeq = ghostSeq;
            }
            foreach (var t in _tracks.Values)
            {
                if (!t.IsGhost) continue;
                if (!_ghosts.TryGetValue(t.Pid, out var list)) _ghosts[t.Pid] = list = new List<HistoryTrack>();
                list.Add(t);
            }
            foreach (var list in _ghosts.Values) list.Sort((a, b) => a.Key.CompareTo(b.Key));
            Recount();
        }

        private void ReadTrack(BinaryReader r, Dictionary<string, string> names)
        {
            long key = r.ReadInt64();
            uint pid = r.ReadUInt32();
            bool isBody = r.ReadBoolean();
            bool isGhost = r.ReadBoolean();
            bool keyOk = isBody ? IsBodyKey(key) : isGhost ? key > GhostKeyBase : key == CraftKey(pid);
            if (!keyOk || (isBody && isGhost)) throw new InvalidDataException($"track key {key}");
            var t = new HistoryTrack(key, isBody ? 0u : pid, isBody)
            {
                IsGhost = isGhost,
                GhostedUt = r.ReadDouble(),
                ParentPid = r.ReadUInt32(),
                EndUt = r.ReadDouble(),
                EndKind = (IdentityKind)r.ReadByte(),
            };
            if (!Finite(t.GhostedUt) || double.IsNaN(t.EndUt) || double.IsNegativeInfinity(t.EndUt) || !Enum.IsDefined(typeof(IdentityKind), t.EndKind))
                throw new InvalidDataException($"track {key} header");
            if (_tracks.ContainsKey(key)) throw new InvalidDataException($"track {key} twice");
            _tracks[key] = t;

            int entries = Count(r, SavedEntryBytes);
            for (int i = 0; i < entries; i++)
            {
                var e = ReadEntry(r);
                if (!Valid(e)) throw new InvalidDataException($"track {key} entry {i}");
                if (t.HasEntries && !(e.Ut >= t.Last.Ut)) throw new InvalidDataException($"track {key} entries out of order");
                Append(t, e);
            }

            int comps = Count(r, CompositionHeaderBytes);
            for (int i = 0; i < comps; i++)
            {
                var comp = new Composition { Ut = r.ReadDouble(), ComX = r.ReadSingle(), ComY = r.ReadSingle(), ComZ = r.ReadSingle() };
                if (!Finite(comp.Ut) || !Finite(comp.ComX) || !Finite(comp.ComY) || !Finite(comp.ComZ)
                    || (t.Compositions.Count > 0 && comp.Ut < t.Compositions[t.Compositions.Count - 1].Ut))
                    throw new InvalidDataException($"track {key} parts record {i}");
                if (r.ReadBoolean())
                {
                    if (t.Compositions.Count == 0) throw new InvalidDataException($"track {key} shares parts with no record before");
                    comp.Parts = t.Compositions[t.Compositions.Count - 1].Parts;
                }
                else
                {
                    comp.Parts = new PartRecord[Count(r, PartFixedBytes + 1)];
                    for (int k = 0; k < comp.Parts.Length; k++)
                    {
                        uint partPid = r.ReadUInt32();
                        string name = r.ReadString();
                        if (name.Length == 0) name = null;
                        else if (names.TryGetValue(name, out var shared)) name = shared;
                        else names[name] = name;
                        var p = new PartRecord
                        {
                            Pid = partPid, Name = name,
                            Px = r.ReadSingle(), Py = r.ReadSingle(), Pz = r.ReadSingle(),
                            Qx = r.ReadSingle(), Qy = r.ReadSingle(), Qz = r.ReadSingle(), Qw = r.ReadSingle(),
                            RadiusM = r.ReadSingle(),
                        };
                        if (!Finite(p.Px) || !Finite(p.Py) || !Finite(p.Pz) || !Finite(p.Qx) || !Finite(p.Qy) || !Finite(p.Qz) || !Finite(p.Qw)
                            || !Finite(p.RadiusM) || p.RadiusM < 0)
                            throw new InvalidDataException($"track {key} part {partPid}");
                        comp.Parts[k] = p;
                    }
                }
                comp.SizeM = Composition.Size(comp.Parts, comp.ComX, comp.ComY, comp.ComZ);
                t.Compositions.Add(comp);
                if (comp.SizeM > t.MaxSizeM) t.MaxSizeM = comp.SizeM;
            }

            int events = Count(r, SavedEventBytes);
            for (int i = 0; i < events; i++)
            {
                var e = new HistoryEvent { Ut = r.ReadDouble(), Kind = (IdentityKind)r.ReadByte(), Other = r.ReadUInt32() };
                if (!Finite(e.Ut) || !Enum.IsDefined(typeof(IdentityKind), e.Kind) || (t.Events.Count > 0 && e.Ut < t.Events[t.Events.Count - 1].Ut))
                    throw new InvalidDataException($"track {key} event {i}");
                t.Events.Add(e);
            }
        }

        private bool Valid(in HistoryEntry e)
        {
            int bodies = _bodies.Length > 0 ? _bodies.Length : MaxBodyIndex;
            return Finite(e.Ut) && Finite(e.Rx) && Finite(e.Ry) && Finite(e.Rz) && Finite(e.Vx) && Finite(e.Vy) && Finite(e.Vz)
                && Finite(e.Qx) && Finite(e.Qy) && Finite(e.Qz) && Finite(e.Qw) && Finite(e.Wx) && Finite(e.Wy) && Finite(e.Wz)
                && e.Body >= 0 && e.Body < bodies
                && Enum.IsDefined(typeof(HistoryKind), e.Kind) && Enum.IsDefined(typeof(HistoryReason), e.Reason);
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static HistoryEntry ReadEntry(BinaryReader r) => new HistoryEntry
        {
            Ut = r.ReadDouble(),
            Rx = r.ReadDouble(), Ry = r.ReadDouble(), Rz = r.ReadDouble(),
            Vx = r.ReadDouble(), Vy = r.ReadDouble(), Vz = r.ReadDouble(),
            Qx = r.ReadSingle(), Qy = r.ReadSingle(), Qz = r.ReadSingle(), Qw = r.ReadSingle(),
            Wx = r.ReadSingle(), Wy = r.ReadSingle(), Wz = r.ReadSingle(),
            Body = r.ReadInt32(),
            Kind = (HistoryKind)r.ReadByte(),
            Reason = (HistoryReason)r.ReadByte(),
        };

        /* A count no larger than the bytes left could hold at `minBytes` each, so damage fails rather than allocating. */
        private static int Count(BinaryReader r, int minBytes)
        {
            int n = r.ReadInt32();
            long left = r.BaseStream.Length - r.BaseStream.Position;
            if (n < 0 || (long)n * minBytes > left) throw new InvalidDataException($"count {n} with {left} bytes left");
            return n;
        }

        /* Every chunk's and track's bounds from its entries, against the bodies as now known. */
        private void RebuildSpheres()
        {
            foreach (var t in _tracks.Values)
            {
                bool hasPrev = false;
                HistoryEntry prev = default;
                foreach (var chunk in t.Chunks)
                {
                    chunk.Spheres.Clear();
                    for (int k = 0; k < chunk.Count; k++)
                    {
                        if (hasPrev) FoldSpanInto(chunk.Spheres, prev, chunk.Items[k]);
                        FoldEntryInto(chunk.Spheres, chunk.Items[k]);
                        prev = chunk.Items[k];
                        hasPrev = true;
                    }
                }
                t.MergeSpheres();
            }
        }

        /* After a load: the save's slowest speed, unless a slower one is set now. */
        public void RestoreSlowest(double? mps)
        {
            if (!(mps > 0) || double.IsInfinity(mps.Value)) return;
            if (!SlowestLightSpeed.HasValue || mps.Value < SlowestLightSpeed.Value) SlowestLightSpeed = mps;
        }

        /* Game time passed while the ring was off: the save's history now has an unsampled gap after it. */
        public void DropRestored()
        {
            if (RestoredUt.HasValue && !Active) ClearHistory();
        }

        /*
         * Bytes as text a ConfigNode carries whole: base64 with `-` and `_`
         * for `+` and `/`, since a ConfigNode cuts a line at `//` as a comment,
         * and no `=` padding, since `=` separates a key from its value.
         */
        public static string ToSaveText(byte[] data) =>
            Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static byte[] FromSaveText(string text)
        {
            var sb = new StringBuilder(text.Length + 3);
            sb.Append(text).Replace('-', '+').Replace('_', '/');
            while (sb.Length % 4 != 0) sb.Append('=');
            return Convert.FromBase64String(sb.ToString());
        }

        private static uint[] s_crcTable;

        /* CRC-32 (IEEE), to tell a save's history apart from one damaged on the way. */
        public static uint Crc32(byte[] data)
        {
            var table = s_crcTable;
            if (table == null)
            {
                table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
                s_crcTable = table;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return ~crc;
        }
    }
}
