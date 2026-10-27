using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Kerbcast.Api;

namespace Kerbcast
{
    /* How governed command feeds read craft this frame. */
    internal enum WorldModelMode
    {
        /* No world model attached, or the holder returned to truth: KSP's truth, as 1.11. */
        Truth,
        /* Attached with nothing published, or the publisher disposed or detached: every craft unknown. */
        Waiting,
        /* A valid snapshot: the provider only. */
        Model,
        /* The snapshot predates a game-state load or a backwards UT: every record refused stale until a newer epoch. */
        Stale,
    }

    /* One frame's view of the world model: what mode it is in and the snapshot read for the frame. */
    internal struct WorldModelView
    {
        public WorldModelMode Mode;
        /* Null in Truth and Waiting. */
        public WorldModelSnapshot Snapshot;
    }

    /*
     * The holder's world model for one scene: who may publish, the snapshot
     * feeds read, and the rules that fail it closed.
     *
     * The publisher may run on any thread. Each publish copies into a
     * snapshot of Kerbcast's own and swaps it in atomically. The main thread
     * captures the current snapshot once per frame (Capture), so every read in
     * a frame sees one snapshot. A swapped-out snapshot records the frame it
     * left in; it goes back to the pool only once a later frame has begun,
     * when no read can still hold it. Pooled snapshots return as new ones, so
     * readers key any memo by Generation, which no two fills share.
     *
     * A tenure is one lock holder's time with the world model: it starts at
     * the first attach and ends when the lock is cleared or the scene ends.
     * Epochs are compared within one tenure.
     */
    internal sealed class WorldModelStore
    {
        /* Pooled snapshots and builder storage; past this, they go to the GC. */
        public const int PoolCap = 4;
        /* Retired snapshots waiting for a frame. With no frame reading (no
           command feed on screen) the rest are dropped to the GC at once. */
        public const int RetiredCap = PoolCap * 2;

        private readonly object _gate = new object();
        private readonly Action<string> _log;
        private readonly Action<IReadOnlyDictionary<string, string>> _bind;

        /* Under the gate. */
        private int _attachToken;
        private bool _closed;
        private bool _governed;
        private long _lastEpoch;
        private bool _hasEpoch;
        private bool _invalid;
        private long _invalidEpoch;
        private bool _pendingLoad;

        private WorldModelSnapshot _current;
        private long _frame;
        private long _heartbeat;
        private long _generation;

        private readonly ConcurrentQueue<WorldModelSnapshot> _retired = new ConcurrentQueue<WorldModelSnapshot>();
        private int _retiredCount;
        private readonly ConcurrentStack<WorldModelSnapshot> _pool = new ConcurrentStack<WorldModelSnapshot>();
        private readonly ConcurrentStack<BuilderStorage> _storage = new ConcurrentStack<BuilderStorage>();

        /* Main thread only. */
        private readonly List<WorldModelSnapshot> _toRecycle = new List<WorldModelSnapshot>();
        private long _capturedFrame = long.MinValue;
        private WorldModelView _view;
        private readonly Dictionary<string, SiteKind> _siteKinds = new Dictionary<string, SiteKind>(StringComparer.Ordinal);
        private readonly HashSet<string> _clashes = new HashSet<string>(StringComparer.Ordinal);
        private double _lastUt = double.NaN;

        /* `bind` receives each accepted snapshot's site bindings, on the publisher's thread. */
        public WorldModelStore(Action<string> log, Action<IReadOnlyDictionary<string, string>> bind)
        {
            _log = log;
            _bind = bind;
        }

        /* Snapshots made rather than taken from the pool, for tests and the log. */
        public int Allocated;

        public int Pooled => _pool.Count;

        /* When set, each copy of an accepted builder is timed into it (telemetry). */
        public CostMeter FillMeter { get; set; }

        /* Records in the current snapshot; 0 with none. Any thread. */
        public int CurrentRecords => Volatile.Read(ref _current)?.RecordCount ?? 0;

        /* Retired snapshots still held for recycling. */
        public int Retired => Volatile.Read(ref _retiredCount);

        /* Main thread: memos readers keep with the store so they outlive any one reader. Keyed by their owner's choosing. */
        public readonly Dictionary<string, object> Memos = new Dictionary<string, object>(StringComparer.Ordinal);

