using UnityEngine;

namespace Kerbcast
{
    /* Hullcam pan capability. Limits come from the part's PartCapabilities
       entry; Steer maps a target yaw/pitch in the standard (visual) frame onto
       the part's yaw/pitch joints, absorbing this camera's YawInvert and joint
       mapping so core never sees the flip. Joint resolution + rest poses are
       captured once, at the mesh's rest pose, when the mount source is built. */
    public sealed class HullcamPanCapability : IPanCapability
    {
        private readonly PanCapability _caps;

        /* Mesh joints driven by pan slew. Null when the caps table leaves the
           transform name empty (no animated joint in the mesh). */
        private readonly Transform _yawTransform;
        private readonly Quaternion _yawRestRot;
        private readonly Vector3 _yawRestLocalPos;
        private readonly Transform _pitchTransform;
        private readonly Quaternion _pitchRestRot;
        private readonly Transform _yawBaseTransform;
        private readonly Quaternion _yawBaseRestRot;

        private HullcamPanCapability(
            PanCapability caps,
            Transform yawTransform, Quaternion yawRestRot, Vector3 yawRestLocalPos,
            Transform pitchTransform, Quaternion pitchRestRot,
            Transform yawBaseTransform, Quaternion yawBaseRestRot)
        {
            _caps = caps;
            _yawTransform = yawTransform;
            _yawRestRot = yawRestRot;
            _yawRestLocalPos = yawRestLocalPos;
            _pitchTransform = pitchTransform;
            _pitchRestRot = pitchRestRot;
            _yawBaseTransform = yawBaseTransform;
            _yawBaseRestRot = yawBaseRestRot;
        }

        /* Null when the part is not steerable (no yaw/pitch travel), matching
           the old SupportsPan test exactly. Otherwise resolves the mesh joints
           off `mount` at rest pose and captures their rest transforms — the same
           reads SetCameras used to make inline (surface-map sites 616/842/853),
           and at the same instant (mount construction, before any pan). */
        internal static HullcamPanCapability TryCreate(ICameraMountSource mount, PanCapability caps)
        {
            if (!caps.SupportsPan) return null;

            Transform yaw = null;
            Quaternion yawRest = Quaternion.identity;
            Vector3 yawRestPos = Vector3.zero;
            if (!string.IsNullOrEmpty(caps.YawTransformName))
            {
                yaw = mount.FindModelTransform(caps.YawTransformName);
                if (yaw != null)
                {
                    yawRest = yaw.localRotation;
                    yawRestPos = yaw.localPosition;
                    Debug.Log($"[Kerbcast] pan '{mount.PartName}' yaw transform '{caps.YawTransformName}' found, restRot={yawRest} restPos={yawRestPos}");
                }
                else
                    Debug.LogWarning($"[Kerbcast] pan '{mount.PartName}' yaw transform '{caps.YawTransformName}' not found");
            }

            Transform pitch = null;
            Quaternion pitchRest = Quaternion.identity;
            if (!string.IsNullOrEmpty(caps.PitchTransformName))
            {
                pitch = mount.FindModelTransform(caps.PitchTransformName);
                if (pitch != null)
                    pitchRest = pitch.localRotation;
                else
                    Debug.LogWarning($"[Kerbcast] pan '{mount.PartName}' pitch transform '{caps.PitchTransformName}' not found");
            }

            Transform yawBase = null;
            Quaternion yawBaseRest = Quaternion.identity;
            if (!string.IsNullOrEmpty(caps.YawBaseTransformName))
            {
                yawBase = mount.FindModelTransform(caps.YawBaseTransformName);
                if (yawBase != null)
                {
                    yawBaseRest = yawBase.localRotation;
                    Debug.Log($"[Kerbcast] pan '{mount.PartName}' yaw-base transform '{caps.YawBaseTransformName}' found");
                }
                else
                    Debug.LogWarning($"[Kerbcast] pan '{mount.PartName}' yaw-base transform '{caps.YawBaseTransformName}' not found");
            }

            return new HullcamPanCapability(
                caps, yaw, yawRest, yawRestPos, pitch, pitchRest, yawBase, yawBaseRest);
        }

        public float YawMin => _caps.YawMin;
        public float YawMax => _caps.YawMax;
        public float PitchMin => _caps.PitchMin;
        public float PitchMax => _caps.PitchMax;
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }

        /* Config + joint data the standardised (core-side) steer needs but that
           isn't on the IPanCapability contract: the slew/rate speeds for the
           rate integration + slew, and the yaw joint the near camera parents to
           / the aim solve reads. YawInvert is read here by AimAt so its solve
           and this Steer stay on the same sign convention. */
        public float SlewDegPerSec => _caps.SlewDegPerSec;
        public float PanRateDegPerSec => _caps.PanRateDegPerSec;
        public bool YawInvert => _caps.YawInvert;
        public Vector3? CameraMountLocal => _caps.CameraMountLocal;
        public Transform YawJoint => _yawTransform;
        public Quaternion YawJointRestRot => _yawRestRot;

        /* Apply a target yaw/pitch (standard visual frame: +yaw = pan right,
           +pitch = up) to the mesh joints. Relocated verbatim from
           KerbcastCamera's per-frame pan drive: a compound single-joint head
           gets one Euler; a yaw-only head honours YawInvert; the pitch pivot and
           the co-rotating base reproduce the old behaviour. The near camera is
           core's, so it is untouched here. */
        public void Steer(float yaw, float pitch)
        {
            Yaw = yaw;
            Pitch = pitch;

            // When yaw and pitch share a single joint (e.g. launchcam's
            // hc_launchcam), applying them separately would fight — the second
            // assignment overwrites the first. Apply a single compound Euler so
            // both axes land in one rotation.
            bool compoundJoint = _yawTransform != null && _pitchTransform != null
                && ReferenceEquals(_yawTransform, _pitchTransform);

            if (compoundJoint)
            {
                var rotation = _yawRestRot
                    * Quaternion.Euler(-pitch, yaw, 0f);

                if (_caps.PitchPivotLocalY != 0f)
                {
                    // The physical hinge sits above the transform origin. Rotating
                    // around the origin would swing the whole head from the base;
                    // instead rotate around the hinge by adjusting localPosition so
                    // the pivot stays fixed.
                    var pivot = _yawRestLocalPos + new Vector3(0f, _caps.PitchPivotLocalY, 0f);
                    _yawTransform.localPosition = pivot + rotation * (_yawRestLocalPos - pivot);
                }
                _yawTransform.localRotation = rotation;

                // Co-rotate the static base in yaw so the moving head doesn't
                // clip into it (pitch is not applied to the base).
                if (_yawBaseTransform != null)
                    _yawBaseTransform.localRotation = _yawBaseRestRot
                        * Quaternion.Euler(0f, yaw, 0f);
            }
            else
            {
                if (_yawTransform != null)
                {
                    // The near camera is parented rigidly to this joint, so a
                    // single rotation drives BOTH the visible head and the lens
                    // together — no head/lens sign disagreement. YawInvert negates
                    // the angle for a joint whose local frame would otherwise turn
                    // the rigid head+lens opposite to the operator's +panYaw =
                    // pan-right convention (TopJoint).
                    float jointYaw = _caps.YawInvert ? -yaw : yaw;
                    _yawTransform.localRotation = _yawRestRot
                        * Quaternion.Euler(0f, jointYaw, 0f);
                }
                if (_pitchTransform != null)
                    _pitchTransform.localRotation = _pitchRestRot
                        * Quaternion.Euler(-pitch, 0f, 0f);
            }
        }
    }
}
