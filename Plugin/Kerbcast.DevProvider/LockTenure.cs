using System;
using Kerbcast.Api;

namespace Kerbcast.DevProvider
{
    /*
     * Whether the provider still holds the command lock it took. Once the
     * lock goes (a LockLost for its owner, or a publish refused because it
     * no longer holds it) the provider stops publishing and aiming until it
     * takes the lock again at the next scene start. Without the lock any
     * caller may aim, so a provider that kept aiming would undo every other
     * client's aim.
     */
    public sealed class LockTenure
    {
        private readonly string _owner;

        public LockTenure(string owner) => _owner = owner;

        public bool Holding { get; private set; }

        public void Took() => Holding = true;

        /* True, once, when `lost` ends this owner's tenure. */
        public bool Lost(LockLostEvent lost)
        {
            if (!Holding || lost == null || !string.Equals(lost.Owner, _owner, StringComparison.Ordinal)) return false;
            Holding = false;
            return true;
        }

        /* True, once, when a publish result says the lock went with no LockLost heard. */
        public bool Refused(PublishResult result)
        {
            if (!Holding || result != PublishResult.NotLockHolder) return false;
            Holding = false;
            return true;
        }
    }
}
