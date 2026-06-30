// The kerbcast-native contract every visual-mod integration implements.
//
// kerbcast clones KSP's flight cameras into offscreen RenderTextures and
// renders them manually; third-party visual mods attach their effects to
// KSP's real cameras via components / command buffers / singleton-held camera
// references that Camera.CopyFrom does not carry. Each integration replicates,
// per cloned-camera layer, whatever its mod planted on the stock camera, by
// runtime reflection only (no compile-time reference to any mod).
//
// The IntegrationHost treats every integration uniformly through this
// interface, so the render code never names a specific mod and a new mod is
// added by implementing this interface and registering it in the host.

using UnityEngine;

namespace Kerbcast
{
    // Per-frame inputs handed to integrations that need live flight state.
    // Mirrors the quantities FxFrameState already computes so the two stay
    // consistent.
    internal readonly struct IntegrationFrameState
    {
        public readonly Vessel Vessel;
        public readonly CameraLayers Layer;
        public readonly float DeltaTime;
        public readonly float Mach;
        public readonly float DynamicPressureKpa;
        public readonly double Altitude;

        public IntegrationFrameState(Vessel vessel, CameraLayers layer, float deltaTime,
            float mach, float dynamicPressureKpa, double altitude)
        {
            Vessel = vessel;
            Layer = layer;
            DeltaTime = deltaTime;
            Mach = mach;
            DynamicPressureKpa = dynamicPressureKpa;
            Altitude = altitude;
        }
    }

    internal interface ICameraModIntegration
    {
        // Human-readable name for logging.
        string Name { get; }

        // True when the mod is installed and every reflected member the
        // integration needs resolved. Cached after a one-shot probe; an
        // unsupported mod version reports false and the integration no-ops.
        bool IsAvailable { get; }

        // Process-global format fact: true if this mod's effects require MSAA
        // off on every cloned camera and the capture RT. Read by the host to
        // build the IntegrationPolicy fact set.
        bool ForcesNoMsaa { get; }

        // True if this integration must run work every frame (live-state
        // effects); false for apply-once-at-setup integrations.
        bool NeedsPerFrame { get; }

        // The layers this integration attaches to (the host skips others).
        CameraLayers AppliesToLayers { get; }

        // Attach this mod's per-camera state to one cloned camera at setup.
        // Must track exactly what it adds and roll back on partial failure.
        void ApplyToLayer(Camera cam, CameraLayers layer);

        // Remove exactly what ApplyToLayer added to this camera/layer; never
        // strip state the integration did not add.
        void RemoveFromLayer(Camera cam, CameraLayers layer);

        // Per-frame work, called immediately before this layer's Render() when
        // NeedsPerFrame is true. No-op for apply-once integrations.
        void PerFrame(Camera cam, CameraLayers layer, in IntegrationFrameState state);
    }
}