        /* Whether governed feeds read the world model rather than truth. */
        public bool Governed => Volatile.Read(ref _governed);

        /* Real time since the last accepted publish or attach is under `seconds`. */
        public bool HeardWithin(double seconds)
        {
            long last = Interlocked.Read(ref _heartbeat);
            return last != 0 && Stopwatch.GetTimestamp() - last < seconds * Stopwatch.Frequency;
        }

        /* Main thread: the lock was taken with WorldModelRequired. Every craft
           is unknown from now until the holder attaches and publishes. */
        public void Require()
        {
            lock (_gate)
            {
                if (_governed) return;
                _governed = true;
                _log?.Invoke("[Kerbcast][WORLD] the lock requires a world model; every craft unknown until one is published");
            }
            _capturedFrame = long.MinValue;
        }

        /* Main thread: a new publisher for the lock holder, which starts with
           nothing published. The previous one is retired. A pending load's
           staleness stays: only a newer epoch lifts it. */
        public WorldModelPublisher Attach(string owner)
        {
            lock (_gate)
            {
                _attachToken++;
                _governed = true;
                DropCurrent();
                Beat();
                _log?.Invoke($"[Kerbcast][WORLD] {owner} attached a world model; feeds read it from now on");
                _capturedFrame = long.MinValue;
                return new WorldModelPublisher(this, owner, _attachToken);
            }
        }

        /* Main thread: the lock was cleared or released. Truth again, with nothing carried into the next tenure. */
        public void EndTenure()
        {
            lock (_gate)
            {
                _attachToken++;
                bool was = _governed;
                _governed = false;
                _hasEpoch = false;
                _invalid = false;
                _pendingLoad = false;
                DropCurrent();
                if (was) _log?.Invoke("[Kerbcast][WORLD] world model tenure ended; feeds read truth");
            }
            _capturedFrame = long.MinValue;
        }

        /* Whether Kerbcast runs in some scene now, from any thread; a closed store answers NotRunning when it does not. Null reads as running. */
        public Func<bool> Running { get; set; }

        /* Main thread: the scene ended; every publisher now gets SceneChanged, or NotRunning while no scene runs Kerbcast. */
        public void Close()
        {
            lock (_gate) _closed = true;
            EndTenure();
            Memos.Clear();
        }

        /* Main thread: a game-state load. The snapshot is invalid until a
           publish with a newer epoch; before any publish, the first one is. */
        public void InvalidateForLoad() => Invalidate("game state loaded");

        /*
         * Main thread: the view every read in this frame shares. The first call
         * in a new frame (`frame` is the engine's frame count) recycles what no
         * frame can still hold, invalidates on a backwards UT, then reads the
         * current snapshot and its mode once.
         */
        public WorldModelView Capture(long frame, double ut)
        {
            if (frame == _capturedFrame) return _view;
            _capturedFrame = frame;
            long f = Interlocked.Increment(ref _frame);
            Recycle(f);
            if (!double.IsNaN(_lastUt) && ut < _lastUt) Invalidate($"UT went back from {_lastUt:F3} to {ut:F3}");
            _lastUt = ut;
            lock (_gate)
            {
                var snap = Volatile.Read(ref _current);
                if (_invalid && snap != null && snap.Epoch > _invalidEpoch)
                {
                    _invalid = false;
                    _log?.Invoke($"[Kerbcast][WORLD] snapshot valid again at epoch {snap.Epoch}");
                }
                if (!_governed) _view = new WorldModelView { Mode = WorldModelMode.Truth };
                else if (snap == null) _view = new WorldModelView { Mode = WorldModelMode.Waiting };
                else _view = new WorldModelView { Mode = _invalid ? WorldModelMode.Stale : WorldModelMode.Model, Snapshot = snap };
            }
            if (_view.Snapshot != null) _view.Snapshot.ReaderThread = Environment.CurrentManagedThreadId;
            return _view;
        }

