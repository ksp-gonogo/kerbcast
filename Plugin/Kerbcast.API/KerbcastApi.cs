using System;
using System.Threading;

namespace Kerbcast.Api
{
    /**
     * <summary>
     * The entry point, safe on any thread. A mod checks for Kerbcast with
     * <c>KerbcastApi.Instance != null</c> (after finding "KerbcastAPI" in
     * AssemblyLoader.loadedAssemblies, for a soft dependency) and, when built against a newer minor,
     * calls <see cref="Supports"/> before using a newer feature.
     * </summary>
     */
    public static class KerbcastApi
    {
        static IKerbcast instance;
        static Func<ApiFeature, bool> supported = Shipped;

        /**
         * <summary>
         * Kerbcast in this scene, null when it is absent or not running here (outside flight and the
         * Space Center, or when it refused to start).
         * </summary>
         */
        public static IKerbcast Instance => Volatile.Read(ref instance);

        /** <summary>The API minor this Kerbcast implements: API version 1.ApiMinor.</summary> */
        public static int ApiMinor => ApiVersion.Minor;

        /** <summary>Whether this Kerbcast implements a feature. A member of an unsupported feature exists but throws NotSupportedException.</summary> */
        public static bool Supports(ApiFeature feature) => Volatile.Read(ref supported)(feature);

        /**
         * <summary>
         * Merges, splits, destructions and recoveries of craft, raised on the main thread. A subscription
         * lasts for the whole process, across scenes and whether or not <see cref="Instance"/> is set;
         * events start once Kerbcast has first started and continue in every scene. One subscriber's
         * exception is logged and does not stop the others.
         * </summary>
         */
        public static event Action<IdentityEvent> Identity;

        /**
         * <summary>
         * The command lock left its holder without a <see cref="ICommand.ReleaseLock"/>: the scene is ending,
         * or the player released it in game. Raised on the main thread, after which the lock is free. At a
         * scene's end the dying scene refuses every take, so take the lock again in the next scene, not from
         * the handler. Like <see cref="Identity"/>, a subscription lasts for the whole process and one
         * subscriber's exception does not stop the others.
         * </summary>
         */
        public static event Action<LockLostEvent> LockLost;

        static bool Shipped(ApiFeature feature)
        {
            switch (feature)
            {
                case ApiFeature.CameraControl:
                case ApiFeature.CraftIdentity:
                case ApiFeature.Throttle:
                case ApiFeature.CommandFeeds:
                case ApiFeature.CommandLock:
                case ApiFeature.CommandAim:
                case ApiFeature.WorldModel:
                case ApiFeature.LightSpeed:
                    return true;
                default:
                    return false;
            }
        }

        internal static void Register(IKerbcast kerbcast) => Volatile.Write(ref instance, kerbcast);

        /* Clears Instance only if it is still `kerbcast`, so a late teardown
           cannot remove its successor. */
        internal static void Unregister(IKerbcast kerbcast) => Interlocked.CompareExchange(ref instance, null, kerbcast);

        /* Null restores the shipped set. */
        internal static void OverrideFeatures(Func<ApiFeature, bool> features) =>
            Volatile.Write(ref supported, features ?? Shipped);

        /* Each handler runs on its own, so a throwing subscriber reaches
           `onError` (rethrown when null) instead of stopping the rest. */
        internal static void RaiseIdentity(IdentityEvent change, Action<Exception> onError) => Raise(Identity, change, onError);

        internal static void RaiseLockLost(LockLostEvent lost, Action<Exception> onError) => Raise(LockLost, lost, onError);

        static void Raise<T>(Action<T> handlers, T e, Action<Exception> onError) where T : class
        {
            if (e == null || handlers == null) return;
            foreach (Action<T> h in handlers.GetInvocationList())
            {
                try { h(e); }
                catch (Exception ex) when (onError != null) { onError(ex); }
            }
        }
    }

    /* Enforces "main thread only" for the members that promise it. */
    internal sealed class MainThread
    {
        readonly int threadId;

        /* Takes the calling thread as the main one. */
        public MainThread()
        {
            threadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool IsCurrent => Thread.CurrentThread.ManagedThreadId == threadId;

        public void Check(string member)
        {
            if (!IsCurrent)
                throw new InvalidOperationException($"Kerbcast.Api: {member} must be called on the main thread");
        }

        public static NotSupportedException NotYet(string member) =>
            new NotSupportedException($"Kerbcast.Api: {member} is not supported by this Kerbcast; check KerbcastApi.Supports first");
    }
}
