/*
 * Client-side camera recording.
 *
 * Records an already-received WebRTC camera feed, annotating the clip with
 * the mission-time (UT) clock so a consumer can align frames to telemetry.
 * Two timings: "capture" keeps the feed's encoded H.264 frames as received
 * and times them by their RTP timestamps (when the game captured them), so
 * a network stall leaves no trace in the file; "arrival" records the decoded
 * track with the browser's MediaRecorder, which times frames as they arrive.
 * Capture timing needs the client built with `recordingTiming: "capture"`;
 * a clip falls back to arrival timing when its frames cannot be copied.
 * Client-side apart from one keyframe request when a capture-timed clip
 * starts.
 *
 * The value-add over a bare MediaRecorder is the UT track: `utStart` /
 * `utEnd` bracket the clip, and `utSamples` is a ~1Hz series of
 * (recording-time, UT) points sampled from the client's capture clock, so a
 * consumer can interpolate any frame's UT near-frame-tight. Out of flight the
 * clock is unknown, so samples fall back to wall-clock timing (`ut` absent)
 * and upgrade automatically once UT appears.
 */

import { CameraLifecycle, type CameraState } from "./__generated__/types";
import { CaptureClip, type CaptureClipCut, type EncodedFrameCopy } from "./captureClip";
import type { ContactGap } from "./client";

/**
 * How a clip's frames are timed. `"capture"`: by when the game captured each
 * frame (its RTP timestamp), so playback keeps the game's pace through
 * network stalls; an MP4 of the frames as received, never re-encoded.
 * `"arrival"`: by when each frame reached the browser (MediaRecorder), so a
 * stall plays back as a freeze followed by a burst.
 */
export type RecordingTiming = "capture" | "arrival";

/**
 * One point on a recording's UT track. `t` is milliseconds into the clip;
 * `ut` is the KSP universal time (seconds) at that point, or absent when out
 * of flight (no clock).
 */
export interface UtSample {
  t: number;
  ut?: number;
}

/**
 * Why a recording ended. `"user"`: stopped by a call (a Stop button, a
 * grouped stop). `"max-duration"`: its `maxDurationMs` ran out.
 * `"camera-gone"`: its camera went away (destroyed, as in a scene change, or
 * dropped from the camera list), or the game went back in time under it (a
 * revert or quickload, which starts a new capture epoch). `"feed-closed"`:
 * the camera is still there but its feed left this client (its tile removed
 * or switched to another camera, its track ended) and did not come back
 * within {@link SOURCE_LOST_GRACE_MS}. `"disconnected"`: the client lost the
 * sidecar and did not get the camera back within {@link RECONNECT_GRACE_MS}.
 * Whatever the reason, the clip keeps everything recorded up to then.
 */
export type RecordingEndReason =
  | "user"
  | "max-duration"
  | "camera-gone"
  | "feed-closed"
  | "disconnected";

/**
 * How long a recording waits for its camera's feed to come back after the
 * feed leaves this client while still connected (a slot freed or moved, a
 * track ended) before it finishes as `"feed-closed"`. Covers a feed that is
 * unmounted and mounted again in one layout change.
 */
export const SOURCE_LOST_GRACE_MS = 2000;

/**
 * How long a recording waits for the client to reconnect and its camera's
 * feed to return before it finishes as `"disconnected"`, counted from when
 * the client last heard from the sidecar, so a page that was paused or asleep
 * through the drop counts that time too. A silence this long that ends with
 * messages arriving again (ones held back while the page was paused) ends
 * the recording the same way.
 *
 * A sidecar that takes `resume` keeps a departed client's force and size
 * hold for 30 s from when it reaps the old connection. The earliest reap is
 * at the client's last contact (a connection closed outright); a silent one
 * is reaped about 30 s later (ICE reports Disconnected after 5 s of silence,
 * the reaper waits 30 s). So the holds last at least 30 s from last contact,
 * and 25 s leaves 5 s for the resume itself, while a drop the page only
 * finds through its 15 s ping watchdog and 2 s reconnect backoff still has
 * about 8 s to come back.
 */
export const RECONNECT_GRACE_MS = 25_000;

/** The finished, in-memory result of a single-feed recording. */
export interface RecordingHandle {
  recordingId: string;
  /** The camera this clip was recorded from. */
  flightId: number;
  /** The recorded media, in `mimeType`. */
  blob: Blob;
  /** The negotiated container/codec (e.g. `video/mp4;codecs=avc1`). */
  mimeType: string;
  /**
   * How the clip's frames are timed. Always set by
   * {@link RecordingController}; a handle built elsewhere without it is
   * taken as `"arrival"`.
   */
  timing?: RecordingTiming;
  /** UT (seconds) at recording start, or absent when out of flight. */
  utStart?: number;
  /** UT (seconds) at recording stop, or absent when out of flight. */
  utEnd?: number;
  /**
   * (clip-time, UT) series across the clip. Always present. A capture-timed
   * clip whose frames carried capture stamps has one sample per frame, `t`
   * being the frame's own time in the file and `ut` exact; otherwise the
   * samples come from the ~1Hz capture clock.
   */
  utSamples: UtSample[];
  /** `blob.size` in bytes. */
  byteSize: number;
  /**
   * Recording duration in milliseconds: the media length for a
   * capture-timed clip with frames, the wall-clock span otherwise.
   */
  durationMs: number;
  /**
   * Why the recording ended. Always set by {@link RecordingController}; a
   * handle built elsewhere without it is taken as `"user"`.
   */
  endedBy?: RecordingEndReason;
}

/** Passed to {@link RecordingController.onRecordingEnded}. */
export interface RecordingEndedEvent {
  /** The saved clip, as it stands in the snapshot. */
  recording: RecordingHandle;
  /** Set when the clip belongs to a grouped recording. */
  groupId?: string;
  endedBy: RecordingEndReason;
}

/** Options for {@link RecordingController.startRecording}. */
export interface StartRecordingOptions {
  /**
   * Preferred container/codec for an arrival-timed clip. Tried first; if
   * MediaRecorder can't support it, negotiation falls back (mp4 -> webm/vp9
   * -> webm). Omit to let negotiation choose. A capture-timed clip is always
   * H.264 in MP4, the codec the feed already uses.
   */
  mimeType?: string;
  /**
   * Auto-stop after this many milliseconds. The clip finalizes on its own and
   * is retrievable via {@link RecordingController.fetchRecording}. Omit for
   * manual stop only.
   */
  maxDurationMs?: number;
  /**
   * Force this feed to the operator's full render size for the duration of
   * the recording (overrides the display-size demand and any viewer
   * quality preset; still capped by an operator render-size cap and the
   * adaptive shed). Off by default. The recording arms and
   * waits (bounded) until the browser is decoding full-size frames before
   * the clip starts: MediaRecorder fixes the clip's frame size when it
   * starts, so a later bump would never reach the file. A grouped recording
   * arms every member and starts all their recorders together once every
   * feed is ready (or the shared bound passes). Released automatically on
   * stop, discard, or auto-stop.
   */
  forceFullResolution?: boolean;
}

/**
 * Bound on the arm-and-wait: how long a forced recording waits for its feed
 * (or, for a group, every member's feed) to reach full resolution before
 * starting the recorder anyway. Never hangs a start indefinitely (feed out
 * of flight, wedged, or the sidecar/plugin round trip taking longer than
 * expected).
 */
export const ARM_TIMEOUT_MS = 3000;

/** Options for {@link RecordingController.stopGroupedRecording}. */
export interface StopGroupedRecordingOptions {
  /**
   * Coverage guarantee: hold finalization until every feed's latest UT sample
   * has passed this UT (seconds), so no feed falls short of the common window.
   * Omit to stop all feeds at the current moment (best-effort).
   *
   * The guarantee only holds for members that are still recording when the
   * target arrives. A member started with its own `maxDurationMs` can
   * auto-stop before reaching `targetUt`; that member's `utEnd` then falls
   * short, which silently narrows (or, if the ranges no longer overlap,
   * clears) the resulting `commonUtWindow`. Pair a grouped `targetUt` with an
   * unbounded (or generously long) per-member `maxDurationMs`, or none at all.
   */
  targetUt?: number;
  /**
   * Bound on how long to wait for `targetUt` coverage before giving up and
   * finalizing with whatever each feed has captured so far. Guards against a
   * UT that never arrives (feed out of flight, sim frozen, warp reversed)
   * hanging the stop forever. Ignored when `targetUt` is omitted. Defaults to
   * {@link DEFAULT_COVERAGE_TIMEOUT_MS}.
   */
  coverageTimeoutMs?: number;
}

/** Default {@link StopGroupedRecordingOptions.coverageTimeoutMs}. */
export const DEFAULT_COVERAGE_TIMEOUT_MS = 10_000;

/** The result of {@link RecordingController.startGroupedRecording}. */
export interface GroupedRecordingStart {
  groupId: string;
  /**
   * The real per-feed recording ids the controller minted internally, in the
   * order `flightIds` was given. These are the ids that work with the
   * single-recording surface (`isRecording`, `stopRecording`,
   * `discardRecording`) for cancelling one member of the group.
   */
  recordingIds: string[];
}

/** The finished, in-memory result of a grouped (multi-feed) recording. */
export interface GroupedRecordingHandle {
  groupId: string;
  /** One handle per feed, in the order the group was started. */
  recordings: RecordingHandle[];
  /**
   * The UT span all feeds cover: [max(utStart), min(utEnd)] across the clips
   * that ran to the group's stop. A clip whose camera went away early does
   * not narrow it (unless every clip did), so losing one camera never cuts
   * the others short. Absent when any clip lacks UT or the ranges don't
   * overlap. Each clip's
   * own `utSamples` let a consumer map this window to that clip's
   * recording-time and trim/seek itself (metadata alignment; no physical cut).
   */
  commonUtWindow?: [number, number];
}

/**
 * An in-progress recording, surfaced so the UI can draw a REC dot and an
 * elapsed timer. `groupId` is set when the recording is part of a grouped set.
 */
