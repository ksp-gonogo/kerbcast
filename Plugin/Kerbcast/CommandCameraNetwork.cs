using System;
using System.Collections.Generic;
using UnityEngine;

namespace Kerbcast
{
    /* One place a camera network may show its craft from, as the host listed it. */
    internal sealed class NetworkVantageSpec
    {
        public CommandVantage Vantage;
        /* Replaces the site's own name wherever the vantage is named; null keeps it. */
        public string Label;
        /* Where the camera stands when no vantage can see the craft. */
        public bool Fallback;

        public string Key => CommandCameraSite.KeyFor(Vantage) ?? Vantage.ToString();
    }

    /* A vantage's state as the last scoring pass left it, for the status and the API. */
    internal struct NetworkVantageReading
    {
        public string SiteKey;
        public string Label;
        public Api.SiteKind Kind;
        /* A craft vantage's persistentId, 0 for any other kind. */
        public uint VantagePersistentId;
        public bool Active;
        public bool Fallback;
        public VantageState State;
        /* Stops; NaN unless the vantage can see the craft. */
        public double Score;
        /* The body hiding the craft while State is Occluded, else null. */
        public string Occluder;
    }

    /* What a network needs from the camera it drives, so the provider builds it beside the rig. */
    internal sealed class NetworkWiring
    {
        public uint FlightId;
        public SubjectSelection Selection;
        public IFixSource Truth;
        public CommandAuthority Authority;
        public Func<CommandVantage, string> LaunchedFrom;
        public Func<CommandCameraSite, Func<double>> FovMin;
        public Func<CommandCameraSite, Func<OpticsLens>> Lens;
        public Func<CommandCameraSite, OpticsKnobs> ScoreKnobs;
        public Func<double> DaylightStops;
        public int TerrainSamples;
    }

    /*
     * A camera network: one command camera that follows its craft through
     * a host's list of vantages and shows whichever has the clearest view.
     * The camera keeps one rig, flightId and stream; a cut re-sites the rig
     * whole (CommandCameraRig.Resite) before the frame solves.
     *
     * Every vantage keeps what a camera standing there alone would have: its
     * zoom limit and lens, its own skyline and forecast, and, under a world
     * model, its own knowledge of the craft. A vantage is gated and scored
     * only on what its own site knows, so a cut can never reveal a craft the
     * camera is not meant to know about.
     *
     * Scored twice a second of real time, without allocating. A vantage whose
     * site is not placed yet is retried every second for 30 s, then reported
     * unresolved; the rest of the list carries on.
     */
    internal sealed class CommandCameraNetwork : IDisposable
    {
        private const string Tag = "[Kerbcast][CMDCAM]";
        private const float RetrySeconds = 1f;
        private const float GiveUpSeconds = 30f;

        private sealed class Entry
        {
            public NetworkVantageSpec Spec;
            public CommandCameraSite Site;
            public CommandCameraOcclusion Occlusion;
            public CommandCameraForecast Forecast;
            public IFixSource Known;
            public Func<double> FovMin;
            public Func<OpticsLens> Lens;
            public string Label;
            public string Key;
            /* The vantage exactly as asked, heights and bearings included, to tell a re-sent vantage from a moved one. */
            public string Asked;
            public int Attempts;
            public float FirstAttemptAt;
            public float NextAttemptAt;
            public bool GaveUp;
            public bool LoggedPending;
        }

        private readonly NetworkWiring _w;
        private readonly List<Entry> _entries = new List<Entry>();
        private NetworkSwitch _switch = new NetworkSwitch();
        private NetworkTuning _tuning;
        private readonly VantageState[] _states = new VantageState[NetworkMath.MaxVantages];
        private readonly double[] _scores = new double[NetworkMath.MaxVantages];
        private readonly bool[] _clearAhead = new bool[NetworkMath.MaxVantages];
        private readonly string[] _labels = new string[NetworkMath.MaxVantages];
        private readonly string[] _keys = new string[NetworkMath.MaxVantages];
        private readonly string[] _occluders = new string[NetworkMath.MaxVantages];
        private float _passAt = float.MinValue;
        /* What a scoring pass costs, smoothed, for the occasional log line a rig check reads. */
        private const float CostLogSeconds = 30f;
        private const double CostAlpha = 0.2;
        private double _passMs;
        private double _passMsMax;
        private float _costLoggedAt = float.MinValue;
        private bool _force = true;
        private bool _loggedHeld;
        private int _fallback = -1;
        private bool _subscribed;

