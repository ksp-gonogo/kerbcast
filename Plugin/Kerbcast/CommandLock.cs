using System;
using Kerbcast.Api;

namespace Kerbcast
{
    /*
     * The one command lock for a scene: who holds it, with what options and
     * silence text, and the holder's light speed. Kerbcast.API's TakeLock
     * and the obsolete KerbcastControl.SetCommandAuthority both take this
     * lock, so there is no second way in. Every owner call is a heartbeat; 5 s
     * of real time with none (and no publish) times the holder out, which
     * blanks every governed feed and never returns to truth.
     *
     * Taking it starts a new world-model tenure, so no publisher from an
     * earlier holder counts; with WorldModelRequired every craft is unknown
     * from now until the first publish. Releasing it ends the tenure: truth
     * again. Main thread only; the world model's publisher stays any thread.
     */
    internal sealed class CommandLock
    {
        public const float TimeoutSeconds = 5f;

        private readonly WorldModelStore _store;
        private readonly Action<string> _log;
        private float _lastCall;

        public CommandLock(WorldModelStore store, Action<string> log)
        {
            _store = store;
            _log = log;
        }

        public string Owner { get; private set; }
        public bool Held => Owner != null;
        public LockOptions Options { get; private set; }
        /* What the holder's feeds show once it falls silent; null for CommandCameraMath.DefaultSilentText. */
        public string SilentText { get; private set; }
        /* Null for no delay. */
        public double? LightSpeed { get; private set; }

        /* KSP.log's one line for each lock loss, so a loss and its reason show without a listening mod. */
        public static string LostLine(string owner, LockLossReason reason) =>
            $"[Kerbcast][CMDCAM] command lock lost: {owner} (reason {reason})";

        public bool Holds(string owner) => owner != null && string.Equals(Owner, owner, StringComparison.Ordinal);

        /* Real time, so it keeps counting while the game is paused or warping. */
        public bool TimedOut(float realNow) =>
            Held && realNow - _lastCall > TimeoutSeconds && !_store.HeardWithin(TimeoutSeconds);

        public void Heartbeat(float realNow)
        {
            _lastCall = realNow;
            Calls++;
        }

        /* Every owner call and renewal so far, the reserved take included; it only grows. */
        public int Calls { get; private set; }

        /* Kerbcast's own, not the owner's: restarts the silence clock without counting a call. */
        public void RestartSilence(float realNow) => _lastCall = realNow;

        /* The holder's world model attached or published within `seconds` of real time. */
        public bool WorldModelHeardWithin(double seconds) => _store.HeardWithin(seconds);

        /* Taken starts a new tenure; Renewed keeps it, taking the new options and silence text. */
        public LockResult Take(string owner, LockOptions options, string silentText, float realNow)
        {
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("an owner is required", nameof(owner));
            ReservedAssemblies.CheckText(owner, nameof(owner));
            ReservedAssemblies.CheckText(silentText, nameof(silentText));
            if (Held && !Holds(owner)) return LockResult.HeldByOther;
            bool renewed = Held;
            bool required = (Options & LockOptions.WorldModelRequired) != 0;
            Owner = owner;
            Options = options;
            SilentText = silentText;
            Heartbeat(realNow);
            if (!renewed)
            {
                _store.EndTenure();
                _log?.Invoke($"[Kerbcast][CMDCAM] command lock taken: {owner}{((options & LockOptions.WorldModelRequired) != 0 ? " (world model required)" : "")}");
            }
            /* A renewal is a heartbeat: it must not undo a Detach(returnToTruth). Only newly gaining the option requires a model. */
            if ((options & LockOptions.WorldModelRequired) != 0 && (!renewed || !required)) _store.Require();
            return renewed ? LockResult.Renewed : LockResult.Taken;
        }

        /* False, with nothing changed, unless `owner` holds the lock. */
        public bool Release(string owner)
        {
            if (!Held || !Holds(owner)) return false;
            _log?.Invoke($"[Kerbcast][CMDCAM] command lock released: {owner}");
            Clear();
            return true;
        }

        /* The scene ended or the authority reset: nothing of the tenure survives. */
        public void Clear()
        {
            Owner = null;
            Options = LockOptions.None;
            SilentText = null;
            LightSpeed = null;
            _store.EndTenure();
        }

        /* False, with nothing changed, unless `owner` holds the lock. */
        public bool SetLightSpeed(string owner, double? metresPerSecond, float realNow)
        {
            if (metresPerSecond.HasValue && !(Vec3d.Finite(metresPerSecond.Value) && metresPerSecond.Value > 0))
                throw new ArgumentOutOfRangeException(nameof(metresPerSecond), metresPerSecond, "must be finite and positive, or null");
            if (!Holds(owner)) return false;
            Heartbeat(realNow);
            LightSpeed = metresPerSecond;
            return true;
        }
    }