export interface ActiveRecordingInfo {
  recordingId: string;
  flightId: number;
  /** `performance.now()`/`Date.now()` at start, for an elapsed timer. */
  startedAt: number;
  groupId?: string;
  /**
   * True while a forced recording is armed (force sent, waiting for the
   * feed to reach full resolution) but the recorder has not started yet.
   * Forced grouped members stay arming until every member is ready, then
   * flip together. Always false for a non-forced recording. A UI shows a
   * distinct "arming" state while this is true.
   */
  arming: boolean;
  /**
   * True from the moment the recording is stopped until its clip is in
   * {@link RecordingsSnapshot.recordings} (or its group's). A capture-timed
   * clip settles within milliseconds; an arrival-timed one waits for
   * MediaRecorder's last chunk. A page that must not close mid-save waits
   * for this to clear, or awaits the promise `stopRecording` returns.
   */
  stopping: boolean;
}

/**
 * Everything {@link RecordingController} currently knows, as one referentially
 * stable object: the same reference between mutations, a new one after each.
 * That stability is what makes it safe to hand straight to
 * `useSyncExternalStore` -- a fresh object on every call would spin components
 * into an infinite render loop.
 */
export interface RecordingsSnapshot {
  /** Finished standalone (non-grouped) clips, newest last. */
  recordings: RecordingHandle[];
  /** Finished grouped sets, newest last. */
  groups: GroupedRecordingHandle[];
  /** Recordings currently in progress (single + grouped members). */
  active: ActiveRecordingInfo[];
}

/**
 * Per-camera surface {@link RecordingController} needs: the live stream plus
 * enough of the full camera handle to drive a forced full-resolution
 * recording (send the force, observe the resulting render size).
 */
export interface RecordingClientCamera {
  readonly mediaStream: MediaStream | null;
  readonly state: CameraState | null;
  /**
   * The feed's physical/ceiling maximum render size, or `null` when not
   * known to the client (e.g. `discover()` was never called). When known,
   * lets the controller recognise a forced feed that has actually reached
   * its ceiling rather than one merely unchanged since before the force.
   */
  readonly maxRenderSize: { width: number; height: number } | null;
  setForceFullResolution(force: boolean): Promise<void>;
  /**
   * Holds the feed at its current size while it is recorded. Without it a
   * recording that crosses a size change still plays (the clip gets a
   * sample entry per size), but some tools handle that worse.
   */
  setSizeHold?(hold: boolean): Promise<void>;
  /**
   * Whether this client receives the feed now. A recording whose feed stays
   * gone past a grace finishes on its own; without it only a destroyed or
   * unlisted camera ends one.
   */
  readonly hasSource?: boolean;
  on(event: "change", handler: (state: CameraState) => void): () => void;
  on(event: "source", handler: (hasSource: boolean) => void): () => void;
  /**
   * Copies the feed's encoded frames until the returned function is called,
   * or returns null when it can't. Without it every clip is arrival-timed.
   */
  tapEncodedFrames?(onFrame: (frame: EncodedFrameCopy) => void): (() => void) | null;
  /** Whether {@link tapEncodedFrames} would copy frames now; decides a clip's timing at start. */
  canTapEncodedFrames?(): boolean;
  /** Asks the sidecar for a keyframe, so a capture-timed clip can open promptly. */
  requestKeyframe?(): Promise<void>;
}

/**
 * Minimal surface {@link RecordingController} needs from the client. The full
 * `KerbcastClient` satisfies it; a narrow interface keeps the controller
 * unit-testable without a whole client.
 */
export interface RecordingClient {
  camera(flightId: number): RecordingClientCamera;
  /**
   * The capture clock. A change of `epoch` (a revert or quickload) ends a
   * recording, since its UT would run backwards; without it only a camera
   * going away does.
   */
  readonly clock: { readonly captureUt: number | null; readonly epoch?: number };
  /** The connection state; a recording losing its feed while not `"connected"` waits for a reconnect. */
  readonly state?: string;
  /**
   * When the client last heard from the sidecar, as `Date.now()`. A dropped
   * connection's {@link RECONNECT_GRACE_MS} counts from here; without it,
   * from when the recording saw its feed go.
   */
  readonly lastContactAt?: number | null;
  on(event: "settings-change", handler: (data: unknown) => void): () => void;
  /** A recording whose camera leaves the list, or is listed as destroyed, finishes. */
  on(event: "cameras-change", handler: (cameras: CameraState[]) => void): () => void;
  /** A connection that drops while a feed is missing gives the feed {@link RECONNECT_GRACE_MS} to return. */
  on(event: "state-change", handler: (state: string) => void): () => void;
  /** A silence of {@link RECONNECT_GRACE_MS} or more ends a recording as `"disconnected"`. */
  on(event: "contact-gap", handler: (gap: ContactGap) => void): () => void;
  /**
   * A frame's mission time by RTP timestamp; only `source: "exact"` results
   * are used, giving a capture-timed clip its per-frame UT track.
   */
  frameUt?(
    flightId: number,
    rtpTimestamp: number,
  ): { ut: number; epoch?: number; source: "exact" | "estimated" } | null;
}

/* MediaRecorder is a DOM lib global; keep a minimal structural view so this
   module builds without dom.iterable and stays mockable in jsdom. */
interface MediaRecorderLike {
  state: string;
  start(timeslice?: number): void;
  stop(): void;
  addEventListener(type: string, cb: (e: { data?: Blob }) => void): void;
  removeEventListener(type: string, cb: (e: { data?: Blob }) => void): void;
}
interface MediaRecorderCtor {
  new (stream: MediaStream, options?: { mimeType?: string }): MediaRecorderLike;
  isTypeSupported?(type: string): boolean;
}

/** What a {@link ClipRecorder} hands back when it stops. */
interface ClipOutput {
  blob: Blob;
  mimeType: string;
  timing: RecordingTiming;
  /** Per-frame UT samples on the clip's own timeline (capture timing only). */
  frameUtSamples?: UtSample[];
  /** When the clip's first frame arrived (capture timing only), `now()` clock. */
  firstFrameAt?: number | null;
  /** The clip's media length in ms (capture timing only, when it has frames). */
  mediaDurationMs?: number;
  /** Cuts a stretch of the clip from its own samples (capture timing only). */
  cut?: (startMs: number, endMs: number) => CaptureClipCut | null;
}

/** One clip's media capture, either timing. Started at most once. */
interface ClipRecorder {
  readonly timing: RecordingTiming;
  /** The clip's container type as known before it stops. */
  readonly mimeType: string;
  start(): void;
  /** Ends a started clip and resolves its media. */
  stop(): Promise<ClipOutput>;
  /** Abandons the clip, started or not, releasing everything it holds. */
  cancel(): void;
  /**
   * Sets what to call when a started clip ends without {@link stop}: the
   * browser stopped MediaRecorder because its track ended.
   */
  onEndedByItself?(handler: () => void): void;
}

/** Arrival timing: MediaRecorder over the decoded track. */
function mediaRecorderClip(recorder: MediaRecorderLike, mimeType: string): ClipRecorder {
  const chunks: Blob[] = [];
  let ended = false;
  let stopping = false;
  let endedByItself: (() => void) | null = null;
  const waiting: Array<() => void> = [];
  recorder.addEventListener("dataavailable", (e) => {
    if (e.data && e.data.size > 0) chunks.push(e.data);
  });
  /* The browser stops a recorder whose tracks all end and fires "stop"
     once; a later stop() would wait for it forever. */
  recorder.addEventListener("stop", () => {
    ended = true;
    if (!stopping) endedByItself?.();
    for (const settle of waiting.splice(0)) settle();
  });
  return {
    timing: "arrival",
    mimeType,
    start: () => recorder.start(),
    stop: () =>
      new Promise<ClipOutput>((resolve) => {
        const settle = (): void =>
          resolve({ blob: new Blob(chunks, { type: mimeType }), mimeType, timing: "arrival" });
        if (ended) {
          settle();
          return;
        }
        stopping = true;
        waiting.push(settle);
        try {
          recorder.stop();
        } catch {
          /* Already inactive: its "stop" event is on the way. */
        }
      }),
    onEndedByItself: (handler) => {
      endedByItself = handler;
    },
    cancel: () => {
      stopping = true;
      try {
        if (recorder.state !== "inactive") recorder.stop();
      } catch {
        /* a recorder already torn down by the browser is fine to ignore */
      }
    },
  };
}

/**
 * How often a capture-timed clip waiting for a keyframe asks again. The
 * sidecar honours each request with an IDR on the next encoded frame, and
 * its periodic keyframe (every 2 s) bounds the wait if a request is lost.
 */
export const KEYFRAME_RETRY_MS = 1000;

/**
 * A hidden tab runs no animation frames, and a clip must still save there,
 * so the wait for a paint gives up after this long.
 */
const PAINT_WAIT_MAX_MS = 100;

/** Resolves once the page has had a frame to paint in, or after {@link PAINT_WAIT_MAX_MS}. */
function afterPaint(): Promise<void> {
  return new Promise((resolve) => {
    const raf = (globalThis as { requestAnimationFrame?: (cb: () => void) => number })
      .requestAnimationFrame;
    if (typeof raf !== "function") {
      setTimeout(resolve, 16);
      return;
    }
    raf(() => setTimeout(resolve, 0));
    setTimeout(resolve, PAINT_WAIT_MAX_MS);
  });
}

/** The muxes waiting their turn, so they run one per frame. */
let muxQueue: Promise<unknown> = Promise.resolve();

/**
 * Runs a clip's mux after the next paint. A long clip's mux holds the page
 * for a noticeable moment, so the stopping state a UI shows (a SAVING badge)
 * has to reach the screen first; queued muxes, as a REC+ stop makes, each
 * get their own frame rather than running back to back.
 */
function muxAfterPaint<T>(mux: () => T): Promise<T> {
  const turn = muxQueue.then(afterPaint).then(mux);
  muxQueue = turn.catch(() => {});
  return turn;
}

/**
 * Capture timing: copies the feed's encoded frames from start to stop and
 * muxes them into an MP4 timed by RTP. Returns null when the feed's frames
 * can't be copied, so the caller falls back to arrival timing.
 */
