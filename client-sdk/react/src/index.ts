/**
 * @jonpepler/kerbcam-react
 *
 * React components and hooks for kerbcam camera feeds. This package
 * is a peer to @jonpepler/kerbcam (the core SDK) and requires React 18+
 * and styled-components 6+ as peer dependencies.
 */

// Context and provider
export {
  KerbcamProvider,
  createClientSubscriptions,
  useKerbcamClient,
  useKerbcamSubscriptions,
} from "./context";
export type { KerbcamProviderProps, KerbcamSubscriptions } from "./context";

// Hooks
export { useKerbcamCameras } from "./hooks/useKerbcamCameras";
export { useKerbcamStream } from "./hooks/useKerbcamStream";

// Camera label utilities
export { buildCameraLabeler } from "./cameraLabels";
export type { LabelableCamera } from "./cameraLabels";

// Lifecycle utilities
export { getCameraLifecycle, isCameraDestroyed } from "./lifecycle";
export type { CameraLifecycle } from "./lifecycle";

// CameraFeed component
export { CameraFeed } from "./CameraFeed";
export type { CameraFeedHandle, CameraFeedProps, FeedAction } from "./CameraFeed";
