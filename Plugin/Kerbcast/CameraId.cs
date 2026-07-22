namespace Kerbcast
{
    /* Unity-free camera wire-id helpers. Kept out of Unity-bound code so the
       pure id math is unit-testable and reusable by camera-source providers. */
    public static class CameraId
    {
        /* Stable wire id for a camera module. Module 0 keeps the bare part
           flightID (back-compat); modules 1+ get a Knuth-hash mix of id +
           index + name so a multi-camera part yields distinct, stable ids
           that do not clash with KSP's sequential part flightIDs. */
        public static uint Synthetic(uint baseId, int moduleIdx, string cameraName)
        {
            if (moduleIdx == 0) return baseId;
            unchecked
            {
                uint h = baseId;
                h = h * 2654435761u + (uint)moduleIdx;
                if (!string.IsNullOrEmpty(cameraName))
                {
                    foreach (var ch in cameraName) h = h * 2654435761u + ch;
                }
                return h;
            }
        }
    }
}