function captureClipRecorder(
  cam: RecordingClientCamera,
  lookupUt: (rtpTimestamp: number) => { ut: number } | null,
): ClipRecorder | null {
  if (!cam.tapEncodedFrames || !cam.canTapEncodedFrames?.()) return null;
  let clip: CaptureClip | null = null;
  let untap: (() => void) | null = null;
  let retry: ReturnType<typeof setInterval> | undefined;
  const release = (): void => {
    untap?.();
    untap = null;
    if (retry != null) clearInterval(retry);
    retry = undefined;
  };
  const askForKeyframe = (): void => {
    void cam.requestKeyframe?.().catch(() => {});
  };
  return {
    timing: "capture",
    mimeType: "video/mp4",
    start: () => {
      const target = new CaptureClip(lookupUt);
      clip = target;
      untap = cam.tapEncodedFrames?.((frame) => target.push(frame)) ?? null;
      if (!untap) {
        /* Lost between the decision and the start (a transform set while arming). */
        console.warn("kerbcast: frame copy became unavailable before the recording started; the clip will be empty");
        return;
      }
      askForKeyframe();
      /* Also covers a mid-clip source change, which waits for a fresh keyframe. */
      retry = setInterval(() => {
        if (target.waitingForKeyframe) askForKeyframe();
      }, KEYFRAME_RETRY_MS);
    },
    stop: () => {
      release();
      const finished = clip ?? new CaptureClip();
      clip = null;
      return muxAfterPaint((): ClipOutput => {
        const result = finished.finish();
        return {
          blob: result.blob,
          mimeType: result.mimeType,
          timing: "capture",
          frameUtSamples: result.frameUtSamples,
          firstFrameAt: result.firstFrameAt,
          ...(result.frameCount > 0 ? { mediaDurationMs: result.mediaDurationMs } : {}),
          cut: (startMs: number, endMs: number) => finished.cut(startMs, endMs),
        };
      });
    },
    cancel: () => {
      release();
      clip = null;
    },
  };
}

/**
 * The UT track for a finished clip. Per-frame stamps win when there are any;
 * otherwise the ~1Hz clock samples, which for a capture-timed clip are moved
 * onto its timeline (it opens at its first keyframe, not at start).
 */
function clipUtTrack(
  output: ClipOutput,
  clockSamples: UtSample[],
  startedAt: number,
  utStart: number | undefined,
  utEnd: number | undefined,
): { utSamples: UtSample[]; utStart?: number; utEnd?: number } {
  const frames = output.frameUtSamples ?? [];
  if (frames.length > 0) {
    return { utSamples: frames, utStart: frames[0].ut, utEnd: frames[frames.length - 1].ut };
  }
  if (output.timing === "capture" && output.firstFrameAt != null) {
    const offset = output.firstFrameAt - startedAt;
    return {
      utSamples: clockSamples.map((s) => ({ ...s, t: Math.max(0, s.t - offset) })),
      utStart,
      utEnd,
    };
  }
  return { utSamples: clockSamples, utStart, utEnd };
}

/** Preference order for mime negotiation, after any caller-supplied choice. */
const MIME_CANDIDATES = [
  "video/mp4;codecs=avc1",
  "video/webm;codecs=vp9",
  "video/webm",
];

/**
 * Pick a MediaRecorder mime type. Tries `preferred` first (when given), then
 * the standard preference order, using `MediaRecorder.isTypeSupported`. Falls
 * back to `video/webm` when nothing reports support (or the API is absent).
 */
export function negotiateMimeType(preferred?: string): string {
  const ctor = (globalThis as { MediaRecorder?: MediaRecorderCtor }).MediaRecorder;
  const candidates = preferred ? [preferred, ...MIME_CANDIDATES] : MIME_CANDIDATES;
  if (ctor && typeof ctor.isTypeSupported === "function") {
    for (const candidate of candidates) {
      if (ctor.isTypeSupported(candidate)) return candidate;
    }
  }
  return "video/webm";
}

/** Monotonic clock for recording-time. Falls back to Date.now under jsdom. */
function now(): number {
  return typeof performance !== "undefined" && typeof performance.now === "function"
    ? performance.now()
    : Date.now();
}

/**
 * Intersection of the feeds' UT ranges: [max(utStart), min(utEnd)]. Returns
 * undefined if any clip lacks a full range or the spans don't overlap.
 */
export function commonUtWindow(recordings: RecordingHandle[]): [number, number] | undefined {
  if (recordings.length === 0) return undefined;
  let lo = -Infinity;
  let hi = Infinity;
  for (const rec of recordings) {
    if (rec.utStart == null || rec.utEnd == null) return undefined;
    lo = Math.max(lo, rec.utStart);
    hi = Math.min(hi, rec.utEnd);
  }
  return lo <= hi ? [lo, hi] : undefined;
}

/**
 * Recording-time (ms since clip start) for a mission UT, linearly interpolated
 * off a clip's `utSamples`. UT is linear in recording time at constant
 * time-warp (the launch/landing/docking moments a group captures), so this maps
 * the common UT window onto each clip's own timeline for a keyframe trim.
 * Clamps to the sampled span; returns null when the clip has no UT samples.
 */
export function utToRecordingTimeMs(samples: UtSample[], ut: number): number | null {
  const pts = samples.filter((s): s is { t: number; ut: number } => s.ut != null);
  if (pts.length === 0) return null;
  if (ut <= pts[0].ut) return pts[0].t;
  for (let i = 0; i < pts.length - 1; i++) {
    const a = pts[i];
    const b = pts[i + 1];
    if (ut >= a.ut && ut <= b.ut) {
      if (b.ut === a.ut) return a.t;
      const frac = (ut - a.ut) / (b.ut - a.ut);
      return a.t + frac * (b.t - a.t);
    }
  }
  return pts[pts.length - 1].t;
}

/**
 * Remux-trims a grouped clip to a recording-time window. Implemented by a
 * lazily-loaded package (Mediabunny) so single recordings and non-recording
 * consumers never pull it. Keyframe-boundary remux only: no decode/re-encode.
 */
export interface GroupTrimmer {
  /** Cut `blob` to [startMs, endMs] of its own timeline; resolves a new Blob. */
  trim(blob: Blob, mimeType: string, startMs: number, endMs: number): Promise<Blob>;
}

/** Loads a {@link GroupTrimmer}, or resolves null when trimming is unavailable. */
export type TrimmerLoader = () => Promise<GroupTrimmer | null>;

/**
 * Watches the decoded frame size of a feed's live stream, calling `onSize`
 * whenever it is known or changes (repeats are fine). Returns a stop function
 * that releases everything the probe holds. The arm-and-wait gates a forced
 * recording on this, not on the reported camera state: the state flips to
 * the full size before the decoder delivers full-size frames, and
 * MediaRecorder locks the clip to whatever size it sees first.
 */
export type DecodedSizeProbe = (
  flightId: number,
  stream: MediaStream,
  onSize: (width: number, height: number) => void,
) => () => void;

/* Structural view of requestVideoFrameCallback, which not every DOM lib
   version types. */
interface FrameCallbackVideo {
  requestVideoFrameCallback?(
    cb: (now: number, meta: { width?: number; height?: number }) => void,
  ): number;
  cancelVideoFrameCallback?(id: number): void;
}

/**
 * Default {@link DecodedSizeProbe}: plays the stream into an off-DOM muted
 * `<video>` and reads the decoded size from each frame
 * (requestVideoFrameCallback where available) and from `resize` /
 * `loadedmetadata`. `track.getSettings()` is not used because Chromium often
 * reports stale or missing dimensions for remote WebRTC tracks. Outside a
 * browser it never reports, so a forced recording falls back to the arm
 * timeout.
 */
export const videoElementDecodedSizeProbe: DecodedSizeProbe = (_flightId, stream, onSize) => {
  if (typeof document === "undefined") return () => {};
  const video = document.createElement("video");
  const vfc = video as unknown as FrameCallbackVideo;
  video.muted = true;
  video.playsInline = true;
  let stopped = false;
  let frameId: number | undefined;

  const report = (): void => {
    if (!stopped && video.videoWidth > 0 && video.videoHeight > 0) {
      onSize(video.videoWidth, video.videoHeight);
    }
  };
  const onFrame = (_now: number, meta: { width?: number; height?: number }): void => {
    if (stopped) return;
    if (meta?.width && meta.height) onSize(meta.width, meta.height);
    else report();
    frameId = vfc.requestVideoFrameCallback?.(onFrame);
  };

  video.addEventListener("resize", report);
  video.addEventListener("loadedmetadata", report);
  video.srcObject = stream;
  if (typeof vfc.requestVideoFrameCallback === "function") {
    frameId = vfc.requestVideoFrameCallback(onFrame);
  }
  try {
    const played = video.play() as Promise<void> | undefined;
    played?.catch(() => {});
  } catch {
    /* play() unsupported or refused: resize/loadedmetadata may still report */
  }
  report();

  return () => {
    stopped = true;
    video.removeEventListener("resize", report);
    video.removeEventListener("loadedmetadata", report);
    if (frameId != null) vfc.cancelVideoFrameCallback?.(frameId);
    try {
      video.pause();
    } catch {
      /* nothing to pause */
    }
    video.srcObject = null;
  };
};

/**
 * How far below the ceiling a decoded frame may be and still count as full
 * size. Encoders round frame dimensions to even or macroblock multiples, so
 * an exact match is not guaranteed; the tile sizes the gate rejects are far
 * further off than this.
 */
const DECODED_SIZE_TOLERANCE_PX = 16;

/** Options for {@link RecordingController}. */
export interface RecordingControllerOptions {
  /**
   * Override the grouped-recording trimmer loader (tests inject a fake; a
   * consumer could supply mp4box.js instead). Defaults to a dynamic
   * `import("mediabunny")` keyframe remux.
   */
  loadTrimmer?: TrimmerLoader;
  /**
   * Override how a forced recording observes its feed's decoded frame size
   * (tests inject a fake; a harness without real decoding can follow the
   * camera state instead). Defaults to {@link videoElementDecodedSizeProbe}.
   */
  probeDecodedSize?: DecodedSizeProbe;
}

/**
 * Structural view of the Mediabunny module surface the remux trim needs.
 * Deliberately not `typeof import("mediabunny")`: keeping it a plain
 * structural type means a caller can hand in the module however it got
 * resolved -- the bare package (bundler consumers), or a pre-built bundle
 * served from elsewhere (the sidecar's embedded web page serves its own
 * copy locally; see {@link createMediabunnyTrimmerLoader}).
 */
