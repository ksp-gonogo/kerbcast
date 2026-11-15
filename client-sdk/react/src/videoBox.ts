import { useEffect, useState } from "react";

export interface VideoBox {
  boxW: number;
  boxH: number;
  mediaW: number;
  mediaH: number;
}

/** Tracks the rendered box and intrinsic size of a video element. */
export function useVideoBox(video: HTMLVideoElement | null): VideoBox {
  const [box, setBox] = useState<VideoBox>({ boxW: 0, boxH: 0, mediaW: 0, mediaH: 0 });
  useEffect(() => {
    if (!video) return;
    const measure = () => {
      const next = {
        boxW: video.clientWidth,
        boxH: video.clientHeight,
        mediaW: video.videoWidth,
        mediaH: video.videoHeight,
      };
      setBox((prev) =>
        prev.boxW === next.boxW &&
        prev.boxH === next.boxH &&
        prev.mediaW === next.mediaW &&
        prev.mediaH === next.mediaH
          ? prev
          : next,
      );
    };
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(video);
    video.addEventListener("loadedmetadata", measure);
    video.addEventListener("resize", measure);
    return () => {
      observer.disconnect();
      video.removeEventListener("loadedmetadata", measure);
      video.removeEventListener("resize", measure);
    };
  }, [video]);
  return box;
}

/** The element's CSS `object-fit`, as the two fits a feed uses. */
export function videoFit(video: HTMLVideoElement | null): "contain" | "cover" {
  if (!video || typeof getComputedStyle !== "function") return "contain";
  return getComputedStyle(video).objectFit === "cover" ? "cover" : "contain";
}

/**
 * How far a frame's aspect may sit from its box's before the feed stops
 * covering the box and letterboxes instead, as a fraction. Streams are sized
 * in whole 16 px blocks, so a frame lands up to about a percent off the shape
 * laid out for it (848x480 in a 16:9 box). Letterboxing that leaves a few
 * pixels of black bar down two edges; covering crops under a pixel or two
 * from the others, which nobody sees.
 */
export const NEAR_SHAPE_TOLERANCE = 0.02;

/**
 * The fit for a feed's video: `cover` when the frame is within
 * {@link NEAR_SHAPE_TOLERANCE} of the box's shape, `contain` otherwise or
 * while either size is unknown.
 */
export function nearShapeFit(
  boxWidth: number,
  boxHeight: number,
  mediaWidth: number,
  mediaHeight: number,
): "contain" | "cover" {
  if (!(boxWidth > 0 && boxHeight > 0 && mediaWidth > 0 && mediaHeight > 0)) return "contain";
  const off = (mediaWidth / mediaHeight) / (boxWidth / boxHeight);
  return Math.abs(off - 1) <= NEAR_SHAPE_TOLERANCE ? "cover" : "contain";
}
