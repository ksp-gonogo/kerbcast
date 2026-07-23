using kOS;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Utilities;

namespace Kerbcast.Kos
{
    /* Per-camera kerboscript structure. Reads flow through
       control.ViewOf(id) on every getter so slewed FOV/pan values stay
       live; writes route through the seam, which already gates on
       capabilities and clamps to bounds. Suffix registration is Unity-free, so
       the whole surface loads in the headless test; AIM/STOPAIM only touch the
       owner's update observer (and BORESIGHT/POSITION a kOS Vector) when
       actually invoked. Every getter tolerates a vanished camera
       (ViewOf == null) by returning a benign default rather than throwing. */
    [KOSNomenclature("KerbcastCamera")]
    public class KerbcastCameraStruct : Structure
    {
        readonly SharedObjects shared;
        readonly uint id;
        readonly IKerbcastControl control;
        /* Owning addon: routes AIM tracking through its per-CPU update observer.
           May be null in unit probes that never touch AIM. */
        readonly KerbcastAddon owner;

        /* The kerboscript delegate currently supplying AIM targets, or null
           when cleared. Held so the getter can echo it back. */
        UserDelegate currentDel;

        public KerbcastCameraStruct(SharedObjects shared, uint id, IKerbcastControl control, KerbcastAddon owner)
        {
            this.shared = shared;
            this.id = id;
            this.control = control;
            this.owner = owner;

            AddSuffix("UID", new Suffix<StringValue>(() => new StringValue(id.ToString())));
            AddSuffix("NAME", new Suffix<StringValue>(() => new StringValue(V()?.CameraName ?? "")));

            AddSuffix("SUPPORTSZOOM", new Suffix<BooleanValue>(() => V()?.SupportsZoom ?? false));
            AddSuffix("SUPPORTSPAN", new Suffix<BooleanValue>(() => V()?.SupportsPan ?? false));

            AddSuffix("FOV", new SetSuffix<ScalarValue>(
                () => V()?.Fov ?? 0f,
                value => control.SetFov(id, (float)value.GetDoubleValue())));
            AddSuffix("FOVMIN", new Suffix<ScalarValue>(() => V()?.FovMin ?? 0f));
            AddSuffix("FOVMAX", new Suffix<ScalarValue>(() => V()?.FovMax ?? 0f));

            AddSuffix("PANYAW", new SetSuffix<ScalarValue>(
                () => V()?.PanYaw ?? 0f,
                value => { var v = V(); if (v != null) control.SetPan(id, (float)value.GetDoubleValue(), v.PanPitch); }));
            AddSuffix("PANPITCH", new SetSuffix<ScalarValue>(
                () => V()?.PanPitch ?? 0f,
                value => { var v = V(); if (v != null) control.SetPan(id, v.PanYaw, (float)value.GetDoubleValue()); }));
            AddSuffix("PANYAWMIN", new Suffix<ScalarValue>(() => V()?.PanYawMin ?? 0f));
            AddSuffix("PANYAWMAX", new Suffix<ScalarValue>(() => V()?.PanYawMax ?? 0f));
            AddSuffix("PANPITCHMIN", new Suffix<ScalarValue>(() => V()?.PanPitchMin ?? 0f));
            AddSuffix("PANPITCHMAX", new Suffix<ScalarValue>(() => V()?.PanPitchMax ?? 0f));

            /* BORESIGHT: world-space unit forward of the stream (after slew).
               POSITION: lens offset in the same frame as TARGET:POSITION. Steer
               the vessel so BORESIGHT points at (TARGET:POSITION - POSITION) to
               hold a target the mount alone can't reach. */
            AddSuffix("BORESIGHT", new Suffix<kOS.Suffixed.Vector>(() =>
            {
                var v = V();
                return v == null ? new kOS.Suffixed.Vector(0, 0, 0)
                                 : new kOS.Suffixed.Vector(v.BoresightX, v.BoresightY, v.BoresightZ);
            }));
            AddSuffix("POSITION", new Suffix<kOS.Suffixed.Vector>(() =>
            {
                var v = V();
                return v == null ? new kOS.Suffixed.Vector(0, 0, 0)
                                 : new kOS.Suffixed.Vector(v.PositionX, v.PositionY, v.PositionZ);
            }));

            /* AIM: accept a Vector (aim once at that point and hold) OR a
               delegate/callback returning a Vector (track continuously, the pump
               re-evaluates it each frame). Typed as Structure, not UserDelegate,
               so a plain Vector reaches the setter; a raw Scalar can't be assigned
               to a suffix at all, so clearing is `cam:STOPAIM()` rather than an
               assignment. No-op on cameras that can't pan. */
            AddSuffix("AIM", new SetSuffix<Structure>(
                () => CallbackGetter(currentDel),
                v =>
                {
                    if (!(V()?.SupportsPan ?? false)) return;
                    if (v is UserDelegate del && !(del is NoDelegate))
                    {
                        currentDel = del;
                        owner.SetAim(id, del);        // continuous tracking
                    }
                    else if (v is kOS.Suffixed.Vector vec)
                    {
                        currentDel = null;
                        owner.SetAim(id, null);       // drop any tracking source
                        control.AimAt(id, vec.X, vec.Y, vec.Z); // aim once, then hold
                    }
                }));

            /* STOPAIM: clear any AIM source; the mount holds its last angle. The
               clear idiom (a Scalar can't be assigned to a suffix, so this is a
               method rather than `SET AIM TO 0`). */
            AddSuffix("STOPAIM", new NoArgsVoidSuffix(() =>
            {
                currentDel = null;
                owner.SetAim(id, null);
            }));

            /* LOOKAT: same as `SET cam:AIM TO <vector>` in function form, returning
               whether the seam accepted it (false when the camera can't pan). */
            AddSuffix("LOOKAT", new OneArgsSuffix<BooleanValue, kOS.Suffixed.Vector>(
                pos => control.AimAt(id, pos.X, pos.Y, pos.Z) ? BooleanValue.True : BooleanValue.False));

            /* TRACK: high-level auto-track mode, "none" | "vessel" | "target".
               SET is synchronous (applies at once, no round-trip) and only
               honoured on a pan+zoom camera; GET returns the current mode. The
               state is linked with the browser: a mode set here reflects in
               every browser, and a browser-set mode reads back here. Distinct
               from the low-level AIM/LOOKAT (a bespoke per-frame vector): if
               both are set on one camera the mode wins frame-by-frame. */
            AddSuffix("TRACK", new SetSuffix<StringValue>(
                () => new StringValue(TrackModeToString(control.GetTrackMode(id))),
                value => control.SetTrackMode(id, TrackModeFromString(value.ToString()))));
        }

        /* mode int (0/1/2) <-> kerboscript string. Unrecognised strings map to
           none; "active"/"activevessel" are accepted aliases for "vessel". */
        static string TrackModeToString(int mode)
        {
            switch (mode)
            {
                case 1: return "vessel";
                case 2: return "target";
                default: return "none";
            }
        }

        static int TrackModeFromString(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "vessel":
                case "active":
                case "activevessel": return 1;
                case "target": return 2;
                default: return 0;
            }
        }

        /* Mirror kOS Widget: hand kerboscript a NoDelegate instead of a raw
           null so `PRINT cam:AIM` is well-typed. */
        static UserDelegate CallbackGetter(UserDelegate d) => d ?? NoDelegate.Instance;

        /* Fresh view each read: reflects live slewed state, and returns null
           once the camera is gone (getters above degrade gracefully). */
        KosCameraView V() => control.ViewOf(id);
    }
}
