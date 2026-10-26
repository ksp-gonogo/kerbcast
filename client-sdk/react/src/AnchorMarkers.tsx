import {
  fitFrame,
  frameToScreen,
  type DisplayedFrameUt,
  type FittedFrame,
  type FrameAnchor,
  type FrameFit,
  type FrameUtSource,
} from "@ksp-gonogo/kerbcast";
import { useEffect, useState, type ReactNode } from "react";
import styled from "styled-components";
import { useKerbcastClient } from "./context";
import { useVideoBox, videoFit } from "./videoBox";

const MARKER_STROKE_PX = 2;
const MARKER_MIN_HALF_PX = 14;
const MARKER_ARM_FRACTION = 0.3;
const MARKER_MIN_ARM_PX = 6;
const READOUT_GAP_PX = 11;

/** Default colours; each is overridable through the named CSS custom property. */
export const ANCHOR_MARKER_COLOURS = {
  clear: "var(--kerbcast-anchor-clear, #4caf50)",
  occluded: "var(--kerbcast-anchor-occluded, #f44336)",
} as const;

/** The four marks around an anchor, clockwise from the top. */
export type AnchorCorner = "top" | "right" | "bottom" | "left";

/**
 * How a mark is drawn. `corners`: a right-angle corner of a box around the
 * anchor, like the command brackets; each key names the corner that starts
 * its side going clockwise, so `top` is the top left, `right` the top right,
 * `bottom` the bottom right and `left` the bottom left. `triangles`: a
 * triangle outside the box on that side, pointing at the anchor.
 */
export type AnchorMarkerShape = "corners" | "triangles";

export interface AnchorCornerStyle {
  colour: string;
  dashed: boolean;
  shape: AnchorMarkerShape;
}

/** Changes to one default mark; unset fields keep the default. */
export interface AnchorCornerOverride {
  colour?: string;
  dashed?: boolean;
  hidden?: boolean;
  shape?: AnchorMarkerShape;
}

export type AnchorCornerOverrides = Partial<Record<AnchorCorner, AnchorCornerOverride>>;

/** The marker box for one anchor, in the overlay SVG's pixels (the video element's box). */
export interface AnchorMarkerGeometry {
  /** Where the anchor is on screen. Off the box when it is out of frame. */
  cx: number;
  cy: number;
  /** Half the box side: the anchor's apparent radius on screen, never below a minimum. */
  half: number;
  /** Length of each corner stroke, and the side of each triangle. */
  arm: number;
  stroke: number;
}

/** An anchor with its place on screen. */
export interface PlacedAnchor {
  anchor: FrameAnchor;
  x: number;
  y: number;
}

/** Everything an anchor overlay needs to line up with the drawn markers. */
export interface AnchorContext {
  geometry: AnchorMarkerGeometry;
  /** Where the frame lands in the element, after `object-fit`. */
  content: FittedFrame;
  /** Size of the overlay SVG, the video element's box. */
  width: number;
  height: number;
  /** Mission UT of the displayed frame the anchors describe. */
  displayUt: number;
  utSource: FrameUtSource;
  /** Every drawn anchor in this frame with its screen position. */
  anchors: readonly PlacedAnchor[];
  /** The displayed frame, with its raw per-frame results and size. */
  frame: DisplayedFrameUt;
}

/**
 * Consumer changes to a feed's anchor markers. Each part is optional and is
 * called for every drawn anchor on every displayed frame. What an anchor
 * means, and which colour says so, is the consumer's: Kerbcast only reports
 * where it is.
 */
export interface AnchorOverlay {
  /** Default shape of every mark. Default `"corners"`. */
  shape?: AnchorMarkerShape;
  /** Restyle or hide the four marks of one anchor. */
  corners?:
    | AnchorCornerOverrides
    | ((anchor: FrameAnchor, ctx: AnchorContext) => AnchorCornerOverrides | null | undefined);
  /**
   * SVG content drawn above the marks, in the same pixel space. Runs for an
   * anchor out of frame or behind the lens too (its marks are left out then),
   * so an overlay can point to it from the edge.
   */
  render?: (anchor: FrameAnchor, ctx: AnchorContext) => ReactNode;
  /** Text under the marker, or null for none. Default none. */
  readout?: (anchor: FrameAnchor, ctx: AnchorContext) => string | null;
  /**
   * Game-side owners whose anchors are drawn as well as this client's own,
   * e.g. `["gonogo"]`. Default: only this client's.
   */
  owners?: readonly string[];
}

