namespace Kerbcast
{
    /* The camera surface KerbcastCore drives uniformly across sources
       (Hullcam part cameras, kerbal face cameras). Rich per-camera
       diagnostics used only by the status-file writer stay on
       KerbcastCamera and are reached via a type check there. */
    internal interface ICamera
    {
        uint FlightId { get; }
        Vessel Vessel { get; }
        bool IsAlive { get; }
        bool Subscribed { get; }
        int RefreshFailureStreak { get; set; }
        bool OwnsPart(Part part);
        void MarkFxDirty();
        void Refresh(bool mayIssueReadback);
        void ApplyAutoShed(int level);
        void WriteInfoManifest();
        void Dispose();
        void DisposeDestroyed();
    }
}
