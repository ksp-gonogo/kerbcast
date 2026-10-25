import type {
  CommandAuthorityState,
  CommandTrackEvent,
  CommandTrackState,
} from "./__generated__/types";

/**
 * Every word a command feed's target-bracket readout can show. One table so a
 * host can match or restyle the strings without re-deriving them.
 */
export const COMMAND_TRACK_LABELS = {
  camera: {
    inView: "IN VIEW",
    inShadow: "IN SHADOW",
    occluded: "OCCLUDED",
    noFix: "NO FIX",
  },
  separator: " · ",
} as const satisfies {
  camera: Record<string, string>;
  separator: string;
};

/** The camera axis of a command feed: what the lens can see at the aim point. */
export type CommandCameraAxis = "inView" | "inShadow" | "occluded" | "noFix";

/**
 * A command track resolved for one displayed UT: the discrete fields come from
 * the latest event at or before that UT, the continuous ones from the
 * snapshot as sent.
 */
export interface EffectiveCommandTrack {
  hasFix: boolean;
  rangeM: number | null;
  bracketHalfPx: number | null;
  frameHeightPx: number;
  framingFits: boolean;
  occluded: boolean;
  occluder: string;
  sunlit: boolean;
  blank: string | null;
  authority: CommandAuthorityState | null;
  camera: CommandCameraAxis;
}

/** Latest event with `ut <= displayUt`, or `null` when none qualifies. */
export function commandEventAt(
  events: readonly CommandTrackEvent[],
  displayUt: number,
): CommandTrackEvent | null {
  let found: CommandTrackEvent | null = null;
  for (const e of events) {
    if (e.ut <= displayUt && (found === null || e.ut >= found.ut)) found = e;
  }
  return found;
}

/**
 * Resolve the state to show at `displayUt`. Without a displayed UT, or when
 * every event is newer than it, the snapshot's own discrete fields apply.
 */
export function effectiveCommandTrack(
  track: CommandTrackState,
  displayUt?: number | null,
): EffectiveCommandTrack {
  const event =
    displayUt != null && track.events?.length ? commandEventAt(track.events, displayUt) : null;
  const occluded = event ? event.occluded : track.occluded;
  const occluder = (event ? event.occluder : track.occluder) ?? "";
  const sunlit = event ? event.sunlit : track.sunlit;
  const blank = (event ? event.blank : track.blank) || null;
  const hasFix = track.hasFix;
  const camera: CommandCameraAxis = !hasFix
    ? "noFix"
    : occluded
      ? "occluded"
      : sunlit
        ? "inView"
        : "inShadow";
  return {
    hasFix,
    rangeM: track.rangeM ?? null,
    bracketHalfPx: track.bracketHalfPx ?? null,
    frameHeightPx: track.frameHeightPx,
    framingFits: track.framingFits,
    occluded,
    occluder,
    sunlit,
    blank,
    authority: track.authority ?? null,
    camera,
  };
}

/** Range as `850 m` under 1 km, `4.2 km` under 10 km, `11,508 km` beyond. */
export function formatCommandRange(metres: number): string {
  if (metres < 1000) return `${Math.round(metres)} m`;
  if (metres < 10_000) return `${(metres / 1000).toFixed(1)} km`;
  const km = String(Math.round(metres / 1000)).replace(/\B(?=(\d{3})+(?!\d))/g, ",");
  return `${km} km`;
}

/** Camera half of the readout, e.g. `IN VIEW` or `OCCLUDED MUN`. */
export function commandCameraLabel(state: EffectiveCommandTrack): string {
  const label = COMMAND_TRACK_LABELS.camera[state.camera];
  if (state.camera === "occluded" && state.occluder) {
    return `${label} ${state.occluder.toUpperCase()}`;
  }
  return label;
}

/** The default readout under the brackets: camera part, then the range when known. */
export function commandReadout(state: EffectiveCommandTrack): string {
  const parts = [commandCameraLabel(state)];
  if (state.rangeM !== null) parts.push(formatCommandRange(state.rangeM));
  return parts.join(COMMAND_TRACK_LABELS.separator);
}
