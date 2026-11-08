import { useEffect, useState } from "react";
import { useKerbcastClient } from "../context";

/**
 * Whether this client wants camera `flightId` but has no video slot to carry
 * it (see `KerbcastCameraHandle.waitingForSlot`). False for a null id.
 */
export function useKerbcastWaitingForSlot(flightId: number | null): boolean {
  const client = useKerbcastClient();
  const [waiting, setWaiting] = useState(() =>
    flightId === null ? false : client.camera(flightId).waitingForSlot,
  );

  useEffect(() => {
    if (flightId === null) {
      setWaiting(false);
      return;
    }
    const cam = client.camera(flightId);
    setWaiting(cam.waitingForSlot);
    return cam.on("waiting", setWaiting);
  }, [client, flightId]);

  return waiting;
}
