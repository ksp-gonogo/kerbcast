using kOS.Safe.Encapsulation;
using kOS.Safe.Execution;

namespace Kerbcast.Kos
{
    /* Real IAimLease over a kOS UserDelegate. Wraps the delegate plus the
       TriggerInfo for its most recent one-shot evaluation. The poll/re-arm
       cadence mirrors kOS Label.ScheduleTextUpdate: re-trigger with
       InterruptPriority.CallbackOnce each time the previous call finishes. The
       callback is expected to RETURN a kOS Vector (the aim target). */
    internal sealed class UserDelegateAimLease : IAimLease
    {
        readonly UserDelegate del;
        TriggerInfo trigger;

        public UserDelegateAimLease(UserDelegate del)
        {
            this.del = del;
        }

        /* No trigger yet means nothing is pending, so it is safe to arm. */
        public bool Finished => trigger == null || trigger.CallbackFinished;

        public bool TryResult(out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (trigger != null && trigger.ReturnValue is kOS.Suffixed.Vector v)
            {
                x = v.X;
                y = v.Y;
                z = v.Z;
                return true;
            }
            return false;
        }

        public void Rearm()
        {
            trigger = del.TriggerOnFutureUpdate(InterruptPriority.CallbackOnce);
        }
    }
}
