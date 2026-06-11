// Unit test for SidecarLifecycle, the once-per-session sidecar process
// state machine.
//
// The bug this guards against: the old per-flight lifecycle spawned the
// sidecar in every Flight Awake and killed it in every OnDestroy. A real
// 25-minute Windows session showed 6 kill/relaunch cycles (one per scene
// change, including FLIGHT-to-FLIGHT reverts), each costing a settings
// reload + encoder re-probe + every WebRTC peer torn down, with 15-26s of
// dead air per cycle. The machine pinned here spawns once on the first
// flight entry, treats later entries as no-ops, kills only on game quit,
// and keeps the old crash backoff + healthy-uptime refund semantics.
//
// Exit code 0 = pass, 1 = fail.

using System;
using Kerbcam;

int failures = 0;
void Check(bool cond, string msg)
{
    if (cond) Console.WriteLine("  ok   " + msg);
    else { Console.Error.WriteLine("  FAIL " + msg); failures++; }
}

const int MaxRestarts = 5;
const double Delay = 5.0;
const double HealthyUptime = 60.0;

SidecarLifecycle Machine(bool autoSpawn = true) =>
    new SidecarLifecycle(autoSpawn, MaxRestarts, Delay, HealthyUptime);

// --- 1. First flight entry of the session spawns; later entries don't. ---
{
    var m = Machine();
    Check(m.OnFlightEntered() == SidecarAction.Spawn, "first flight entry -> Spawn");
    m.OnSpawned();
    Check(m.OnFlightEntered() == SidecarAction.None, "second flight entry (scene change) -> None, process kept");
    Check(m.OnFlightEntered() == SidecarAction.None, "FLIGHT-to-FLIGHT revert -> None, process kept");
    Check(m.Running, "still running across scene changes");
}

// --- 2. Scene exit is not an event: nothing but quit kills the process. ---
{
    var m = Machine();
    m.OnFlightEntered();
    m.OnSpawned();
    /* A KSP session's worth of ticks between flights (KSC, VAB, map). */
    for (int i = 0; i < 10_000; i++)
        Check2(m.Tick(0.016) == SidecarAction.None, ref failures);
    Check(m.Running, "ticking outside flight never kills or respawns");
    Check(m.OnGameQuit() == SidecarAction.Kill, "game quit -> Kill");
    Check(m.Quitting, "machine is terminal after quit");
    Check(m.Tick(1.0) == SidecarAction.None, "no tick action after quit");
    Check(m.OnFlightEntered() == SidecarAction.None, "no spawn after quit");
}

// --- 3. Quit while not running kills nothing. ---
{
    var m = Machine();
    Check(m.OnGameQuit() == SidecarAction.None, "quit before any spawn -> None");
}

// --- 4. AutoSpawnSidecar=false: machine never spawns or kills. ---
{
    var m = Machine(autoSpawn: false);
    Check(m.OnFlightEntered() == SidecarAction.None, "autoSpawn=false: flight entry -> None");
    Check(m.Tick(100.0) == SidecarAction.None, "autoSpawn=false: tick -> None");
    Check(m.OnGameQuit() == SidecarAction.None, "autoSpawn=false: quit -> None (nothing to kill)");
}

// --- 5. Crash arms a backoff relaunch; delay grows per attempt. ---
{
    var m = Machine();
    m.OnFlightEntered();
    m.OnSpawned();

    var v1 = m.OnCrashed();
    Check(!v1.GaveUp && v1.Attempt == 1, "first crash -> attempt 1");
    Check(Math.Abs(v1.RestartDelaySeconds - Delay) < 1e-9, "first backoff = base delay");
    Check(m.Tick(Delay - 0.1) == SidecarAction.None, "no spawn inside the backoff window");
    Check(m.Tick(0.2) == SidecarAction.Spawn, "spawn once the backoff elapses");
    m.OnSpawned();

    var v2 = m.OnCrashed();
    Check(v2.Attempt == 2 && Math.Abs(v2.RestartDelaySeconds - 2 * Delay) < 1e-9,
        "second crash -> attempt 2, doubled delay");
}

// --- 6. Bounded attempts: crash looping gives up; flight entry re-arms. ---
{
    var m = Machine();
    m.OnFlightEntered();
    m.OnSpawned();
    for (int i = 0; i < MaxRestarts; i++)
    {
        var v = m.OnCrashed();
        Check2(!v.GaveUp, ref failures);
        while (m.Tick(1.0) != SidecarAction.Spawn) { }
        m.OnSpawned();
    }
    var final = m.OnCrashed();
    Check(final.GaveUp, "crash after max attempts -> gave up");
    Check(m.GaveUp, "machine reports GaveUp");
    Check(m.Tick(1_000.0) == SidecarAction.None, "no relaunch while gave up");
    Check(m.OnFlightEntered() == SidecarAction.Spawn, "next flight entry re-arms a gave-up machine and spawns");
}

// --- 7. Healthy uptime refunds the crash budget. ---
{
    var m = Machine();
    m.OnFlightEntered();
    m.OnSpawned();
    m.OnCrashed();                                  // attempt 1
    while (m.Tick(1.0) != SidecarAction.Spawn) { }
    m.OnSpawned();
    for (int i = 0; i < (int)HealthyUptime + 1; i++) m.Tick(1.0);
    Check(m.Attempts == 0, "sustained healthy run refunds the attempt budget");
    var v = m.OnCrashed();
    Check(v.Attempt == 1 && Math.Abs(v.RestartDelaySeconds - Delay) < 1e-9,
        "post-refund crash starts back at attempt 1 / base delay");
}

// --- 8. Flight entry during a pending backoff spawns immediately. ---
{
    var m = Machine();
    m.OnFlightEntered();
    m.OnSpawned();
    m.OnCrashed();
    Check(m.RestartArmed, "backoff armed after crash");
    Check(m.OnFlightEntered() == SidecarAction.Spawn,
        "flight entry short-circuits the backoff (operator wants streams now)");
    Check(!m.RestartArmed, "backoff disarmed by the flight-entry spawn");
    m.OnSpawned();
    Check(m.Tick(1_000.0) == SidecarAction.None, "stale backoff never double-spawns");
}

// --- 9. Spawn failure does not retry-loop; next flight entry retries. ---
{
    var m = Machine();
    Check(m.OnFlightEntered() == SidecarAction.Spawn, "entry asks for spawn");
    m.OnSpawnFailed();                              // e.g. binary missing
    Check(m.Tick(1_000.0) == SidecarAction.None, "no retry loop after a failed spawn");
    Check(m.OnFlightEntered() == SidecarAction.Spawn, "next flight entry retries the spawn");
}

// --- 10. Crash during quit is ignored (no relaunch race at shutdown). ---
{
    var m = Machine();
    m.OnFlightEntered();
    m.OnSpawned();
    m.OnGameQuit();
    var v = m.OnCrashed();
    Check(v.Attempt == 0 && !v.GaveUp, "exit observed during quit -> no verdict");
    Check(m.Tick(1_000.0) == SidecarAction.None, "no relaunch during quit");
}

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

/* Loop-friendly assert: counts silently so 10k-iteration checks don't
   print 10k ok lines. */
static void Check2(bool cond, ref int failures)
{
    if (!cond) { Console.Error.WriteLine("  FAIL (loop assertion)"); failures++; }
}
