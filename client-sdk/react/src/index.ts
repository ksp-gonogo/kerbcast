/**
 * @ksp-gonogo/kerbcast-react
 *
 * React components and hooks for kerbcast camera feeds. This package
 * is a peer to @ksp-gonogo/kerbcast (the core SDK) and requires React 18+
 * and styled-components 6+ as peer dependencies.
 */

// Context and provider
export {
  KerbcastProvider,
  createClientSubscriptions,
  createClientDisplaySizes,
  useKerbcastClient,
  useKerbcastSubscriptions,
  useKerbcastDisplaySizes,
} from "./context";
export type {
  KerbcastProviderProps,
  KerbcastSubscriptions,
  KerbcastDisplaySizes,
} from "./context";

// Hooks
export { useKerbcastCameras } from "./hooks/useKerbcastCameras";
export { useKerbcastStream } from "./hooks/useKerbcastStream";
export { useKerbcastClock } from "./hooks/useKerbcastClock";
export { useDisplayedFrameUt } from "./hooks/useDisplayedFrameUt";
export { useKerbcastInFlight } from "./hooks/useKerbcastInFlight";
export { useKerbcastWaitingForSlot } from "./hooks/useKerbcastWaitingForSlot";
export { useReportDisplaySize } from "./hooks/useReportDisplaySize";
export { useRecordings } from "./hooks/useRecordings";
export type { RecordingsStore, ActiveRecordingInfo } from "./hooks/useRecordings";

// Shared elapsed-timer helpers (REC badges, RecGroupBar's toolbar)
export { nowMs, formatElapsed } from "./timing";

// Shared standby glyph (used by CameraFeed + downstream dashboards)
export { StandbyIcon } from "./StandbyIcon";

// Camera label utilities
export { buildCameraLabeler } from "./cameraLabels";
export type { LabelableCamera } from "./cameraLabels";

// Lifecycle utilities
export { getCameraLifecycle, isCameraDestroyed } from "./lifecycle";
export type { CameraLifecycle } from "./lifecycle";

// CameraFeed component
export { CameraFeed } from "./CameraFeed";
export type {
  CameraFeedHandle,
  CameraFeedProps,
  CameraStreamHook,
  FeedAction,
} from "./CameraFeed";

// Target brackets geometry and the overlay API for drawing on top of them
export { bracketGeometry, containedContentRect, COMMAND_BRACKET_COLOURS } from "./CommandBrackets";
export type {
  BracketGeometry,
  BracketPoint,
  CommandBracketContext,
  CommandBracketCorner,
  CommandBracketCornerOverride,
  CommandBracketCornerOverrides,
  CommandBracketCornerStyle,
  CommandBracketOverlay,
  ContentRect,
} from "./CommandBrackets";

// Anchored markers: CameraFeed's `anchors` and `anchorOverlay`, and the
// pieces for drawing a frame's anchors over a video of your own
export {
  AnchorMarkers,
  ANCHOR_MARKER_COLOURS,
  anchorMarkerGeometry,
  useDisplayedFrame,
} from "./AnchorMarkers";
export type {
  AnchorContext,
  AnchorCorner,
  AnchorCornerOverride,
  AnchorCornerOverrides,
  AnchorCornerStyle,
  AnchorMarkerGeometry,
  AnchorMarkerShape,
  AnchorMarkersProps,
  AnchorOverlay,
  PlacedAnchor,
} from "./AnchorMarkers";

// FeedActionBar: the shared action row (tracking, fullscreen, PiP, quality,
// close, REC) both CameraFeed and KerbalFaceFeed render, with the issue-#6
// overflow (⋮) engine.
export { FeedActionBar } from "./FeedActionBar";
export type { FeedActionBarEntry } from "./FeedActionBar";

// KerbalFaceFeed — shared single-kerbal-face primitive
export { KerbalFaceFeed } from "./KerbalFaceFeed";
export type { KerbalFaceFeedProps, KerbalFeedState } from "./KerbalFaceFeed";

// RecordingsTray: a presentational drawer over the recordings store
export { RecordingsTray, recordingEndNote } from "./RecordingsTray";
export type { RecordingsTrayProps } from "./RecordingsTray";
