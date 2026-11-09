using System;
using System.Globalization;

namespace Kerbcast
{
    /* Why a network vantage cannot show its craft now, or Ok when it can. Ordered as Gate tests them. */
    internal enum VantageState
    {
        Ok,
        Unresolved,
        Disabled,
        Gone,
        IsSubject,
        NoSubject,
        Unknown,
        TooFar,
        BelowHorizon,
        Occluded,
        /* The site resolved but the lens cannot be placed now (a ridden craft with no orbit to read). */
        Unplaced,
        /* Not scored yet: a vantage just added to the list. */
        Pending,
    }

    /* Why the network moved to another vantage. */
    internal enum CutReason
    {
        None,
        /* The first vantage the camera stands at. */
        Start,
        /* Another vantage beat the current one by the margin after the hold. */
        Better,
        /* The current vantage cannot see the craft now. */
        Lost,
        /* The current vantage is about to lose sight of the craft. */
        Forecast,
        /* No vantage could see the craft, and now one can. */
        Regained,
        /* No vantage can see the craft, so the host's fallback stands in. */
        Fallback,
        /* The host's new list no longer has the current vantage. */
        Removed,
        /* No vantage can see the craft and the lens may not stay where it is (gone, unplaced, or inside a craft). */
        Unplaceable,
    }

    /* How a network holds after a cut. */
    internal struct NetworkTuning
    {
        public const double DefaultMinHoldUt = 10.0;
        public const double DefaultMinHoldRealSeconds = 3.0;
        public const double DefaultMarginStops = 0.5;

        public double MinHoldUt;
        public double MinHoldRealSeconds;
        public double MarginStops;

        public static NetworkTuning Default => new NetworkTuning
        {
            MinHoldUt = DefaultMinHoldUt,
            MinHoldRealSeconds = DefaultMinHoldRealSeconds,
            MarginStops = DefaultMarginStops,
        };

        public bool Valid => AtLeastZero(MinHoldUt) && AtLeastZero(MinHoldRealSeconds) && AtLeastZero(MarginStops);

        private static bool AtLeastZero(double v) => !double.IsNaN(v) && !double.IsInfinity(v) && v >= 0;

        /* Game seconds the hold lasts at `warp`: the game floor, or the real floor at that rate when longer. */
        public double HoldUtAt(double warp) => Math.Max(MinHoldUt, MinHoldRealSeconds * Math.Max(1.0, warp));
    }

    /* What one vantage knows and sees of the craft, gathered in game for Gate and Score. */
    internal struct VantageInput
    {
        public bool Resolved;
        /* A ground station the game has turned off. */
        public bool Disabled;
        /* The craft the lens rides no longer exists. */
        public bool Gone;
        /* The lens rides the craft it would watch. */
        public bool IsSubject;
        /* The lens has a position now. */
        public bool Placed;
        /* The network has a craft to follow. */
        public bool HasSubject;
        /* The craft as this vantage's own site knows it. */
        public CraftFixState Fix;
        /* Career tracking rules put the craft beyond this vantage's range. */
        public bool TooFar;
        /* The lens's own ground hides the craft. */
        public bool BelowHorizon;
        /* Another body hides the craft. */
        public bool Occluded;
        /* Its name. */
        public string Occluder;
        public OpticsSite Optics;
        public double RangeM;
    }

    /* A cut the network decided on: where to, and why. Index -1 and None for staying. */
    internal struct NetworkPick
    {
        public int Index;
        public CutReason Reason;

        public static NetworkPick Stay => new NetworkPick { Index = -1, Reason = CutReason.None };
        public bool Cuts => Index >= 0;
    }

    /*
     * The tracking network's rules, Unity-free. A vantage is gated first (it
     * either can see the craft or says why not), then scored in stops by the
     * optics model: higher is a clearer picture. NetworkSwitch decides when
     * the camera moves between vantages.
     */
    internal static class NetworkMath
    {
        /* The most vantages one network takes. */
        public const int MaxVantages = 64;
        /* Real seconds between scoring passes. */
        public const double PassRealSeconds = 0.5;
        /* How close, in real seconds, a forecast loss of sight must be before the network leaves for another vantage. */
        public const double LossLeadRealSeconds = 1.0;
        /* Samples across the loss lead and across a candidate's hold. */
        public const int LossSamples = 8;
        public const int HoldSamples = 4;
        /* A forecast never looks further ahead than this, in game seconds. */
        public const double ForecastMaxUt = 3600.0;
        public const string DefaultName = "Tracking network";

        /* Whether `caller` may replace the vantages of a mod's network or remove it: its owner may, and one made under no name is anyone's. */
        public static bool MayChangeHostNetwork(string owner, string caller) =>
            string.IsNullOrEmpty(owner) || string.Equals(owner, caller ?? "", StringComparison.Ordinal);
        /* Longest label a vantage shows; a longer one is cut. Labels ride every frame. */
        public const int MaxLabelLength = 64;

