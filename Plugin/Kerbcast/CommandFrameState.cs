using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kerbcast
{
    /*
     * What a command camera's frame shows, taken when the frame is rendered
     * and carried in that frame's metadata, so a viewer gets it with the
     * picture and never ahead of it. Numbers that do not apply are NaN and
     * strings null; both are left out of the JSON.
     */
    internal struct CommandFrameState
    {
        public bool HasFix;
        public double RangeM;
        public double BracketHalfPx;
        public int FrameHeightPx;
        public bool FramingFits;
        public double TrueOffsetPx;
        public bool Occluded;
        public string Occluder;
        public bool Sunlit;
        public string Blank;
        public string AuthorityOwner;
        public uint SubjectPersistentId;
        public string AimKind;
        /* Why the subject has no position: "unknown", "notPositioned", "refused" or "stale"; null when it has one or there is none. */
        public string Refusal;
        /* The world model's own code and words for a "refused" subject. */
        public string RefusalCode;
        public string RefusalText;
        /* The instant the subject's known position is taken at: render UT, or its past instant under a signal speed. */
        public double SubjectUt;
        /* Where knowledge came from: "truth", "waiting", "model" or "stale". */
        public string WorldModel;
        /* The world model snapshot the frame read, with the UT its knowledge describes; -1 and NaN when none was read. */
        public long WorldModelSeq;
        public double KnowledgeUt;
        /* The world model scope the feed's site is bound to; null when unbound or under truth. */
        public string Scope;

        public static CommandFrameState Empty => new CommandFrameState
        {
            RangeM = double.NaN,
            BracketHalfPx = double.NaN,
            TrueOffsetPx = double.NaN,
            SubjectUt = double.NaN,
            KnowledgeUt = double.NaN,
            WorldModelSeq = -1,
            FramingFits = true,
        };

        /* Longest refusal text a frame carries; a provider's words past this are cut. */
        public const int MaxRefusalText = 200;

        /* Every key AppendJson can write, for the sidecar's and the SDK's tests to agree on. */
        public static readonly string[] Keys =
        {
            "hasFix", "rangeM", "bracketHalfPx", "frameHeightPx", "framingFits", "trueOffsetPx", "occluded",
            "occluder", "sunlit", "blank", "authority", "subjectPersistentId", "aimKind", "refusal", "refusalCode",
            "refusalText", "subjectUt", "worldModel", "worldModelSeq", "knowledgeUt", "scope",
        };

        public void AppendJson(StringBuilder sb)
        {
            sb.Append("{\"hasFix\":").Append(HasFix ? "true" : "false");
            Number(sb, "rangeM", RangeM);
            Number(sb, "bracketHalfPx", BracketHalfPx);
            sb.Append(",\"frameHeightPx\":").Append(FrameHeightPx.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"framingFits\":").Append(FramingFits ? "true" : "false");
            Number(sb, "trueOffsetPx", TrueOffsetPx);
            sb.Append(",\"occluded\":").Append(Occluded ? "true" : "false");
            sb.Append(",\"occluder\":");
            AnchorJson.AppendString(sb, Occluder ?? "");
            sb.Append(",\"sunlit\":").Append(Sunlit ? "true" : "false");
            Text(sb, "blank", Blank);
            if (AuthorityOwner != null)
            {
                sb.Append(",\"authority\":{\"owner\":");
                AnchorJson.AppendString(sb, AuthorityOwner);
                sb.Append('}');
            }
            sb.Append(",\"subjectPersistentId\":").Append(SubjectPersistentId.ToString(CultureInfo.InvariantCulture));
            Text(sb, "aimKind", AimKind);
            Text(sb, "refusal", Refusal);
            Text(sb, "refusalCode", RefusalCode);
            Text(sb, "refusalText", RefusalText != null && RefusalText.Length > MaxRefusalText ? RefusalText.Substring(0, MaxRefusalText) : RefusalText);
            Ut(sb, "subjectUt", SubjectUt);
            Text(sb, "worldModel", WorldModel);
            if (WorldModelSeq >= 0) sb.Append(",\"worldModelSeq\":").Append(WorldModelSeq.ToString(CultureInfo.InvariantCulture));
            Ut(sb, "knowledgeUt", KnowledgeUt);
            Text(sb, "scope", Scope);
            sb.Append('}');
        }

        private static void Number(StringBuilder sb, string key, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            sb.Append(",\"").Append(key).Append("\":").Append(AnchorJson.Num(v));
        }

        /* UTs keep every digit: a client compares them with frame UTs. */
        private static void Ut(StringBuilder sb, string key, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            sb.Append(",\"").Append(key).Append("\":").Append(v.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void Text(StringBuilder sb, string key, string v)
        {
            if (v == null) return;
            sb.Append(",\"").Append(key).Append("\":");
            AnchorJson.AppendString(sb, v);
        }

        /* The subject's refusal from its fix state and the source's code: null for a positioned subject or none at all. */
        public static string RefusalFor(uint subjectPersistentId, CraftFixState state, string refusalCode)
        {
            if (subjectPersistentId == 0) return null;
            switch (state)
            {
                case CraftFixState.Positioned: return null;
                case CraftFixState.Listed: return CommandCameraMath.EligibleNotPositioned;
                case CraftFixState.Refused:
                    return refusalCode == CommandCameraMath.RefusalStale ? CommandCameraMath.EligibleStale : CommandCameraMath.EligibleRefused;
                default: return "unknown";
            }
        }
    }

    /* A frame's ring metadata: the boundary distance, the command camera's state and the anchor results. */
    internal static class FrameMetaJson
    {
        /*
         * One JSON object in UTF-8 into `buffer` (grown as needed), each part
         * present only when there is one: `boundaryM` when finite, `command`
         * when given, `anchors` when there are results. Anchor results that
         * would take it past `maxBytes` are left out whole, so it always
         * parses. Returns the byte count, 0 for nothing to send or when even
         * the parts without anchors do not fit.
         */
        public static int Encode(double boundaryM, CommandFrameState? command, List<AnchorResult> anchors,
            StringBuilder sb, ref byte[] buffer, int maxBytes)
        {
            int n = EncodeOnce(boundaryM, command, anchors, sb, ref buffer, maxBytes);
            /* Multi-byte text can take the whole past the limit; the command state and boundary go without anchors then. */
            if (n == 0 && anchors != null && anchors.Count > 0)
                n = EncodeOnce(boundaryM, command, null, sb, ref buffer, maxBytes);
            return n;
        }

        private static int EncodeOnce(double boundaryM, CommandFrameState? command, List<AnchorResult> anchors,
            StringBuilder sb, ref byte[] buffer, int maxBytes)
        {
            bool boundary = !double.IsNaN(boundaryM) && !double.IsInfinity(boundaryM);
            int anchorCount = anchors != null ? anchors.Count : 0;
            if (!boundary && !command.HasValue && anchorCount == 0) return 0;
            sb.Length = 0;
            sb.Append('{');
            bool first = true;
            if (boundary)
            {
                sb.Append("\"boundaryM\":").Append(AnchorJson.Num(boundaryM));
                first = false;
            }
            if (command.HasValue)
            {
                sb.Append(first ? "" : ",").Append("\"command\":");
                command.Value.AppendJson(sb);
                first = false;
            }
            if (anchorCount > 0)
            {
                int open = sb.Length;
                sb.Append(first ? "" : ",").Append("\"anchors\":[");
                int kept = 0;
                for (int i = 0; i < anchorCount; i++)
                {
                    int mark = sb.Length;
                    if (kept > 0) sb.Append(',');
                    anchors[i].AppendJson(sb);
                    /* Ids and owners are near enough ASCII that chars bound bytes from below; the exact count is checked after encoding. */
                    if (sb.Length + 2 > maxBytes) { sb.Length = mark; break; }
                    kept++;
                }
                if (kept == 0) sb.Length = open;
                else
                {
                    sb.Append(']');
                    first = false;
                }
            }
            if (first) return 0;
            sb.Append('}');
            string text = sb.ToString();
            int bytes = Encoding.UTF8.GetByteCount(text);
            if (bytes > maxBytes) return 0;
            if (buffer == null || buffer.Length < bytes) buffer = new byte[Math.Max(bytes, 1024)];
            return Encoding.UTF8.GetBytes(text, 0, text.Length, buffer, 0);
        }
    }

    /*
     * A command camera's fields in the status file, the bracket contract of
     * clients older than 1.12. Its `commandTrack` reaches every viewer once a
     * second whatever frame it is showing. Every field is written, lock or
     * not: under a lock each is the world model's knowledge at the site (the
     * fix, the range to it, occlusion and sunlight at it) or the picture's
     * output (`trueOffsetPx` only while the picture shows the craft), never
     * truth. A 1.12 client reads the state from each frame instead and holds
     * a locked snapshot's knowledge until a frame carries it, so a delayed
     * view never shows knowledge before its picture. The key sets are frozen:
     * a new field rides the frames.
     */
    internal static class CommandTrackJson
    {
        /* The 1.11 track keys, sent with or without a lock. */
        public static readonly string[] TrackKeys =
        {
            "ut", "hasFix", "rangeM", "occluded", "occluder", "sunlit", "bracketHalfPx", "frameHeightPx",
            "framingFits", "trueOffsetPx", "blank", "authority", "events",
        };
        /* Keys inside each event; every event has its own `ut`. */
        public static readonly string[] EventKeys = { "ut", "occluded", "occluder", "sunlit", "blank", "predicted" };
        /* The 1.11 camera keys, sent with or without a lock. */
        public static readonly string[] CameraKeys = { "occluded", "blocker", "rangeM", "elevationDeg", "sunlit", "subjectName", "subjectPersistentId" };

        /* The camera's top-level readings, as `      "key": value,` lines. */
        public static void AppendCameraFields(StringBuilder sb, CommandFrameState s, double elevationDeg, string subjectName)
        {
            var ci = CultureInfo.InvariantCulture;
            sb.Append("      \"occluded\": ").Append(s.Occluded ? "true" : "false").Append(",\n");
            sb.Append("      \"blocker\": ");
            AnchorJson.AppendString(sb, s.Occluder ?? "");
            sb.Append(",\n");
            sb.Append("      \"rangeM\": ").Append(s.HasFix && !double.IsNaN(s.RangeM) ? s.RangeM.ToString("F1", ci) : "null").Append(",\n");
            sb.Append("      \"elevationDeg\": ").Append(s.HasFix && !double.IsNaN(elevationDeg) ? elevationDeg.ToString("F3", ci) : "null").Append(",\n");
            sb.Append("      \"sunlit\": ").Append(s.Sunlit ? "true" : "false").Append(",\n");
            sb.Append("      \"subjectName\": ");
            AnchorJson.AppendString(sb, subjectName ?? "");
            sb.Append(",\n");
            sb.Append("      \"subjectPersistentId\": ").Append(s.SubjectPersistentId.ToString(ci)).Append(",\n");
        }

        public static void Append(StringBuilder sb, double ut, CommandFrameState s, IReadOnlyList<CommandTrackEvent> events)
        {
            var ci = CultureInfo.InvariantCulture;
            sb.Append("{\"ut\":").Append(ut.ToString("R", ci));
            sb.Append(",\"hasFix\":").Append(s.HasFix ? "true" : "false");
            sb.Append(",\"rangeM\":").Append(s.HasFix && !double.IsNaN(s.RangeM) ? s.RangeM.ToString("F1", ci) : "null");
            sb.Append(",\"occluded\":").Append(s.Occluded ? "true" : "false");
            sb.Append(",\"occluder\":");
            AnchorJson.AppendString(sb, s.Occluder ?? "");
            sb.Append(",\"sunlit\":").Append(s.Sunlit ? "true" : "false");
            sb.Append(",\"bracketHalfPx\":").Append(s.HasFix && !double.IsNaN(s.BracketHalfPx) ? s.BracketHalfPx.ToString("0.###", ci) : "null");
            sb.Append(",\"frameHeightPx\":").Append(s.FrameHeightPx.ToString(ci));
            sb.Append(",\"framingFits\":").Append(s.FramingFits ? "true" : "false");
            sb.Append(",\"trueOffsetPx\":").Append(!double.IsNaN(s.TrueOffsetPx) ? s.TrueOffsetPx.ToString("0.###", ci) : "null");
            sb.Append(",\"blank\":");
            if (s.Blank == null) sb.Append("null");
            else AnchorJson.AppendString(sb, s.Blank);
            sb.Append(",\"authority\":");
            if (s.AuthorityOwner == null) sb.Append("null");
            else
            {
                sb.Append("{\"owner\":");
                AnchorJson.AppendString(sb, s.AuthorityOwner);
                sb.Append('}');
            }
            sb.Append(",\"events\":[");
            for (int i = 0; i < (events != null ? events.Count : 0); i++)
            {
                var e = events[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"ut\":").Append(e.Ut.ToString("R", ci));
                sb.Append(",\"occluded\":").Append(e.Occluded ? "true" : "false");
                sb.Append(",\"occluder\":");
                AnchorJson.AppendString(sb, e.Occluder ?? "");
                sb.Append(",\"sunlit\":").Append(e.Sunlit ? "true" : "false");
                sb.Append(",\"blank\":");
                if (e.Blank == null) sb.Append("null");
                else AnchorJson.AppendString(sb, e.Blank);
                if (e.Predicted) sb.Append(",\"predicted\":true");
                sb.Append('}');
            }
            sb.Append("]}");
        }
    }
}
