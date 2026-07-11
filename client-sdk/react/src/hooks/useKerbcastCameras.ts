import type { CameraState } from "@ksp-gonogo/kerbcast";
import { useEffect, useState } from "react";
import { useKerbcastClient } from "../context";

/**
 * Live snapshot of the kerbcast camera registry. Returns the empty list
 * before the data channel handshake completes (and after a disconnect).
 * Subscribes via the underlying `KerbcastClient`'s `cameras-change` event for
 * one synchronous push per server-side snapshot or state-changed message.
 */
export function useKerbcastCameras(): CameraState[] {
  const client = useKerbcastClient();

  const [cameras, setCameras] = useState<CameraState[]>(() => [
    ...client.cameras,
  ]);

  useEffect(() => {
    setCameras([...client.cameras]);
    return client.on("cameras-change", (next) => setCameras([...next]));
  }, [client]);

  return cameras;
}