        public string Name { get; }
        public bool NoView => _switch.NoView;
        public int Count => _entries.Count;

        public CommandCameraNetwork(NetworkWiring wiring, string name, IList<NetworkVantageSpec> vantages, NetworkTuning tuning)
        {
            _w = wiring;
            Name = string.IsNullOrEmpty(name) || name.Trim().Length == 0 ? NetworkMath.DefaultName : name.Trim();
            _tuning = tuning;
            SetVantages(vantages, tuning);
        }

        /* A rig takes the network, standing at the first resolved vantage: the next pass picks afresh, as for a new camera. */
        public void Attach()
        {
            _switch = new NetworkSwitch();
            _force = true;
            if (_subscribed) return;
            GameEvents.onVesselPersistentIdChanged.Add(OnMerged);
            _subscribed = true;
        }

        /* The site to build the camera at: the first vantage that resolves, tried on the retry schedule. Null while none has. */
        public CommandCameraSite FirstSite(float now)
        {
            Resolve(now);
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Site != null) return _entries[i].Site;
            return null;
        }

        /* Every vantage has failed to resolve within its 30 s. */
        public bool AllGaveUp
        {
            get
            {
                for (int i = 0; i < _entries.Count; i++)
                    if (!_entries[i].GaveUp) return false;
                return true;
            }
        }

        /* Whether a vantage still waits for its site, so the provider keeps polling. */
        public bool RetryPending
        {
            get
            {
                for (int i = 0; i < _entries.Count; i++)
                    if (_entries[i].Site == null && !_entries[i].GaveUp) return true;
                return false;
            }
        }

        /*
         * Replaces the list. A vantage already in it (by site key) keeps its
         * place, skyline and forecast; the camera stays where it is when its
         * vantage survives, and cuts on the next pass when it does not. The
         * hold carries on across the change.
         */
        public void SetVantages(IList<NetworkVantageSpec> vantages, NetworkTuning tuning)
        {
            _tuning = tuning;
            Entry current = _switch.Current >= 0 && _switch.Current < _entries.Count ? _entries[_switch.Current] : null;
            var next = new List<Entry>(vantages.Count);
            _fallback = -1;
            for (int i = 0; i < vantages.Count && next.Count < NetworkMath.MaxVantages; i++)
            {
                var spec = vantages[i];
                string key = spec.Key;
                string asked = spec.Vantage.ToString();
                Entry e = null;
                for (int j = 0; j < _entries.Count && e == null; j++)
                    if (_entries[j].Asked == asked && !next.Contains(_entries[j])) e = _entries[j];
                if (e == null) e = NewEntry(spec, key);
                else if (e.GaveUp)
                {
                    /* Sent again, so the place may exist now. */
                    e.GaveUp = false;
                    e.Attempts = 0;
                    e.LoggedPending = false;
                    e.NextAttemptAt = float.MinValue;
                }
                e.Spec = spec;
                e.Label = e.Site != null ? NetworkMath.Label(spec.Label, e.Site.DisplayName, e.Site.NodeName) : NetworkMath.Label(spec.Label, null, key);
                if (spec.Fallback && _fallback < 0) _fallback = next.Count;
                next.Add(e);
            }
            _entries.Clear();
            _entries.AddRange(next);
            for (int i = 0; i < _entries.Count; i++)
            {
                _states[i] = VantageState.Pending;
                _scores[i] = double.NaN;
                _occluders[i] = null;
            }
            _switch.Renumber(current != null ? _entries.IndexOf(current) : -1);
            if (current != null && _switch.Current >= 0) _relabel = true;
            _force = true;
            Debug.Log($"{Tag} cam={_w.FlightId} network '{Name}' vantages: {Describe()} hold={_tuning.MinHoldUt:F1}s/{_tuning.MinHoldRealSeconds:F1}s real margin={_tuning.MarginStops:F2} stops");
        }

        private bool _relabel;

        private Entry NewEntry(NetworkVantageSpec spec, string key)
        {
            var e = new Entry
            {
                Spec = spec,
                Key = key,
                Asked = spec.Vantage.ToString(),
                Occlusion = new CommandCameraOcclusion(_w.TerrainSamples, _w.FlightId),
                NextAttemptAt = float.MinValue,
            };
            e.Forecast = new CommandCameraForecast(e.Occlusion);
            var store = _w.Authority.WorldModel;
            /* A scoring source never records its craft into the selection the picture reads: only the feed's own source does. */
            e.Known = new FeedFixSource(_w.Truth,
                new ProviderFixSource(store, _w.Authority.Sites, () => e.Site, _w.Selection, _w.FlightId, recordsSubject: false), store);
            return e;
        }