interface MediabunnyModule {
  Input: new (opts: { source: unknown; formats: unknown }) => unknown;
  BlobSource: new (blob: Blob) => unknown;
  ALL_FORMATS: unknown;
  BufferTarget: new () => { buffer: ArrayBuffer | null };
  Output: new (opts: { format: unknown; target: unknown }) => unknown;
  Mp4OutputFormat: new () => unknown;
  Conversion: {
    init(opts: {
      input: unknown;
      output: unknown;
      trim: { start: number; end: number };
    }): Promise<{ execute(): Promise<void> }>;
  };
}

/**
 * Builds a {@link GroupTrimmer} from an already-resolved Mediabunny module
 * namespace: keyframe-boundary remux via its Conversion API, no re-encode.
 */
function mediabunnyTrimmer(mb: MediabunnyModule): GroupTrimmer {
  return {
    async trim(blob, mimeType, startMs, endMs): Promise<Blob> {
      const input = new mb.Input({
        source: new mb.BlobSource(blob),
        formats: mb.ALL_FORMATS,
      });
      const target = new mb.BufferTarget();
      const output = new mb.Output({ format: new mb.Mp4OutputFormat(), target });
      const conversion = await mb.Conversion.init({
        input,
        output,
        trim: { start: startMs / 1000, end: endMs / 1000 },
      });
      await conversion.execute();
      if (!target.buffer) throw new Error("mediabunny produced no output buffer");
      return new Blob([target.buffer], { type: mimeType });
    },
  };
}

/**
 * Builds a {@link TrimmerLoader} around a caller-supplied module import.
 * `loadMediabunnyTrimmer` (the default, below) resolves the bare
 * `import("mediabunny")` specifier for bundler consumers. A
 * consumer that can't rely on that resolution -- the sidecar's embedded web
 * page, which serves its own copy of the package locally for an offline/LAN
 * install with no CDN -- supplies a different `importModule` (e.g. a dynamic
 * import of a served URL) and passes the result as
 * {@link RecordingControllerOptions.loadTrimmer}. Resolves null on any
 * failure, so a group degrades to metadata-only rather than failing.
 */
export function createMediabunnyTrimmerLoader(
  importModule: () => Promise<unknown>,
): TrimmerLoader {
  return async () => {
    try {
      return mediabunnyTrimmer((await importModule()) as MediabunnyModule);
    } catch {
      return null;
    }
  };
}

/**
 * Default trimmer: dynamically imports Mediabunny by its package name and
 * remuxes with its Conversion API. Loaded on group-start so it's ready by
 * stop.
 */
const loadMediabunnyTrimmer: TrimmerLoader = createMediabunnyTrimmerLoader(
  () => import("mediabunny"),
);

/** Live per-feed recording state, held between start and stop. */
interface ActiveRecording {
  recordingId: string;
  flightId: number;
  mimeType: string;
  recorder: ClipRecorder;
  startedAt: number;
  utStart?: number;
  utSamples: UtSample[];
  /** Most recent UT seen (for the grouped coverage guarantee); undefined until first UT. */
  latestUt?: number;
  /** The capture epoch its UT belongs to, set once a clock is known. */
  epoch?: number;
  /** Detach from the client's settings-change clock feed. */
  unsubscribeClock: () => void;
  maxDurationTimer?: ReturnType<typeof setTimeout>;
  /** Resolves once the recorder's stop event has flushed the final chunk. */
  stopPromise?: Promise<RecordingHandle>;
  /**
   * Set when discarded while `stopPromise` was already settling (recorder
   * `inactive`, "stop" event not yet fired). Tells the pending `onStop` to
   * bail rather than resurrect a handle for a recording that was discarded.
   */
  discarded?: boolean;
  /**
   * True while a forced recording is armed (force sent, recorder not started
   * yet). A grouped member stays armed until its whole group starts.
   */
  arming?: boolean;
  /** True once this recording has acquired the per-feed force ref-count. */
  forced?: boolean;
  /** True while this recording holds the per-feed size hold ref-count. */
  held?: boolean;
  /**
   * Cancels a pending arm-wait subscription (single, or one member of a
   * group). Set while a {@link RecordingController.watchResolutionReady}
   * wait is outstanding; cleared once it settles or is cancelled. Always
   * cancelled on stop/discard so a torn-down recording never leaks a
   * "change" listener or decoded-size probe, or starts its recorder late.
   */
  cancelResolutionWatch?: () => void;
  /** The last decoded frame size the arm-wait saw, for the start log line. */
  lastDecodedSize?: () => { width: number; height: number } | null;
  /** Detaches the watch on the camera that ends this recording when the camera goes away. */
  unwatchCamera: () => void;
}

/** Arm state for a forced grouped recording whose recorders have not started yet. */
interface GroupArm {
  /** Members whose feed has been observed at full resolution. */
  ready: Set<string>;
  /** The shared {@link ARM_TIMEOUT_MS} bound; starts every member when it fires. */
  timer: ReturnType<typeof setTimeout>;
}

/** `WxH` for a log line, or `unknown` when no decoded frame was seen. */
function formatSize(size: { width: number; height: number } | null): string {
  return size ? `${size.width}x${size.height}` : "unknown";
}

let idCounter = 0;
function mintId(prefix: string): string {
  idCounter += 1;
  return `${prefix}_${Date.now().toString(36)}_${idCounter.toString(36)}`;
}

/**
 * Owns every in-flight and finished recording for one client. Reached via
 * `client.recording`. All state is in-memory: a page reload discards it.
 *
 * Observable: {@link subscribe} + {@link getSnapshot} let every call site
 * (a tile's own REC control, a RecordingsTray mounted elsewhere) read the same
 * state, so a clip started or finished through one is visible to all the
 * others -- no per-instance state to fall out of sync.
 */
export class RecordingController {
  private readonly client: RecordingClient;
  private readonly active = new Map<string, ActiveRecording>();
  private readonly finished = new Map<string, RecordingHandle>();
  /** flightId -> recordingId, so a second start on a busy feed is rejected. */
  private readonly byFlight = new Map<number, string>();
  private readonly groups = new Map<string, string[]>();
  private readonly finishedGroups = new Map<string, GroupedRecordingHandle>();
  /** Per-group trimmer load kicked off at group-start, awaited at stop. */
  private readonly groupTrimmers = new Map<string, Promise<GroupTrimmer | null>>();
  private readonly loadTrimmer: TrimmerLoader;
  private readonly probeDecodedSize: DecodedSizeProbe;
  /** recordingIds in a grouped set, so they stay out of the standalone `recordings` list. */
  private readonly groupMemberIds = new Set<string>();
  /** recordingId -> groupId, so discarding one member can pull it out of its group's array. */
  private readonly memberOfGroup = new Map<string, string>();
  /** Grouped capture-timed members' own cutters, held from their stop until the group is trimmed. */
  private readonly cutters = new Map<string, NonNullable<ClipOutput["cut"]>>();
  /**
   * Per-feed force ref-count. The sidecar OR-aggregates force across
   * viewers already; this is the local mirror so two overlapping
   * recordings of the same feed (in this one controller) release force
   * only when the last of them stops.
   */
  private readonly forceCounts = new Map<number, number>();
  /**
   * Per-feed force-true send status: "pending" while a send is in flight,
   * "sent" once it has actually succeeded. Absent means neither: either
   * never attempted, or a prior attempt failed. Distinct from
   * {@link forceCounts} so a failed send never masquerades as a completed
   * one; see {@link acquireForce}.
   */
  private readonly forceSendState = new Map<number, "pending" | "sent">();
  /** Per-feed size hold ref-count: held while any recording of the feed runs. */
  private readonly holdCounts = new Map<number, number>();
  /** groupId -> arm state, present only while a forced group is still arming. */
  private readonly groupArms = new Map<string, GroupArm>();
  private readonly listeners = new Set<() => void>();
  private readonly endedListeners = new Set<(event: RecordingEndedEvent) => void>();
  /** groupId -> the stop in progress, so a second stop (user or automatic) joins it. */
  private readonly groupStops = new Map<string, Promise<GroupedRecordingHandle>>();
  private snapshot: RecordingsSnapshot = { recordings: [], groups: [], active: [] };
  /** While true, {@link rebuild} is a no-op; used to batch startGroupedRecording's
   *  per-member starts into a single notification once the group is fully set up. */
  private suppressRebuild = false;

  constructor(client: RecordingClient, options?: RecordingControllerOptions) {
    this.client = client;
    this.loadTrimmer = options?.loadTrimmer ?? loadMediabunnyTrimmer;
    this.probeDecodedSize = options?.probeDecodedSize ?? videoElementDecodedSizeProbe;
  }

  /** Subscribe to snapshot changes. Returns an unsubscribe function. */
  subscribe = (fn: () => void): (() => void) => {
    this.listeners.add(fn);
    return () => {
      this.listeners.delete(fn);
    };
  };

  /** The current snapshot; the same object reference until the next mutation. */
  getSnapshot = (): RecordingsSnapshot => this.snapshot;

  /**
   * Calls `fn` each time a recording's clip is saved, with why it ended, so a
   * UI can say when a recording stopped on its own (its camera went away, the
   * connection did not come back). A grouped member is reported when its own
   * clip is saved, before the group is trimmed. Returns an unsubscribe
   * function.
   */
  onRecordingEnded(fn: (event: RecordingEndedEvent) => void): () => void {
    this.endedListeners.add(fn);
    return () => {
      this.endedListeners.delete(fn);
    };
  }

  private emitEnded(handle: RecordingHandle): void {
    const groupId = this.memberOfGroup.get(handle.recordingId);
    const event: RecordingEndedEvent = {
      recording: handle,
      ...(groupId != null ? { groupId } : {}),
      endedBy: handle.endedBy ?? "user",
    };
    for (const fn of this.endedListeners) {
      try {
        fn(event);
      } catch (err) {
        console.error("kerbcast: recording-ended listener failed", err);
      }
    }
  }

