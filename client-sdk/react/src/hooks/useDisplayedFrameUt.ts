import { useEffect, useState } from "react";
import type { DisplayedFrameUt } from "@ksp-gonogo/kerbcast";
import { useKerbcastClient } from "../context";

/**
 * Mission time of the frame `video` is showing for camera `flightId`,
 * updated on every presented frame (so the calling component re-renders at
 * the video's frame rate). `source` says whether the UT is the frame's own
 * capture stamp (`"exact"`) or the capture clock extrapolated
 * (`"estimated"`). `null` until the first frame with a known UT, and while
 * either argument is null. For per-frame work without re-rendering, use
 * `client.watchDisplayedFrames` directly.
 */
export function useDisplayedFrameUt(
  flightId: number | null,
  video: HTMLVideoElement | null,
): DisplayedFrameUt | null {
  const client = useKerbcastClient();
  const [frame, setFrame] = useState<DisplayedFrameUt | null>(null);
  useEffect(() => {
    setFrame(null);
    if (flightId === null || !video) return;
    return client.watchDisplayedFrames(flightId, video, setFrame);
  }, [client, flightId, video]);
  return frame;
}