/** The marker box for an anchor at `centre` with `radiusPx` capture pixels on a `frameHeight`-tall frame. */
export function anchorMarkerGeometry(
  centre: { x: number; y: number },
  radiusPx: number,
  frameHeight: number,
  content: FittedFrame,
): AnchorMarkerGeometry {
  const scale = frameHeight > 0 ? content.height / frameHeight : 1;
  const half = Math.max(MARKER_MIN_HALF_PX, radiusPx * scale);
  const arm = Math.max(MARKER_MIN_ARM_PX, MARKER_ARM_FRACTION * half * 2);
  return { cx: centre.x, cy: centre.y, half, arm, stroke: MARKER_STROKE_PX };
}

const CORNERS: readonly AnchorCorner[] = ["top", "right", "bottom", "left"];

/** SVG path data for one mark. Corners are open strokes; triangles are closed. */
function markPath(corner: AnchorCorner, shape: AnchorMarkerShape, g: AnchorMarkerGeometry): string {
  const { cx, cy, half: h, arm: a } = g;
  if (shape === "triangles") {
    const t = a * 0.8;
    const w = a / 2;
    switch (corner) {
      case "top":
        return `M${cx} ${cy - h}L${cx - w} ${cy - h - t}L${cx + w} ${cy - h - t}Z`;
      case "right":
        return `M${cx + h} ${cy}L${cx + h + t} ${cy - w}L${cx + h + t} ${cy + w}Z`;
      case "bottom":
        return `M${cx} ${cy + h}L${cx + w} ${cy + h + t}L${cx - w} ${cy + h + t}Z`;
      default:
        return `M${cx - h} ${cy}L${cx - h - t} ${cy + w}L${cx - h - t} ${cy - w}Z`;
    }
  }
  const l = cx - h, r = cx + h, tp = cy - h, b = cy + h;
  switch (corner) {
    case "top":
      return `M${l} ${tp + a}L${l} ${tp}L${l + a} ${tp}`;
    case "right":
      return `M${r - a} ${tp}L${r} ${tp}L${r} ${tp + a}`;
    case "bottom":
      return `M${r} ${b - a}L${r} ${b}L${r - a} ${b}`;
    default:
      return `M${l + a} ${b}L${l} ${b}L${l} ${b - a}`;
  }
}

/** Whether a feed shows this anchor: this client's own, or one from a listed owner. */
function drawn(a: FrameAnchor, owners: readonly string[] | undefined): boolean {
  return a.owner === undefined || (owners?.includes(a.owner) ?? false);
}

export interface AnchorMarkersProps {
  /** The displayed frame whose anchors to draw. */
  frame: DisplayedFrameUt | null;
  video: HTMLVideoElement | null;
  overlay?: AnchorOverlay | null;
  /** How the video fits its box. Default: read from the element's CSS. */
  fit?: FrameFit;
}

/**
 * Markers for the anchors in one displayed frame: by default four corner
 * marks around each anchor sized from its apparent radius, green when clear
 * and red and dashed when occluded. Positions come from the frame's own
 * stamp, so they match the picture under any delay.
 */
