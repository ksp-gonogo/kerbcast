using System.Collections.Generic;

namespace Kerbcast
{
    /* One camera a provider found on a vessel: the mount plus what
       KerbcastCore needs to construct/track a KerbcastCamera. */
    public struct MountDescriptor
    {
        public ICameraMountSource Source;
        public uint FlightId;
        public string PartName; // key for KerbcastSettings + PartCapabilities

        public MountDescriptor(ICameraMountSource source, uint flightId, string partName)
        {
            Source = source;
            FlightId = flightId;
            PartName = partName;
        }
    }

    /* A source of cameras on a vessel. KerbcastCore enumerates every provider
       uniformly; each owns its own discovery. */
    public interface ICameraSourceProvider
    {
        IEnumerable<MountDescriptor> Enumerate(Vessel vessel);
    }
}
