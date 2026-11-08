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
        public uint Revision;
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
    }

    /* The answer to one save or delete, sent back by the client's token. */
    internal struct SavedNetworkOutcome
    {
        public string Token;
        public bool Ok;
        public string NetworkId;
        public string Code;
        public string Text;

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
        public const int OutcomesKept = 16;

        private readonly List<SavedNetwork> _networks = new List<SavedNetwork>();
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

        public void Record(SavedNetworkOutcome outcome)
        {
            if (string.IsNullOrEmpty(outcome.Token)) return;
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
            var existing = r.NetworkId.Length > 0 ? Find(r.NetworkId) : null;
            if (r.NetworkId.Length > 0 && existing == null)
                return SavedNetworkOutcome.Refused(token, r.NetworkId, SavedNetworkCodes.NotFound, "no saved network with that id");
            if (existing != null && r.BaseRevision >= 0 && r.BaseRevision != existing.Revision)
                return SavedNetworkOutcome.Refused(token, r.NetworkId, SavedNetworkCodes.Stale,
                    $"the network changed since revision {r.BaseRevision}; it is at {existing.Revision}");
            var refused = Check(r);
            if (refused != null) return SavedNetworkOutcome.Refused(token, r.NetworkId, refused[0], refused[1]);
            int slot = existing != null ? existing.Slot : FreeSlot();
            if (slot < 0) return SavedNetworkOutcome.Refused(token, null, SavedNetworkCodes.Full, "every network slot is taken");

            var n = existing ?? new SavedNetwork { NetworkId = newId(), Slot = slot };
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
            if (existing == null)
            {
                _networks.Add(n);
                _nextSlot = (slot + 1) % CameraId.NetworkSlots;
            }
            saved = n;
            return new SavedNetworkOutcome { Token = token, Ok = true, NetworkId = n.NetworkId };
        }

        public SavedNetworkOutcome Delete(CommandCameraRequest r, out SavedNetwork removed)
        {
            removed = Find(r.NetworkId);
            if (removed == null)
                return SavedNetworkOutcome.Refused(r.Token, r.NetworkId, SavedNetworkCodes.NotFound, "no saved network with that id");
            _networks.Remove(removed);
            return new SavedNetworkOutcome { Token = r.Token, Ok = true, NetworkId = removed.NetworkId };
        }

        /* A viewer pointed the network's camera at a craft (or back at the active vessel): the saved watch follows. True when it changed. */
        public bool SetWatch(uint flightId, uint persistentId)
        {
            var n = FindByFlightId(flightId);
            if (n == null || n.WatchPersistentId == persistentId) return false;
            n.WatchPersistentId = persistentId;
            n.Revision++;
            return true;
        }

        /* [code, text] when the request cannot be saved, else null. */
        private static string[] Check(CommandCameraRequest r)
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

        private static SavedNetwork Copy(SavedNetwork n) => new SavedNetwork
        {
            NetworkId = n.NetworkId,
            Slot = n.Slot,
            Name = n.Name,
            WatchPersistentId = n.WatchPersistentId,
            Vantages = new List<SavedVantage>(n.Vantages),
            MinHoldUt = n.MinHoldUt,
            MinHoldRealSeconds = n.MinHoldRealSeconds,
            MarginStops = n.MarginStops,
            Revision = n.Revision,
        };

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

        public static SavedNetworkSet FromCfg(string text, List<string> skipped)
        {
            var set = new SavedNetworkSet();
            var root = CfgNode.Parse(text ?? "");
            var top = root.Child("KERBCAST_NETWORKS") ?? root;
            var inv = CultureInfo.InvariantCulture;
            int.TryParse(top.Value("nextSlot"), NumberStyles.Integer, inv, out set._nextSlot);
            if (set._nextSlot < 0 || set._nextSlot >= CameraId.NetworkSlots) set._nextSlot = 0;
            var slots = new HashSet<int>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in top.Children("NETWORK"))
            {
                string id = node.Value("id");
                if (!int.TryParse(node.Value("slot"), NumberStyles.Integer, inv, out int slot)
                    || slot < 0 || slot >= CameraId.NetworkSlots || id.Length == 0)
                {
                    skipped?.Add($"network '{node.Value("name")}': no id or slot");
                    continue;
                }
                if (!slots.Add(slot) || !ids.Add(id))
                {
                    skipped?.Add($"network {id}: slot {slot} or its id is taken by an earlier one");
                    continue;
                }
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
                uint.TryParse(node.Value("watch"), NumberStyles.Integer, inv, out n.WatchPersistentId);
                string bad = null;
                foreach (var vn in node.Children("VANTAGE"))
                {
                    var v = VantageFrom(vn);
                    string why = v == null ? $"unknown kind '{vn.Value("kind")}'" : v.Invalid();
                    if (why != null) { bad = why; break; }
                    n.Vantages.Add(new SavedVantage
                    {
                        Vantage = v,
                        Label = CleanLabel(vn.Value("label")),
                        Fallback = string.Equals(vn.Value("fallback"), "True", StringComparison.OrdinalIgnoreCase),
                    });
                }
                if (bad == null && (n.Vantages.Count == 0 || n.Vantages.Count > NetworkMath.MaxVantages))
                    bad = $"{n.Vantages.Count} vantages";
                if (bad != null)
                {
                    skipped?.Add($"network {id} '{n.Name}': {bad}");
                    continue;
                }
                set._networks.Add(n);
            }
            return set;
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
        public string Json(Func<uint, bool> running, IList<SavedNetwork> hosts)
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
            sb.Append("],\"results\":[");
            for (int i = 0; i < _outcomes.Count; i++)
            {
                var o = _outcomes[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"token\":").Append(Str(o.Token)).Append(",\"ok\":").Append(o.Ok ? "true" : "false");
                if (!string.IsNullOrEmpty(o.NetworkId)) sb.Append(",\"networkId\":").Append(Str(o.NetworkId));
                if (o.Code != null) sb.Append(",\"code\":").Append(Str(o.Code));
                if (o.Text != null) sb.Append(",\"text\":").Append(Str(o.Text));
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

        /* Unbalanced braces close what is open at the end; a stray "}" is ignored. */
        public static CfgNode Parse(string text)
        {
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
            return root;
        }
    }
}