export function AnchorMarkers({ frame, video, overlay, fit }: AnchorMarkersProps) {
  const { boxW, boxH, mediaW, mediaH } = useVideoBox(video);
  if (!frame?.anchors?.length) return null;
  const frameW = frame.frameWidth || mediaW;
  const frameH = frame.frameHeight || mediaH;
  if (!(frameW > 0 && frameH > 0 && boxW > 0 && boxH > 0)) return null;
  const content = fitFrame(boxW, boxH, frameW, frameH, fit ?? videoFit(video));
  const placed: PlacedAnchor[] = frame.anchors
    .filter((a) => drawn(a, overlay?.owners))
    .map((a) => ({ anchor: a, ...frameToScreen(a, frameW, frameH, content) }));
  if (placed.length === 0) return null;
  const shape = overlay?.shape ?? "corners";

  const items = placed.map(({ anchor, x, y }) => {
    const geometry = anchorMarkerGeometry({ x, y }, anchor.apparentRadiusPx, frameH, content);
    const ctx: AnchorContext = {
      geometry,
      content,
      width: boxW,
      height: boxH,
      displayUt: frame.ut,
      utSource: frame.source,
      anchors: placed,
      frame,
    };
    const overrides =
      typeof overlay?.corners === "function" ? overlay.corners(anchor, ctx) : overlay?.corners;
    const base: AnchorCornerStyle = anchor.occluded
      ? { colour: ANCHOR_MARKER_COLOURS.occluded, dashed: true, shape }
      : { colour: ANCHOR_MARKER_COLOURS.clear, dashed: false, shape };
    const visible = anchor.inFrame && !anchor.behind;
    const readout = overlay?.readout ? overlay.readout(anchor, ctx) : null;
    return { anchor, geometry, ctx, overrides, base, visible, readout };
  });

  return (
    <>
      <MarkerSvg aria-hidden="true" data-anchor-markers="" width={boxW} height={boxH}>
        {items.map(({ anchor, geometry, ctx, overrides, base, visible }) => (
          <g key={`${anchor.owner ?? ""}/${anchor.id}`} data-anchor={anchor.id} data-owner={anchor.owner}>
            {visible &&
              CORNERS.map((corner) => {
                const o = overrides?.[corner];
                if (o?.hidden) return null;
                const s = o?.shape ?? base.shape;
                const colour = o?.colour ?? base.colour;
                const dashed = o?.dashed ?? base.dashed;
                const filled = s === "triangles";
                return (
                  <path
                    key={corner}
                    data-corner={corner}
                    data-shape={s}
                    d={markPath(corner, s, geometry)}
                    strokeWidth={geometry.stroke}
                    strokeDasharray={dashed ? "3 2" : undefined}
                    style={filled ? { fill: dashed ? "none" : colour, stroke: colour } : { fill: "none", stroke: colour }}
                  />
                );
              })}
            {overlay?.render && <g data-anchor-overlay="">{overlay.render(anchor, ctx)}</g>}
          </g>
        ))}
      </MarkerSvg>
      {items.map(
        ({ anchor, geometry, readout, visible }) =>
          readout &&
          visible && (
            <Readout
              key={`${anchor.owner ?? ""}/${anchor.id}`}
              data-anchor-readout={anchor.id}
              style={{ left: geometry.cx, top: geometry.cy + geometry.half + READOUT_GAP_PX }}
            >
              {readout}
            </Readout>
          ),
      )}
    </>
  );
}

/**
 * The displayed frame of `flightId` on `video`, re-rendering on every
 * presented frame while `enabled`. Kept to the component that draws from it,
 * so a feed's other UI does not re-render at the frame rate.
 */
export function useDisplayedFrame(
  enabled: boolean,
  flightId: number | null,
  video: HTMLVideoElement | null,
): DisplayedFrameUt | null {
  const client = useKerbcastClient();
  const [frame, setFrame] = useState<DisplayedFrameUt | null>(null);
  useEffect(() => {
    setFrame(null);
    if (!enabled || flightId === null || !video) return;
    return client.watchDisplayedFrames(flightId, video, (f) =>
      /* Skip re-renders between frames that carry the same results. */
      setFrame((prev) =>
        prev && prev.anchors === f.anchors && prev.rtpTimestamp === f.rtpTimestamp ? prev : f,
      ),
    );
  }, [client, enabled, flightId, video]);
  return frame;
}

/** Self-subscribing markers for a feed: watches its displayed frames and draws their anchors. */
export function LiveAnchorMarkers({
  flightId,
  video,
  overlay,
  fit,
}: {
  flightId: number | null;
  video: HTMLVideoElement | null;
  overlay?: AnchorOverlay | null;
  fit?: FrameFit;
}) {
  const frame = useDisplayedFrame(true, flightId, video);
  return <AnchorMarkers frame={frame} video={video} overlay={overlay} fit={fit} />;
}

const MarkerSvg = styled.svg`
  position: absolute;
  top: 0;
  left: 0;
  z-index: 1;
  overflow: visible;
  pointer-events: none;
`;

const Readout = styled.div`
  position: absolute;
  z-index: 1;
  transform: translateX(-50%);
  white-space: nowrap;
  color: #fff;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.12em;
  text-shadow: 0 1px 2px rgba(0, 0, 0, 0.9);
  pointer-events: none;
`;
