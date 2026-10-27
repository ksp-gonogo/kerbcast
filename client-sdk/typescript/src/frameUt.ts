import type { FrameAnchor, FrameCommandState, FrameStampPayload } from "./__generated__/types";

/**
 * Where a frame's UT came from. `"exact"`: looked up from the frame's own
 * capture stamp, matched by RTP timestamp. `"estimated"`: either the stamp
 * of a nearby older frame, when a displayed frame through the noise pipeline
 * can't be identified exactly (it lags the picture, never leads it), or,
 * with no stamp at all, the ~1 Hz capture clock extrapolated to now, which
 * runs ahead of the picture by the pipeline latency (typically 0.15 to
 * 0.2 s).
 */
export type FrameUtSource = "exact" | "estimated";

/** Mission time of one video frame. */
export interface FrameUt {
  /** KSP universal time (seconds) the frame was rendered at. */
  ut: number;
  /** Capture epoch `ut` belongs to; it changes on a revert, quickload or scene reload. */
  epoch: number;
  source: FrameUtSource;
}

/**
 * What a frame's stamp reports beyond its UT. Each field is present only when
 * the stamp carried it: a 1.12 sidecar and plugin for `fov` and the size, and
 * `anchors` only once this client has set some (or the game has).
 */
export interface FrameStampExtras {
  /**
   * Where each anchor landed in this frame, in capture pixels of a
   * `frameWidth` by `frameHeight` frame: this client's own (no `owner`) and
   * those set in game (with their `owner`).
   */
  anchors?: FrameAnchor[];
  /** Vertical field of view the frame was rendered with, degrees. */
  fov?: number;
  /** The capture frame's size, the space anchor positions are in. */
  frameWidth?: number;
  frameHeight?: number;
  /**
   * The frame's live bound in seconds: the light time covering what of the
   * lens's own craft and site the frame draws at `ut`. 0 with no signal
   * speed. Other loaded craft near the lens are still drawn at `ut` and are
   * not covered yet; once craft beyond the boundary are hidden, the bound
   * covers everything drawn at `ut`. A delayed view releases the frame no
   * sooner than `ut` plus this plus its own delay from the lens. 0 from a
   * game older than 1.12, which has no light speed. Absent when the game
   * could not read it, and from a sidecar older than 1.12: a view applying
   * a light speed treats that as unknown and holds the frame.
   */
  liveLightTimeS?: number;
  /** The picture's boundary distance in metres; absent with no light speed. */
  boundaryM?: number;
  /** A command camera's state as this frame shows it (1.12 and later). */
  command?: FrameCommandState;
}

/** A frame the browser presented on a watched video element. */
export interface DisplayedFrameUt extends FrameUt, FrameStampExtras {
  flightId: number;
  /**
   * RTP timestamp of the source frame `ut` was looked up for (through the
   * noise pipeline, the source frame composited into the presented frame),
   * or `null` when none is known.
   */
  rtpTimestamp: number | null;
  /** When the frame is shown, on the `performance.now()` clock. */
  presentedAt: number;
}

/**
 * Longest the capture clock is extrapolated past its last ~1 Hz sample. A
 * paused game stops sending samples while UT holds, so running on would
 * invent time.
 */
export const MAX_UT_EXTRAPOLATION_MS = 2000;

/** The capture clock advanced to `atMs` at the sampled warp rate, capped. */
export function extrapolateCaptureUt(
  captureUt: number,
  warpRate: number,
  sampledAtMs: number,
  atMs: number,
): number {
  const elapsedMs = Math.min(Math.max(0, atMs - sampledAtMs), MAX_UT_EXTRAPOLATION_MS);
  return captureUt + (elapsedMs / 1000) * warpRate;
}

/** Stamps kept per camera: about 8 s of 60 fps video, far past any jitter buffer. */
export const FRAME_STAMP_HISTORY = 512;

/**
 * Longest a stamp is taken to precede its frame on screen. The sidecar
 * sends a frame's stamp just before the frame's packets, so the frame is
 * shown after the jitter buffer, decode and the next composite. On a
 * loopback a frame reached the screen 20 to 50 ms after it was drawn; a
 * stamp older than this is assumed to be on screen already.
 */
export const STAMP_TO_DISPLAY_MAX_MS = 150;

