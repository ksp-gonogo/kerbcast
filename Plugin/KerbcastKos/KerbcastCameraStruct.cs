using kOS;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Utilities;

namespace Kerbcast.Kos
{
    /* Per-camera kerboscript structure. Minimal for Task 4: identity only.
       Task 5 fleshes out the read suffixes and FOV/pan sets against the
       control seam; the ctor already carries what those need. */
    [KOSNomenclature("KerbcastCamera")]
    public class KerbcastCameraStruct : Structure
    {
        readonly SharedObjects shared;
        readonly uint id;
        readonly IKerbcastControl control;

        public KerbcastCameraStruct(SharedObjects shared, uint id, IKerbcastControl control)
        {
            this.shared = shared;
            this.id = id;
            this.control = control;
            AddSuffix("UID", new Suffix<StringValue>(() => new StringValue(id.ToString())));
        }
    }
}
