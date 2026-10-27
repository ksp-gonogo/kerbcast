using System;
using Kerbcast.Api;

namespace Kerbcast
{
    /*
     * The one command lock for a scene: who holds it, with what options and
     * silence text, and the holder's signal speed. Kerbcast.API's TakeLock
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
        public double? SignalSpeed { get; private set; }

        /* KSP.log's one line for each lock loss, so a loss and its reason show without a listening mod. */
        public static string LostLine(string owner, LockLossReason reason) =>
            $"[Kerbcast][CMDCAM] command lock lost: {owner} (reason {reason})";

        public bool Holds(string owner) => owner != null && string.Equals(Owner, owner, StringComparison.Ordinal);

        /* Real time, so it keeps counting while the game is paused or warping. */
        public bool TimedOut(float realNow) =>
            Held && realNow - _lastCall > TimeoutSeconds && !_store.HeardWithin(TimeoutSeconds);

        public void Heartbeat(float realNow) => _lastCall = realNow;

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
            _lastCall = realNow;
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
            SignalSpeed = null;
            _store.EndTenure();
        }

        /* False, with nothing changed, unless `owner` holds the lock. */
        public bool SetSignalSpeed(string owner, double? metresPerSecond, float realNow)
        {
            if (metresPerSecond.HasValue && !(Vec3d.Finite(metresPerSecond.Value) && metresPerSecond.Value > 0))
                throw new ArgumentOutOfRangeException(nameof(metresPerSecond), metresPerSecond, "must be finite and positive, or null");
            if (!Holds(owner)) return false;
            _lastCall = realNow;
            SignalSpeed = metresPerSecond;
            return true;
        }
    }

    /*
     * A lock holder's claim on every scene to come: each one starts locked for
     * the owner, with every craft unknown until it attaches, while the owner's
     * assembly is loaded. It never times out into truth. It is saved with
     * Kerbcast's settings, keeps the silence text the owner last took the
     * lock with, and lapses at startup when the assembly is gone.
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