        /*
         * Main thread: a world model binds sites by key alone, so two sites of
         * different kinds sharing a key (a Kerbal Konstructs base that is both
         * a station and a launch site) read one scope. Logged once per key.
         */
        public void NoteSite(string key, SiteKind kind)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!_siteKinds.TryGetValue(key, out var known))
            {
                _siteKinds[key] = kind;
                return;
            }
            if (known != kind && _clashes.Add(key))
                _log?.Invoke($"[Kerbcast][WORLD] site key '{key}' names both a {known} and a {kind} site; both read the scope bound to that key");
        }

        internal bool Holds(WorldModelPublisher p)
        {
            lock (_gate) return !_closed && p.Token == _attachToken && p.Live;
        }

        internal BuilderStorage RentStorage() => _storage.TryPop(out var s) ? s : new BuilderStorage();

        internal void ReturnStorage(BuilderStorage s)
        {
            if (s != null && _storage.Count < PoolCap) _storage.Push(s);
        }

        /*
         * The builder is copied into a rented snapshot before the gate is
         * taken, so a frame's Capture never waits on a big copy; the checks,
         * swap and bindings run under the gate. A refused copy goes back to
         * the pool.
         */
        internal PublishResult Admit(WorldModelPublisher p, WorldModelBuilder builder, out string error)
        {
            error = null;
            var snap = builder.FirstError == null ? Rent() : null;
            var meter = FillMeter;
            long t0 = meter != null ? Stopwatch.GetTimestamp() : 0;
            snap?.Fill(builder);
            double fillMs = meter != null ? (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency : 0;
            var result = AdmitFilled(p, builder, snap, out error);
            if (meter != null && result == PublishResult.Accepted) meter.Record(fillMs);
            if (result != PublishResult.Accepted && snap != null && _pool.Count < PoolCap) _pool.Push(snap);
            return result;
        }

        private PublishResult AdmitFilled(WorldModelPublisher p, WorldModelBuilder builder, WorldModelSnapshot snap, out string error)
        {
            error = null;
            lock (_gate)
            {
                if (_closed)
                {
                    var running = Running;
                    if (running != null && !running())
                    {
                        error = "Kerbcast is not running";
                        return PublishResult.NotRunning;
                    }
                    error = "the scene changed since the publisher attached";
                    return PublishResult.SceneChanged;
                }
                if (!p.Live || p.Token != _attachToken)
                {
                    error = $"{p.Owner} does not hold the lock";
                    return PublishResult.NotLockHolder;
                }
                if (builder.Sequence <= p.Last)
                {
                    error = $"sequence {builder.Sequence} is not after {p.Last}";
                    return PublishResult.OutOfOrder;
                }
                if (_hasEpoch && builder.Epoch < _lastEpoch)
                {
                    error = $"epoch {builder.Epoch} is older than {_lastEpoch}";
                    return PublishResult.OlderEpoch;
                }
                if (builder.FirstError != null)
                {
                    error = builder.FirstError;
                    return PublishResult.Invalid;
                }
                snap.Stamp(Interlocked.Increment(ref _generation));
                p.Last = builder.Sequence;
                _lastEpoch = builder.Epoch;
                _hasEpoch = true;
                if (_pendingLoad)
                {
                    /* Published after a load that came before any snapshot: it may describe the timeline that went. */
                    _pendingLoad = false;
                    _invalid = true;
                    _invalidEpoch = builder.Epoch;
                }
                Retire(Interlocked.Exchange(ref _current, snap));
                Beat();
                _bind?.Invoke(snap.Bindings);
                return PublishResult.Accepted;
            }
        }

        internal void Detach(WorldModelPublisher p, bool returnToTruth)
        {
            lock (_gate)
            {
                if (_closed || !p.Live || p.Token != _attachToken) return;
                p.Detached = true;
                DropCurrent();
                if (returnToTruth) _governed = false;
                _log?.Invoke(returnToTruth
                    ? $"[Kerbcast][WORLD] {p.Owner} detached its world model; feeds read truth"
                    : $"[Kerbcast][WORLD] {p.Owner} detached its world model; every craft unknown until the lock is released");
            }
        }

        internal void Dispose(WorldModelPublisher p)
        {
            lock (_gate)
            {
                if (p.Disposed) return;
                bool live = !_closed && p.Live && p.Token == _attachToken;
                p.Disposed = true;
                if (!live) return;
                DropCurrent();
                _log?.Invoke($"[Kerbcast][WORLD] {p.Owner} disposed its world model; every craft unknown until the lock is released");
            }
        }

        private void Invalidate(string why)
        {
            lock (_gate)
            {
                if (!_governed) return;
                if (!_hasEpoch)
                {
                    _pendingLoad = true;
                    _log?.Invoke($"[Kerbcast][WORLD] {why} before any publish: the next snapshot is stale until one with a newer epoch");
                    return;
                }
                if (!_invalid) _log?.Invoke($"[Kerbcast][WORLD] {why}: the snapshot is stale until a publish with an epoch after {_lastEpoch}");
                _invalid = true;
                _invalidEpoch = _lastEpoch;
            }
        }

        /* Under the gate. */
        private void DropCurrent() => Retire(Interlocked.Exchange(ref _current, null));

        private void Retire(WorldModelSnapshot old)
        {
            if (old == null) return;
            if (Interlocked.Increment(ref _retiredCount) > RetiredCap)
            {
                /* Nobody is recycling: let the GC have it. No frame can be reading
                   a snapshot it never captured, and one it did capture is only
                   dropped, never reused. */
                Interlocked.Decrement(ref _retiredCount);
                return;
            }
            old.RetiredAt = Interlocked.Read(ref _frame);
            _retired.Enqueue(old);
        }

        private void Beat() => Interlocked.Exchange(ref _heartbeat, Stopwatch.GetTimestamp());

        private WorldModelSnapshot Rent()
        {
            if (_pool.TryPop(out var s)) return s;
            Interlocked.Increment(ref Allocated);
            return new WorldModelSnapshot();
        }

        /* Main thread, at the start of frame `f`: a snapshot retired in an
           earlier frame can no longer be held by any read. */
        private void Recycle(long f)
        {
            while (_retired.TryDequeue(out var s))
            {
                Interlocked.Decrement(ref _retiredCount);
                _toRecycle.Add(s);
            }
            for (int i = _toRecycle.Count - 1; i >= 0; i--)
            {
                var s = _toRecycle[i];
                if (s.RetiredAt >= f) continue;
                _toRecycle.RemoveAt(i);
                if (_pool.Count < PoolCap) _pool.Push(s);
            }
        }
    }

    /* The holder's publisher. Safe on any thread. */
    internal sealed class WorldModelPublisher : IWorldModelPublisher
    {
        private readonly WorldModelStore _store;

        internal readonly string Owner;
        internal readonly int Token;
        /* The rest change under the store's gate. */
        internal long Last = long.MinValue;
        internal bool Detached;
        internal bool Disposed;

        internal WorldModelPublisher(WorldModelStore store, string owner, int token)
        {
            _store = store;
            Owner = owner;
            Token = token;
        }

        internal bool Live => !Disposed && !Detached;

        /* long.MinValue before the first accepted publish. */
        public long LastAcceptedSequence => Interlocked.Read(ref Last);

        public bool Attached => _store.Holds(this);

        public WorldModelBuilder Begin(long sequence, long epoch, double knowledgeUt) =>
            new WorldModelBuilder(this, sequence, epoch, knowledgeUt, _store.RentStorage());

        public PublishResult Publish(WorldModelBuilder builder, out string error)
        {
            Own(builder);
            Consume(builder);
            try
            {
                return _store.Admit(this, builder, out error);
            }
            finally
            {
                _store.ReturnStorage(builder.Release());
            }
        }

        public void Abandon(WorldModelBuilder builder)
        {
            Own(builder);
            Consume(builder);
            _store.ReturnStorage(builder.Release());
        }

        /* Only one publish or abandon gets the builder, so its storage is never handed back while it is being copied. */
        private static void Consume(WorldModelBuilder builder)
        {
            if (!builder.TryConsume()) throw new InvalidOperationException("WorldModelBuilder: already published or abandoned");
        }

        public void Detach(bool returnToTruth) => _store.Detach(this, returnToTruth);

        public void Dispose() => _store.Dispose(this);

        private void Own(WorldModelBuilder builder)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (!ReferenceEquals(builder.Publisher, this)) throw new ArgumentException("the builder came from another publisher", nameof(builder));
            if (!builder.IsLive) throw new InvalidOperationException("WorldModelBuilder: already published or abandoned");
        }
    }
}
