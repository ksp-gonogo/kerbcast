import type {
  CommandAuthorityState,
  CommandCentreInfo,
  CommandTrackEvent,
  CommandTrackState,
  FrameCommandState,
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
 * A command track resolved for one displayed frame. From a 1.12 game every
 * field comes from the frame's own state (`fromFrame`). From an older one, or
 * before the first stamped frame, the discrete fields come from the latest
 * event at or before the displayed UT and the continuous ones from the
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
  /**
   * Why the subject has no position (`unknown`, `notPositioned`, `refused`,
   * `stale`), or `null` when it has one. Only from a frame's own state.
   */
  refusal?: string | null;
  /** The world model's own code and words for a `refused` subject. */
  refusalCode?: string | null;
  refusalText?: string | null;
  /**
   * The camera has no subject to aim at yet and shows its placeholder
   * framing. Only from a frame's own state.
   */
  waitingForTarget?: boolean;
  /** True when every field came from the displayed frame's own state. */
  fromFrame?: boolean;
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

function cameraAxis(hasFix: boolean, occluded: boolean, sunlit: boolean): CommandCameraAxis {
  return !hasFix ? "noFix" : occluded ? "occluded" : sunlit ? "inView" : "inShadow";
}

/** The state one frame shows, from the state that rode with it. */
export function commandTrackFromFrame(frame: FrameCommandState): EffectiveCommandTrack {
  return {
    hasFix: frame.hasFix,
    rangeM: frame.rangeM ?? null,
    bracketHalfPx: frame.bracketHalfPx ?? null,
    frameHeightPx: frame.frameHeightPx,
    framingFits: frame.framingFits,
    occluded: frame.occluded,
    occluder: frame.occluder ?? "",
    sunlit: frame.sunlit,
    blank: frame.blank || null,
    authority: frame.authority ?? null,
    camera: cameraAxis(frame.hasFix, frame.occluded, frame.sunlit),
    refusal: frame.refusal ?? null,
    refusalCode: frame.refusalCode ?? null,
    refusalText: frame.refusalText ?? null,
    waitingForTarget: frame.waitingForTarget ?? false,
    fromFrame: true,
  };
}

/**
 * Resolve the state to show for the displayed frame. With the frame's own
 * state (`frame`, from `DisplayedFrameUt.command`) that is used whole, so
 * nothing shows before the picture it describes. Otherwise the snapshot is
 * replayed against `displayUt`: without one, or when every event is newer
 * than it, the snapshot's own discrete fields apply. While a lock is held
 * the snapshot is the site's knowledge at its own UT, so with a displayed UT
 * its fix, range and bracket apply only once that UT has reached it, and
 * until then it reads as no fix. Without a displayed UT (an older game sends
 * no frame stamps) the snapshot applies as sent.
 */
export function effectiveCommandTrack(
  track: CommandTrackState,
  displayUt?: number | null,
  frame?: FrameCommandState | null,
): EffectiveCommandTrack {
  if (frame) return commandTrackFromFrame(frame);
  const event =
    displayUt != null && track.events?.length ? commandEventAt(track.events, displayUt) : null;
  const occluded = event ? event.occluded : track.occluded;
  const occluder = (event ? event.occluder : track.occluder) ?? "";
  const sunlit = event ? event.sunlit : track.sunlit;
  const blank = (event ? event.blank : track.blank) || null;
  const held = track.authority != null && displayUt != null && displayUt < track.ut;
  const hasFix = track.hasFix && !held;
  const camera = cameraAxis(hasFix, occluded, sunlit);
  return {
    hasFix,
    rangeM: held ? null : (track.rangeM ?? null),
    bracketHalfPx: held ? null : (track.bracketHalfPx ?? null),
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

/** How far back, in UT, a targets list is kept by default: two hours, past any display lag a view should need. */
export const COMMAND_CENTRE_MAX_LAG_UT = 7200;

/** Most targets lists kept per camera by default, whatever the window: ranges change every second in orbit. */
export const COMMAND_CENTRE_MAX_ENTRIES = 600;

/**
 * Recent `commandCentre` snapshots per camera, so a delayed view can show
 * the subject, range and targets list as they stood at its displayed frame
 * rather than as they stand now. A snapshot from a 1.12 game carries the UT
 * it describes; one without (an older game or sidecar) applies at once.
 * A snapshot whose content is unchanged keeps the UT it first had, and a
 * snapshot at an earlier UT (a load or revert) drops what came after it.
 * Lists are kept back to `maxLagUt` before the newest, always with one at
 * or before that, and never more than `maxEntries`: past that the oldest go,
 * and a UT older than every list kept finds none. Unchanged targets are
 * shared between snapshots.
 */
export class CommandCentreHistory {
  private readonly byFlight = new Map<number, CommandCentreInfo[]>();

  constructor(
    private readonly maxLagUt: number = COMMAND_CENTRE_MAX_LAG_UT,
    private readonly maxEntries: number = COMMAND_CENTRE_MAX_ENTRIES,
  ) {}

  record(flightId: number, centre: CommandCentreInfo | null | undefined): void {
    if (!centre) return;
    let list = this.byFlight.get(flightId);
    if (!list) {
      list = [];
      this.byFlight.set(flightId, list);
    }
    const ut = centre.ut;
    if (ut != null) while (list.length && (list[list.length - 1].ut ?? -Infinity) > ut) list.pop();
    const last = list[list.length - 1];
    if (last && sameContent(last, centre)) return;
    list.push(last ? shareTargets(centre, last) : centre);
    const newest = ut ?? -Infinity;
    const cutoff = newest - this.maxLagUt;
    let drop = 0;
    while (drop + 1 < list.length && (list[drop + 1].ut ?? -Infinity) <= cutoff) drop++;
    drop = Math.max(drop, list.length - Math.max(1, this.maxEntries));
    if (drop > 0) list.splice(0, drop);
  }

  /**
   * The newest snapshot whose UT is at or before `displayUt`, or `null`
   * when every snapshot is newer. Without a displayed UT, the newest.
   */
  at(flightId: number, displayUt?: number | null): CommandCentreInfo | null {
    const list = this.byFlight.get(flightId);
    if (!list?.length) return null;
    if (displayUt == null) return list[list.length - 1];
    for (let i = list.length - 1; i >= 0; i--) {
      const ut = list[i].ut;
      if (ut == null || ut <= displayUt) return list[i];
    }
    return null;
  }

  /** Snapshots kept for `flightId`, oldest first. */
  size(flightId: number): number {
    return this.byFlight.get(flightId)?.length ?? 0;
  }

  forget(flightId: number): void {
    this.byFlight.delete(flightId);
  }

  /** Forget every camera except those in `keep`. */
  retain(keep: Iterable<number>): void {
    const kept = new Set(keep);
    for (const flightId of [...this.byFlight.keys()]) if (!kept.has(flightId)) this.byFlight.delete(flightId);
  }

  clear(): void {
    this.byFlight.clear();
  }
}

function sameContent(a: CommandCentreInfo, b: CommandCentreInfo): boolean {
  return JSON.stringify({ ...a, ut: undefined }) === JSON.stringify({ ...b, ut: undefined });
}

/* The same targets as `previous` keep its objects, so a long history of small changes stays small. */
function shareTargets(centre: CommandCentreInfo, previous: CommandCentreInfo): CommandCentreInfo {
  const before = new Map(previous.targets.map((t) => [t.persistentId, t]));
  return {
    ...centre,
    targets: centre.targets.map((t) => {
      const old = before.get(t.persistentId);
      return old && JSON.stringify(old) === JSON.stringify(t) ? old : t;
    }),
  };
}
