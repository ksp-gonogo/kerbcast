import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { useKerbcastDisplaySizes } from "../context";

/*
 * Debounce + bucket for self-measured display-size reporting. The first
 * measurement after mount reports promptly (so a feed never sits at a stale
 * resolution until it is resized); later churn is debounced. Auto sets the
 * resolution, never the shape: the request keeps the camera's configured
 * aspect and is sized to the picture of that aspect that fits the box (the
 * feed letterboxes with object-fit: contain). The width is ceiled to a
 * BUCKET_PX multiple: that meets-the-minimum-need (never under-serve) and
 * gives a material-change threshold so a feed re-reports only when a resize
 * crosses a bucket boundary. The height follows at the aspect, rounded to
 * even for H.264.
 */
const DEBOUNCE_MS = 200;
const BUCKET_PX = 16;

/*
 * A resolution change must never cause another one. A box sized from the
 * stream (a frame kept at the video's aspect, a `<video>` at its intrinsic
 * size) would otherwise close a loop: the stream sets the box, the box sets
 * the request, the request sets the stream. A stream that arrives smaller
 * than asked (adaptive quality, a sidecar cap) would then shrink the next
 * request, and so on down.
 *
 * FOLLOW_MS: a box change that starts this soon after the stream changed
 * shape, and matches what that change explains, is the stream's doing. It is
 * adopted as the new baseline and not reported. Layout reacts to a stream
 * change within a frame or two; this leaves room for a slow phone.
 *
 * SUSPECT_MS / HOLD_MS: the backstop for a follow the check above misses (a
 * very late layout, or one that does not keep the stream's shape). A box
 * change this close to a stream change may still be a loop, so only one such
 * report is let through per HOLD_MS; further ones are refused. Box changes
 * away from any stream change always report.
 */
const FOLLOW_MS = 1000;
const SUSPECT_MS = 2500;
const HOLD_MS = 5000;
const ASPECT_TOLERANCE = 0.06;
const MAGNITUDE_SLACK = 1.15;

function bucket(px: number): number {
  const b = Math.ceil(px / BUCKET_PX) * BUCKET_PX;
  return b < BUCKET_PX ? BUCKET_PX : b;
}

/** Shape of a camera whose configured capture size is not known. */
export const DEFAULT_FEED_ASPECT = 16 / 9;

/**
 * Report size for a `width` x `height` box: the `aspect` picture that fits it,
 * width bucketed. Never the box's own shape.
 */
export function displayReportSize(
  width: number,
  height: number,
  aspect: number = DEFAULT_FEED_ASPECT,
): { width: number; height: number } {
  const a = aspect > 0 && Number.isFinite(aspect) ? aspect : DEFAULT_FEED_ASPECT;
  const fit = width > 0 && height > 0 ? Math.min(width, height * a) : width;
  const w = bucket(fit);
  return { width: w, height: Math.max(2, Math.round(w / a / 2) * 2) };
}

export interface Size {
  width: number;
  height: number;
}

/** The stream changing from `from` (null when nothing was known before) to `to`. */
export interface StreamChange {
  from: Size | null;
  to: Size;
}

function spread(a: number, b: number): number {
  return a > b ? a / b : b / a;
}

/**
 * Whether a box going from `prev` to `box` is explained by `change`: the new
 * box has the new stream's aspect, and it grew or shrank no more than the
 * stream's own change in size or aspect accounts for. A layout that fits the
 * stream into a fixed tile changes with the aspect; one that shows the video
 * at its own size changes with the size. A box that jumps further (entering
 * fullscreen at the same aspect) is a layout change.
 */
export function followsStream(prev: Size, box: Size, change: StreamChange): boolean {
  const { to, from } = change;
  if (!(to.width > 0) || !(to.height > 0)) return false;
  if (!(prev.width > 0) || !(prev.height > 0)) return false;
  const aspect = to.width / to.height;
  const heightFits =
    Math.abs(box.height - box.width / aspect) <= Math.max(2, ASPECT_TOLERANCE * box.height);
  const widthFits =
    Math.abs(box.width - box.height * aspect) <= Math.max(2, ASPECT_TOLERANCE * box.width);
  if (!heightFits && !widthFits) return false;
  if (!from) return true;
  const explained = Math.max(
    spread(to.width * to.height, from.width * from.height),
    spread(aspect, from.width / from.height),
  );
  return spread(box.width * box.height, prev.width * prev.height) <= explained * MAGNITUDE_SLACK;
}

export interface ReportDisplaySizeOptions {
  /**
   * Report while mounted. Default true. Set false for a fixed-resolution feed
   * that should not drive auto-resolution. This is a RESOLUTION concern only,
   * independent of whether the feed shows its action UI.
   */
  enabled?: boolean;
  /**
   * Report a square (side x side, the min edge) rather than a picture at
   * `aspect`. Face cams are square; part cams are not.
   */
  square?: boolean;
  /**
   * Width over height of the picture to ask for, normally the camera's
   * configured capture size. Default 16:9. Ignored when `square` is set.
   */
  aspect?: number;
  /**
   * The `<video>` playing this camera. Its resolution changes are watched so a
   * box resize that only follows the stream is not reported back.
   */
  videoRef?: React.RefObject<HTMLVideoElement | null>;
  /**
   * The size the sidecar says it is rendering this camera at, for the same
   * purpose: a layout may size itself from this rather than from the video.
   */
  streamSize?: Size | null;
}

interface TimedChange extends StreamChange {
  at: number;
}

