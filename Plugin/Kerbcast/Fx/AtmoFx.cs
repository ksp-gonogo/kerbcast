// Pluggable atmospheric-FX framework. Each visual layer (core sheath, bowshock,
// trail, embers — and, in future, a Firefly port) is an IAtmoFxEffect. The
// per-camera FxHost owns the camera reference, the master on/off gate, and the
// per-frame FxFrameState; each effect owns *its own* rendering surface (its own
// CommandBuffer, mesh, or particle system). Nothing here owns a shared
// CommandBuffer — that's deliberate, so an effect that brings its own (e.g. a
// Firefly port) slots in without fighting the host.

using UnityEngine;

namespace Kerbcast
{
    // Shared layer index for kerbcast-FX GameObjects (e.g. the ember
    // ParticleSystem). KSP's main flight cameras don't cull this layer by
    // default, so anything on it renders ONLY on kerbcast's near-cams (which
    // explicitly OR the layer into their cullingMask in SetCameras).
    // Pick a high index that KSP itself doesn't use.
    internal static class AtmoFxConstants
    {
        public const int Layer = 22;
        public const int LayerMask = 1 << 22;
    }

    // Individually-toggleable FX layers. Bits map 1:1 to settings.cfg tokens
    // (CORE, BOWSHOCK, TRAIL, EMBERS) and to each effect's IAtmoFxEffect.Layer.
    [System.Flags]
    internal enum AtmoFxLayers
    {
        None = 0,
        Core = 1,      // windward plasma sheath + streaks (the wind↔plasma continuum)
        Bowshock = 2,  // shock cone ahead of the vessel
        Trail = 4,     // plasma wake behind the vessel
        Embers = 8,    // shedding spark/ember particles
        Firefly = 16,  // capture the Firefly mod's reentry plasma instead of the kerbcast plasma
        All = Core | Bowshock | Trail | Embers,
    }

    // Per-frame inputs handed to every effect. Effects derive whatever they need
    // (intensity, windward direction, emission rate) from these — there is no
    // single shared "intensity" global, because each layer keys off different
    // quantities (heat vs velocity vector vs heating rate).
    internal readonly struct FxFrameState
    {
        public readonly Vessel Vessel;
        public readonly Camera NearCam;
        public readonly Vector3 VelocityWorld; // surface velocity, world space
        public readonly float Mach;
        public readonly float DynamicPressure; // kPa
        public readonly float Dt;
        public readonly float Time;

        public FxFrameState(Vessel vessel, Camera nearCam, Vector3 velocityWorld,
            float mach, float dynamicPressure, float dt, float time)
        {
            Vessel = vessel;
            NearCam = nearCam;
            VelocityWorld = velocityWorld;
            Mach = mach;
            DynamicPressure = dynamicPressure;
            Dt = dt;
            Time = time;
        }
    }

    // A single pluggable FX layer. Effects own their rendering surface and
    // lifecycle; the host only sequences them.
    internal interface IAtmoFxEffect
    {
        // The toggle bit this effect answers to.
        AtmoFxLayers Layer { get; }

        // Load assets / attach to the camera. Return false if unavailable (e.g.
        // shader bundle missing, or a host mod isn't installed) — the host then
        // drops the effect silently instead of erroring every frame.
        bool TryInitialize(Camera nearCam);

        // The tracked vessel (or its part set) changed — rebuild per-vessel
        // state (CommandBuffer draw list, meshes, emitters).
        void OnVesselChanged(Vessel vessel);

        // Per-frame update: compute own intensity from state, set material
        // params, (de)activate the rendering surface. Must be a cheap no-op
        // when the effect's intensity is ~0 so non-heating flight costs nothing.
        void Render(in FxFrameState state);

        void Dispose();
    }
}