interface StoredStamp {
  ut: number;
  epoch: number;
  extras: FrameStampExtras | null;
  /** When the stamp arrived, on the `monotonicNowMs` clock. */
  arrivedAt: number;
}

/** A looked-up stamp: the frame's UT and epoch plus whatever extras it carried. */
export type StampLookup = { ut: number; epoch: number } & FrameStampExtras;

/** Only the extras a stamp carried, so a frame without them has no such keys. */
function extrasOf(stamp: FrameStampPayload): FrameStampExtras | null {
  const extras: FrameStampExtras = {};
  if (stamp.anchors?.length) extras.anchors = stamp.anchors;
  if (stamp.fov != null) extras.fov = stamp.fov;
  if (stamp.width != null) extras.frameWidth = stamp.width;
  if (stamp.height != null) extras.frameHeight = stamp.height;
  if (stamp.liveLightTimeS != null) extras.liveLightTimeS = stamp.liveLightTimeS;
  if (stamp.boundaryM != null) extras.boundaryM = stamp.boundaryM;
  if (stamp.command != null) extras.command = stamp.command;
  return Object.keys(extras).length ? extras : null;
}

function withExtras<T extends object>(base: T, extras: FrameStampExtras | null): T & FrameStampExtras {
  return extras ? { ...base, ...extras } : base;
}

/**
 * Recent per-frame capture stamps, keyed by camera then RTP timestamp. RTP
 * timestamps are only meaningful within one peer connection, so the client
 * clears this whenever it reconnects.
 */
export class FrameStampStore {
  private readonly byFlight = new Map<number, Map<number, StoredStamp>>();

  record(stamp: FrameStampPayload, arrivedAt = monotonicNowMs()): void {
    let stamps = this.byFlight.get(stamp.flightId);
    if (!stamps) {
      stamps = new Map();
      this.byFlight.set(stamp.flightId, stamps);
    }
    /* Re-inserting moves the key to the back, keeping eviction oldest-first. */
    stamps.delete(stamp.rtpTimestamp);
    stamps.set(stamp.rtpTimestamp, {
      ut: stamp.captureUt,
      epoch: stamp.captureEpoch,
      extras: extrasOf(stamp),
      arrivedAt,
    });
    while (stamps.size > FRAME_STAMP_HISTORY) {
      const oldest = stamps.keys().next().value as number;
      stamps.delete(oldest);
    }
  }

  lookup(flightId: number, rtpTimestamp: number): StampLookup | null {
    const s = this.byFlight.get(flightId)?.get(rtpTimestamp);
    return s ? withExtras({ ut: s.ut, epoch: s.epoch }, s.extras) : null;
  }

  /**
   * The newest stamp that arrived at or before `atMs`. For a frame presented
   * at `t`, `newestArrivedBy(t - STAMP_TO_DISPLAY_MAX_MS)` is a frame already
   * on screen by then: the same frame or an older one, never a newer one.
   */
  newestArrivedBy(
    flightId: number,
    atMs: number,
  ): (StampLookup & { rtpTimestamp: number }) | null {
    const stamps = this.byFlight.get(flightId);
    if (!stamps) return null;
    let found: [number, StoredStamp] | null = null;
    for (const [rtpTimestamp, s] of stamps) {
      if (s.arrivedAt > atMs) break;
      found = [rtpTimestamp, s];
    }
    if (!found) return null;
    const [rtpTimestamp, s] = found;
    return withExtras({ ut: s.ut, epoch: s.epoch, rtpTimestamp }, s.extras);
  }

  clear(): void {
    this.byFlight.clear();
  }
}

/** The slice of `requestVideoFrameCallback` metadata the watcher reads. */
export interface VideoFrameCallbackMetadataLike {
  expectedDisplayTime?: number;
  presentationTime?: number;
  mediaTime?: number;
  rtpTimestamp?: number;
}

export type VideoWithFrameCallback = HTMLVideoElement & {
  requestVideoFrameCallback?: (
    cb: (now: number, metadata: VideoFrameCallbackMetadataLike) => void,
  ) => number;
  cancelVideoFrameCallback?: (id: number) => void;
};

/** Monotonic clock; falls back to `Date.now` where `performance` is missing. */
export function monotonicNowMs(): number {
  return typeof performance !== "undefined" && typeof performance.now === "function"
    ? performance.now()
    : Date.now();
}
