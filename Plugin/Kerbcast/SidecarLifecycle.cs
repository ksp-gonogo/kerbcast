// Unity-free sidecar process lifecycle state machine, owned by
// KerbcastSidecarHost. One process per KSP game session: spawned on the
// first flight scene of the session, left running across every scene
// change after that, killed only on game exit. Crashes relaunch with a
// bounded, growing backoff; a sustained healthy run refunds the budget.
//
// The host translates Spawn/Kill actions into Process calls and feeds
// back OnSpawned / OnSpawnFailed / OnCrashed. All methods are
// main-thread only; the host marshals its Exited event onto Tick's
// thread before calling in.

namespace Kerbcast
{
    /* What the host should do with the process right now. */
    public enum SidecarAction
    {
        None,
        Spawn,
        Kill,
    }

    /* Verdict for an unexpected exit: relaunch after a delay (Attempt > 0)
       or stop trying for now (GaveUp). Both false/zero means the machine
       is not managing the process (quitting, or auto-spawn disabled). */
    public struct SidecarCrashVerdict
    {
        public bool GaveUp;
        public int Attempt;
        public int MaxAttempts;
        public double RestartDelaySeconds;
    }

    public sealed class SidecarLifecycle
    {
        private readonly bool _autoSpawn;
        private readonly int _maxRestarts;
        private readonly double _restartDelaySeconds;
        private readonly double _healthyUptimeSeconds;

        private bool _running;
        private bool _quitting;
        private bool _gaveUp;
        private bool _restartArmed;
        private double _cooldown;
        private double _uptime;
        private int _attempts;

        public SidecarLifecycle(
            bool autoSpawn,
            int maxRestarts,
            double restartDelaySeconds,
            double healthyUptimeSeconds)
        {
            _autoSpawn = autoSpawn;
            _maxRestarts = maxRestarts;
            _restartDelaySeconds = restartDelaySeconds;
            _healthyUptimeSeconds = healthyUptimeSeconds;
        }

        public bool Running => _running;
        public bool GaveUp => _gaveUp;
        public bool Quitting => _quitting;
        public int Attempts => _attempts;
        public bool RestartArmed => _restartArmed;
        public double RestartCooldownSeconds => _restartArmed ? _cooldown : 0.0;

        /* Flight scene entered. Spawns on the first flight of the session;
           later entries find the process running and do nothing. Re-entry
           also re-arms a gave-up machine (the per-scene fresh crash budget
           the old per-flight lifecycle had) and short-circuits a pending
           crash backoff, because the operator entering flight wants streams
           now, not in N seconds. */
        public SidecarAction OnFlightEntered()
        {
            if (_quitting || !_autoSpawn) return SidecarAction.None;
            _gaveUp = false;
            _attempts = 0;
            if (_running) return SidecarAction.None;
            _restartArmed = false;
            _cooldown = 0.0;
            return SidecarAction.Spawn;
        }

        /* Game exit: the only point the process is intentionally killed.
           Terminal; no spawn can follow. */
        public SidecarAction OnGameQuit()
        {
            _quitting = true;
            _restartArmed = false;
            return _running ? SidecarAction.Kill : SidecarAction.None;
        }

        public void OnSpawned()
        {
            _running = true;
            _uptime = 0.0;
        }

        /* Spawn failed (missing binary, exec error). No retry loop: the
           next flight entry tries again, matching the old per-scene
           behaviour. */
        public void OnSpawnFailed()
        {
            _running = false;
        }

        /* Unexpected exit (the process died without OnGameQuit). Decides
           immediately between an armed backoff relaunch and giving up;
           a later OnFlightEntered re-arms a gave-up machine. */
        public SidecarCrashVerdict OnCrashed()
        {
            _running = false;
            var verdict = new SidecarCrashVerdict { MaxAttempts = _maxRestarts };
            if (_quitting || !_autoSpawn || _gaveUp)
            {
                verdict.GaveUp = _gaveUp;
                return verdict;
            }
            if (_attempts >= _maxRestarts)
            {
                _gaveUp = true;
                verdict.GaveUp = true;
                return verdict;
            }
            _attempts++;
            _cooldown = _restartDelaySeconds * _attempts;
            _restartArmed = true;
            verdict.Attempt = _attempts;
            verdict.RestartDelaySeconds = _cooldown;
            return verdict;
        }

        /* Per-frame. Counts an armed relaunch down to Spawn; refunds the
           crash budget after a sustained healthy run so an isolated crash
           hours later still gets the full retry budget. */
        public SidecarAction Tick(double dt)
        {
            if (_quitting) return SidecarAction.None;
            if (_running)
            {
                if (_attempts > 0)
                {
                    _uptime += dt;
                    if (_uptime >= _healthyUptimeSeconds) _attempts = 0;
                }
                return SidecarAction.None;
            }
            if (!_restartArmed) return SidecarAction.None;
            _cooldown -= dt;
            if (_cooldown > 0.0) return SidecarAction.None;
            _restartArmed = false;
            _uptime = 0.0;
            return SidecarAction.Spawn;
        }
    }
}