        public static VantageState Gate(in VantageInput v)
        {
            if (!v.Resolved) return VantageState.Unresolved;
            if (v.Disabled) return VantageState.Disabled;
            if (v.Gone) return VantageState.Gone;
            if (!v.Placed) return VantageState.Unplaced;
            if (v.IsSubject) return VantageState.IsSubject;
            if (!v.HasSubject) return VantageState.NoSubject;
            if (v.Fix != CraftFixState.Positioned) return VantageState.Unknown;
            if (v.TooFar) return VantageState.TooFar;
            if (v.BelowHorizon) return VantageState.BelowHorizon;
            if (v.Occluded) return VantageState.Occluded;
            return VantageState.Ok;
        }

        /* The vantage's picture quality in stops, `daylightStops` being what full daylight at the lens costs. */
        public static double Score(in VantageInput v, double daylightStops) =>
            OpticsModel.Assess(v.Optics, v.RangeM, daylightStops).ClarityStops;

        /* Whether a vantage can stand in at all: it has a place to put the lens, and the lens does not ride the craft it watches. */
        public static bool Placeable(VantageState s) =>
            s != VantageState.Unresolved && s != VantageState.Disabled && s != VantageState.Gone
            && s != VantageState.IsSubject && s != VantageState.Unplaced && s != VantageState.Pending;

        /* The wire's code for a state. */
        public static string Code(VantageState s)
        {
            switch (s)
            {
                case VantageState.Ok: return "ok";
                case VantageState.Unresolved: return "unresolved";
                case VantageState.Disabled: return "disabled";
                case VantageState.Gone: return "gone";
                case VantageState.IsSubject: return "subject";
                case VantageState.NoSubject: return "noSubject";
                case VantageState.Unknown: return "unknown";
                case VantageState.TooFar: return "tooFar";
                case VantageState.BelowHorizon: return "belowHorizon";
                case VantageState.Unplaced: return "unplaced";
                case VantageState.Pending: return "pending";
                default: return "occluded";
            }
        }

        public static string Code(CutReason r)
        {
            switch (r)
            {
                case CutReason.Start: return "start";
                case CutReason.Better: return "better";
                case CutReason.Lost: return "lost";
                case CutReason.Forecast: return "forecast";
                case CutReason.Regained: return "regained";
                case CutReason.Fallback: return "fallback";
                case CutReason.Removed: return "removed";
                case CutReason.Unplaceable: return "unplaceable";
                default: return "none";
            }
        }

        /* A vantage's label: the host's when it gave one, else the site's own name, cut to MaxLabelLength. */
        public static string Label(string custom, string siteDisplayName, string siteKey)
        {
            string label = !string.IsNullOrEmpty(custom) && custom.Trim().Length > 0 ? custom.Trim()
                : string.IsNullOrEmpty(siteDisplayName) ? siteKey ?? "" : siteDisplayName;
            return label.Length > MaxLabelLength ? label.Substring(0, MaxLabelLength) : label;
        }

        /*
         * Whether stock builds a ground station's network node, as
         * CommNetHome.CreateNode decides: every station with the extra ground
         * stations on, otherwise only the home station and permanent ones
         * where other launch sites are allowed.
         */
        public static bool StationEnabled(bool extraStationsOn, bool isHome, bool isPermanent, bool otherLaunchSites) =>
            extraStationsOn || isHome || (isPermanent && otherLaunchSites);

        /*
         * Why StationEnabled has a station off, for the site catalog; null
         * while it is on. Under stock's rule the extra ground stations option
         * is always what would turn it on again.
         */
        public static string StationOffReason(bool extraStationsOn, bool isHome, bool isPermanent, bool otherLaunchSites) =>
            StationEnabled(extraStationsOn, isHome, isPermanent, otherLaunchSites) ? null : "extraStationsOff";

        /* The best vantage that can see the craft, the current one winning ties; -1 when none can. */
        public static int Best(VantageState[] states, double[] scores, int count, int current, int except = -1)
        {
            int best = -1;
            for (int i = 0; i < count; i++)
            {
                if (i == except || states[i] != VantageState.Ok) continue;
                if (best < 0 || scores[i] > scores[best] || (scores[i] == scores[best] && i == current)) best = i;
            }
            return best;
        }