  /**
   * Ends a recording when its camera goes away, so the clip is saved rather
   * than left recording a camera nobody can stop any more. A destroyed or
   * unlisted camera ends it at once. A feed that leaves this client gets
   * {@link SOURCE_LOST_GRACE_MS} to come back, or, once the connection has
   * dropped, until {@link RECONNECT_GRACE_MS} after the sidecar was last
   * heard from, as a reconnect brings it back.
   */
  private watchCamera(rec: ActiveRecording): () => void {
    const cam = this.client.camera(rec.flightId);
    let timer: ReturnType<typeof setTimeout> | undefined;
    const clearTimer = (): void => {
      if (timer != null) clearTimeout(timer);
      timer = undefined;
    };
    const gone = (reason: RecordingEndReason): void => {
      clearTimer();
      this.endOnItsOwn(rec.recordingId, reason);
    };
    const connected = (): boolean => this.client.state == null || this.client.state === "connected";
    /* Whether the connection dropped while the feed was missing: a reconnect
       that is already back by the short grace still rebinds feeds after it. */
    let dropped = false;
    /* When the feed was last known live, as Date.now(). Once the connection
       has dropped that is the last word from the sidecar, which can be long
       before the page saw the drop if it was paused or asleep. */
    let liveAt = 0;
    const noteDropped = (): void => {
      dropped = true;
      liveAt = Math.min(liveAt, this.client.lastContactAt ?? liveAt);
    };
    const pastReconnectGrace = (): boolean => Date.now() - liveAt >= RECONNECT_GRACE_MS;
    const waitForReconnect = (): void => {
      clearTimer();
      if (pastReconnectGrace()) {
        gone(connected() ? "feed-closed" : "disconnected");
        return;
      }
      timer = setTimeout(waitForReconnect, liveAt + RECONNECT_GRACE_MS - Date.now());
    };
    const onSource = (hasSource: boolean): void => {
      if (hasSource) {
        /* A page that was paused or asleep runs its overdue timers late, and
           a reconnect can bring the feed back before they do. */
        if (timer != null && dropped && pastReconnectGrace()) {
          gone("disconnected");
          return;
        }
        clearTimer();
        return;
      }
      if (timer != null) return;
      liveAt = Date.now();
      dropped = false;
      if (!connected()) {
        noteDropped();
        waitForReconnect();
        return;
      }
      /* A camera that left the list ended the recording already, so one
         still recording here is listed and only its feed is gone. */
      timer = setTimeout(() => {
        if (connected() && !dropped) gone("feed-closed");
        else waitForReconnect();
      }, SOURCE_LOST_GRACE_MS);
    };
    /* Every camera state change also arrives as a new list. */
    const offCameras = this.client.on("cameras-change", (cameras) => {
      const listed = cameras.find((c) => c.flightId === rec.flightId);
      if (!listed || listed.lifecycle === CameraLifecycle.Destroyed) gone("camera-gone");
    });
    const offSource = cam.on("source", onSource);
    const offState = this.client.on("state-change", (state) => {
      if (state === "connected" || timer == null || dropped) return;
      /* A watchdog tears the feeds down just before it reports the drop. */
      noteDropped();
      waitForReconnect();
    });
    /* A paused page can be handed the messages held back during the pause
       before its watchdog runs, so the silence they end counts on its own. */
    const offGap = this.client.on("contact-gap", (gap) => {
      if (gap.to - gap.from >= RECONNECT_GRACE_MS) gone("disconnected");
    });
    if (cam.hasSource === false) onSource(false);
    return () => {
      clearTimer();
      offCameras();
      offSource();
      offState();
      offGap();
    };
  }

  /**
   * Finishes a recording that ended without a stop call and saves its clip.
   * A grouped member that was the last one recording finishes its group too,
   * so a scene change that takes every camera still yields the grouped set.
   */
  private endOnItsOwn(recordingId: string, reason: RecordingEndReason): void {
    const rec = this.active.get(recordingId);
    if (!rec || rec.stopPromise) return;
    console.info(`kerbcast: recording ${recordingId} of camera ${rec.flightId} ended (${reason}); saving what it has`);
    void this.stopWith(recordingId, reason).catch(() => {});
    const groupId = this.memberOfGroup.get(recordingId);
    if (groupId == null) return;
    const ids = this.groups.get(groupId) ?? [];
    const allEnded = ids.every((id) => {
      const member = this.active.get(id);
      return !member || member.stopPromise != null;
    });
    if (allEnded) void this.stopGroupedRecording(groupId).catch(() => {});
  }

  /**
   * Recompute the snapshot from current state and notify subscribers. Called
   * at the end of every state-mutating operation, including the async settle
   * points (recorder stop, grouped stop) where the data actually changes.
   */
  private rebuild(): void {
    if (this.suppressRebuild) return;
    const recordings = [...this.finished.values()].filter(
      (r) => !this.groupMemberIds.has(r.recordingId),
    );
    const groups = [...this.finishedGroups.values()];
    const active: ActiveRecordingInfo[] = [...this.active.values()].map((rec) => {
      let groupId: string | undefined;
      for (const [gid, ids] of this.groups) {
        if (ids.includes(rec.recordingId)) {
          groupId = gid;
          break;
        }
      }
      return {
        recordingId: rec.recordingId,
        flightId: rec.flightId,
        startedAt: rec.startedAt,
        groupId,
        arming: rec.arming ?? false,
        stopping: rec.stopPromise != null,
      };
    });
    this.snapshot = { recordings, groups, active };
    for (const l of this.listeners) l();
  }

  /**
   * Acquire the local force ref-count for a feed; sends
   * `setForceFullResolution(true)` unless one is already in flight or has
   * already succeeded for this feed (tracked in {@link forceSendState}, not
   * just the ref-count reaching 1). The sidecar OR-aggregates force across
   * viewers already; this ref-count is the belt-and-braces local mirror for
   * two overlapping recordings of the same feed in this one controller.
   *
   * A send failure clears `forceSendState` instead of leaving it "sent", so
   * it never masquerades as done: a later acquire (e.g. a second overlapping
   * recording's 1->2 transition) sees the feed as not-yet-forced and retries
   * the send, rather than silently leaving the feed un-forced forever.
   */
  private acquireForce(flightId: number): void {
    const count = (this.forceCounts.get(flightId) ?? 0) + 1;
    this.forceCounts.set(flightId, count);
    if (this.forceSendState.has(flightId)) return;
    this.forceSendState.set(flightId, "pending");
    void this.client
      .camera(flightId)
      .setForceFullResolution(true)
      .then(() => {
        this.forceSendState.set(flightId, "sent");
      })
      .catch((err: unknown) => {
        this.forceSendState.delete(flightId);
        console.error(
          `kerbcast: setForceFullResolution(true) failed for feed ${flightId}; will retry on next acquire`,
          err,
        );
      });
  }

  /**
   * Release one holder of a feed's force ref-count; sends
   * `setForceFullResolution(false)` once the count reaches zero. Never goes
   * below zero (a stray extra release is a no-op, not an underflow). Clears
   * `forceSendState` too, so a later, unrelated recording of this feed
   * starts its own send from a clean slate.
   */
  private releaseForce(flightId: number): void {
    const count = (this.forceCounts.get(flightId) ?? 0) - 1;
    if (count <= 0) {
      this.forceCounts.delete(flightId);
      this.forceSendState.delete(flightId);
      void this.client
        .camera(flightId)
        .setForceFullResolution(false)
        .catch(() => {});
    } else {
      this.forceCounts.set(flightId, count);
    }
  }

  /**
   * Starts a recording's recorder and holds its feed at the size it is
   * rendering now, so the clip never changes size. Sent as the recorder
   * starts, after any arming, so a forced feed is held at full size.
   */
  private startRecorder(rec: ActiveRecording): void {
    rec.recorder.onEndedByItself?.(() => {
      const connected = this.client.state == null || this.client.state === "connected";
      this.endOnItsOwn(rec.recordingId, connected ? "feed-closed" : "disconnected");
    });
    rec.recorder.start();
    rec.held = true;
    const count = (this.holdCounts.get(rec.flightId) ?? 0) + 1;
    this.holdCounts.set(rec.flightId, count);
    if (count === 1) this.sendSizeHold(rec.flightId, true);
  }

  /** Lets go of a recording's size hold; the feed is released with its last holder. */
  private releaseHold(rec: ActiveRecording): void {
    if (!rec.held) return;
    rec.held = false;
    const count = (this.holdCounts.get(rec.flightId) ?? 0) - 1;
    if (count > 0) {
      this.holdCounts.set(rec.flightId, count);
      return;
    }
    this.holdCounts.delete(rec.flightId);
    this.sendSizeHold(rec.flightId, false);
  }

  private sendSizeHold(flightId: number, hold: boolean): void {
    const send = this.client.camera(flightId).setSizeHold?.(hold);
    void send?.catch((err: unknown) => {
      console.warn(`kerbcast: setSizeHold(${hold}) failed for feed ${flightId}`, err);
    });
  }

  /**
   * Fallback readiness for a feed whose ceiling is unknown to the client (no
   * `maxRenderSize`), judged from the reported camera state alone. Ready once
   * the operator size has increased past `baseline` (the first REAL state
   * observed at or after watch-start; see {@link watchResolutionReady}) and
   * the rendered size has caught up to it. `baseline` is `null` until that
   * first real state arrives, and a `null` baseline is never ready: a
   * placeholder `{0,0}` would let any positive operator size look like an
   * increase. A feed already at its true ceiling when forced is not
   * recognised until something else changes its operator size, so the wait
   * usually times out instead, but the pre-force snapshot itself is never
   * treated as ready. A state whose rendered size has not yet caught up to
   * its own operator size (adaptive shed still catching up) is never ready.
   */
  private stateIncreasedPastBaseline(
    state: CameraState | null,
    baseline: { width: number; height: number } | null,
  ): boolean {
    if (!state || !baseline) return false;
    if (state.renderWidth !== state.operatorWidth || state.renderHeight !== state.operatorHeight) {
      return false;
    }
    return state.operatorWidth > baseline.width && state.operatorHeight > baseline.height;
  }

  /**
   * Seeds a fallback baseline from an observed state, or returns `null` if
   * the state isn't a REAL observation (operator size still zero, i.e. no
   * dimensions have ever been reported for this feed). A `null` result
   * means the caller must keep waiting for a state worth basing readiness
   * on, rather than latching onto a `{0,0}` placeholder.
   */
  private seedResolutionBaseline(
    state: CameraState | null,
  ): { width: number; height: number } | null {
    if (!state) return null;
    if (state.operatorWidth <= 0 && state.operatorHeight <= 0) return null;
    return { width: state.operatorWidth, height: state.operatorHeight };
  }

