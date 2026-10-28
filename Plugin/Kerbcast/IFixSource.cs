using System.Collections.Generic;

namespace Kerbcast
{
    /* One answer from a fix source about one craft at one UT. */
    internal struct CraftFix
    {
        public CraftFixState State;
        public uint PersistentId;
        public string Name;
        /* KSP's VesselType name ("Ship", "Probe", "Debris"); empty when the source has none. */
        public string Kind;
        /* Tracking Station discovery, for the career eligibility rules; null
           when the source does not track discovery, which reads as owned. */
        public DiscoveryLevels? Discovery;
        /* World position and its body; set only when Positioned. */
        public DVec3 World;
        public CelestialBody Body;
        /* The craft's size and how far it may be from where the source puts
           it, in metres; NaN when the source gives none. A Listed craft may
           carry them too. */
        public double RadiusM;
        public double UncertaintyM;
        /* Why a Refused craft is refused: an opaque code (CommandCameraMath.RefusalStale
           for a record out of date) and optional words to show. */
        public string RefusalCode;
        public string RefusalText;

        public bool Positioned => State == CraftFixState.Positioned;

        public static CraftFix Unknown(uint persistentId) => new CraftFix
        {
            State = CraftFixState.Unknown,
            PersistentId = persistentId,
            Name = "",
            Kind = "",
            RadiusM = double.NaN,
            UncertaintyM = double.NaN,
        };

        public static CraftFix Refused(uint persistentId, string code, string text)
        {
            var fix = Unknown(persistentId);
            fix.State = CraftFixState.Refused;
            fix.RefusalCode = code;
            fix.RefusalText = text;
            return fix;
        }
    }

    /*
     * The one way command camera code asks what is known about a craft:
     * where it is, how it moves, how big it is for framing, whether it
     * exists, which craft a feed's request resolves to and which craft the
     * targets list offers. Each feed has its own source, which resolves the
     * feed's SubjectSelection; questions no feed asks go to FixSources.For.
     *
     * What the picture shows (where the craft is drawn, its light and
     * occlusion as drawn, clip planning) never comes from here: that is
     * DrawnSubject. A source may answer Unknown, Refused or Listed for any
     * craft, the subject included, and every caller treats those as no fix.
     */
    internal interface IFixSource
    {
        /* The subject's known position is the drawn one (both read KSP), so a
           craft aim's readings stand for the drawn craft, as in 1.11. */
        bool KnowsWhatIsDrawn { get; }
        /* A Listed craft takes the career rules with no range, as 1.11 listed
           a craft KSP could not place, rather than reading notPositioned. */
        bool ListsUnplacedByCareerRules { get; }
        /* KSP's merges move the feed's request to the surviving craft. Under a
           world model identity is the model's, which says so with its aliases. */
        bool IdentityIsKsp { get; }

        /* The craft the feed's request resolved to; 0 when there is none. */
        uint SubjectPersistentId { get; }
        string SubjectName { get; }

        /* The subject at `ut`. */
        CraftFix Subject(double ut);
        /* How the subject moves on from `ut`, for the forecast, and the UT that
           motion holds until (infinity for truth). False when the source
           cannot carry it forward. */
        bool SubjectMotion(double ut, out BodyMotion motion, out double validUntilUt);
        /* The radius the subject is framed by, in metres; NaN when the source has no size for it. */
        double SubjectRadiusM { get; }
        /* A sphere the subject fits in that frames it closer than the radius does: its centre's offset from the subject's position, world axes, and its radius. False when the source has none. */
        bool SubjectShape(out DVec3 centreOffset, out double radiusM);

        /* Whether the source knows a craft with this persistentId. */
        bool Knows(uint persistentId);
        /* Any craft by persistentId at `ut`. */
        CraftFix Find(uint persistentId, double ut);
        /* Every craft the source would list at `ut`, except `exceptPersistentId`
           (the lens's own craft). With `place` false no position is computed,
           so no row carries one, whatever its state. */
        void List(double ut, uint exceptPersistentId, bool place, List<CraftFix> into);
    }

    internal static class FixSources
    {
        /* The source for questions about craft that no feed asks, such as the
           targets list from `site` and aim checks. */
        public static IFixSource For(CommandCameraSite site) => ForKey(site?.NodeName);

        /* The same by site key: a created camera that is not built yet has no
           site, only the key its vantage will have (null when that is not
           known yet, which under a world model knows no craft). Truth unless
           the lock holder has attached a world model. */
        public static IFixSource ForKey(string siteKey)
        {
            var store = KerbcastCore.Instance?.CommandAuthority.WorldModel;
            if (store == null || store.Capture(UnityEngine.Time.frameCount, Planetarium.GetUniversalTime()).Mode == WorldModelMode.Truth)
                return TruthFixSource.Scope;
            return ProviderFixSource.ForKey(store, siteKey);
        }
    }

    /* A feed's source: truth, or the holder's world model once it attaches, chosen for each frame. */
    internal sealed class FeedFixSource : IFixSource
    {
        private readonly IFixSource _truth;
        private readonly IFixSource _model;
        private readonly WorldModelStore _store;

        public FeedFixSource(IFixSource truth, IFixSource model, WorldModelStore store)
        {
            _truth = truth;
            _model = model;
            _store = store;
        }

        private IFixSource Now =>
            _store.Capture(UnityEngine.Time.frameCount, Planetarium.GetUniversalTime()).Mode == WorldModelMode.Truth ? _truth : _model;

        public bool KnowsWhatIsDrawn => Now.KnowsWhatIsDrawn;
        public bool ListsUnplacedByCareerRules => Now.ListsUnplacedByCareerRules;
        public bool IdentityIsKsp => Now.IdentityIsKsp;
        public uint SubjectPersistentId => Now.SubjectPersistentId;
        public string SubjectName => Now.SubjectName;
        public CraftFix Subject(double ut) => Now.Subject(ut);
        public bool SubjectMotion(double ut, out BodyMotion motion, out double validUntilUt) =>
            Now.SubjectMotion(ut, out motion, out validUntilUt);
        public double SubjectRadiusM => Now.SubjectRadiusM;
        public bool SubjectShape(out DVec3 centreOffset, out double radiusM) => Now.SubjectShape(out centreOffset, out radiusM);
        public bool Knows(uint persistentId) => Now.Knows(persistentId);
        public CraftFix Find(uint persistentId, double ut) => Now.Find(persistentId, ut);
        public void List(double ut, uint exceptPersistentId, bool place, List<CraftFix> into) => Now.List(ut, exceptPersistentId, place, into);
    }
}
