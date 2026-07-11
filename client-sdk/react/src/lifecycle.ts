import type { CameraState } from "@ksp-gonogo/kerbcast";
import { CameraLifecycle } from "@ksp-gonogo/kerbcast";

export type { CameraLifecycle };

export function getCameraLifecycle(cam: CameraState): CameraLifecycle {
  return cam.lifecycle === CameraLifecycle.Destroyed
    ? CameraLifecycle.Destroyed
    : CameraLifecycle.Active;
}

export function isCameraDestroyed(cam: CameraState): boolean {
  return getCameraLifecycle(cam) === CameraLifecycle.Destroyed;
}
