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
