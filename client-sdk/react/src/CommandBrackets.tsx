import {
  commandReadout,
  type EffectiveCommandTrack,
  type FrameUtSource,
} from "@ksp-gonogo/kerbcast";
import { useEffect, useState, type ReactNode } from "react";
import styled from "styled-components";

const BRACKET_STROKE_PX = 2;
const BRACKET_MIN_HALF_PX = 14;
const BRACKET_ARM_FRACTION = 0.3;
const BRACKET_MIN_ARM_PX = 6;
const READOUT_GAP_PX = 11;

/** Default colours; each is overridable through the named CSS custom property. */
export const COMMAND_BRACKET_COLOURS = {
  clear: "var(--kerbcast-bracket-clear, #4caf50)",
  occluded: "var(--kerbcast-bracket-occluded, #f44336)",
} as const;

export interface ContentRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Where `object-fit: contain` media actually lands inside its box. Falls back
 * to the whole box while the media size is unknown.
 */
export function containedContentRect(
  boxWidth: number,
  boxHeight: number,
  mediaWidth: number,
  mediaHeight: number,
): ContentRect {
  if (mediaWidth <= 0 || mediaHeight <= 0 || boxWidth <= 0 || boxHeight <= 0) {
    return { x: 0, y: 0, width: Math.max(0, boxWidth), height: Math.max(0, boxHeight) };
  }
  const scale = Math.min(boxWidth / mediaWidth, boxHeight / mediaHeight);
  const width = mediaWidth * scale;
  const height = mediaHeight * scale;
  return { x: (boxWidth - width) / 2, y: (boxHeight - height) / 2, width, height };
}

export interface BracketPoint {
  x: number;
  y: number;
}

/** The bracket box, in the pixel space of the bracket SVG (the video element's box). */
export interface BracketGeometry {
  /** Box centre, which is the aim point. */
  cx: number;
  cy: number;
  /** Half the box side in display px. */
  half: number;
  /** Length of each corner stroke. */
  arm: number;
  stroke: number;
  /** The box's top-left and bottom-right corner points. */
  topLeft: BracketPoint;
  bottomRight: BracketPoint;
}

/**
 * The bracket box centred on the displayed frame. `bracketHalfPx` is in
 * capture px of a `frameHeightPx`-tall frame, so it scales by the displayed
 * height; a missing half-size draws the minimum box.
 */
export function bracketGeometry(
  rect: ContentRect,
  bracketHalfPx: number | null,
  frameHeightPx: number,
): BracketGeometry {
  const scale = frameHeightPx > 0 ? rect.height / frameHeightPx : 1;
  const half = Math.max(BRACKET_MIN_HALF_PX, (bracketHalfPx ?? 0) * scale);
  const arm = Math.max(BRACKET_MIN_ARM_PX, BRACKET_ARM_FRACTION * half * 2);
  const cx = rect.x + rect.width / 2;
  const cy = rect.y + rect.height / 2;
  return {
    cx,
    cy,
    half,
    arm,
    stroke: BRACKET_STROKE_PX,
    topLeft: { x: cx - half, y: cy - half },
    bottomRight: { x: cx + half, y: cy + half },
  };
}

export type CommandBracketCorner = "topLeft" | "bottomRight";

export interface CommandBracketCornerStyle {
  colour: string;
  dashed: boolean;
}

/** Changes to one default corner; unset fields keep the default. */
export interface CommandBracketCornerOverride {
  colour?: string;
  dashed?: boolean;
  hidden?: boolean;
}

export type CommandBracketCornerOverrides = Partial<
  Record<CommandBracketCorner, CommandBracketCornerOverride>
>;

/** Everything a bracket overlay needs to line up with the drawn brackets. */
export interface CommandBracketContext {
  geometry: BracketGeometry;
  /** Size of the bracket SVG, the video element's box. */
  width: number;
  height: number;
  /** Frame UT the brackets describe. */
  displayUt: number;
  utSource: FrameUtSource | "override" | null;
  state: EffectiveCommandTrack;
}

/**
 * Consumer additions to a command feed's brackets. Each part is optional and
 * is called on every render with the context for the displayed frame.
 */
export interface CommandBracketOverlay {
  /**
   * SVG content drawn inside the bracket SVG above the default corners, in
   * the same pixel space. Runs in every state, with or without a fix and
   * over a blanked feed; `ctx.state` says which.
   */
  render?: (ctx: CommandBracketContext) => ReactNode;
  /** Restyle or hide the default corners. */
  corners?:
    | CommandBracketCornerOverrides
    | ((ctx: CommandBracketContext) => CommandBracketCornerOverrides | null | undefined);
  /**
   * The readout text under the box. Receives the default text, which is
   * empty while the feed is blanked; return a replacement (append to keep
   * it), or null to hide the readout. Runs in every state.
   */
  readout?: (defaultText: string, ctx: CommandBracketContext) => string | null;
}