        /* One KSP.log line per cut: from, to, why, and every vantage's state and score. */
        public static string CutLine(uint flightId, double ut, NetworkPick pick, int from, string[] labels, string[] keys,
            VantageState[] states, double[] scores, int count)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(160 + count * 40);
            sb.Append("[Kerbcast][CMDCAM] cam=").Append(flightId.ToString(inv))
                .Append(" UT=").Append(ut.ToString("F2", inv))
                .Append(" event=network-cut reason=").Append(Code(pick.Reason))
                .Append(" from=").Append(from >= 0 && from < count ? $"'{labels[from]}' ({keys[from]})" : "none")
                .Append(" to='").Append(labels[pick.Index]).Append("' (").Append(keys[pick.Index]).Append(')');
            if (from >= 0 && from < count) sb.Append(" fromScore=").Append(Stops(states[from], scores[from], inv));
            sb.Append(" toScore=").Append(Stops(states[pick.Index], scores[pick.Index], inv)).Append(" vantages=[");
            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('\'').Append(labels[i]).Append("' ").Append(Code(states[i]));
                if (states[i] == VantageState.Ok) sb.Append(' ').Append(scores[i].ToString("F2", inv));
            }
            return sb.Append(']').ToString();
        }

        private static string Stops(VantageState s, double score, IFormatProvider inv) =>
            s == VantageState.Ok ? score.ToString("F2", inv) : Code(s);
    }

    /*
     * When the network cuts. Without sight on the current vantage it cuts at
     * once to the best one that can see, or stays (on the host's fallback when
     * it named one) with no view. With sight it leaves early only for a
     * forecast loss, to a vantage clear through its own hold; otherwise it
     * holds for MinHoldUt game seconds and MinHoldRealSeconds real seconds
     * after a cut, then moves only for a vantage MarginStops clearer.
     */
    internal sealed class NetworkSwitch
    {
        private double _cutUt = double.NaN;
        private double _cutReal = double.NaN;
        private bool _removed;

        public int Current { get; private set; } = -1;
        /* No vantage could see the craft at the last decision. */
        public bool NoView { get; private set; }
        /* No vantage could even stand in at the last decision, so the feed has nowhere to show from. */
        public bool NothingPlaceable { get; private set; }

        /* `clearAhead[i]` says vantage i stays clear through a hold from now; null when not forecast, which counts as clear. */
        public NetworkPick Decide(VantageState[] states, double[] scores, int count, int fallback, bool lossSoon,
            bool[] clearAhead, double ut, double real, NetworkTuning tuning)
        {
            NothingPlaceable = FirstPlaceable(states, count) < 0;
            if (count <= 0) return NetworkPick.Stay;
            int best = NetworkMath.Best(states, scores, count, Current);
            if (Current < 0 || Current >= count)
            {
                bool started = Current < 0 && !_removed;
                int to = best >= 0 ? best
                    : fallback >= 0 && fallback < count && NetworkMath.Placeable(states[fallback]) ? fallback
                    : FirstPlaceable(states, count);
                NoView = best < 0;
                return to < 0 ? NetworkPick.Stay : new NetworkPick { Index = to, Reason = started ? CutReason.Start : CutReason.Removed };
            }
            if (states[Current] != VantageState.Ok)
            {
                if (best >= 0)
                {
                    var reason = NoView ? CutReason.Regained : CutReason.Lost;
                    NoView = false;
                    return new NetworkPick { Index = best, Reason = reason };
                }
                NoView = true;
                if (fallback >= 0 && fallback < count && fallback != Current && NetworkMath.Placeable(states[fallback]))
                    return new NetworkPick { Index = fallback, Reason = CutReason.Fallback };
                if (NetworkMath.Placeable(states[Current])) return NetworkPick.Stay;
                int elsewhere = FirstPlaceable(states, count);
                return elsewhere >= 0 ? new NetworkPick { Index = elsewhere, Reason = CutReason.Unplaceable } : NetworkPick.Stay;
            }
            NoView = false;
            if (lossSoon)
            {
                int ahead = -1;
                for (int i = 0; i < count; i++)
                {
                    if (i == Current || states[i] != VantageState.Ok || (clearAhead != null && !clearAhead[i])) continue;
                    if (ahead < 0 || scores[i] > scores[ahead]) ahead = i;
                }
                if (ahead >= 0) return new NetworkPick { Index = ahead, Reason = CutReason.Forecast };
            }
            /* Game time that ran back (a revert, a quickload) ends the hold rather than stretching it. */
            bool held = (ut < _cutUt || !(ut - _cutUt < tuning.MinHoldUt)) && !(real - _cutReal < tuning.MinHoldRealSeconds);
            if (held && best >= 0 && best != Current && scores[best] >= scores[Current] + tuning.MarginStops)
                return new NetworkPick { Index = best, Reason = CutReason.Better };
            return NetworkPick.Stay;
        }

        /* The camera now stands at `index`, from `ut` and real time `real`. */
        public void CutTo(int index, double ut, double real)
        {
            Current = index;
            _removed = false;
            _cutUt = ut;
            _cutReal = real;
        }

        /* The list changed: the current vantage is now at `index`, -1 when it was removed. The hold carries on. */
        public void Renumber(int index)
        {
            /* Only a vantage the camera stood at can be removed; before the first cut the next one is still the start. */
            _removed = index < 0 && (Current >= 0 || _removed);
            Current = index;
        }

        private static int FirstPlaceable(VantageState[] states, int count)
        {
            for (int i = 0; i < count; i++)
                if (NetworkMath.Placeable(states[i])) return i;
            return -1;
        }
    }
}
