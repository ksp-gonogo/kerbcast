import { useEffect, useState } from "react";
import { useKerbcastClient, useKerbcastSubscriptions } from "../context";

/**
 * Live `MediaStream` for one kerbcast camera. Returns `null` while the WebRTC
 * track hasn't arrived yet (during connection setup, or after a disconnect).
 * Components bind the stream to a `<video>`'s `srcObject` directly.
 *
 * While mounted with a non-null `flightId`, acquires a subscription slot via
 * the context `KerbcastSubscriptions`. Releases the slot when the component
 * unmounts or the `flightId` changes.
 */
export function useKerbcastStream(flightId: number | null): MediaStream | null {
  const client = useKerbcastClient();
  const subscriptions = useKerbcastSubscriptions();

  const [stream, setStream] = useState<MediaStream | null>(() => {
    if (flightId === null) return null;
    return client.camera(flightId).mediaStream;
  });

  useEffect(() => {
    if (flightId === null) {
      setStream(null);
      return;
    }
    const cam = client.camera(flightId);
    setStream(cam.mediaStream);
    const off = cam.on("stream", setStream);
    subscriptions.acquire(flightId);
    return () => {
      off();
      subscriptions.release(flightId);
    };
  }, [client, subscriptions, flightId]);

  return stream;
}