function cameraCornerStyle(state: EffectiveCommandTrack): CommandBracketCornerStyle {
  return state.occluded
    ? { colour: COMMAND_BRACKET_COLOURS.occluded, dashed: true }
    : { colour: COMMAND_BRACKET_COLOURS.clear, dashed: false };
}

function applyCorner(
  base: CommandBracketCornerStyle,
  override: CommandBracketCornerOverride | undefined,
): CommandBracketCornerStyle & { hidden: boolean } {
  return {
    colour: override?.colour ?? base.colour,
    dashed: override?.dashed ?? base.dashed,
    hidden: override?.hidden ?? false,
  };
}

/** Tracks the rendered box and intrinsic size of a video element. */
function useVideoBox(video: HTMLVideoElement | null) {
  const [box, setBox] = useState({ boxW: 0, boxH: 0, mediaW: 0, mediaH: 0 });
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

export interface CommandBracketsProps {
  state: EffectiveCommandTrack;
  displayUt: number;
  /** Where `displayUt` came from, mirrored to the readout as `data-ut-source`. */
  utSource?: FrameUtSource | "override" | null;
  video: HTMLVideoElement | null;
  /** Frame aspect to assume until the video reports its own size. */
  fallbackSize?: { width: number; height: number } | null;
  overlay?: CommandBracketOverlay | null;
}

/**
 * Target brackets for a command camera feed: the aim point sits at frame
 * centre, so the box is always centred on the displayed video. Both corners
 * show the camera axis by default; an overlay can restyle either and draw
 * its own marks. Without a fix, or over a blanked feed, the default corners
 * are left out but the overlay and readout still run.
 */
export function CommandBrackets({
  state,
  displayUt,
  utSource,
  video,
  fallbackSize,
  overlay,
}: CommandBracketsProps) {
  const { boxW, boxH, mediaW, mediaH } = useVideoBox(video);
  const rect = containedContentRect(
    boxW,
    boxH,
    mediaW || fallbackSize?.width || 0,
    mediaH || fallbackSize?.height || 0,
  );
  const g = bracketGeometry(rect, state.bracketHalfPx, state.frameHeightPx || mediaH);
  const ctx: CommandBracketContext = {
    geometry: g,
    width: boxW,
    height: boxH,
    displayUt,
    utSource: utSource ?? null,
    state,
  };
  const overrides =
    typeof overlay?.corners === "function" ? overlay.corners(ctx) : overlay?.corners;
  const camera = cameraCornerStyle(state);
  const topLeft = applyCorner(camera, overrides?.topLeft);
  const bottomRight = applyCorner(camera, overrides?.bottomRight);
  const { x: left, y: top } = g.topLeft;
  const { x: right, y: bottom } = g.bottomRight;
  const blanked = !!state.blank;
  const drawCorners = state.hasFix && !blanked;
  const defaultReadout = blanked ? "" : commandReadout(state);
  const readout = overlay?.readout ? overlay.readout(defaultReadout, ctx) : defaultReadout;

  return (
    <>
      {(drawCorners || overlay?.render) && (
        <BracketSvg aria-hidden="true" data-camera-axis={state.camera} width={boxW} height={boxH}>
          {drawCorners && !topLeft.hidden && (
            <path
              data-corner="topLeft"
              d={`M${left} ${top + g.arm}L${left} ${top}L${left + g.arm} ${top}`}
              fill="none"
              strokeWidth={g.stroke}
              strokeDasharray={topLeft.dashed ? "3 2" : undefined}
              style={{ stroke: topLeft.colour }}
            />
          )}
          {drawCorners && !bottomRight.hidden && (
            <path
              data-corner="bottomRight"
              d={`M${right} ${bottom - g.arm}L${right} ${bottom}L${right - g.arm} ${bottom}`}
              fill="none"
              strokeWidth={g.stroke}
              strokeDasharray={bottomRight.dashed ? "3 2" : undefined}
              style={{ stroke: bottomRight.colour }}
            />
          )}
          {overlay?.render && <g data-bracket-overlay="">{overlay.render(ctx)}</g>}
        </BracketSvg>
      )}
      {readout && (
        <Readout
          data-ut-source={utSource ?? undefined}
          style={{ left: g.cx, top: bottom + READOUT_GAP_PX }}
        >
          {readout}
        </Readout>
      )}
    </>
  );
}

/** Reason a command feed is blanked, centred on black over the feed. */
export function CommandBlank({ reason }: { reason: string }) {
  return (
    <BlankOverlay role="status" aria-label="Feed blanked">
      <BlankText>{reason}</BlankText>
    </BlankOverlay>
  );
}

const BracketSvg = styled.svg`
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

const BlankOverlay = styled.div`
  position: absolute;
  inset: 0;
  z-index: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 1rem;
  background: #000;
  pointer-events: none;
`;

const BlankText = styled.span`
  color: rgba(255, 255, 255, 0.85);
  font-size: 0.85rem;
  font-weight: 600;
  letter-spacing: 0.12em;
  text-align: center;
  text-transform: uppercase;
`;