    /*
     * A reserved scene's lock, taken for its owner before the owner could
     * call. If the owner never calls (installed but broken, say), the
     * reservation lapses for the scene after LapseSeconds of real time: the
     * lock stays held and the feeds stay blank, since a slow but healthy
     * owner must never have its viewers shown truth, but browser clients may
     * create and remove command cameras again. The owner's first call or
     * publish ends the lapse. The wait starts at the scene's first frame, so
     * a long load does not count against the owner.
     */
    internal sealed class ReservationWatch
    {
        public const float LapseSeconds = 30f;

        private string _owner;
        private bool _started;
        private bool _lapsed;
        private float _from;
        private int _calls;

        /* Right after Kerbcast takes the lock for `owner`, so every owner call from here counts, even one before the first frame. */
        public void Watch(string owner, CommandLock commandLock)
        {
            _owner = owner;
            _started = false;
            _lapsed = false;
            _calls = commandLock.Calls;
        }

        /* The scene's first frame. */
        public void Start(float realNow)
        {
            if (_owner == null || _started) return;
            _started = true;
            _from = realNow;
        }

        /* The owner whose reservation lapses now, once; null while it may still call, once it has, or once lapsed. */
        public string Lapse(CommandLock commandLock, float realNow)
        {
            if (_owner == null || !_started || _lapsed) return null;
            if (Answered(commandLock))
            {
                _owner = null;
                return null;
            }
            if (realNow - _from < LapseSeconds) return null;
            _lapsed = true;
            return _owner;
        }

        /* Whether the scene is lapsed now; an owner call, a publish or the lock leaving its owner ends it for good. */
        public bool Lapsed(CommandLock commandLock)
        {
            if (!_lapsed) return false;
            if (!Answered(commandLock)) return true;
            _lapsed = false;
            _owner = null;
            return false;
        }

        private bool Answered(CommandLock commandLock) =>
            !commandLock.Holds(_owner) || commandLock.Calls != _calls || commandLock.WorldModelHeardWithin(LapseSeconds);
    }

    /*
     * A lock holder's claim on every scene to come: each one starts locked for
     * the owner, with every craft unknown until it attaches, while the owner's
     * assembly is loaded. A silent holder times out into blank feeds, never
     * truth, and a scene where the owner never calls at all lets browsers
     * manage the blank cameras (ReservationWatch). It is saved with
     * Kerbcast's settings, keeps the silence text the owner last took the
     * lock with, lapses at startup when the assembly is gone, and the player
     * can clear it in game.
     */
    internal sealed class LockReservation
    {
        public string Owner { get; private set; }
        public string Assembly { get; private set; }
        public string SilentText { get; private set; }
        public bool Held => Owner != null;

        /* Called after every change, to save it. */
        public Action Changed { get; set; }

        /* The owner's later call replaces it; another owner's throws (see ReservedAssemblies.Check). */
        public void Reserve(string owner, string ownerAssembly)
        {
            ReservedAssemblies.Check(owner, ownerAssembly, Owner);
            if (!Holds(owner)) SilentText = null;
            Owner = owner;
            Assembly = ownerAssembly;
            Changed?.Invoke();
        }

        /* Ignored for anyone but the reservation's owner. */
        public void Clear(string owner)
        {
            if (!Holds(owner)) return;
            Owner = null;
            Assembly = null;
            SilentText = null;
            Changed?.Invoke();
        }

        /* The player's way out, whoever the owner is. */
        public void ClearForPlayer()
        {
            if (!Held) return;
            Owner = null;
            Assembly = null;
            SilentText = null;
            Changed?.Invoke();
        }

        /* The owner took the lock with this silence text; reserved scenes start with it. */
        public void NoteSilentText(string owner, string silentText)
        {
            if (!Holds(owner) || string.Equals(SilentText, silentText, StringComparison.Ordinal)) return;
            SilentText = silentText;
            Changed?.Invoke();
        }

        /* Restores a saved reservation without saving it again. */
        public void Load(string owner, string ownerAssembly, string silentText)
        {
            Owner = string.IsNullOrEmpty(owner) ? null : owner;
            Assembly = Owner == null ? null : ownerAssembly;
            SilentText = Owner == null || string.IsNullOrEmpty(silentText) ? null : silentText;
        }

        /* At startup: drops the reservation when its assembly is missing, empty or always loaded. True when it lapsed. */
        public bool LapseUnless(Func<string, bool> assemblyLoaded)
        {
            if (!Held) return false;
            if (!string.IsNullOrEmpty(Assembly) && !ReservedAssemblies.IsReserved(Assembly) && assemblyLoaded(Assembly)) return false;
            Owner = null;
            Assembly = null;
            SilentText = null;
            Changed?.Invoke();
            return true;
        }

        private bool Holds(string owner) => owner != null && string.Equals(Owner, owner, StringComparison.Ordinal);
    }
}
