namespace Kerbcast
{
    /* Unity-free camera wire-id helpers. Kept out of Unity-bound code so the
       pure id math is unit-testable and reusable by camera-source providers. */
    public static class CameraId
    {
        /* Bit 31 is the namespace tag: part ids clear it, kerbal ids set it, so
           the two id spaces are disjoint BY CONSTRUCTION. This does NOT rely on
           KSP flightIDs being "small" — they aren't:
           ShipConstruction.GetUniqueFlightID returns (uint)Guid.GetHashCode(), a
           full 32-bit value that sets bit 31 ~half the time. Masking part ids to
           31 bits is what guarantees the split; without it a multi-camera part's
           hash (or a raw flightID) could land on a kerbal id. */
        private const uint KerbalTag = 0x80000000u;
        private const uint PartMask = 0x7FFFFFFFu;

        /*
         * Saved camera networks take the top 65536 ids of the bit-31 space,
         * 0xFFFF0000 to 0xFFFFFFFE, one per slot the save hands out, so a
         * network keeps its id across scenes, reloads and sidecar restarts.
         * Part ids clear bit 31 and never reach it. A kerbal or command camera
         * hash that lands in it has bit 16 cleared (Unreserved), which moves it
         * just below; only those 1 in 32768 hashes change, and nothing else can
         * ever produce an id in the range.
         */
        public const uint NetworkRangeStart = 0xFFFF0000u;
        public const int NetworkSlots = 0xFFFF;

        /* The wire id of saved network slot `slot`, 0 to NetworkSlots - 1. */
        public static uint NetworkWireId(int slot)
        {
            if (slot < 0 || slot >= NetworkSlots) throw new System.ArgumentOutOfRangeException(nameof(slot));
            return NetworkRangeStart | (uint)slot;
        }

        /* True for an id in the saved network range. */
        public static bool IsNetworkId(uint flightId) => (flightId & NetworkRangeStart) == NetworkRangeStart;

        private static uint Unreserved(uint id) => IsNetworkId(id) ? id & ~0x00010000u : id;

        /* Stable wire id for a camera module, in the PART namespace (bit 31 clear).
           Module 0 uses the part flightID; modules 1+ get a Knuth-hash mix of id +
           index + name so a multi-camera part yields distinct, stable ids. Masked
           to 31 bits so a part id can never collide with a kerbal id. */
        public static uint Synthetic(uint baseId, int moduleIdx, string cameraName)
        {
            if (moduleIdx == 0) return baseId & PartMask;
            unchecked
            {
                uint h = baseId;
                h = h * 2654435761u + (uint)moduleIdx;
                if (!string.IsNullOrEmpty(cameraName))
                {
                    foreach (var ch in cameraName) h = h * 2654435761u + ch;
                }
                return h & PartMask;
            }
        }

        /* Wire id for a kerbal camera, in the KERBAL namespace (bit 31 set).
           Derived from a stable hash of the kerbal's roster name, which is unique
           and stable across seat<->EVA — unlike persistentID, which KSP reassigns
           on EVA (GetUniquepersistentId). So the SAME kerbal maps to the SAME id
           whether seated or on EVA, giving one continuous feed. Uses the same
           deterministic Knuth mix as Synthetic, NOT String.GetHashCode (which is
           not stable across Mono / runs). */
        public static uint KerbalWireId(string kerbalName)
        {
            return Unreserved((NameHash(kerbalName) & PartMask) | KerbalTag);
        }

        /* Wire id for a command camera. Shares the bit-31 space
           with kerbal ids rather than carving out a new tag, because every 31-bit
           value is already reachable by both existing namespaces: a third tag
           would mean re-hashing kerbal ids and breaking stored ones. The "ground:"
           prefix (SiteHashKey) keeps a site apart from a kerbal of the same name,
           so the two can only meet on a 1-in-2^31 hash collision, which the
           provider logs. The manifest's kind, not the id, says which it is. */
        public static uint CommandCameraWireId(string stationNodeName)
        {
            return Unreserved((NameHash(SiteHashKey(stationNodeName)) & PartMask) | KerbalTag);
        }

        /* The key a created command camera's wire id hashes, in place of a
           station node name. Never a CommNet nodeName, so a created camera
           can not take the default camera's id, and a fresh serial per
           creation keeps created cameras apart from each other. */
        public static string CreatedCommandCameraKey(int serial)
        {
            return "kerbcast:command:" + serial;
        }

        /* What a site's wire id hashes: "ground:<nodeName>". Changing it
           would renumber every command camera. */
        internal static string SiteHashKey(string siteKey)
        {
            return "ground:" + (siteKey ?? string.Empty);
        }

        [System.Obsolete("Command feeds are named by SiteKey and SiteKind; this is only the wire id's hash input")]
        public static string CommandCentreId(string stationNodeName) => SiteHashKey(stationNodeName);

        /* True when a wire-id is in the KERBAL namespace (bit 31 set), i.e. a
           kerbal camera rather than a part camera. Lets Unity-free code tell the
           two id spaces apart without duplicating the tag constant. Command
           camera ids also set bit 31, so this reads "not a part camera". */
        public static bool IsKerbalId(uint flightId)
        {
            return (flightId & KerbalTag) != 0;
        }

        /* Human label for a kerbal camera. Prefers the display name, falling back
           to the roster name when displayName is empty. A kerbal sourced fresh
           from the EVA part can have an empty displayName (the seated path
           populates it, the EVA-construction path may not), which would leave the
           EVA POV camera unlabelled; the roster name is always populated and is
           the same lineage the wire-id derives from, so the label and the FlightId
           always agree. Returns "" (never null) when both are absent. */
        public static string KerbalCameraName(string displayName, string rosterName)
        {
            if (!string.IsNullOrEmpty(displayName)) return displayName;
            return string.IsNullOrEmpty(rosterName) ? string.Empty : rosterName;
        }

        private static uint NameHash(string name)
        {
            unchecked
            {
                uint h = 2166136261u; // fixed non-zero seed
                if (!string.IsNullOrEmpty(name))
                    foreach (var ch in name) h = h * 2654435761u + ch;
                return h;
            }
        }
    }
}
