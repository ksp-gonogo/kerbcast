# Changelog

## 0.12.0 - 2026-06-07

Initial release. Versioned in lockstep with `@jonpepler/kerbcam` and the
sidecar binary.

### Added

- `CameraFeed`: the full-bleed camera feed component extracted from gonogo's
  dashboard widget. Hover/tap-revealed chrome, camera picker menu, Next/Prev
  step buttons, map-style zoom rod (hold-to-zoom buttons + debounced absolute
  FoV slider), pan pad with discrete arrows and an analog drag ball, SIGNAL
  LOST overlay, optional debug readout, automatic 16:9 render sizing via
  ResizeObserver.
- `CameraFeedHandle` imperative ref (`stepCamera`, `setZoomRate`,
  `setPanAxis`, `nudgeZoom`, `nudgePan`) for host platforms that drive the
  feed from their own input systems.
- `KerbcamProvider` / `useKerbcamClient` context carrying the
  `KerbcamClient`, plus a pluggable `KerbcamSubscriptions` seam with a
  refcounting default (`createClientSubscriptions`).
- `useKerbcamCameras` and `useKerbcamStream` hooks.
- `buildCameraLabeler` and `isCameraDestroyed` utilities.
- Theming hooks: accent colors read `--kerbcam-accent` /
  `--kerbcam-accent-wash` CSS custom properties with the original values as
  fallbacks.
