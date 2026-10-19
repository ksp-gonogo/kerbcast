import type { CameraState, DecodedSizeProbe, KerbcastClient } from "@ksp-gonogo/kerbcast";

/**
 * A {@link DecodedSizeProbe} for the mock harness and its tests, which have
 * no real decoder: the mock's canvas tracks never change size, so the
 * reported camera state stands in for the decoded frame size. Takes a getter
 * so it can be handed to the client's own constructor options.
 */
export function stateFollowingDecodedSizeProbe(client: () => KerbcastClient): DecodedSizeProbe {
  return (flightId, _stream, onSize) => {
    const cam = client().camera(flightId);
    const report = (state: CameraState | null): void => {
      if (state && state.renderWidth > 0 && state.renderHeight > 0) {
        onSize(state.renderWidth, state.renderHeight);
      }
    };
    const unsubscribe = cam.on("change", report);
    report(cam.state);
    return unsubscribe;
  };
}
