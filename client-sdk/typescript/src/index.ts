// Re-export every type the typeshare codegen emits from the Rust
// protocol module. New code should prefer the higher-level
// `KerbcamClient` re-exported below, but the underlying wire types
// stay available for consumers that want to roll their own transport.
export * from "./__generated__/types";

export {
  KerbcamClient,
  BrowserKerbcamTransport,
  type BrowserKerbcamTransportOptions,
  type InboundVideoStats,
  type KerbcamCameraHandle,
  type KerbcamClientConfig,
  type KerbcamConnectionState,
  type KerbcamClientEvents,
  type KerbcamCameraEvents,
  type KerbcamTransport,
  type KerbcamPeer,
  type KerbcamDataChannel,
  type DiscoveredCamera,
  type NoiseConfig,
} from "./client";

export {
  PanZoomController,
  type PanZoomCommandSink,
  type PanZoomBounds,
  type PanZoomControllerOptions,
} from "./panZoom";
