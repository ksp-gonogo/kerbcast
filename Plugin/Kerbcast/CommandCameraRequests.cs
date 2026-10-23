using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kerbcast
{
    internal enum CommandRequestOp
    {
        Create,
        Destroy,
        LookAt,
    }

    /* A point for a command camera to look at instead of its craft, as a
       client sent it. Kind is "vessel" or "surface"; altitude is metres above
       sea level, as CommandAim.Surface takes it. */
    internal sealed class CommandLookAtSpec
    {
        public string Kind = "";
        public uint PersistentId;
        public string Body = "";
        public double Latitude, Longitude, Altitude;
        public double FramingMarginM;

        public string Invalid()
        {
            if (!Finite(FramingMarginM) || FramingMarginM < 0) return "framingMarginM must be a finite number, 0 or more";
            if (Kind == "vessel") return PersistentId == 0 ? "a vessel look-at needs a persistentId" : null;
            if (Kind != "surface") return $"unknown look-at kind '{Kind}'";
            if (Body.Length == 0) return "a surface look-at needs a body name";
            if (!Finite(Latitude) || Latitude < -90 || Latitude > 90) return "latitude must be within -90..90";
            if (!Finite(Longitude) || !Finite(Altitude)) return "longitude and altitude must be finite";
            return null;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }

    /* One entry of global.commands.json, which the sidecar appends to for
       every create-command-camera, destroy-command-camera and
       set-command-look-at a client sends. */
    internal sealed class CommandCameraRequest
    {
        public ulong Id;
        public CommandRequestOp Op;
        /* Create: the client's token, echoed in the new camera's manifest. */
        public string Token = "";
        public string Name = "";
        public CommandVantage Vantage;
        public uint TargetPersistentId;
        /* Create and LookAt; null on LookAt clears it. */
        public CommandLookAtSpec LookAt;
        /* Destroy and LookAt. */
        public uint FlightId;

        /* The requests in `json`, malformed entries skipped with a reason in
           `skipped`. An unreadable file yields an empty list. */
        public static List<CommandCameraRequest> ParseFile(string json, List<string> skipped)
        {
            var result = new List<CommandCameraRequest>();
            object root;
            try { root = MiniJson.Parse(json); }
            catch (FormatException ex)
            {
                skipped?.Add("unreadable: " + ex.Message);
                return result;
            }
            if (!(root is Dictionary<string, object> obj) || !(obj.TryGetValue("requests", out var reqs))
                || !(reqs is List<object> list))
                return result;
            foreach (var item in list)
            {
                var req = FromJson(item as Dictionary<string, object>, out string why);
                if (req != null) result.Add(req);
                else skipped?.Add(why);
            }
            return result;
        }

        private static CommandCameraRequest FromJson(Dictionary<string, object> o, out string why)
        {
            why = null;
            if (o == null) { why = "not an object"; return null; }
            double idNum = Num(o, "id", -1);
            if (idNum < 1 || idNum != Math.Floor(idNum)) { why = "missing id"; return null; }
            var req = new CommandCameraRequest { Id = (ulong)idNum };
            string op = Str(o, "op");
            switch (op)
            {
                case "create":
                    req.Op = CommandRequestOp.Create;
                    req.Token = Str(o, "token");
                    req.Name = Str(o, "name");
                    req.TargetPersistentId = U32(o, "targetPersistentId");
                    req.Vantage = VantageFrom(Obj(o, "vantage"), out why);
                    if (req.Vantage == null) { why = $"request {req.Id}: {why}"; return null; }
                    req.LookAt = LookAtFrom(Obj(o, "lookAt"));
                    return req;
                case "destroy":
                    req.Op = CommandRequestOp.Destroy;
                    req.FlightId = U32(o, "flightId");
                    return req;
                case "lookAt":
                    req.Op = CommandRequestOp.LookAt;
                    req.FlightId = U32(o, "flightId");
                    req.LookAt = LookAtFrom(Obj(o, "lookAt"));
                    return req;
            }
            why = $"request {req.Id}: unknown op '{op}'";
            return null;
        }

        private static CommandVantage VantageFrom(Dictionary<string, object> v, out string why)
        {
            why = null;
            if (v == null) { why = "no vantage"; return null; }
            switch (Str(v, "kind"))
            {
                case "launchSite":
                    return CommandVantage.LaunchSite(Str(v, "site"),
                        Num(v, "distance", CommandVantage.DefaultPadDistance),
                        Num(v, "bearing", CommandVantage.DefaultPadBearing),
                        Num(v, "height", CommandVantage.DefaultPadHeight));
                case "station":
                    return CommandVantage.Station(Str(v, "station"), Num(v, "height", CommandVantage.DefaultStationHeight));
                case "surface":
                    return CommandVantage.Surface(Str(v, "body"), Num(v, "latitude", double.NaN),
                        Num(v, "longitude", double.NaN), Num(v, "height", CommandVantage.DefaultSurfaceHeight));
                case "vessel":
                    return CommandVantage.Vessel(U32(v, "persistentId"));
            }
            why = $"unknown vantage kind '{Str(v, "kind")}'";
            return null;
        }

        private static CommandLookAtSpec LookAtFrom(Dictionary<string, object> l)
        {
            if (l == null) return null;
            return new CommandLookAtSpec
            {
                Kind = Str(l, "kind"),
                PersistentId = U32(l, "persistentId"),
                Body = Str(l, "body"),
                Latitude = Num(l, "latitude", double.NaN),
                Longitude = Num(l, "longitude", double.NaN),
                Altitude = Num(l, "altitude", 0),
                FramingMarginM = Num(l, "framingMarginM", 0),
            };
        }

        private static Dictionary<string, object> Obj(Dictionary<string, object> o, string key) =>
            o.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        private static string Str(Dictionary<string, object> o, string key) =>
            o.TryGetValue(key, out var v) && v is string s ? s : "";

        private static double Num(Dictionary<string, object> o, string key, double fallback) =>
            o.TryGetValue(key, out var v) && v is double d ? d : fallback;

        private static uint U32(Dictionary<string, object> o, string key)
        {
            double d = Num(o, key, 0);
            return d >= 0 && d <= uint.MaxValue && d == Math.Floor(d) ? (uint)d : 0u;
        }
    }

    /* Which requests in global.commands.json are new. Ids only grow, so a
       request at or below the last applied id is a replay of the same file
       and is skipped; the file is re-read whole on every change. A create's
       token is claimed once per scene, so a client retrying with the same
       token, or a create replayed after its camera was destroyed, never
       makes a second camera. */
    internal sealed class CommandRequestLedger
    {
        private readonly HashSet<string> _tokens = new HashSet<string>(StringComparer.Ordinal);

        public ulong LastAppliedId { get; private set; }

        /* A new scene starts past every request already in the file: those
           were for the scene before. */
        public void SeedFrom(List<CommandCameraRequest> existing)
        {
            foreach (var r in existing)
                if (r.Id > LastAppliedId) LastAppliedId = r.Id;
        }

        public List<CommandCameraRequest> Fresh(List<CommandCameraRequest> requests)
        {
            var fresh = new List<CommandCameraRequest>();
            foreach (var r in requests)
                if (r.Id > LastAppliedId) fresh.Add(r);
            fresh.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (fresh.Count > 0) LastAppliedId = fresh[fresh.Count - 1].Id;
            return fresh;
        }

        /* False when the token was already used this scene. An empty token
           is never deduplicated. */
        public bool ClaimToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return true;
            return _tokens.Add(token);
        }
    }

    /* Just enough JSON for the sidecar's request file: objects, arrays,
       strings, numbers, true, false and null. Numbers come back as double. */
    internal static class MiniJson
    {
        public static object Parse(string json)
        {
            if (json == null) throw new FormatException("null input");
            int i = 0;
            object v = Value(json, ref i);
            Skip(json, ref i);
            if (i != json.Length) throw new FormatException($"trailing data at {i}");
            return v;
        }

        private static void Skip(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object Value(string s, ref int i)
        {
            Skip(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ObjectAt(s, ref i);
            if (c == '[') return ArrayAt(s, ref i);
            if (c == '"') return StringAt(s, ref i);
            if (Word(s, ref i, "true")) return true;
            if (Word(s, ref i, "false")) return false;
            if (Word(s, ref i, "null")) return null;
            return NumberAt(s, ref i);
        }

        private static bool Word(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        private static Dictionary<string, object> ObjectAt(string s, ref int i)
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                Skip(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException($"expected a key at {i}");
                string key = StringAt(s, ref i);
                Skip(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException($"expected ':' at {i}");
                i++;
                o[key] = Value(s, ref i);
                Skip(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw new FormatException($"expected ',' or '}}' at {i}");
            }
        }

        private static List<object> ArrayAt(string s, ref int i)
        {
            var a = new List<object>();
            i++;
            Skip(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value(s, ref i));
                Skip(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw new FormatException($"expected ',' or ']' at {i}");
            }
        }

        private static string StringAt(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("short \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"bad escape at {i - 1}");
                }
            }
            throw new FormatException("unterminated string");
        }

        private static double NumberAt(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == start
                || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                throw new FormatException($"bad value at {start}");
            return d;
        }
    }
}
