using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kerbcast
{
    /*
     * A tracking network a viewer saved with the game. Its slot fixes its
     * camera's flightId (CameraId.NetworkWireId) for the life of the save.
     * A network a mod made in game is listed beside these with Owner set; it
     * is never stored.
     */
    internal sealed class SavedNetwork
    {
        public string NetworkId = "";
        public int Slot;
        public string Name = NetworkMath.DefaultName;
        /* The craft it watches; 0 for the active vessel. */
        public uint WatchPersistentId;
        public List<SavedVantage> Vantages = new List<SavedVantage>();
        /* NaN keeps the game's default. */
        public double MinHoldUt = double.NaN, MinHoldRealSeconds = double.NaN, MarginStops = double.NaN;
        /* Bumps on every change; a viewer's retarget bumps only this. */
        public uint Revision;
        /* The revision of the last save from a form, which an edit must not predate. */
        public uint ContentRevision;
        /* The token of the request that created it, so a repeat of that request finds it rather than making another. */
        public string CreateToken;
        /* Set only on a mod's network, listed read only, with the camera it runs as. */
        public string Owner;
        public uint HostFlightId;

        public uint FlightId => Owner != null ? HostFlightId : CameraId.NetworkWireId(Slot);

        public bool HasOptions => !double.IsNaN(MinHoldUt) || !double.IsNaN(MinHoldRealSeconds) || !double.IsNaN(MarginStops);

        public NetworkTuning Tuning
        {
            get
            {
                var t = NetworkTuning.Default;
                if (!double.IsNaN(MinHoldUt)) t.MinHoldUt = MinHoldUt;
                if (!double.IsNaN(MinHoldRealSeconds)) t.MinHoldRealSeconds = MinHoldRealSeconds;
                if (!double.IsNaN(MarginStops)) t.MarginStops = MarginStops;
                return t;
            }
        }

        /* The camera must be rebuilt for this change: its name is in the manifest. */
        public static bool NeedsRebuild(SavedNetwork before, SavedNetwork after) => before.Name != after.Name;

        /*
         * How an edit reaches the running camera. A browser may edit under a
         * held command lock, but the live aim stays the holder's: the edit is
         * stored, its watch is not applied, and a rebuild keeps what the
         * holder set.
         */
        public static SavedNetworkEditPlan PlanEdit(SavedNetwork before, SavedNetwork after, bool running, bool lockHeld)
        {
            bool rebuild = NeedsRebuild(before, after) || !running;
            return new SavedNetworkEditPlan
            {
                Rebuild = rebuild,
                ApplyWatch = !rebuild && !lockHeld && before.WatchPersistentId != after.WatchPersistentId,
                CarryLiveAim = lockHeld,
            };
        }
    }

    internal struct SavedNetworkEditPlan
    {
        /* The camera is destroyed and started again under its flightId. */
        public bool Rebuild;
        /* The edited watch becomes the camera's live aim. */
        public bool ApplyWatch;
        /* A rebuild restores the camera's current aim and zoom rather than starting on the saved watch. */
        public bool CarryLiveAim;
    }

    /* The answer to one save or delete, sent back by the client's token. */
    internal struct SavedNetworkOutcome
    {
        public string Token;
        public bool Ok;
        public string NetworkId;
        public string Code;
        public string Text;
        /* Unix milliseconds when it was decided, so a sidecar that restarts can still send a recent one. */
        public long AtMs;

        public static SavedNetworkOutcome Refused(string token, string networkId, string code, string text) =>
            new SavedNetworkOutcome { Token = token, Ok = false, NetworkId = networkId, Code = code, Text = text };
    }

    /*
     * Every saved network of one save, Unity-free: validation, slots,
     * revisions, the networks.cfg text and the global.networks.json body.
     * The in-game NetworkStore loads and writes it and runs the cameras.
     */
    internal sealed class SavedNetworkSet
    {
        public const string FileName = "networks.cfg";
        public const string JsonFileName = "global.networks.json";
        public const int MaxNameLength = 48;
        /* Outcomes kept in the JSON file for the sidecar to pick up. */
        public const int OutcomesKept = 64;

        private readonly List<SavedNetwork> _networks = new List<SavedNetwork>();
        /* Networks this version cannot read (a newer one wrote them), written back as they were. */
        private readonly List<CfgNode> _kept = new List<CfgNode>();
        private readonly HashSet<int> _keptSlots = new HashSet<int>();
        private readonly List<SavedNetworkOutcome> _outcomes = new List<SavedNetworkOutcome>();
        private int _nextSlot;

        public IReadOnlyList<SavedNetwork> Networks => _networks;
        public IReadOnlyList<SavedNetworkOutcome> Outcomes => _outcomes;

        public SavedNetwork Find(string networkId)
        {
            if (string.IsNullOrEmpty(networkId)) return null;
            foreach (var n in _networks)
                if (n.NetworkId == networkId) return n;
            return null;
        }

        public SavedNetwork FindByFlightId(uint flightId)
        {
            foreach (var n in _networks)
                if (n.FlightId == flightId) return n;
            return null;
        }

        /* A token keeps one entry: a repeat replaces it with a strictly later atMs, which is how the sidecar tells a new answer from one it has sent. */
        public void Record(SavedNetworkOutcome outcome, long atMs)
        {
            if (string.IsNullOrEmpty(outcome.Token)) return;
            int earlier = _outcomes.FindIndex(o => o.Token == outcome.Token);
            if (earlier >= 0)
            {
                atMs = Math.Max(atMs, _outcomes[earlier].AtMs + 1);
                _outcomes.RemoveAt(earlier);
            }
            outcome.AtMs = atMs;
            _outcomes.Add(outcome);
            if (_outcomes.Count > OutcomesKept) _outcomes.RemoveAt(0);
        }

        /*
         * Validates and applies a save. `before` is a copy of the network as
         * it stood (null on create) and `saved` the network now stored; both
         * null when refused. `newId` makes a fresh networkId.
         */
        public SavedNetworkOutcome Save(CommandCameraRequest r, Func<string> newId, out SavedNetwork before, out SavedNetwork saved)
        {
            before = null;
            saved = null;
            string token = r.Token;
            if (r.RefusalCode != null) return SavedNetworkOutcome.Refused(token, r.NetworkId, r.RefusalCode, r.RefusalText);
            if (r.NetworkId.Length == 0 && !string.IsNullOrEmpty(token))
                foreach (var made in _networks)
                    if (made.CreateToken == token) return new SavedNetworkOutcome { Token = token, Ok = true, NetworkId = made.NetworkId };
            var existing = r.NetworkId.Length > 0 ? Find(r.NetworkId) : null;
            if (r.NetworkId.Length > 0 && existing == null)
                return SavedNetworkOutcome.Refused(token, r.NetworkId, SavedNetworkCodes.NotFound, "no saved network with that id");
            /* A viewer's retarget since the form opened is not a conflict: the form's save carries its own watch. */
            if (existing != null && r.BaseRevision >= 0 && (r.BaseRevision < existing.ContentRevision || r.BaseRevision > existing.Revision))
                return SavedNetworkOutcome.Refused(token, r.NetworkId, SavedNetworkCodes.Stale,
                    $"the network changed since revision {r.BaseRevision}; it is at {existing.Revision}");
            var refused = Check(r);
            if (refused != null) return SavedNetworkOutcome.Refused(token, r.NetworkId, refused[0], refused[1]);
            int slot = existing != null ? existing.Slot : FreeSlot();
            if (slot < 0) return SavedNetworkOutcome.Refused(token, null, SavedNetworkCodes.Full, "every network slot is taken");

            var n = existing ?? new SavedNetwork { NetworkId = newId(), Slot = slot, CreateToken = string.IsNullOrEmpty(token) ? null : token };
            if (existing != null) before = Copy(existing);
            n.Name = CleanName(r.Name);
            n.WatchPersistentId = r.Aim?.PersistentId ?? 0;
            n.Vantages = new List<SavedVantage>(r.Vantages.Count);
            foreach (var v in r.Vantages)
                n.Vantages.Add(new SavedVantage { Vantage = v.Vantage, Label = CleanLabel(v.Label), Fallback = v.Fallback });
            n.MinHoldUt = r.MinHoldUt;
            n.MinHoldRealSeconds = r.MinHoldRealSeconds;
            n.MarginStops = r.MarginStops;
            n.Revision = existing != null ? existing.Revision + 1 : 1;
            n.ContentRevision = n.Revision;
            if (existing == null)
            {
                _networks.Add(n);
                _nextSlot = (slot + 1) % CameraId.NetworkSlots;
            }
            saved = n;
            return new SavedNetworkOutcome { Token = token, Ok = true, NetworkId = n.NetworkId };
        }

        /*
         * Undoes a Save or Delete whose effect could not be kept (its camera
         * refused, or the file could not be written): `saved` with `before`
         * null is a create to remove, with `before` set an edit to revert,
         * and `removed` a delete to put back where it was.
         */
        public void Undo(SavedNetwork before, SavedNetwork saved, SavedNetwork removed = null, int removedAt = -1)
        {
            if (removed != null)
            {
                _networks.Insert(Math.Max(0, Math.Min(removedAt, _networks.Count)), removed);
                return;
            }
            if (saved == null) return;
            if (before == null)
            {
                _networks.Remove(saved);
                return;
            }
            CopyInto(before, saved);
        }

        public int IndexOf(SavedNetwork n) => _networks.IndexOf(n);

        public SavedNetworkOutcome Delete(CommandCameraRequest r, out SavedNetwork removed)
        {
            removed = Find(r.NetworkId);
            if (removed == null)
                return SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.NotFound, "no saved network with that id");
            _networks.Remove(removed);
            return new SavedNetworkOutcome { Token = r.Token, Ok = true, NetworkId = removed.NetworkId };
        }

        /* A viewer pointed the network's camera at a craft (or back at the active vessel): the saved watch follows. True when it changed. */
        public bool SetWatch(uint flightId, uint persistentId) => SetWatch(flightId, persistentId, out _, out _);

        /* SetWatch with what Undo needs if the change cannot be kept. */
        public bool SetWatch(uint flightId, uint persistentId, out SavedNetwork before, out SavedNetwork saved)
        {
            before = null;
            saved = FindByFlightId(flightId);
            if (saved == null || saved.WatchPersistentId == persistentId)
            {
                saved = null;
                return false;
            }
            before = Copy(saved);
            saved.WatchPersistentId = persistentId;
            saved.Revision++;
            return true;
        }

        /* Every network with a craft to watch. */
        public IEnumerable<SavedNetwork> Watching()
        {
            foreach (var n in _networks)
                if (n.WatchPersistentId != 0) yield return n;
        }

        /*
         * A dock or claw grab retired `oldPid` into `newPid`: every craft
         * vantage and watch on it follows. Like a retarget it bumps only
         * Revision, so a form already open is not made stale. True when
         * anything changed. KSP announces each merge twice; the second finds
         * nothing to move.
         */
        public bool FollowMerge(uint oldPid, uint newPid)
        {
            if (oldPid == 0 || newPid == 0 || oldPid == newPid) return false;
            bool any = false;
            foreach (var n in _networks)
            {
                bool changed = n.WatchPersistentId == oldPid;
                if (changed) n.WatchPersistentId = newPid;
                for (int i = 0; i < n.Vantages.Count; i++)
                {
                    var sv = n.Vantages[i];
                    if (sv.Vantage.Kind != CommandVantageKind.Vessel || sv.Vantage.PersistentId != oldPid) continue;
                    n.Vantages[i] = new SavedVantage { Vantage = CommandVantage.Vessel(newPid), Label = sv.Label, Fallback = sv.Fallback };
                    changed = true;
                }
                if (!changed) continue;
                n.Revision++;
                any = true;
            }
            return any;
        }

        /* [code, text] when the request cannot be saved, else null. */
        internal static string[] Check(CommandCameraRequest r)
        {
            string name = r.Name?.Trim() ?? "";
            if (name.Length > MaxNameLength) return new[] { SavedNetworkCodes.BadName, $"a name takes at most {MaxNameLength} characters" };
            if (r.Vantages == null || r.Vantages.Count == 0) return new[] { SavedNetworkCodes.NoVantages, "a network needs at least one vantage" };
            if (r.Vantages.Count > NetworkMath.MaxVantages)
                return new[] { SavedNetworkCodes.TooManyVantages, $"a network takes at most {NetworkMath.MaxVantages} vantages" };
            for (int i = 0; i < r.Vantages.Count; i++)
            {
                var v = r.Vantages[i].Vantage;
                string why = v == null ? "no vantage" : v.Invalid();
                if (why != null) return new[] { SavedNetworkCodes.BadVantage, $"vantage {i}: {why}" };
            }
            if (r.Aim != null)
            {
                if (r.Aim.Kind != "vessel") return new[] { SavedNetworkCodes.BadAim, "a network watches a craft (a vessel aim)" };
                string why = r.Aim.Invalid();
                if (why != null) return new[] { SavedNetworkCodes.BadAim, why };
            }
            if (!AtLeastZeroOrUnset(r.MinHoldUt) || !AtLeastZeroOrUnset(r.MinHoldRealSeconds) || !AtLeastZeroOrUnset(r.MarginStops))
                return new[] { SavedNetworkCodes.BadNumber, "hold times and margin must be finite, 0 or more" };
            return null;
        }

        private static bool AtLeastZeroOrUnset(double v) => double.IsNaN(v) || (!double.IsInfinity(v) && v >= 0);

        private int FreeSlot()
        {
            var used = new HashSet<int>();
            foreach (var n in _networks) used.Add(n.Slot);
            used.UnionWith(_keptSlots);
            for (int k = 0; k < CameraId.NetworkSlots; k++)
            {
                int slot = (_nextSlot + k) % CameraId.NetworkSlots;
                if (!used.Contains(slot)) return slot;
            }
            return -1;
        }

        private static string CleanName(string name)
        {
            string s = Clean(name);
            return s.Length == 0 ? NetworkMath.DefaultName : s;
        }

        private static string CleanLabel(string label)
        {
            string s = Clean(label);
            if (s.Length == 0) return null;
            return s.Length > NetworkMath.MaxLabelLength ? s.Substring(0, NetworkMath.MaxLabelLength) : s;
        }

        /* One line, trimmed: the file keeps a value per line. */
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s) sb.Append(char.IsControl(c) ? ' ' : c);
            return sb.ToString().Trim();
        }

        private static SavedNetwork Copy(SavedNetwork n)
        {
            var c = new SavedNetwork();
            CopyInto(n, c);
            return c;
        }

        private static void CopyInto(SavedNetwork from, SavedNetwork to)
        {
            to.NetworkId = from.NetworkId;
            to.Slot = from.Slot;
            to.Name = from.Name;
            to.WatchPersistentId = from.WatchPersistentId;
            to.Vantages = new List<SavedVantage>(from.Vantages);
            to.MinHoldUt = from.MinHoldUt;
            to.MinHoldRealSeconds = from.MinHoldRealSeconds;
            to.MarginStops = from.MarginStops;
            to.Revision = from.Revision;
            to.ContentRevision = from.ContentRevision;
            to.CreateToken = from.CreateToken;
        }

        /*
         * networks.cfg: KSP's ConfigNode layout, one value per line, so a
         * player can read it. A network whose vantages no longer parse is
         * dropped with a reason in `skipped`; the rest load.
         */
        public string ToCfg()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("KERBCAST_NETWORKS\n{\n");
            sb.Append("\tnextSlot = ").Append(_nextSlot.ToString(inv)).Append('\n');
            foreach (var n in _networks)
            {
                sb.Append("\tNETWORK\n\t{\n");
                Line(sb, 2, "id", n.NetworkId);
                Line(sb, 2, "slot", n.Slot.ToString(inv));
                Line(sb, 2, "name", n.Name);
                Line(sb, 2, "revision", n.Revision.ToString(inv));
                Line(sb, 2, "contentRevision", n.ContentRevision.ToString(inv));
                if (n.CreateToken != null) Line(sb, 2, "createToken", n.CreateToken);
                Line(sb, 2, "watch", n.WatchPersistentId.ToString(inv));
                if (!double.IsNaN(n.MinHoldUt)) Line(sb, 2, "minHoldUt", n.MinHoldUt.ToString("R", inv));
                if (!double.IsNaN(n.MinHoldRealSeconds)) Line(sb, 2, "minHoldRealSeconds", n.MinHoldRealSeconds.ToString("R", inv));
                if (!double.IsNaN(n.MarginStops)) Line(sb, 2, "marginStops", n.MarginStops.ToString("R", inv));
                foreach (var sv in n.Vantages)
                {
                    var v = sv.Vantage;
                    sb.Append("\t\tVANTAGE\n\t\t{\n");
                    Line(sb, 3, "kind", KindTag(v.Kind));
                    switch (v.Kind)
                    {
                        case CommandVantageKind.LaunchSite:
                            if (v.Name.Length > 0) Line(sb, 3, "site", v.Name);
                            Line(sb, 3, "distance", v.Distance.ToString("R", inv));
                            Line(sb, 3, "bearing", v.Bearing.ToString("R", inv));
                            Line(sb, 3, "height", v.Height.ToString("R", inv));
                            break;
                        case CommandVantageKind.Station:
                            Line(sb, 3, "station", v.Name);
                            Line(sb, 3, "height", v.Height.ToString("R", inv));
                            break;
                        case CommandVantageKind.Surface:
                            Line(sb, 3, "body", v.Body);
                            Line(sb, 3, "latitude", v.Latitude.ToString("R", inv));
                            Line(sb, 3, "longitude", v.Longitude.ToString("R", inv));
                            Line(sb, 3, "height", v.Height.ToString("R", inv));
                            break;
                        default:
                            Line(sb, 3, "persistentId", v.PersistentId.ToString(inv));
                            break;
                    }
                    if (sv.Label != null) Line(sb, 3, "label", sv.Label);
                    if (sv.Fallback) Line(sb, 3, "fallback", "True");
                    sb.Append("\t\t}\n");
                }
                sb.Append("\t}\n");
            }
            foreach (var node in _kept) node.Write(sb, 1);
            sb.Append("}\n");
            return sb.ToString();
        }

        private static void Line(StringBuilder sb, int depth, string key, string value)
        {
            sb.Append('\t', depth).Append(key).Append(" = ").Append(Clean(value)).Append('\n');
        }

        private static string KindTag(CommandVantageKind k)
        {
            switch (k)
            {
                case CommandVantageKind.LaunchSite: return "launchSite";
                case CommandVantageKind.Station: return "station";
                case CommandVantageKind.Surface: return "surface";
                default: return "vessel";
            }
        }

        /*
         * The set in networks.cfg text. False when the text is not a networks
         * file (no KERBCAST_NETWORKS node, or cut off part way), so the caller
         * never saves over it. A network this version cannot read is kept as
         * it was and written back, its slot and id still taken, with the
         * reason in `skipped`.
         */
        public static bool TryFromCfg(string text, List<string> skipped, out SavedNetworkSet set)
        {
            set = new SavedNetworkSet();
            if (string.IsNullOrWhiteSpace(text)) return true;
            var root = CfgNode.Parse(text, out bool balanced);
            var top = root.Child("KERBCAST_NETWORKS");
            if (top == null || !balanced) return false;
            var inv = CultureInfo.InvariantCulture;
            int.TryParse(top.Value("nextSlot"), NumberStyles.Integer, inv, out set._nextSlot);
            if (set._nextSlot < 0 || set._nextSlot >= CameraId.NetworkSlots) set._nextSlot = 0;
            var slots = new HashSet<int>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in top.ChildNodes)
            {
                if (node.Name != "NETWORK")
                {
                    skipped?.Add($"unknown node '{node.Name}'");
                    set._kept.Add(node);
                    continue;
                }
                string id = node.Value("id");
                bool slotRead = int.TryParse(node.Value("slot"), NumberStyles.Integer, inv, out int slot)
                    && slot >= 0 && slot < CameraId.NetworkSlots;
                string bad = null;
                if (!slotRead || id.Length == 0) bad = "no id or slot";
                else if (slots.Contains(slot) || ids.Contains(id)) bad = $"slot {slot} or its id is taken by an earlier one";
                var n = bad != null ? null : Network(node, id, slot, out bad);
                if (n == null)
                {
                    skipped?.Add($"network {id} '{node.Value("name")}': {bad}");
                    set._kept.Add(node);
                    if (slotRead && slots.Add(slot)) set._keptSlots.Add(slot);
                    if (id.Length > 0) ids.Add(id);
                    continue;
                }
                slots.Add(slot);
                ids.Add(id);
                set._networks.Add(n);
            }
            return true;
        }

        private static SavedNetwork Network(CfgNode node, string id, int slot, out string bad)
        {
            var inv = CultureInfo.InvariantCulture;
            bad = null;
            var n = new SavedNetwork
            {
                NetworkId = id,
                Slot = slot,
                Name = CleanName(node.Value("name")),
                MinHoldUt = Num(node, "minHoldUt"),
                MinHoldRealSeconds = Num(node, "minHoldRealSeconds"),
                MarginStops = Num(node, "marginStops"),
            };
            uint.TryParse(node.Value("revision"), NumberStyles.Integer, inv, out n.Revision);
            if (!uint.TryParse(node.Value("contentRevision"), NumberStyles.Integer, inv, out n.ContentRevision)) n.ContentRevision = n.Revision;
            string token = node.Value("createToken");
            n.CreateToken = token.Length > 0 ? token : null;
            uint.TryParse(node.Value("watch"), NumberStyles.Integer, inv, out n.WatchPersistentId);
            foreach (var vn in node.Children("VANTAGE"))
            {
                var v = VantageFrom(vn);
                string why = v == null ? $"unknown kind '{vn.Value("kind")}'" : v.Invalid();
                if (why != null)
                {
                    bad = why;
                    return null;
                }
                n.Vantages.Add(new SavedVantage
                {
                    Vantage = v,
                    Label = CleanLabel(vn.Value("label")),
                    Fallback = string.Equals(vn.Value("fallback"), "True", StringComparison.OrdinalIgnoreCase),
                });
            }
            if (n.Vantages.Count == 0 || n.Vantages.Count > NetworkMath.MaxVantages)
            {
                bad = $"{n.Vantages.Count} vantages";
                return null;
            }
            return n;
        }

        private static CommandVantage VantageFrom(CfgNode n)
        {
            double Get(string key, double fallback)
            {
                double v = Num(n, key);
                return double.IsNaN(v) ? fallback : v;
            }
            switch (n.Value("kind"))
            {
                case "launchSite":
                    return CommandVantage.LaunchSite(n.Value("site"), Get("distance", CommandVantage.DefaultPadDistance),
                        Get("bearing", CommandVantage.DefaultPadBearing), Get("height", CommandVantage.DefaultPadHeight));
                case "station":
                    return CommandVantage.Station(n.Value("station"), Get("height", CommandVantage.DefaultStationHeight));
                case "surface":
                    return CommandVantage.Surface(n.Value("body"), Num(n, "latitude"), Num(n, "longitude"),
                        Get("height", CommandVantage.DefaultSurfaceHeight));
                case "vessel":
                    uint.TryParse(n.Value("persistentId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint pid);
                    return CommandVantage.Vessel(pid);
            }
            return null;
        }

        private static double Num(CfgNode n, string key) =>
            double.TryParse(n.Value(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : double.NaN;

        /*
         * global.networks.json: the saved networks, then `hosts` (mods'
         * networks, always running), each with its camera when `running`
         * says it has one in this scene, and the latest outcomes.
         */
        public string Json(Func<uint, bool> running, IList<SavedNetwork> hosts, bool editable)
        {
            var sb = new StringBuilder(256 + _networks.Count * 512);
            sb.Append("{\"networks\":[");
            bool first = true;
            foreach (var n in _networks)
            {
                if (!first) sb.Append(',');
                first = false;
                AppendJson(sb, n, running(n.FlightId));
            }
            if (hosts != null)
                foreach (var n in hosts)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendJson(sb, n, true);
                }
            sb.Append("],\"editable\":").Append(editable ? "true" : "false").Append(",\"results\":[");
            for (int i = 0; i < _outcomes.Count; i++)
            {
                var o = _outcomes[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"token\":").Append(Str(o.Token)).Append(",\"ok\":").Append(o.Ok ? "true" : "false");
                if (!string.IsNullOrEmpty(o.NetworkId)) sb.Append(",\"networkId\":").Append(Str(o.NetworkId));
                if (o.Code != null) sb.Append(",\"code\":").Append(Str(o.Code));
                if (o.Text != null) sb.Append(",\"text\":").Append(Str(o.Text));
                if (o.AtMs > 0) sb.Append(",\"atMs\":").Append(o.AtMs.ToString(CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append("]}\n");
            return sb.ToString();
        }

        private static void AppendJson(StringBuilder sb, SavedNetwork n, bool running)
        {
            var inv = CultureInfo.InvariantCulture;
            sb.Append("{\"networkId\":").Append(Str(n.NetworkId))
                .Append(",\"name\":").Append(Str(n.Name));
            if (n.WatchPersistentId != 0)
                sb.Append(",\"watch\":{\"kind\":\"vessel\",\"persistentId\":").Append(n.WatchPersistentId.ToString(inv)).Append('}');
            sb.Append(",\"vantages\":[");
            for (int i = 0; i < n.Vantages.Count; i++)
            {
                var sv = n.Vantages[i];
                var v = sv.Vantage;
                if (i > 0) sb.Append(',');
                sb.Append("{\"vantage\":{\"kind\":\"").Append(KindTag(v.Kind)).Append('"');
                switch (v.Kind)
                {
                    case CommandVantageKind.LaunchSite:
                        if (v.Name.Length > 0) sb.Append(",\"site\":").Append(Str(v.Name));
                        sb.Append(",\"distance\":").Append(Number(v.Distance)).Append(",\"bearing\":").Append(Number(v.Bearing))
                            .Append(",\"height\":").Append(Number(v.Height));
                        break;
                    case CommandVantageKind.Station:
                        sb.Append(",\"station\":").Append(Str(v.Name)).Append(",\"height\":").Append(Number(v.Height));
                        break;
                    case CommandVantageKind.Surface:
                        sb.Append(",\"body\":").Append(Str(v.Body)).Append(",\"latitude\":").Append(Number(v.Latitude))
                            .Append(",\"longitude\":").Append(Number(v.Longitude)).Append(",\"height\":").Append(Number(v.Height));
                        break;
                    default:
                        sb.Append(",\"persistentId\":").Append(v.PersistentId.ToString(inv));
                        break;
                }
                sb.Append('}');
                if (sv.Label != null) sb.Append(",\"label\":").Append(Str(sv.Label));
                sb.Append(",\"fallback\":").Append(sv.Fallback ? "true" : "false").Append('}');
            }
            sb.Append(']');
            if (n.HasOptions)
            {
                sb.Append(",\"options\":{");
                bool first = true;
                void Opt(string key, double v)
                {
                    if (double.IsNaN(v)) return;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append('"').Append(key).Append("\":").Append(Number(v));
                }
                Opt("minHoldUt", n.MinHoldUt);
                Opt("minHoldRealSeconds", n.MinHoldRealSeconds);
                Opt("marginStops", n.MarginStops);
                sb.Append('}');
            }
            sb.Append(",\"revision\":").Append(n.Revision.ToString(inv));
            if (running) sb.Append(",\"flightId\":").Append(n.FlightId.ToString(inv));
            if (n.Owner != null) sb.Append(",\"owner\":").Append(Str(n.Owner));
            if (n.CreateToken != null) sb.Append(",\"createToken\":").Append(Str(n.CreateToken));
            sb.Append('}');
        }

        private static string Number(double v) =>
            double.IsNaN(v) || double.IsInfinity(v) ? "0" : v.ToString("R", CultureInfo.InvariantCulture);

        private static string Str(string s)
        {
            var sb = new StringBuilder((s?.Length ?? 0) + 2);
            sb.Append('"');
            foreach (var c in s ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }

    /* Just enough of KSP's ConfigNode text for networks.cfg: named nodes in braces and "key = value" lines. */
    internal sealed class CfgNode
    {
        public readonly string Name;
        private readonly List<KeyValuePair<string, string>> _values = new List<KeyValuePair<string, string>>();
        private readonly List<CfgNode> _children = new List<CfgNode>();

        private CfgNode(string name) { Name = name; }

        /* The first value under `key`, or "". */
        public string Value(string key)
        {
            foreach (var kv in _values)
                if (kv.Key == key) return kv.Value;
            return "";
        }

        public CfgNode Child(string name)
        {
            foreach (var c in _children)
                if (c.Name == name) return c;
            return null;
        }

        public IEnumerable<CfgNode> Children(string name)
        {
            foreach (var c in _children)
                if (c.Name == name) yield return c;
        }

        public IReadOnlyList<CfgNode> ChildNodes => _children;

        /* The node as it was read, values first, for a network kept unread. */
        public void Write(StringBuilder sb, int depth)
        {
            sb.Append('\t', depth).Append(Name).Append('\n');
            sb.Append('\t', depth).Append("{\n");
            foreach (var kv in _values) sb.Append('\t', depth + 1).Append(kv.Key).Append(" = ").Append(kv.Value).Append('\n');
            foreach (var c in _children) c.Write(sb, depth + 1);
            sb.Append('\t', depth).Append("}\n");
        }

        /* `balanced` is false when a brace is left open (a file cut off) or a "}" has nothing to close. */
        public static CfgNode Parse(string text, out bool balanced)
        {
            balanced = true;
            var root = new CfgNode("");
            var stack = new Stack<CfgNode>();
            stack.Push(root);
            string pendingName = null;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line == "{")
                {
                    var node = new CfgNode(pendingName ?? "");
                    stack.Peek()._children.Add(node);
                    stack.Push(node);
                    pendingName = null;
                    continue;
                }
                if (line == "}")
                {
                    pendingName = null;
                    if (stack.Count > 1) stack.Pop();
                    else balanced = false;
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq < 0)
                {
                    string name = line.EndsWith("{") ? line.Substring(0, line.Length - 1).Trim() : line;
                    if (line.EndsWith("{"))
                    {
                        var node = new CfgNode(name);
                        stack.Peek()._children.Add(node);
                        stack.Push(node);
                        pendingName = null;
                    }
                    else pendingName = name;
                    continue;
                }
                pendingName = null;
                stack.Peek()._values.Add(new KeyValuePair<string, string>(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
            }
            if (stack.Count != 1) balanced = false;
            return root;
        }
    }

    /*
     * The save and delete tokens seen this game session and each one's last
     * answer. A request carried into the next scene is not applied twice, and
     * a client retrying a token still hears back, even from an earlier scene.
     */
    internal sealed class SavedNetworkTokens
    {
        private readonly int _kept;
        private readonly Queue<string> _order = new Queue<string>();
        private readonly Dictionary<string, SavedNetworkOutcome> _seen = new Dictionary<string, SavedNetworkOutcome>(StringComparer.Ordinal);

        public SavedNetworkTokens(int kept) { _kept = kept; }

        /* False when the token was claimed before. An empty token is never remembered. */
        public bool Claim(string token)
        {
            if (string.IsNullOrEmpty(token)) return true;
            if (_seen.ContainsKey(token)) return false;
            _seen[token] = default;
            _order.Enqueue(token);
            while (_order.Count > _kept) _seen.Remove(_order.Dequeue());
            return true;
        }

        public void Remember(SavedNetworkOutcome outcome)
        {
            if (!string.IsNullOrEmpty(outcome.Token) && _seen.ContainsKey(outcome.Token)) _seen[outcome.Token] = outcome;
        }

        public bool TryLast(string token, out SavedNetworkOutcome outcome)
        {
            outcome = default;
            return !string.IsNullOrEmpty(token) && _seen.TryGetValue(token, out outcome) && outcome.Token != null;
        }
    }

    /*
     * When global.networks.json is rewritten: only when its text changes or
     * a change asks for it, and never after Stop, whose read-only listing
     * must stand until the next scene writes its own.
     */
    internal sealed class SavedNetworkPublisher
    {
        private readonly Func<string, bool> _write;
        private string _written;

        public bool Stopped { get; private set; }

        public SavedNetworkPublisher(Func<string, bool> write) { _write = write; }

        public void Publish(string json, bool force)
        {
            if (Stopped || (!force && json == _written)) return;
            if (_write(json)) _written = json;
        }

        public void Stop(string json)
        {
            if (Stopped) return;
            Stopped = true;
            if (_write(json)) _written = json;
        }
    }

    /*
     * networks.cfg on disk. A save writes a flushed .tmp and swaps it in,
     * keeping the last file as .bak, so a crash at any point leaves a whole
     * file to load. Unity-free, so each case is tested without the game.
     */
    internal static class SavedNetworkFile
    {
        public enum LoadResult
        {
            /* Read, or no file yet: saves may go ahead. */
            Ok,
            /* The file is there but is not a networks file: saving would lose it. */
            Unreadable,
        }

        /*
         * The set at `path`. A missing or empty file is recovered from its
         * .tmp (a save cut off before the swap) or its .bak, noted in `log`.
         */
        public static LoadResult Load(string path, List<string> log, out SavedNetworkSet set)
        {
            set = new SavedNetworkSet();
            string text = ReadOrNull(path);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var skipped = new List<string>();
                if (!SavedNetworkSet.TryFromCfg(text, skipped, out set))
                {
                    set = new SavedNetworkSet();
                    log?.Add($"{path} is not a networks file; saved networks are read only until it is fixed or removed");
                    return LoadResult.Unreadable;
                }
                foreach (var why in skipped) log?.Add($"{path}: {why}; kept as written");
                return LoadResult.Ok;
            }
            foreach (var spare in new[] { path + ".tmp", path + ".bak" })
            {
                string spareText = ReadOrNull(spare);
                if (string.IsNullOrWhiteSpace(spareText)) continue;
                var skipped = new List<string>();
                if (!SavedNetworkSet.TryFromCfg(spareText, skipped, out var recovered)) continue;
                set = recovered;
                log?.Add($"{path} was {(text == null ? "missing" : "empty")}; recovered {set.Networks.Count} network(s) from {System.IO.Path.GetFileName(spare)}");
                return LoadResult.Ok;
            }
            return LoadResult.Ok;
        }

        public static void Save(string path, string text)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            string tmp = path + ".tmp";
            using (var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > 0)
                System.IO.File.Replace(tmp, path, path + ".bak");
            else
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                System.IO.File.Move(tmp, path);
            }
        }

        /* Null only for a file that is not there. Any other read error reaches the caller: taking it for a missing file would recover an older spare and later save over the real one. */
        private static string ReadOrNull(string path)
        {
            try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; }
            catch (System.IO.FileNotFoundException) { return null; }
            catch (System.IO.DirectoryNotFoundException) { return null; }
        }
    }
}
