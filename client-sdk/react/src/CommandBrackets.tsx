import {
  COMMAND_AIM_ANCHOR_ID,
  COMMAND_SUBJECT_ANCHOR_ID,
  KERBCAST_ANCHOR_OWNER,
  anchorPoint,
  commandReadout,
  findAnchor,
  frameToScreen,
  type EffectiveCommandTrack,
  type FrameAnchor,
  type FrameUtSource,
} from "@ksp-gonogo/kerbcast";
import { type ReactNode } from "react";
import styled from "styled-components";
import { useDisplayedFrame } from "./AnchorMarkers";
import { useVideoBox } from "./videoBox";

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
  /**
   * Box centre: where the subject is in the displayed frame, or the frame
   * centre (the aim point) while that is not known.
   */
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
 * The bracket box, centred on the displayed frame or on `centre` (in the
 * same pixel space as `rect`). `bracketHalfPx` is in capture px of a
 * `frameHeightPx`-tall frame, so it scales by the displayed height; a
 * missing half-size draws the minimum box.
 */
export function bracketGeometry(
  rect: ContentRect,
  bracketHalfPx: number | null,
  frameHeightPx: number,
  centre?: BracketPoint | null,
): BracketGeometry {
  const scale = frameHeightPx > 0 ? rect.height / frameHeightPx : 1;
  const half = Math.max(BRACKET_MIN_HALF_PX, (bracketHalfPx ?? 0) * scale);
  const arm = Math.max(BRACKET_MIN_ARM_PX, BRACKET_ARM_FRACTION * half * 2);
  const cx = centre ? centre.x : rect.x + rect.width / 2;
  const cy = centre ? centre.y : rect.y + rect.height / 2;
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
  /**
   * The subject's anchor in the displayed frame: where the game projected
   * the craft, whether it is in frame and whether something hides it.
   * `null` without one (an older sidecar or plugin, or before the first
   * stamped frame), when the box is centred on the frame instead.
   */
  subject?: FrameAnchor | null;
  /**
   * True while the aim lies off the frame (the view is still turning onto
   * it): the box then sits on the frame's edge nearest the aim, with a
   * chevron pointing on toward it.
   */
  aimOffscreen?: boolean;
}

/** `g` moved so the whole box lies inside `rect`. */
function clampToRect(g: BracketGeometry, rect: ContentRect): BracketGeometry {
  const clamp = (v: number, lo: number, hi: number) => (lo > hi ? (lo + hi) / 2 : Math.min(hi, Math.max(lo, v)));
  const cx = clamp(g.cx, rect.x + g.half, rect.x + rect.width - g.half);
  const cy = clamp(g.cy, rect.y + g.half, rect.y + rect.height - g.half);
  return {
    ...g,
    cx,
    cy,
    topLeft: { x: cx - g.half, y: cy - g.half },
    bottomRight: { x: cx + g.half, y: cy + g.half },
  };
}

