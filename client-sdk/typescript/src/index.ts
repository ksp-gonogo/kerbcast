// Re-export every type the typeshare codegen emits from the Rust
// protocol module. New code should prefer the higher-level
// `KerbcastClient` re-exported below, but the underlying wire types
// stay available for consumers that want to roll their own transport.
export * from "./__generated__/types";

export {
  KerbcastClient,
  BrowserKerbcastTransport,
  type BrowserKerbcastTransportOptions,
  type InboundVideoStats,
  type KerbcastCameraHandle,
  type KerbcastClientConfig,
  type KerbcastConnectionState,
  type KerbcastClientEvents,
  type KerbcastCameraEvents,
  type KerbcastTransport,
  type KerbcastPeer,
  type KerbcastDataChannel,
  type DiscoveredCamera,
  type NoiseConfig,
} from "./client";

export {
  PanZoomController,
  type PanZoomCommandSink,
  type PanZoomBounds,
  type PanZoomControllerOptions,
} from "./panZoom";
