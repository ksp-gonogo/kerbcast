import type { CameraState } from "@jonpepler/kerbcast";
import { CameraLifecycle } from "@jonpepler/kerbcast";

export type { CameraLifecycle };

export function getCameraLifecycle(cam: CameraState): CameraLifecycle {
  return cam.lifecycle === CameraLifecycle.Destroyed
    ? CameraLifecycle.Destroyed
    : CameraLifecycle.Active;
}

export function isCameraDestroyed(cam: CameraState): boolean {
  return getCameraLifecycle(cam) === CameraLifecycle.Destroyed;
}
