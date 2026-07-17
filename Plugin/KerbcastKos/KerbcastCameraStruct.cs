using kOS;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Utilities;

namespace Kerbcast.Kos
{
    /* Per-camera kerboscript structure. Reads flow through
       control.ViewOf(id) on every getter so slewed FOV/pan values stay
       live; writes route through the seam, which already gates on
       capabilities and clamps to bounds. Deliberately Unity-free: PART/AIM
       (Task 6) are not registered here, so the whole suffix surface loads in
       the headless test. Every getter tolerates a vanished camera
       (ViewOf == null) by returning a benign default rather than throwing. */
    [KOSNomenclature("KerbcastCamera")]
    public class KerbcastCameraStruct : Structure
    {
        readonly SharedObjects shared;
        readonly uint id;
        readonly IKerbcastControl control;

        /* The kerboscript delegate currently supplying AIM targets, or null
           when cleared. Held so the getter can echo it back. */
        UserDelegate currentDel;

        public KerbcastCameraStruct(SharedObjects shared, uint id, IKerbcastControl control)
        {
            this.shared = shared;
            this.id = id;
            this.control = control;

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

            /* AIM: a callback returning the aim target as a Vector, re-evaluated
               each frame by the pump. Set 0 (NoDelegate) to clear. No-op set on
               cameras that can't pan, mirroring the PAN write gate. */
            AddSuffix("AIM", new SetSuffix<UserDelegate>(
                () => CallbackGetter(currentDel),
                v =>
                {
                    if (!(V()?.SupportsPan ?? false)) return;
                    currentDel = CallbackSetter(v);
                    KerbcastAddon.SetAim(id, currentDel);
                }));

            /* LOOKAT: a one-shot aim at a fixed target vector. Returns whether
               the seam accepted it (false when the camera can't pan / is gone). */
            AddSuffix("LOOKAT", new OneArgsSuffix<BooleanValue, kOS.Suffixed.Vector>(
                pos => control.AimAt(id, pos.X, pos.Y, pos.Z) ? BooleanValue.True : BooleanValue.False));
        }

        /* Mirror kOS Widget: hand kerboscript a NoDelegate instead of a raw
           null, and treat an incoming NoDelegate as "clear". */
        static UserDelegate CallbackGetter(UserDelegate d) => d ?? NoDelegate.Instance;
        static UserDelegate CallbackSetter(UserDelegate d) => d is NoDelegate ? null : d;

        /* Fresh view each read: reflects live slewed state, and returns null
           once the camera is gone (getters above degrade gracefully). */
        KosCameraView V() => control.ViewOf(id);
    }
}
