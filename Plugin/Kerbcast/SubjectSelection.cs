using System;

namespace Kerbcast
{
    /*
     * Which craft a command feed watches, as asked: the active craft (the
     * flight default), a chosen craft by persistentId, or the configured
     * name (the Space Center default, or the latest launch with no name).
     * The feed's fix source resolves the request to a craft and records it
     * here; the picture draws the craft recorded, whatever source resolved
     * it. The rig and the provider change the request; nothing else does.
     */
    internal sealed class SubjectSelection
    {
        private const string Tag = "[Kerbcast][CMDCAM]";

        private readonly bool _defaultFollowActive;
        private readonly Action<string> _log;

        /* Following the active craft, read live, so the feed survives vessel switches. */
        public bool FollowActive { get; private set; }
        /* The configured craft name for the default; empty for the latest launch. */
        public string WantedName { get; }
        /* The craft last chosen by Choose (or followed through a merge); 0 for the default. */
        public uint ChosenPersistentId { get; private set; }
        /* The craft the fix source last resolved the request to; 0 before any. */
        public uint ResolvedPersistentId { get; set; }
        /* Counts changes of request, so a reader can tell a new request from an old one. */
        public int Version { get; private set; }
        /* The command lock is held: a default request (no craft chosen) names no craft at all. */
        public bool Locked { get; set; }
        /* Called after every change of request, so the fix source resolves it at once. */
        public Action Changed { get; set; }

        private SubjectSelection(bool followActive, string wantedName, Action<string> log)
        {
            _defaultFollowActive = followActive;
            FollowActive = followActive;
            WantedName = wantedName ?? "";
            _log = log;
        }

        public static SubjectSelection ActiveVessel(Action<string> log = null) => new SubjectSelection(true, null, log);

        /* Empty name: the most recently launched craft that is not debris. */
        public static SubjectSelection Chosen(string name, Action<string> log = null) => new SubjectSelection(false, name, log);

        /* Watch the craft with this persistentId from now on, whether or not
           it is loaded. 0 returns to the default: the active craft, or the
           configured name at the Space Center. */
        public void Choose(uint persistentId)
        {
            ChosenPersistentId = persistentId;
            FollowActive = persistentId == 0 && _defaultFollowActive;
            Version++;
            _log?.Invoke($"{Tag} target choice: {(persistentId == 0 ? "default" : "persistentId=" + persistentId)}");
            Changed?.Invoke();
        }

        /* Docking or a claw grab merges one craft into another, and only the
           survivor keeps its persistentId. A watched craft that is absorbed is
           followed into the craft it joined instead of being dropped. KSP
           announces each merge twice; the second is a no-op. */
        public void Merged(uint oldPid, uint newPid)
        {
            if (FollowActive || oldPid == newPid) return;
            if (ChosenPersistentId != oldPid && ResolvedPersistentId != oldPid) return;
            _log?.Invoke($"{Tag} target merged: persistentId={oldPid} -> {newPid}");
            ChosenPersistentId = newPid;
            Version++;
            Changed?.Invoke();
        }

        /* The same request again, for when what it may resolve to changed (the lock was taken or released). */
        public void Refresh()
        {
            Version++;
            Changed?.Invoke();
        }

        /* The chosen craft no longer exists: back to the default, without
           announcing a change (the source is already resolving). */
        public void DropChoice()
        {
            ChosenPersistentId = 0;
            FollowActive = _defaultFollowActive;
            Version++;
        }
    }

    /*
     * Runs truth's resolve of a request only while truth is what the feed
     * reads. A request made under a world model is the model's to resolve:
     * truth must not pick for it, nor drop a craft KSP no longer has that the
     * model still knows. Truth resolves it when it is read again.
     */
    internal sealed class TruthResolveGate
    {
        private readonly Func<bool> _truthActive;
        private readonly Action _resolve;
        private bool _pending;

        /* `truthActive` null means truth always reads. */
        public TruthResolveGate(Func<bool> truthActive, Action resolve)
        {
            _truthActive = truthActive;
            _resolve = resolve;
        }

        public bool Pending => _pending;

        public void Changed()
        {
            if (_truthActive != null && !_truthActive())
            {
                _pending = true;
                return;
            }
            _pending = false;
            _resolve();
        }

        /* Before truth reads the subject: a request deferred under the model is resolved now. */
        public void BeforeRead()
        {
            if (_pending) Changed();
        }
    }
}