/** A chevron just outside the box, pointing from its centre toward `toward`. */
function edgeCue(g: BracketGeometry, toward: BracketPoint): string | null {
  const dx = toward.x - g.cx;
  const dy = toward.y - g.cy;
  const len = Math.hypot(dx, dy);
  if (!(len > 0)) return null;
  const ux = dx / len;
  const uy = dy / len;
  const tipX = g.cx + ux * (g.half + g.arm);
  const tipY = g.cy + uy * (g.half + g.arm);
  const backX = tipX - ux * g.arm;
  const backY = tipY - uy * g.arm;
  const w = g.arm * 0.6;
  return `M${backX - uy * w} ${backY + ux * w}L${tipX} ${tipY}L${backX + uy * w} ${backY - ux * w}`;
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

function cameraCornerStyle(occluded: boolean): CommandBracketCornerStyle {
  return occluded
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

export interface CommandBracketsProps {
  state: EffectiveCommandTrack;
  displayUt: number;
  /** Where `displayUt` came from, mirrored to the readout as `data-ut-source`. */
  utSource?: FrameUtSource | "override" | null;
  video: HTMLVideoElement | null;
  /** Frame aspect to assume until the video reports its own size. */
  fallbackSize?: { width: number; height: number } | null;
  overlay?: CommandBracketOverlay | null;
  /**
   * The subject's anchor in the displayed frame. While it is visible and no
   * command lock is held the box sits on it and takes its size from it.
   */
  subject?: FrameAnchor | null;
  /** The capture frame size `subject` is in (the stamp's width and height). */
  subjectFrameSize?: { width: number; height: number } | null;
  /**
   * The aim anchor in the displayed frame. The aimed box sits on it rather
   * than on the frame centre, so it stays on the aim while the view turns
   * onto a moved one.
   */
  aim?: FrameAnchor | null;
}

/**
 * Target brackets for a command camera feed. With no command lock held and
 * the subject's anchor visible in the displayed frame, the box sits on the
 * craft where the game saw it, so it stays on the craft when an aim points
 * elsewhere. Otherwise (the anchor out of frame or occluded, which then
 * carries no position, no anchor at all, or a command authority holding the
 * lock) the box is the 1.11 one: on the aim (the frame centre, or the aim
 * anchor while the view turns onto a moved aim), sized from the aimed range,
 * red and dashed when the aim line is occluded. `ctx.subject` always
 * carries the anchor, so a consumer can draw it as a second mark. An overlay
 * can restyle either corner and draw its own marks. Without a fix, or over a
 * blanked feed, the default corners are left out but the overlay and readout
 * still run.
 */
export function CommandBrackets({
  state,
  displayUt,
  utSource,
  video,
  fallbackSize,
  overlay,
  subject,
  subjectFrameSize,
  aim,
}: CommandBracketsProps) {
  const { boxW, boxH, mediaW, mediaH } = useVideoBox(video);
  const rect = containedContentRect(
    boxW,
    boxH,
    mediaW || fallbackSize?.width || 0,
    mediaH || fallbackSize?.height || 0,
  );
  const frameW = subjectFrameSize?.width || mediaW || fallbackSize?.width || 0;
  const frameH = subjectFrameSize?.height || mediaH || fallbackSize?.height || 0;
  /* Under the lock the box is the authority's aim, as in 1.11. */
  const point =
    subject && !state.authority && frameW > 0 && frameH > 0 ? anchorPoint(subject) : null;
  const placed = point !== null;
  const aimPoint = !point && aim && frameW > 0 && frameH > 0 ? anchorPoint(aim) : null;
  const aimScreen = aimPoint ? frameToScreen(aimPoint, frameW, frameH, { ...rect, scale: 1 }) : null;
  const aimOffscreen = !!aimScreen && (!aim?.inFrame || !!aim?.behind);
  const unclamped = point
    ? bracketGeometry(
        rect,
        subject?.apparentRadiusPx ?? null,
        frameH,
        frameToScreen(point, frameW, frameH, { ...rect, scale: 1 }),
      )
    : bracketGeometry(rect, state.bracketHalfPx, state.frameHeightPx || mediaH, aimScreen);
  /* An aim off the frame (mid-slew) keeps the box on the edge nearest it, with a cue pointing on. */
  const g = aimOffscreen ? clampToRect(unclamped, rect) : unclamped;
  const cue = aimOffscreen && aimScreen ? edgeCue(g, aimScreen) : null;
  const ctx: CommandBracketContext = {
    geometry: g,
    width: boxW,
    height: boxH,
    displayUt,
    utSource: utSource ?? null,
    state,
    subject: subject ?? null,
    aimOffscreen,
  };
  const overrides =
    typeof overlay?.corners === "function" ? overlay.corners(ctx) : overlay?.corners;
  const camera = cameraCornerStyle(placed ? !!subject?.occluded : state.occluded);
  const topLeft = applyCorner(camera, overrides?.topLeft);
  const bottomRight = applyCorner(camera, overrides?.bottomRight);
  const { x: left, y: top } = g.topLeft;
  const { x: right, y: bottom } = g.bottomRight;
  const blanked = !!state.blank;
  const drawCorners = state.hasFix && !blanked;
  /* A feed waiting for its target says so itself; NO FIX would only repeat it. */
  const defaultReadout = blanked || state.waitingForTarget ? "" : commandReadout(state);
  const readout = overlay?.readout ? overlay.readout(defaultReadout, ctx) : defaultReadout;

  return (
    <>
      {(drawCorners || overlay?.render) && (
        <BracketSvg
          aria-hidden="true"
          data-camera-axis={state.camera}
          data-subject={subject ? (placed ? "placed" : "centred") : undefined}
          width={boxW}
          height={boxH}
        >
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
          {drawCorners && cue && (
            <path
              data-aim-cue=""
              d={cue}
              fill="none"
              strokeWidth={g.stroke}
              style={{ stroke: camera.colour }}
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

/**
 * CommandBrackets fed with the subject's and the aim's anchors from each frame of
 * `flightId` that `video` presents. Re-renders at the frame rate, which is
 * why it is its own component.
 */
export function LiveCommandBrackets({
  flightId,
  ...props
}: CommandBracketsProps & { flightId: number | null }) {
  const frame = useDisplayedFrame(true, flightId, props.video);
  const subject = findAnchor(frame?.anchors, COMMAND_SUBJECT_ANCHOR_ID, KERBCAST_ANCHOR_OWNER);
  const aim = findAnchor(frame?.anchors, COMMAND_AIM_ANCHOR_ID, KERBCAST_ANCHOR_OWNER);
  const size =
    frame?.frameWidth && frame.frameHeight
      ? { width: frame.frameWidth, height: frame.frameHeight }
      : null;
  return <CommandBrackets {...props} subject={subject} subjectFrameSize={size} aim={aim} />;
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
