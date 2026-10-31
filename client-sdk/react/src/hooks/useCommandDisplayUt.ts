import { useEffect, useReducer, useRef } from "react";
import {
  extrapolateCaptureUt,
  type DisplayedFrameUt,
  type FrameUtSource,
  type CommandTrackEvent,
  type FrameCommandState,
} from "@ksp-gonogo/kerbcast";
import { useKerbcastClient } from "../context";
import { nowMs } from "../timing";

export { MAX_UT_EXTRAPOLATION_MS, extrapolateCaptureUt } from "@ksp-gonogo/kerbcast";

const TICK_MS = 500;

/** Real milliseconds until the first event after `displayUt`, or `null` when none is ahead. */
export function msUntilNextEvent(
  events: readonly CommandTrackEvent[] | undefined,
  displayUt: number,
  warpRate: number,
): number | null {
  if (!events?.length || !(warpRate > 0)) return null;
  let next = Infinity;
  for (const e of events) if (e.ut > displayUt && e.ut < next) next = e.ut;
  return next === Infinity ? null : ((next - displayUt) / warpRate) * 1000;
}

/** Whether an event's UT lies in `(from, to]`, i.e. moving the display from `from` to `to` changes the state in force. */
export function crossesEvent(
  events: readonly CommandTrackEvent[] | undefined,
  from: number,
  to: number,
): boolean {
  if (!events?.length) return false;
  const lo = Math.min(from, to);
  const hi = Math.max(from, to);
  return events.some((e) => e.ut > lo && e.ut <= hi);
}

/** The UT a command feed is showing and where it came from; `"override"` is a host's own value. */
export interface CommandDisplayUt {
  ut: number;
  source: FrameUtSource | "override";
  /** The command state that rode with the displayed frame (1.12 and later), or the host's own with an override. */
  command?: FrameCommandState;
}

/**
 * What of a frame's command state a viewer can see change: the discrete
 * fields whole, ranges to 3 significant figures, pixel sizes to half a
 * pixel. Frames that agree on it draw the same, so they skip a re-render.
 */
export function commandShownKey(c: FrameCommandState | undefined): string {
  if (!c) return "";
  const sig = (v: number | undefined) => (v == null ? "" : Number(v.toPrecision(3)));
  const half = (v: number | undefined) => (v == null ? "" : Math.round(v * 2));
  return JSON.stringify([
    c.hasFix,
    sig(c.rangeM),
    half(c.bracketHalfPx),
    c.frameHeightPx,
    c.framingFits,
    half(c.trueOffsetPx),
    c.occluded,
    c.occluder,
    c.sunlit,
    c.blank ?? null,
    c.authority?.owner ?? null,
    c.subjectPersistentId,
    c.refusal ?? null,
    c.refusalCode ?? null,
    c.refusalText ?? null,
    c.waitingForTarget ?? false,
  ]);
}

/**
 * The UT a command feed is showing. An explicit `override` (a host with its
 * own delayed playout) wins. Otherwise, given the feed's `flightId` and the
 * `video` element showing it, the presented frame's own capture stamp is
 * used (`source: "exact"`); without one, the client's capture clock is
 * interpolated to now (`"estimated"`). Re-evaluated twice a second while
 * `enabled` so readout ages keep counting, and again the moment the
 * displayed UT reaches the next of `events`, so a state change lands on its
 * frame rather than on the next tick. The displayed frame's own command
 * state comes back as `command`, re-rendering when what it shows changes:
 * an exact frame's, an estimated one's (which lags the picture while the
 * element plays the stream as it arrives; its own UT is used too, so events
 * and lists stay on that frame),
 * or with an override the host's own `overrideCommand`. `null` when
 * nothing is known.
 */
export function useCommandDisplayUt(
  enabled: boolean,
  override?: number,
  events?: readonly CommandTrackEvent[],
  flightId?: number | null,
  video?: HTMLVideoElement | null,
  overrideCommand?: FrameCommandState | null,
): CommandDisplayUt | null {
  const client = useKerbcastClient();
  const sample = useRef<{ captureUt: number | null; warpRate: number; at: number }>({
    captureUt: client.clock.captureUt,
    warpRate: client.clock.warpRate,
    at: nowMs(),
  });
  const frame = useRef<DisplayedFrameUt | null>(null);
  const shown = useRef<CommandDisplayUt | null>(null);
  const eventsRef = useRef(events);
  eventsRef.current = events;
  const [, tick] = useReducer((c: number) => c + 1, 0);

  useEffect(() => {
    const take = () => {
      const { captureUt, warpRate } = client.clock;
      sample.current = { captureUt, warpRate, at: nowMs() };
    };
    take();
    return client.on("settings-change", take);
  }, [client]);

  useEffect(() => {
    if (!enabled) return;
    const interval = setInterval(tick, TICK_MS);
    return () => clearInterval(interval);
  }, [enabled]);

  /* Frames re-render only when they change what is drawn: a switch of
     source, a step across an event, or a change the command state shows.
     Ages catch up on the tick. An estimated frame is kept only for the
     command state it carries. */
  const overriding = override !== undefined;
  useEffect(() => {
    frame.current = null;
    if (!enabled || overriding || flightId == null || !video) return;
    return client.watchDisplayedFrames(flightId, video, (f) => {
      frame.current = f.source === "exact" || f.command ? f : null;
      const prev = shown.current;
      if (!frame.current) {
        if (prev?.source === "exact" || prev?.command) tick();
        return;
      }
      if (
        prev === null ||
        (f.source === "exact" && prev.source !== "exact") ||
        (f.source !== "exact" && prev.source === "exact") ||
        crossesEvent(eventsRef.current, prev.ut, f.ut) ||
        commandShownKey(prev.command) !== commandShownKey(f.command)
      ) {
        tick();
      }
    });
  }, [client, enabled, overriding, flightId, video]);

  const { captureUt, warpRate, at } = sample.current;
  const seen = frame.current;
  const withCommand = (d: CommandDisplayUt, c: FrameCommandState | null | undefined): CommandDisplayUt =>
    c ? { ...d, command: c } : d;
  const displayUt: CommandDisplayUt | null = overriding
    ? withCommand({ ut: override, source: "override" }, overrideCommand)
    : seen?.source === "exact"
      ? withCommand({ ut: seen.ut, source: "exact" }, seen.command)
      : seen
        ? withCommand({ ut: seen.ut, source: "estimated" }, seen.command)
        : captureUt === null
          ? null
          : { ut: extrapolateCaptureUt(captureUt, warpRate, at, nowMs()), source: "estimated" };
  shown.current = displayUt;
  const untilEdge =
    enabled && displayUt?.source === "estimated" && !seen
      ? msUntilNextEvent(events, displayUt.ut, warpRate)
      : null;

  /* Edges further out than a tick are caught by a later render's timer. */
  useEffect(() => {
    if (untilEdge === null || untilEdge > TICK_MS) return;
    const timer = setTimeout(tick, Math.max(0, untilEdge) + 1);
    return () => clearTimeout(timer);
  });

  return displayUt;
}