  /**
   * Waits for a forced feed to actually deliver full-resolution frames.
   *
   * With the ceiling known (`maxRenderSize`), the gate is the decoded frame
   * size from {@link probeDecodedSize} reaching it (within
   * {@link DECODED_SIZE_TOLERANCE_PX}). The camera state is deliberately not
   * trusted here: it reports the new size before the decoder delivers
   * full-size frames, and a recorder started in that gap locks the clip to
   * the old size. A feed already at its ceiling resolves on the probe's
   * first report. With the ceiling unknown, falls back to the state-only
   * check ({@link stateIncreasedPastBaseline}).
   *
   * `timeoutMs` omitted means no bound: the wait only ends when the feed
   * becomes ready or the caller cancels it (the grouped path, bounded by the
   * group's one shared timer instead; see {@link armGroup}). Resolves `true`
   * once ready, `false` on timeout. `cancel` abandons the wait (stop/discard)
   * without settling the promise; every exit path releases the "change"
   * subscription, the probe, and the timer. `lastDecoded` reports the most
   * recent decoded size seen, for logging.
   */
  private watchResolutionReady(
    flightId: number,
    timeoutMs?: number,
  ): {
    ready: Promise<boolean>;
    cancel: () => void;
    lastDecoded: () => { width: number; height: number } | null;
  } {
    const cam = this.client.camera(flightId);
    const target = cam.maxRenderSize;
    /* Unknown-ceiling path starts with no baseline until a real state seeds
       one, so a null cam.state at watch-start (e.g. no discover() yet, since
       state and the track are independent transports with no ordering
       guarantee) can never masquerade as a {0,0} baseline. */
    let baseline = target ? null : this.seedResolutionBaseline(cam.state);
    let decoded: { width: number; height: number } | null = null;

    let settled = false;
    let unsubscribe: () => void = () => {};
    let stopProbe: (() => void) | null = null;
    let timer: ReturnType<typeof setTimeout> | undefined;

    const release = (): void => {
      settled = true;
      if (timer != null) clearTimeout(timer);
      unsubscribe();
      stopProbe?.();
      stopProbe = null;
    };

    const ready = new Promise<boolean>((resolve) => {
      const finish = (reached: boolean): void => {
        if (settled) return;
        release();
        resolve(reached);
      };

      if (target) {
        const stream = cam.mediaStream;
        if (stream) {
          const stop = this.probeDecodedSize(flightId, stream, (width, height) => {
            decoded = { width, height };
            if (
              width >= target.width - DECODED_SIZE_TOLERANCE_PX &&
              height >= target.height - DECODED_SIZE_TOLERANCE_PX
            ) {
              finish(true);
            }
          });
          /* The probe may report (and finish the wait) before returning. */
          if (settled) stop();
          else stopProbe = stop;
        }
      } else {
        if (this.stateIncreasedPastBaseline(cam.state, baseline)) {
          finish(true);
          return;
        }
        unsubscribe = cam.on("change", (state) => {
          if (!baseline) {
            /* This is the first real state we've ever seen: it becomes the
               baseline, but a state can't ready itself against its own
               just-established baseline, so wait for the NEXT state. */
            baseline = this.seedResolutionBaseline(state);
            return;
          }
          if (this.stateIncreasedPastBaseline(state, baseline)) finish(true);
        });
      }
      if (!settled && timeoutMs != null) {
        timer = setTimeout(() => finish(false), timeoutMs);
      }
    });

    return {
      ready,
      cancel: (): void => {
        if (settled) return;
        release();
      },
      lastDecoded: () => decoded,
    };
  }

  /**
   * Start recording a feed's live track. Returns a client-minted recordingId
   * synchronously. Throws if the feed has no live track or is already
   * recording.
   *
   * With `opts.forceFullResolution`, the recording arms and waits, bounded by
   * {@link ARM_TIMEOUT_MS}, for the feed to reach full resolution before the
   * recorder actually starts, so the clip opens on full-res frames.
   * `inGroup` is internal (set by {@link startGroupedRecording}): a forced
   * grouped member arms the same way but leaves the wait to the group, which
   * starts every member's recorder together (see {@link armGroup}).
   */
  startRecording(flightId: number, opts?: StartRecordingOptions, inGroup = false): string {
    if (this.byFlight.has(flightId)) {
      throw new Error(`camera ${flightId} is already recording`);
    }
    const stream = this.client.camera(flightId).mediaStream;
    if (!stream || stream.getVideoTracks().length === 0) {
      throw new Error(`camera ${flightId} has no live track to record`);
    }

    const recorder = this.createClipRecorder(flightId, stream, opts?.mimeType, () => rec.epoch);
    const mimeType = recorder.mimeType;

    const recordingId = mintId("rec");
    const startedAt = now();
    const utStart = this.client.clock.captureUt ?? undefined;

    const rec: ActiveRecording = {
      recordingId,
      flightId,
      mimeType,
      recorder,
      startedAt,
      utStart,
      utSamples: [{ t: 0, ut: utStart }],
      latestUt: utStart,
      epoch: utStart != null ? this.client.clock.epoch : undefined,
      unsubscribeClock: () => {},
      unwatchCamera: () => {},
    };

    /* Sample the capture clock on each ~1Hz settings-change push. Duplicates
       (a paused sim) are kept so `t` still advances; ut upgrades from absent
       to present automatically once out-of-flight becomes in-flight. */
    rec.unsubscribeClock = this.client.on("settings-change", () => {
      const ut = this.client.clock.captureUt ?? undefined;
      if (ut != null) rec.epoch ??= this.client.clock.epoch;
      if (this.leftEpoch(rec)) {
        /*
         * A revert can keep the camera's flight id and bring it back within
         * one rescan, so the list may never show it destroyed. Its UT now
         * runs from an earlier point, which a clip's timeline cannot hold.
         */
        this.endOnItsOwn(recordingId, "camera-gone");
        return;
      }
      rec.utSamples.push({ t: now() - rec.startedAt, ut });
      if (ut != null) rec.latestUt = ut;
    });

    this.active.set(recordingId, rec);
    this.byFlight.set(flightId, recordingId);

    const forceRequested = opts?.forceFullResolution === true;
    if (forceRequested) {
      rec.forced = true;
      this.acquireForce(flightId);
    }

    if (opts?.maxDurationMs != null) {
      rec.maxDurationTimer = setTimeout(() => {
        void this.stopWith(recordingId, "max-duration").catch(() => {});
      }, opts.maxDurationMs);
    }

    if (forceRequested && inGroup) {
      /* Grouped path: arm only. startGroupedRecording owns the wait and
         starts every member's recorder in one go. */
      rec.arming = true;
    } else if (forceRequested) {
      /* Single path: arm-and-wait. Show "arming" immediately; the recorder
         itself doesn't start until the feed reaches full resolution (or the
         bounded wait gives up), so the clip never opens on low-res frames. */
      rec.arming = true;
      const watch = this.watchResolutionReady(flightId, ARM_TIMEOUT_MS);
      rec.cancelResolutionWatch = watch.cancel;
      void watch.ready.then((reached) => {
        rec.cancelResolutionWatch = undefined;
        /* Discarded while arming: the recorder was never started and this
           id is gone from `active`, so there is nothing left to do. */
        if (!this.active.has(recordingId)) return;
        console.info(
          `kerbcast: recording ${recordingId} started after arming (${
            reached ? "full resolution" : "arm timeout"
          }); decoded size ${flightId}=${formatSize(watch.lastDecoded())}`,
        );
        rec.arming = false;
        /* The real recording starts now: reset the clock base so the
           elapsed timer and utSamples track begin at actual record start,
           not at arm time. */
        rec.startedAt = now();
        rec.utStart = this.client.clock.captureUt ?? undefined;
        rec.utSamples = [{ t: 0, ut: rec.utStart }];
        rec.latestUt = rec.utStart;
        this.startRecorder(rec);
        this.rebuild();
      });
    } else {
      this.startRecorder(rec);
    }

    /* Last, so a feed already gone too long ends a recording that holds
       everything its stop releases. */
    const unwatch = this.watchCamera(rec);
    if (rec.stopPromise) unwatch();
    else rec.unwatchCamera = unwatch;

    this.rebuild();
    return recordingId;
  }

  /**
   * A capture-timed recorder when the feed's frames can be copied, else
   * MediaRecorder. A frame's stamp counts only in the recording's own
   * epoch: frames after a revert can arrive before the clock push that ends
   * the recording.
   */
  private createClipRecorder(
    flightId: number,
    stream: MediaStream,
    preferredMime: string | undefined,
    epoch: () => number | undefined,
  ): ClipRecorder {
    const capture = captureClipRecorder(this.client.camera(flightId), (rtp) => {
      const frame = this.client.frameUt?.(flightId, rtp);
      if (frame?.source !== "exact") return null;
      const own = epoch();
      return own == null || frame.epoch == null || frame.epoch === own ? { ut: frame.ut } : null;
    });
    if (capture) return capture;
    const mimeType = negotiateMimeType(preferredMime);
    const ctor = (globalThis as { MediaRecorder?: MediaRecorderCtor }).MediaRecorder;
    if (!ctor) throw new Error("MediaRecorder is not available in this environment");
    return mediaRecorderClip(new ctor(stream, { mimeType }), mimeType);
  }

  /**
   * Arms a forced group: watches every member's feed and starts all their
   * recorders together once each has reached full resolution, or once the
   * one shared {@link ARM_TIMEOUT_MS} bound passes, whichever comes first.
   * Members stopped or discarded mid-arm drop out of the wait, so a slow
   * feed that gets cancelled no longer holds the rest back.
   */
  private armGroup(groupId: string, ids: string[]): void {
    const arm: GroupArm = {
      ready: new Set(),
      timer: setTimeout(() => this.startArmedGroup(groupId), ARM_TIMEOUT_MS),
    };
    this.groupArms.set(groupId, arm);
    for (const id of ids) {
      const rec = this.active.get(id);
      if (!rec) continue;
      const watch = this.watchResolutionReady(rec.flightId);
      rec.cancelResolutionWatch = watch.cancel;
      rec.lastDecodedSize = watch.lastDecoded;
      void watch.ready.then(() => {
        rec.cancelResolutionWatch = undefined;
        arm.ready.add(id);
        this.checkGroupArm(groupId);
      });
    }
  }

