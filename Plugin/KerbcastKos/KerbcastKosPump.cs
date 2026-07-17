using System;
using UnityEngine;

namespace Kerbcast.Kos
{
    /* Once-per-frame driver for AIM sources, on the KSP main thread (the only
       place a kerbcast control call may happen). Created programmatically and
       DontDestroyOnLoad'd by KerbcastAddon.EnsurePump; deliberately NOT
       [KSPAddon]-annotated, mirroring gonogo's KosMainThreadDispatcherAddon:
       this is an optional bridge with no compile-time hook for KSP to
       auto-instantiate it. */
    internal sealed class KerbcastKosPump : MonoBehaviour
    {
        public AimSourceRegistry Registry;
        public IKerbcastControl Control;

        Action<uint, double, double, double> apply;

        void Awake()
        {
            /* Cached so Tick doesn't allocate a delegate every frame; reads
               Control at call time, so wiring order doesn't matter. */
            apply = (id, x, y, z) => Control.AimAt(id, x, y, z);
        }

        void Update()
        {
            Registry?.Tick(apply);
        }
    }
}
