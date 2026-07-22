using System.Collections.Generic;

namespace Kerbcast
{
    /* A source of cameras on a vessel. Each provider builds its own ICamera
       instances (with the settings/ring context it was constructed with);
       KerbcastCore enumerates every provider and adds the ones whose FlightId
       it does not already track. Providers skip existing FlightIds so no
       camera (and its ring) is constructed only to be discarded. */
    internal interface ICameraSourceProvider
    {
        IEnumerable<ICamera> Enumerate(Vessel vessel, IReadOnlyCollection<uint> existingFlightIds);
    }
}