        private string Describe()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _entries.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('\'').Append(_entries[i].Label).Append("' ").Append(_entries[i].Spec.Vantage);
                if (i == _fallback) sb.Append(" fallback");
            }
            return sb.ToString();
        }

        /* Resolves every vantage due for another try. */
        private void Resolve(float now)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (e.Site != null || e.GaveUp || now < e.NextAttemptAt) continue;
                if (e.Attempts == 0) e.FirstAttemptAt = now;
                e.Attempts++;
                string pending = null, fatal = null;
                try
                {
                    e.Site = CommandCameraProvider.ResolveVantage(e.Spec.Vantage, _w.LaunchedFrom(e.Spec.Vantage), out pending, out fatal);
                }
                catch (Exception ex)
                {
                    fatal = ex.Message;
                }
                if (e.Site != null)
                {
                    e.Key = e.Site.NodeName;
                    e.Label = NetworkMath.Label(e.Spec.Label, e.Site.DisplayName, e.Site.NodeName);
                    e.FovMin = _w.FovMin(e.Site);
                    e.Lens = _w.Lens(e.Site);
                    Debug.Log($"{Tag} cam={_w.FlightId} network vantage '{e.Label}' resolved: {e.Site}"
                        + (e.Attempts > 1 ? $" after {e.Attempts} attempts" : ""));
                    continue;
                }
                if (fatal != null || now - e.FirstAttemptAt >= GiveUpSeconds)
                {
                    e.GaveUp = true;
                    Debug.LogWarning($"{Tag} cam={_w.FlightId} network vantage '{e.Label}' at {e.Spec.Vantage} unresolved: {fatal ?? pending}");
                    continue;
                }
                if (!e.LoggedPending)
                {
                    e.LoggedPending = true;
                    Debug.Log($"{Tag} cam={_w.FlightId} network vantage '{e.Label}' at {e.Spec.Vantage}: {pending}; retrying every {RetrySeconds:F0}s for up to {GiveUpSeconds:F0}s");
                }
                e.NextAttemptAt = now + RetrySeconds;
            }
        }

        /*
         * One scoring pass when due (twice a second of real time, or at once
         * after the list changed or `force`), cutting `rig` to the vantage
         * the pass picks. Runs before the rig solves, so a cut is whole on
         * the frame it lands on.
         */
        public void Step(CommandCameraRig rig, bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && !_force && now - _passAt < NetworkMath.PassRealSeconds) return;
            /*
             * A flight scene builds its cameras before its first physics tick,
             * while the game is still putting craft and its rotating frame in
             * place: a craft on rails and a landed one are then read in frames
             * that do not agree, and every vantage can read the craft as below
             * its horizon. The pass waits, still due, and the camera stands at
             * its first vantage meanwhile.
             */
            if (!KerbcastControl.SceneCraftLoaded)
            {
                _force = true;
                if (!_loggedHeld)
                {
                    _loggedHeld = true;
                    Debug.Log($"{Tag} cam={_w.FlightId} UT={Planetarium.GetUniversalTime():F2} event=network-held reason=scene-placing-craft");
                }
                return;
            }
            _passAt = now;
            _force = false;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Resolve(now);
            double ut = Planetarium.GetUniversalTime();
            int count = _entries.Count;
            Score(ut, count);

            /* The camera may stand somewhere the list no longer has; it counts as current only while it does. */
            int current = _switch.Current;
            bool lossSoon = false;
            if (current >= 0 && current < count && _states[current] == VantageState.Ok)
            {
                double warp = Math.Max(1.0, TimeWarp.CurrentRate);
                lossSoon = LosesSight(_entries[current], ut, Math.Min(NetworkMath.LossLeadRealSeconds * warp, NetworkMath.ForecastMaxUt),
                    NetworkMath.LossSamples);
                if (lossSoon)
                {
                    double hold = Math.Min(_tuning.HoldUtAt(warp), NetworkMath.ForecastMaxUt);
                    for (int i = 0; i < count; i++)
                        _clearAhead[i] = i != current && _states[i] == VantageState.Ok && !LosesSight(_entries[i], ut, hold, NetworkMath.HoldSamples);
                }
            }
            var pick = _switch.Decide(_states, _scores, count, _fallback, lossSoon, lossSoon ? _clearAhead : null, ut, now, _tuning);
            if (pick.Cuts)
            {
                Debug.Log(NetworkMath.CutLine(_w.FlightId, ut, pick, current, _labels, _keys, _states, _scores, count));
                _switch.CutTo(pick.Index, ut, now);
                var e = _entries[pick.Index];
                /* At the start too, so the rig uses the skyline this pass already built for its site. */
                if (e.Site != rig.Site || pick.Reason == CutReason.Start) rig.Resite(e.Site, e.Occlusion, e.Forecast, e.FovMin, e.Lens, e.Label);
                else rig.Relabel(e.Label);
                _relabel = false;
            }
            else if (_relabel && _switch.Current >= 0)
            {
                _relabel = false;
                rig.Relabel(_entries[_switch.Current].Label);
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _passMs = _passMs > 0 ? _passMs + CostAlpha * (ms - _passMs) : ms;
            if (ms > _passMsMax) _passMsMax = ms;
            if (now - _costLoggedAt >= CostLogSeconds)
            {
                _costLoggedAt = now;
                Debug.Log($"{Tag} cam={_w.FlightId} UT={ut:F2} event=network-status passMs={_passMs:F3} passMsMax={_passMsMax:F3} vantages={count} "
                    + $"active='{(_switch.Current >= 0 && _switch.Current < count ? _labels[_switch.Current] : "none")}' view={(NoView ? "none" : "ok")}");
                _passMsMax = 0;
            }
        }

        /* No vantage can stand in, so the feed blanks rather than show the craft from a place it may not be seen from. */
        public bool NothingPlaceable => _switch.NothingPlaceable;

        /* Gates and scores every vantage from what its own site knows, into _states, _scores, _labels and _keys. */
        private void Score(double ut, int count)
        {
            var rules = GameStationRules.Now();
            bool gated = CommandCameraTargets.Gated();
            double maxRange = gated ? GameVariables.Instance.GetDSNRange(CommandCameraTargets.NormLevel()) : double.PositiveInfinity;
            var over = _w.Authority.OpticsFor(_w.FlightId);
            double daylight = _w.DaylightStops();
            var sun = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            for (int i = 0; i < count; i++)
            {
                var e = _entries[i];
                _labels[i] = e.Label;
                _keys[i] = e.Key;
                var input = Gather(e, ut, rules, maxRange, over, sun);
                _states[i] = NetworkMath.Gate(input);
                _scores[i] = _states[i] == VantageState.Ok ? NetworkMath.Score(input, daylight) : double.NaN;
                _occluders[i] = _states[i] == VantageState.Occluded ? input.Occluder : null;
            }
        }

        private VantageInput Gather(Entry e, double ut, GameStationRules rules, double maxRange, OpticsKnobs over, CelestialBody sun)
        {
            var v = new VantageInput();
            var site = e.Site;
            v.Resolved = site != null;
            if (site == null) return v;
            v.Disabled = !site.StationEnabled(rules);
            v.Gone = site.VantageGone;
            if (v.Disabled || v.Gone) return v;
            /* Placed before the craft is read, so a lens with no position never scores as one standing on the craft. */
            v.Placed = Station(site, ut, out var station, out var body);
            if (!v.Placed) return v;
            var fix = e.Known.Subject(ut);
            uint subject = e.Known.SubjectPersistentId;
            v.IsSubject = site.HasVantage && subject != 0 && site.VantagePersistentId == subject;
            v.HasSubject = subject != 0;
            if (v.IsSubject || !v.HasSubject) return v;
            v.Fix = fix.State;
            if (!fix.Positioned) return v;
            var target = fix.World;
            var toward = target - station;
            v.RangeM = toward.Magnitude;
            v.TooFar = v.RangeM > maxRange;
            var bodyPos = ToD(body.position);
            var up = (station - bodyPos).Normalized;
            double radius = double.IsNaN(fix.RadiusM) ? CommandCameraMath.NominalCraftRadiusM : fix.RadiusM;
            e.Occlusion.UseSite(site);
            string occluder = e.Occlusion.FindOccluder(station, target, out _, radius);
            if (occluder != null)
            {
                if (occluder == body.bodyName) v.BelowHorizon = true;
                else
                {
                    v.Occluded = true;
                    v.Occluder = occluder;
                }
            }
            double air = CommandCameraOptics.AirAt(body, (station - bodyPos).Magnitude);
            double sunElev = double.NaN;
            if (sun != null && sun != body)
            {
                var toSun = (ToD(sun.position) - station).Normalized;
                sunElev = Math.Asin(Math.Max(-1.0, Math.Min(1.0, DVec3.Dot(toSun, up)))) * 180.0 / Math.PI;
            }
            var knobs = over.Over(_w.ScoreKnobs(site));
            v.Optics = new OpticsSite
            {
                ResolutionArcsec = knobs.ResolutionArcsec,
                ZenithSeeingArcsec = knobs.SeeingArcsec,
                SeeingAirmassPower = knobs.SeeingAirmassPower,
                AirFactor = air,
                ElevationDeg = CommandCameraMath.ElevationDeg(toward, up),
                SunElevationDeg = sunElev,
            };
            return v;
        }

        /* The lens's world position now and the body it stands on or orbits. */
        private static bool Station(CommandCameraSite site, double ut, out DVec3 station, out CelestialBody body)
        {
            station = default;
            body = site.StationBody;
            if (site.HasVantage)
            {
                if (!site.TryVantagePosition(ut, out var world, out _)) return false;
                body = site.StationBody;
                station = ToD(world);
                return body != null;
            }
            if (body == null) return false;
            station = ToD(body.GetWorldSurfacePosition(site.Latitude, site.Longitude, site.Altitude));
            return true;
        }

        /*
         * Whether the vantage, by its own forecast of how it and the craft
         * move, loses sight of the craft within `horizonUt` game seconds. No
         * forecast (a point that cannot be placed, a world model record that
         * ends first) says no: the network then reacts on the next pass.
         */
        private static bool LosesSight(Entry e, double ut, double horizonUt, int samples)
        {
            var site = e.Site;
            if (site == null || !(horizonUt > 0)) return false;
            if (!site.TryStationMotion(ut, out var station)) return false;
            if (!e.Known.SubjectMotion(ut, out var aim, out _, out double until)) return false;
            horizonUt = Math.Min(horizonUt, until - ut);
            if (!(horizonUt > 0)) return false;
            double radius = e.Known.SubjectRadiusM;
            if (double.IsNaN(radius)) radius = CommandCameraMath.NominalCraftRadiusM;
            e.Occlusion.UseSite(site);
            if (!e.Forecast.Begin(ut, horizonUt, station, aim, radius)) return false;
            for (int k = 1; k <= samples; k++)
            {
                var state = e.Forecast.StateAt(ut + horizonUt * k / samples);
                if (e.Forecast.Unplaced) return false;
                if (state.Occluded) return true;
            }
            return false;
        }

        /* Every vantage's site key, resolved or as its vantage names it. */
        public void SiteKeys(List<string> into)
        {
            for (int i = 0; i < _entries.Count; i++) into.Add(_entries[i].Key);
        }

        /* Every vantage as the last pass left it. */
        public void Readings(List<NetworkVantageReading> into)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                into.Add(new NetworkVantageReading
                {
                    SiteKey = e.Key,
                    Label = e.Label,
                    Kind = e.Site != null ? e.Site.Kind : KindOf(e.Spec.Vantage.Kind),
                    VantagePersistentId = e.Site != null ? e.Site.VantagePersistentId
                        : e.Spec.Vantage.Kind == CommandVantageKind.Vessel ? e.Spec.Vantage.PersistentId : 0,
                    Active = i == _switch.Current,
                    Fallback = i == _fallback,
                    State = _states[i],
                    Score = _scores[i],
                    Occluder = _occluders[i],
                });
            }
        }

        private static Api.SiteKind KindOf(CommandVantageKind kind)
        {
            switch (kind)
            {
                case CommandVantageKind.Vessel: return Api.SiteKind.Vessel;
                case CommandVantageKind.Surface: return Api.SiteKind.Surface;
                case CommandVantageKind.LaunchSite: return Api.SiteKind.LaunchSite;
                default: return Api.SiteKind.Station;
            }
        }

        /* A merge moves every craft vantage that rode the merged craft; the rig moves the current one too, which is the same site. */
        private void OnMerged(uint oldPid, uint newPid)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var site = _entries[i].Site;
                if (site != null && site.HasVantage && site.FollowMerge(oldPid, newPid))
                    Debug.Log($"{Tag} cam={_w.FlightId} network vantage '{_entries[i].Label}' merged: persistentId={oldPid} -> {newPid}");
            }
        }

        public void Dispose()
        {
            if (!_subscribed) return;
            _subscribed = false;
            GameEvents.onVesselPersistentIdChanged.Remove(OnMerged);
        }

        private static DVec3 ToD(Vector3d v) => new DVec3(v.x, v.y, v.z);
    }
}