  /** Members of a group still armed and waiting to start (not stopped or discarded). */
  private armingMembers(groupId: string): ActiveRecording[] {
    const ids = this.groups.get(groupId) ?? [];
    const members: ActiveRecording[] = [];
    for (const id of ids) {
      const rec = this.active.get(id);
      if (rec && rec.arming && !rec.stopPromise) members.push(rec);
    }
    return members;
  }

  /** Starts an arming group once every member still waiting is ready. */
  private checkGroupArm(groupId: string): void {
    const arm = this.groupArms.get(groupId);
    if (!arm) return;
    if (this.armingMembers(groupId).every((rec) => arm.ready.has(rec.recordingId))) {
      this.startArmedGroup(groupId);
    }
  }

  /**
   * Ends a group's arm and starts every still-armed member's recorder in the
   * same tick, on one shared clock base (`startedAt`, `utStart`), so the
   * clips open together. Cancels any outstanding per-member watches (the
   * timeout path).
   */
  private startArmedGroup(groupId: string): void {
    const arm = this.groupArms.get(groupId);
    if (!arm) return;
    this.groupArms.delete(groupId);
    clearTimeout(arm.timer);
    const members = this.armingMembers(groupId);
    if (members.length === 0) return;
    const startedAt = now();
    const utStart = this.client.clock.captureUt ?? undefined;
    for (const rec of members) {
      rec.cancelResolutionWatch?.();
      rec.cancelResolutionWatch = undefined;
      rec.arming = false;
      rec.startedAt = startedAt;
      rec.utStart = utStart;
      rec.utSamples = [{ t: 0, ut: utStart }];
      rec.latestUt = utStart;
      this.startRecorder(rec);
    }
    const reason = members.every((rec) => arm.ready.has(rec.recordingId))
      ? "all at full resolution"
      : "arm timeout";
    const sizes = members.map(
      (rec) => `${rec.flightId}=${formatSize(rec.lastDecodedSize?.() ?? null)}`,
    );
    console.info(
      `kerbcast: grouped recording ${groupId} started ${members.length} member(s) after arming (${reason}); decoded sizes ${sizes.join(" ")}`,
    );
    this.rebuild();
  }

  /**
   * Abandons a group's arm without starting anything: the caller is tearing
   * the whole group down (stop or discard), so no member should start mid-way.
   */
  private cancelGroupArm(groupId: string): void {
    const arm = this.groupArms.get(groupId);
    if (!arm) return;
    this.groupArms.delete(groupId);
    clearTimeout(arm.timer);
  }

  /**
   * Stop a recording and resolve its finished handle. Idempotent per id: a
   * second call (or a race with maxDurationMs auto-stop) returns the same
   * pending/settled result.
   */
  stopRecording(recordingId: string): Promise<RecordingHandle> {
    return this.stopWith(recordingId, "user");
  }

  /** True once the capture clock has moved on from the epoch the recording's UT belongs to. */
  private leftEpoch(rec: ActiveRecording): boolean {
    const epoch = this.client.clock.epoch;
    return rec.epoch != null && epoch != null && epoch !== rec.epoch;
  }

  /** The clock's UT at a stop, or the last one in the recording's own epoch once the clock has left it. */
  private utEndOf(rec: ActiveRecording): number | undefined {
    return this.leftEpoch(rec) ? rec.latestUt : (this.client.clock.captureUt ?? undefined);
  }

  private stopWith(recordingId: string, reason: RecordingEndReason): Promise<RecordingHandle> {
    const rec = this.active.get(recordingId);
    if (!rec) {
      const done = this.finished.get(recordingId);
      if (done) return Promise.resolve(done);
      return Promise.reject(new Error(`no recording ${recordingId}`));
    }
    if (rec.stopPromise) return rec.stopPromise;

    if (rec.maxDurationTimer) clearTimeout(rec.maxDurationTimer);
    rec.unsubscribeClock();
    rec.unwatchCamera();
    rec.cancelResolutionWatch?.();
    rec.cancelResolutionWatch = undefined;

    if (rec.arming) {
      /* Stopped (e.g. a short maxDurationMs firing) before the arm-wait
         ever let the recorder start: there is nothing to flush from
         MediaRecorder, and calling stop() on a never-started recorder would
         never fire its "stop" event (hanging this promise forever). Settle
         immediately with an empty clip instead. Force is still released
         unconditionally. */
      rec.recorder.cancel();
      if (rec.forced) this.releaseForce(rec.flightId);
      const utEnd = this.utEndOf(rec);
      const handle: RecordingHandle = {
        recordingId,
        flightId: rec.flightId,
        blob: new Blob([], { type: rec.mimeType }),
        mimeType: rec.mimeType,
        timing: rec.recorder.timing,
        utStart: rec.utStart,
        utEnd,
        utSamples: [...rec.utSamples, { t: now() - rec.startedAt, ut: utEnd }],
        byteSize: 0,
        durationMs: now() - rec.startedAt,
        endedBy: reason,
      };
      rec.stopPromise = Promise.resolve(handle);
      this.finished.set(recordingId, handle);
      this.active.delete(recordingId);
      this.byFlight.delete(rec.flightId);
      this.rebuild();
      this.emitEnded(handle);
      /* An arming group member stopped on its own: the rest of its group
         may now be ready to start without it. */
      const groupId = this.memberOfGroup.get(recordingId);
      if (groupId != null) this.checkGroupArm(groupId);
      return rec.stopPromise;
    }

    const utEnd = this.utEndOf(rec);
    rec.utSamples.push({ t: now() - rec.startedAt, ut: utEnd });
    this.releaseHold(rec);

    rec.stopPromise = new Promise<RecordingHandle>((resolve, reject) => {
      const onStop = (output: ClipOutput): void => {
        const { blob } = output;
        const handle: RecordingHandle = {
          recordingId,
          flightId: rec.flightId,
          blob,
          mimeType: output.mimeType,
          timing: output.timing,
          ...clipUtTrack(output, rec.utSamples, rec.startedAt, rec.utStart, utEnd),
          byteSize: blob.size,
          durationMs: output.mediaDurationMs ?? now() - rec.startedAt,
          endedBy: reason,
        };
        /* Unconditional: a discarded-while-settling recording still held the
           force ref-count and must release it, even though it won't be
           resurrected into `finished` below. */
        if (rec.forced) this.releaseForce(rec.flightId);
        /* Discarded while this stop was settling: don't resurrect it into
           `finished`, standalone or grouped. Still resolve so a caller
           awaiting this same promise doesn't hang. */
        if (!rec.discarded) {
          if (output.cut && this.memberOfGroup.has(recordingId)) this.cutters.set(recordingId, output.cut);
          this.finished.set(recordingId, handle);
          this.active.delete(recordingId);
          this.byFlight.delete(rec.flightId);
          this.rebuild();
          this.emitEnded(handle);
        }
        resolve(handle);
      };
      /* A clip that fails to finish still has to leave the active set, or the
         feed would show as recording for good and refuse a new start. */
      const onFail = (err: unknown): void => {
        console.error(`kerbcast: recording ${recordingId} could not be finished`, err);
        if (rec.forced) this.releaseForce(rec.flightId);
        if (!rec.discarded) {
          this.active.delete(recordingId);
          this.byFlight.delete(rec.flightId);
          this.rebuild();
        }
        reject(err instanceof Error ? err : new Error(String(err)));
      };
      let stopping: Promise<ClipOutput>;
      try {
        stopping = rec.recorder.stop();
      } catch (err) {
        stopping = Promise.reject(err);
      }
      void stopping.then(onStop, onFail);
    });
    /* Show the stopping state while the clip settles. */
    this.rebuild();

    return rec.stopPromise;
  }

  /** Return a finished recording's handle, or undefined if unknown/still active. */
  fetchRecording(recordingId: string): RecordingHandle | undefined {
    return this.finished.get(recordingId);
  }

  /** Whether a feed currently has a recording in progress. */
  isRecording(flightId: number): boolean {
    return this.byFlight.has(flightId);
  }

  /**
   * Drop a recording. If still active, tears it down without producing a
   * handle; if finished, frees the stored handle/blob reference. If the id
   * is a member of a still-active group, pulls it out of that group's member
   * set too, so a later `stopGroupedRecording` only waits on (and stops) the
   * ids that remain -- discarding one member no longer corrupts the group.
   */
  discardRecording(recordingId: string): void {
    const rec = this.active.get(recordingId);
    let changed = false;
    if (rec) {
      if (rec.stopPromise) {
        /* stopRecording already called: the recorder is "inactive" but its
           "stop" event hasn't fired. Flag it so that pending onStop bails
           instead of resurrecting a "discarded" recording once it settles.
           That same onStop releases the force ref-count unconditionally, so
           it must not be released again here. */
        rec.discarded = true;
      } else {
        if (rec.maxDurationTimer) clearTimeout(rec.maxDurationTimer);
        rec.unsubscribeClock();
        rec.unwatchCamera();
        rec.cancelResolutionWatch?.();
        rec.cancelResolutionWatch = undefined;
        rec.recorder.cancel();
        this.releaseHold(rec);
        /* No pending stopRecording to release it later (this is the only
           teardown path this recording will see): release the force
           ref-count now, unconditionally. Covers a discard mid-arm too --
           the recorder above was never started (still "inactive"), so this
           is the only place its force gets released. */
        if (rec.forced) this.releaseForce(rec.flightId);
      }
      this.active.delete(recordingId);
      this.byFlight.delete(rec.flightId);
      changed = true;
    }
    if (this.finished.delete(recordingId)) changed = true;
    this.cutters.delete(recordingId);

    const groupId = this.memberOfGroup.get(recordingId);
    if (groupId != null) {
      this.memberOfGroup.delete(recordingId);
      this.groupMemberIds.delete(recordingId);
      const ids = this.groups.get(groupId);
      if (ids) {
        const idx = ids.indexOf(recordingId);
        if (idx !== -1) {
          ids.splice(idx, 1);
          changed = true;
        }
      }
    }

    if (changed) this.rebuild();
    /* Discarding a slow member of an arming group must not keep the others
       waiting on it. */
    if (groupId != null) this.checkGroupArm(groupId);
  }

