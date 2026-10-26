import type { FrameAnchor } from "./__generated__/types";

/** Most anchors one client may hold on one camera; the sidecar refuses more. */
export const MAX_ANCHORS_PER_CAMERA = 16;

/** The `owner` on anchors Kerbcast sets itself. */
export const KERBCAST_ANCHOR_OWNER = "kerbcast";

/** Id of a command camera's subject anchor: a `vessel` anchor with occlusion on, owned by Kerbcast. */
export const COMMAND_SUBJECT_ANCHOR_ID = "subject";

/** How a video element scales its frame into its box, as CSS `object-fit`. */
export type FrameFit = "contain" | "cover";

/**
 * Where a frame lands inside an element's box, in the box's own pixels.
 * Under `cover` the rect overflows the box, so `x` or `y` is negative.
 */
export interface FittedFrame {
  x: number;
  y: number;
  width: number;
  height: number;
  /** Element pixels per frame pixel. */
  scale: number;
}

/**
 * Where a `frameWidth` by `frameHeight` frame lands inside a `boxWidth` by
 * `boxHeight` element under `object-fit: contain` (letterboxed) or `cover`
 * (cropped). While either size is unknown the frame is taken to fill the box.
 */
export function fitFrame(
  boxWidth: number,
  boxHeight: number,
  frameWidth: number,
  frameHeight: number,
  fit: FrameFit = "contain",
): FittedFrame {
  if (frameWidth <= 0 || frameHeight <= 0 || boxWidth <= 0 || boxHeight <= 0) {
    return {
      x: 0,
      y: 0,
      width: Math.max(0, boxWidth),
      height: Math.max(0, boxHeight),
      scale: frameWidth > 0 && boxWidth > 0 ? boxWidth / frameWidth : 1,
    };
  }
  const sx = boxWidth / frameWidth;
  const sy = boxHeight / frameHeight;
  const scale = fit === "cover" ? Math.max(sx, sy) : Math.min(sx, sy);
  const width = frameWidth * scale;
  const height = frameHeight * scale;
  return { x: (boxWidth - width) / 2, y: (boxHeight - height) / 2, width, height, scale };
}

/**
 * A point in capture-frame pixels (an anchor's `x`, `y`) in element pixels,
 * for a frame shown at `fitted` (from {@link fitFrame}). `frameWidth` and
 * `frameHeight` are the capture size the point is in: the stamp's `width`
 * and `height`.
 */
export function frameToScreen(
  point: { x: number; y: number },
  frameWidth: number,
  frameHeight: number,
  fitted: FittedFrame,
): { x: number; y: number } {
  const sx = frameWidth > 0 ? fitted.width / frameWidth : 1;
  const sy = frameHeight > 0 ? fitted.height / frameHeight : 1;
  return { x: fitted.x + point.x * sx, y: fitted.y + point.y * sy };
}

/**
 * An anchor's position in capture-frame pixels, or `null` when the result
 * carries none: an observed result out of frame or occluded.
 */
export function anchorPoint(anchor: FrameAnchor): { x: number; y: number } | null {
  return typeof anchor.x === "number" && typeof anchor.y === "number"
    ? { x: anchor.x, y: anchor.y }
    : null;
}

/** A length in capture-frame pixels (an anchor's `apparentRadiusPx`) in element pixels. */
export function frameLengthToScreen(length: number, frameHeight: number, fitted: FittedFrame): number {
  return frameHeight > 0 ? (length * fitted.height) / frameHeight : length;
}

/**
 * The result for anchor `id` in a frame's results. Without `owner`, this
 * client's own anchor (one with no owner); with it, the one that owner set.
 */
export function findAnchor(
  anchors: readonly FrameAnchor[] | null | undefined,
  id: string,
  owner?: string,
): FrameAnchor | null {
  if (!anchors) return null;
  for (const a of anchors) {
    if (a.id === id && (a.owner ?? undefined) === owner) return a;
  }
  return null;
}
