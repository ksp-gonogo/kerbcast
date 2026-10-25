import { useEffect, useReducer, useRef } from "react";
import {
  extrapolateCaptureUt,
  type DisplayedFrameUt,
  type FrameUtSource,
  type CommandTrackEvent,
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
}

/**
 * The UT a command feed is showing. An explicit `override` (a host with its
 * own delayed playout) wins. Otherwise, given the feed's `flightId` and the
 * `video` element showing it, the presented frame's own capture stamp is
 * used (`source: "exact"`); without one, the client's capture clock is
 * interpolated to now (`"estimated"`). Re-evaluated twice a second while
 * `enabled` so readout ages keep counting, and again the moment the
 * displayed UT reaches the next of `events`, so a state change lands on its
 * frame rather than on the next tick. `null` when nothing is known.
 */
export function useCommandDisplayUt(
  enabled: boolean,
  override?: number,
  events?: readonly CommandTrackEvent[],
  flightId?: number | null,
  video?: HTMLVideoElement | null,
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

  /* Exact frames re-render only when they change what is drawn: a switch of
     source, or a step across an event. Ages catch up on the tick. */
  const overriding = override !== undefined;
  useEffect(() => {
    frame.current = null;
    if (!enabled || overriding || flightId == null || !video) return;
    return client.watchDisplayedFrames(flightId, video, (f) => {
      frame.current = f.source === "exact" ? f : null;
      const prev = shown.current;
      if (!frame.current) {
        if (prev?.source === "exact") tick();
        return;
      }
      if (
        prev === null ||
        prev.source !== "exact" ||
        crossesEvent(eventsRef.current, prev.ut, f.ut)
      ) {
        tick();
      }
    });
  }, [client, enabled, overriding, flightId, video]);

  const { captureUt, warpRate, at } = sample.current;
  const exact = frame.current;
  const displayUt: CommandDisplayUt | null = overriding
    ? { ut: override, source: "override" }
    : exact
      ? { ut: exact.ut, source: "exact" }
      : captureUt === null
        ? null
        : { ut: extrapolateCaptureUt(captureUt, warpRate, at, nowMs()), source: "estimated" };
  shown.current = displayUt;
  const untilEdge =
    enabled && displayUt?.source === "estimated"
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