  // -------------------------------------------------------------------------
  // Grouped recording
  // -------------------------------------------------------------------------

  /**
   * Start recording several feeds as one synchronised moment. All recorders
   * open in a single tick to minimise skew. With `opts.forceFullResolution`,
   * every member arms first and that single tick comes later, once all feeds
   * have reached full resolution or {@link ARM_TIMEOUT_MS} passes (see
   * {@link armGroup}); the members report `arming` until then. Returns the
   * groupId plus the real per-feed recordingIds (so a caller can cancel one
   * member individually via `stopRecording`/`discardRecording`, the same ids
   * that end up on each clip's `RecordingHandle`). If any feed can't start
   * (no track / already recording), the ones already started are discarded
   * and the error rethrows.
   */
  startGroupedRecording(
    flightIds: number[],
    opts?: StartRecordingOptions,
  ): GroupedRecordingStart {
    const started: string[] = [];
    /* Suppress the per-member rebuild() each startRecording call would fire:
       without this, a synchronous subscriber could observe a member as
       active with groupId undefined (this.groups doesn't have the entry yet).
       One rebuild happens below, once the group is fully set up. */
    this.suppressRebuild = true;
    try {
      try {
        for (const flightId of flightIds) {
          started.push(this.startRecording(flightId, opts, /* inGroup */ true));
        }
      } catch (err) {
        for (const id of started) this.discardRecording(id);
        throw err;
      }
      const groupId = mintId("grp");
      this.groups.set(groupId, started);
      for (const id of started) {
        this.groupMemberIds.add(id);
        this.memberOfGroup.set(id, groupId);
      }
      /* Grouped output is physically trimmed to the common window by default, so
         load the trim package now (never fail start over a loader error) so it is
         ready by stop. A single recording never reaches here, so it stays
         dependency-free. Accepted tradeoff: this eagerly imports Mediabunny even
         when every feed ends up negotiating webm (no mp4 clip to trim). */
      this.groupTrimmers.set(
        groupId,
        (async (): Promise<GroupTrimmer | null> => {
          try {
            return await this.loadTrimmer();
          } catch {
            return null;
          }
        })(),
      );
      if (opts?.forceFullResolution === true) this.armGroup(groupId, started);
      return { groupId, recordingIds: [...started] };
    } finally {
      this.suppressRebuild = false;
      this.rebuild();
    }
  }

  /**
   * Stop a grouped recording. With `targetUt`, holds until every feed's latest
   * UT sample has passed it (coverage guarantee) before stopping any feed;
   * without it, stops all now. Resolves the grouped handle with the intersected
   * `commonUtWindow`. See {@link StopGroupedRecordingOptions.targetUt} for the
   * coverage caveat around a member's own `maxDurationMs`.
   */
  stopGroupedRecording(
    groupId: string,
    opts?: StopGroupedRecordingOptions,
  ): Promise<GroupedRecordingHandle> {
    const done = this.finishedGroups.get(groupId);
    if (done) return Promise.resolve(done);
    const pending = this.groupStops.get(groupId);
    if (pending) return pending;
    const ids = this.groups.get(groupId);
    if (!ids) return Promise.reject(new Error(`no grouped recording ${groupId}`));
    const stop = this.finishGroup(groupId, ids, opts);
    this.groupStops.set(groupId, stop);
    const forget = (): void => {
      this.groupStops.delete(groupId);
    };
    stop.then(forget, forget);
    return stop;
  }

  private async finishGroup(
    groupId: string,
    ids: string[],
    opts?: StopGroupedRecordingOptions,
  ): Promise<GroupedRecordingHandle> {
    if (opts?.targetUt != null) {
      await this.awaitCoverage(
        ids,
        opts.targetUt,
        opts.coverageTimeoutMs ?? DEFAULT_COVERAGE_TIMEOUT_MS,
      );
    }

    /* A group stopped while still arming never starts its recorders: each
       still-armed member settles below as an empty clip, the same as a
       single recording stopped mid-arm. */
    this.cancelGroupArm(groupId);

    const settled = await Promise.allSettled(ids.map((id) => this.stopRecording(id)));
    const recordings = settled.flatMap((r) => (r.status === "fulfilled" ? [r.value] : []));
    const ranToStop = recordings.filter((r) => r.endedBy === "user" || r.endedBy === "max-duration");
    const window = commonUtWindow(ranToStop.length > 0 ? ranToStop : recordings);

    /* Physically remux-trim each clip to the common window so the grouped
       output is pre-aligned by default. The UT metadata (utSamples +
       commonUtWindow) still rides along so a consumer can align the trimmed
       clips to telemetry. Trimming never fails the recording: any clip that
       can't be cut cleanly degrades to metadata-only. */
    let finalRecordings = recordings;
    if (window) {
      const trimmer = await (this.groupTrimmers.get(groupId) ?? Promise.resolve(null));
      finalRecordings = await Promise.all(
        recordings.map((rec) => this.maybeTrim(rec, window, trimmer)),
      );
    }
    for (const rec of recordings) this.cutters.delete(rec.recordingId);

    const handle: GroupedRecordingHandle = {
      groupId,
      recordings: finalRecordings,
      commonUtWindow: window,
    };
    this.groups.delete(groupId);
    this.groupTrimmers.delete(groupId);
    this.finishedGroups.set(groupId, handle);
    this.rebuild();
    return handle;
  }

  /**
   * Trim one clip to the common UT window, or return it untouched
   * (metadata-only) when it can't be cut cleanly: a non-mp4 container, an
   * unmappable window, or a trimmer error. A capture-timed clip is cut from
   * its own samples, keeping a sample entry per size it passed through; any
   * other mp4 is remuxed by `trimmer`. The stored per-clip handle is
   * replaced with the trimmed one so fetch/discard stay consistent.
   */
  private async maybeTrim(
    rec: RecordingHandle,
    window: [number, number],
    trimmer: GroupTrimmer | null,
  ): Promise<RecordingHandle> {
    /* MediaRecorder webm output isn't remuxed to mp4 here; degrade to
       metadata-only for those clips (mp4 is the default container). */
    if (!/mp4/i.test(rec.mimeType)) return rec;
    /* A member stopped while still arming has no media to cut. */
    if (rec.byteSize === 0) return rec;
    const startMs = utToRecordingTimeMs(rec.utSamples, window[0]);
    const endMs = utToRecordingTimeMs(rec.utSamples, window[1]);
    if (startMs == null || endMs == null || endMs <= startMs) return rec;
    const cutter = this.cutters.get(rec.recordingId);
    if (cutter) {
      const cut = cutter(startMs, endMs);
      if (!cut) return rec;
      const utSamples = cut.frameUtSamples.length > 0
        ? cut.frameUtSamples
        : rec.utSamples
            .filter((s) => s.t >= cut.startMs && s.t <= cut.startMs + cut.mediaDurationMs)
            .map((s) => ({ ...s, t: s.t - cut.startMs }));
      const trimmed: RecordingHandle = {
        ...rec,
        blob: cut.blob,
        utSamples,
        utStart: utSamples[0]?.ut ?? rec.utStart,
        utEnd: utSamples[utSamples.length - 1]?.ut ?? rec.utEnd,
        byteSize: cut.blob.size,
        durationMs: cut.mediaDurationMs,
      };
      this.finished.set(rec.recordingId, trimmed);
      return trimmed;
    }
    if (!trimmer) return rec;
    try {
      const blob = await trimmer.trim(rec.blob, rec.mimeType, startMs, endMs);
      const trimmed: RecordingHandle = {
        ...rec,
        blob,
        byteSize: blob.size,
        durationMs: endMs - startMs,
      };
      this.finished.set(rec.recordingId, trimmed);
      return trimmed;
    } catch {
      return rec;
    }
  }

  /** Return a finished grouped recording, or undefined if unknown/still active. */
  fetchGroupedRecording(groupId: string): GroupedRecordingHandle | undefined {
    return this.finishedGroups.get(groupId);
  }

  /** Drop a grouped recording and each of its clips. */
  discardGroupedRecording(groupId: string): void {
    /* Accepted gap: racing this against an in-flight stopGroupedRecording(groupId)
       is untested and unlikely misuse (a caller wouldn't normally discard and
       stop the same group at once). */
    let changed = false;
    const active = this.groups.get(groupId);
    /* Before the member discards below, so a still-arming group can't start
       its remaining members partway through being torn down. */
    this.cancelGroupArm(groupId);
    if (active) {
      /* discardRecording now splices ids out of this very array as it goes
         (fix for cancelling one grouped member); iterate a copy so removals
         mid-loop can't skip an id. */
      for (const id of [...active]) this.discardRecording(id);
      this.groups.delete(groupId);
      this.groupTrimmers.delete(groupId);
      changed = true;
    }
    const finished = this.finishedGroups.get(groupId);
    if (finished) {
      for (const rec of finished.recordings) this.discardRecording(rec.recordingId);
      this.finishedGroups.delete(groupId);
      changed = true;
    }
    if (changed) this.rebuild();
  }

  /**
   * Resolve once every listed recording's latest UT sample has reached
   * `targetUt`. UT arrives on the shared ~1Hz clock feed, so this polls the
   * settings-change stream rather than a wall-clock timer. Bounded by
   * `timeoutMs`: if coverage never arrives (feed out of flight, sim frozen,
   * warp reversed), gives up and resolves anyway so the group finalizes with
   * whatever UT each feed has captured, rather than hanging forever. Always
   * releases the settings-change subscription, on either exit path.
   */
  private awaitCoverage(
    recordingIds: string[],
    targetUt: number,
    timeoutMs: number,
  ): Promise<void> {
    const covered = (): boolean =>
      recordingIds.every((id) => {
        const rec = this.active.get(id);
        /* A recording already stopped counts as covered (nothing to wait on). */
        if (!rec) return true;
        return rec.latestUt != null && rec.latestUt >= targetUt;
      });

    if (covered()) return Promise.resolve();

    return new Promise<void>((resolve) => {
      let settled = false;
      const finish = (): void => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        unsubscribe();
        resolve();
      };
      const unsubscribe = this.client.on("settings-change", () => {
        if (covered()) finish();
      });
      const timer = setTimeout(finish, timeoutMs);
    });
  }
}