/**
 * Self-measure a feed element and drive the client's per-consumer
 * `reportDisplaySize` (via the context display-size registry, which collapses
 * multiple feeds of the same camera to one MAX report). Attach `ref` to the
 * element whose rendered pixel box is the display size; an element that
 * appears or changes after mount is picked up on the host's next render. Reporting follows the
 * mount lifecycle: on unmount / `flightId` change / `enabled` flip it
 * deregisters, and the sidecar's clear-on-departure (unsubscribe) does the
 * server-side clear.
 *
 * A box resize that follows a change in the incoming stream is not reported,
 * so a layout sized from the stream cannot feed it back into its own request.
 */
export function useReportDisplaySize(
  flightId: number | null,
  ref: React.RefObject<Element | null>,
  {
    enabled = true,
    square = false,
    aspect = DEFAULT_FEED_ASPECT,
    videoRef,
    streamSize,
  }: ReportDisplaySizeOptions = {},
): void {
  const displaySizes = useKerbcastDisplaySizes();
  const instanceId = useId();

  /*
   * Track the elements behind the refs as state so an element that mounts or
   * is swapped after the first render (conditional render, a feed that appears
   * once a stream arrives) is observed. Checked after every commit of the host
   * component; the state only changes when the element does.
   */
  const [el, setEl] = useState<Element | null>(null);
  const [video, setVideo] = useState<HTMLVideoElement | null>(null);
  useLayoutEffect(() => {
    if (ref.current !== el) setEl(ref.current);
    const v = videoRef?.current ?? null;
    if (v !== video) setVideo(v);
  });

  const changes = useRef<TimedChange[]>([]);
  const lastStream = useRef<Size | null>(null);
  const lastBySource = useRef<Record<"video" | "render", Size | null>>({ video: null, render: null });

  /*
   * Each source is compared with its own last value, so the video and the
   * sidecar's render size reporting the same new shape at different moments
   * are both noted. The first render size is only a baseline: a layout built
   * from it was already in place when the first measurement went out.
   */
  const noteStream = useCallback((source: "video" | "render", width: number, height: number) => {
    if (!(width > 0) || !(height > 0)) return;
    const seen = lastBySource.current[source];
    if (seen && seen.width === width && seen.height === height) return;
    const to = { width, height };
    lastBySource.current[source] = to;
    const from = lastStream.current;
    lastStream.current = to;
    if (!from && source === "render") return;
    const now = Date.now();
    changes.current = changes.current.filter((c) => now - c.at <= SUSPECT_MS + DEBOUNCE_MS);
    changes.current.push({ at: now, from, to });
  }, []);

  useEffect(() => {
    if (!video) return;
    const sync = () => noteStream("video", video.videoWidth, video.videoHeight);
    sync();
    video.addEventListener("loadedmetadata", sync);
    video.addEventListener("resize", sync);
    return () => {
      video.removeEventListener("loadedmetadata", sync);
      video.removeEventListener("resize", sync);
    };
  }, [video, noteStream]);

  /* A layout effect, so it is noted before the observer sees the box this commit lays out. */
  const streamW = streamSize?.width ?? 0;
  const streamH = streamSize?.height ?? 0;
  useLayoutEffect(() => {
    noteStream("render", streamW, streamH);
  }, [streamW, streamH, noteStream]);

  /*
   * The aspect is read through a ref so the camera's configured size arriving
   * after mount re-sizes the current request without a clear and re-register.
   */
  const aspectRef = useRef(aspect);
  const reapply = useRef<(() => void) | null>(null);
  useEffect(() => {
    aspectRef.current = aspect;
    reapply.current?.();
  }, [aspect]);

  useEffect(() => {
    if (flightId === null || !enabled) return;
    if (!el) return;

    let timer: ReturnType<typeof setTimeout> | null = null;
    let lastW = -1;
    let lastH = -1;
    let firstReported = false;
    let baseline: Size | null = null;
    let lastSuspectAt = Number.NEGATIVE_INFINITY;

    const sizeFor = (box: Size): Size => {
      if (!square) return displayReportSize(box.width, box.height, aspectRef.current);
      const side = bucket(Math.min(box.width, box.height));
      return { width: side, height: side };
    };

    const send = ({ width, height }: Size) => {
      lastW = width;
      lastH = height;
      displaySizes.report(flightId, instanceId, width, height);
    };

    /*
     * Judged when the debounce settles, so a stream change noted a little after
     * the observer fired (a host that commits the stream and the box in a
     * different order) still counts.
     */
    const settle = (box: Size, seenAt: number) => {
      const prev = baseline;
      baseline = box;
      const now = Date.now();
      const near = changes.current.filter((c) => c.at <= now && c.at >= seenAt - SUSPECT_MS);
      if (prev && near.some((c) => c.at >= seenAt - FOLLOW_MS && followsStream(prev, box, c))) {
        return;
      }
      const next = sizeFor(box);
      if (next.width === lastW && next.height === lastH) return;
      if (near.length > 0) {
        const held = now - lastSuspectAt < HOLD_MS;
        lastSuspectAt = now;
        if (held) return;
      }
      send(next);
    };

    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      const box = { width: entry.contentRect.width, height: entry.contentRect.height };
      if (!firstReported) {
        firstReported = true;
        baseline = box;
        const next = sizeFor(box);
        if (next.width !== lastW || next.height !== lastH) send(next);
        return;
      }
      const seenAt = Date.now();
      if (timer !== null) clearTimeout(timer);
      timer = setTimeout(() => settle(box, seenAt), DEBOUNCE_MS);
    });
    observer.observe(el);
    reapply.current = () => {
      if (!baseline) return;
      const next = sizeFor(baseline);
      if (next.width !== lastW || next.height !== lastH) send(next);
    };

    return () => {
      reapply.current = null;
      observer.disconnect();
      if (timer !== null) clearTimeout(timer);
      displaySizes.clear(flightId, instanceId);
    };
  }, [displaySizes, flightId, el, enabled, square, instanceId]);
}
