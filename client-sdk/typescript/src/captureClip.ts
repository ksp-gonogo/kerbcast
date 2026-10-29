/*
 * Builds a capture-timed clip from a feed's encoded H.264 frames: each frame
 * is kept as received (no re-encode) and timed by its RTP timestamp, which
 * the sidecar derives from when the game captured the frame. A network stall
 * delays frames but leaves their timestamps alone, so the clip plays back at
 * the game's pace instead of freezing and then bursting.
 */

import {
  Mp4Writer,
  NAL_IDR,
  NAL_PPS,
  NAL_SLICE,
  NAL_SPS,
  RTP_VIDEO_CLOCK_HZ,
  isReferenceNal,
  nalType,
  parseSps,
  sliceFrameNum,
  splitAnnexB,
  toAvccSample,
  type SpsInfo,
} from "./mp4";
import type { UtSample } from "./recording";

/** A copy of one received encoded video frame, taken before decode. */
export interface EncodedFrameCopy {
  /** The frame's bytes as received: H.264 in Annex B. */
  data: ArrayBuffer;
  /** RTP timestamp, on the 90 kHz video clock. */
  rtpTimestamp: number;
  /** RTP synchronisation source, when the browser reports it. */
  ssrc?: number;
  /** When the copy reached the page, on the `performance.now()` clock. */
  receivedAt: number;
}

/**
 * Largest gap between consecutive RTP timestamps taken at face value. A
 * longer step forward, any step back, or a new synchronisation source means
 * the timestamps no longer belong to one stream (the feed moved receiver, or
 * the sender restarted), so the gap is timed by arrival instead.
 */
export const MAX_RTP_STEP_TICKS = 60 * RTP_VIDEO_CLOCK_HZ;

/**
 * How long a clip runs across a dropout: a stretch whose timestamps cannot be
 * compared, as when a reconnect brings the feed back on a new source, or the
 * same source steps backwards or jumps further than {@link MAX_RTP_STEP_TICKS}. Given
 * the real time between the frames either side, in 90 kHz ticks. The clip
 * holds its last picture for that whole time, so the clip stays on the wall
 * clock across the dropout.
 */
export function dropoutTicks(arrivalTicks: number): number {
  return Math.max(1, arrivalTicks);
}

/**
 * Maps RTP timestamps onto a clip timeline in 90 kHz ticks that starts at 0.
 * The 32-bit RTP clock wraps about every 13 hours; consecutive timestamps are
 * compared as a signed 32-bit difference, so a wrap is just another step.
 */
export class RtpTimeline {
  private last: { rtp: number; ssrc?: number; at: number; ticks: number } | null = null;
  private broken = false;

  /**
   * True when a frame stamped `rtp` does not continue the timeline, so
   * {@link next} would time it by arrival. A repeat of the last stamp
   * continues it: the sidecar's step is unsigned and can round or clamp to
   * zero, but never goes back.
   */
  breaks(rtp: number, ssrc: number | undefined): boolean {
    const last = this.last;
    if (!last) return false;
    if (this.broken) return true;
    const step = (rtp - last.rtp) | 0;
    const sameSource = ssrc === undefined || last.ssrc === undefined || ssrc === last.ssrc;
    return !(sameSource && step >= 0 && step <= MAX_RTP_STEP_TICKS);
  }

  /**
   * Times the next frame by arrival whatever its stamp. For frames dropped
   * after a break: the frame that ends the wait can look continuous with the
   * last one kept, as a step back followed by enough time does.
   */
  markBreak(): void {
    if (this.last) this.broken = true;
  }

  /** Ticks since the first frame for a frame stamped `rtp`, received at `at` ms. */
  next(rtp: number, ssrc: number | undefined, at: number): number {
    const last = this.last;
    let ticks = 0;
    if (last) {
      if (this.breaks(rtp, ssrc)) {
        const arrival = Math.round(((at - last.at) * RTP_VIDEO_CLOCK_HZ) / 1000);
        ticks = last.ticks + dropoutTicks(arrival);
      } else {
        /* A repeated stamp still needs its own, later time in the file. */
        ticks = last.ticks + Math.max(1, (rtp - last.rtp) | 0);
      }
    }
    this.broken = false;
    this.last = { rtp, ssrc, at, ticks };
    return ticks;
  }
}

/**
 * True when a picture's `frame_num` shows a reference picture before it went
 * missing. Each reference picture steps `frame_num` by one (modulo
 * `2 ** log2MaxFrameNum`) and a non-reference one does not, so a picture
 * carries the last reference's number or the one after it; anything else
 * means the frames from here on predict from a picture the clip never had.
 * A memory management op 5 would reset the count without an IDR; it is not
 * handled because none of Kerbcast's encoders emit one.
 */
export function missesReference(prevRefFrameNum: number, frameNum: number, log2MaxFrameNum: number): boolean {
  return frameNum !== prevRefFrameNum && frameNum !== (prevRefFrameNum + 1) % 2 ** log2MaxFrameNum;
}

/**
 * Frames back from the newest whose missed UT lookups are retried on each
 * push: about 4 s at 60 fps, for stamps trailing their frames. Older misses
 * wait for {@link CaptureClip.finish}.
 */
export const UT_RETRY_FRAMES = 256;

/** A frame's mission time, from its capture stamp. */
export type FrameUtLookup = (rtpTimestamp: number) => { ut: number } | null;

/** The finished clip from {@link CaptureClip.finish}. */
export interface CaptureClipResult {
  /** An MP4, or an empty blob when no keyframe arrived before the clip ended. */
  blob: Blob;
  mimeType: string;
  /**
   * One sample per frame whose capture stamp was found, `t` being the
   * frame's time in the clip. Empty when no frame had a stamp.
   */
  frameUtSamples: UtSample[];
  /** When the clip's first frame reached the page (`performance.now()` clock), or null with no frames. */
  firstFrameAt: number | null;
  frameCount: number;
  /** The clip's media length in ms: the last frame's time plus its duration. */
  mediaDurationMs: number;
}

/** A stretch of a finished clip, from {@link CaptureClip.cut}. */
export interface CaptureClipCut {
  blob: Blob;
  /** Per-frame UT samples, timed from the cut's own start. */
  frameUtSamples: UtSample[];
  /** Where the cut starts in the full clip, in ms: the keyframe at or before the asked start. */
  startMs: number;
  mediaDurationMs: number;
}

/** What a kept frame leaves behind once its bytes are in the writer. */
interface FrameMeta {
  rtp: number;
  dts: number;
  ut?: number;
}

/**
 * Collects encoded frames for one recording. Frames before the first
 * keyframe are dropped (they cannot be decoded on their own); from there on
 * every frame is kept with its RTP-derived time. The bytes go straight to an
 * {@link Mp4Writer}; only per-frame timing stays here.
 */
export class CaptureClip {
  private readonly writer = new Mp4Writer();
  private readonly frames: FrameMeta[] = [];
  private readonly timeline = new RtpTimeline();
  private sps: Uint8Array | null = null;
  private spsInfo: SpsInfo | null = null;
  private pps: Uint8Array | null = null;
  /** `frame_num` of the last reference picture, or null when unknown. */
  private prevRefFrameNum: number | null = null;
  /** Indices into {@link frames} whose UT lookup has missed so far, oldest first. */
  private utMisses: number[] = [];
  private firstFrameAt: number | null = null;
  private lastSsrc: number | undefined;
  /** False until a keyframe opens the clip, and again after a source change, a break or a lost reference picture until the next one. */
  private decodable = false;

  constructor(private readonly lookupUt?: FrameUtLookup) {}

  /** True once a keyframe has opened the clip. */
  get started(): boolean {
    return this.firstFrameAt !== null;
  }

  /** True while frames are being dropped until the next keyframe. */
  get waitingForKeyframe(): boolean {
    return !this.decodable;
  }

  push(frame: EncodedFrameCopy): void {
    if (frame.ssrc !== undefined && this.lastSsrc !== undefined && frame.ssrc !== this.lastSsrc) {
      /* A new source restarts its reference chain at a keyframe. */
      this.decodable = false;
      this.timeline.markBreak();
      this.prevRefFrameNum = null;
    }
    if (frame.ssrc !== undefined) this.lastSsrc = frame.ssrc;

    const nals = splitAnnexB(new Uint8Array(frame.data));
    let idr = false;
    let slice: Uint8Array | null = null;
    for (const nal of nals) {
      const type = nalType(nal);
      if (type === NAL_SPS) this.setSps(nal);
      else if (type === NAL_PPS) this.pps = nal.slice();
      else if (type === NAL_IDR) idr = true;
      if ((type === NAL_SLICE || type === NAL_IDR) && !slice) slice = nal;
    }
    const sps = this.sps;
    const info = this.spsInfo;
    const pps = this.pps;
    if (!sps || !info || !pps) return;
    if (this.decodable && !idr && this.timeline.breaks(frame.rtpTimestamp, frame.ssrc)) {
      /* After a jump or a step back the frames refer to pictures from before it, so the clip waits for a keyframe as a new source does. */
      this.decodable = false;
      this.timeline.markBreak();
    }
    const frameNum = slice ? this.frameNumOf(slice, info) : null;
    const prevRef = this.prevRefFrameNum;
    if (
      this.decodable &&
      !idr &&
      frameNum !== null &&
      prevRef !== null &&
      !info.frameNumGapsAllowed &&
      missesReference(prevRef, frameNum, info.log2MaxFrameNum)
    ) {
      /*
       * A reference picture went missing on an unbroken timeline (a packet
       * the network never recovered, or frames the tap never saw). The
       * stamps still hold, so the wait is timed by them, but the frames up
       * to the next keyframe would decode as a smear.
       */
      this.decodable = false;
    }
    if (slice && isReferenceNal(slice)) this.prevRefFrameNum = frameNum;
    if (!this.decodable && !idr) return;

    const data = toAvccSample(nals);
    if (data.length === 0) return;
    const dts = this.timeline.next(frame.rtpTimestamp, frame.ssrc, frame.receivedAt);
    this.writer.add(data, dts, idr, sps, pps);
    this.decodable = true;
    this.firstFrameAt ??= frame.receivedAt;
    this.retryUtMisses();
    const ut = this.lookupUt?.(frame.rtpTimestamp)?.ut;
    if (ut == null && this.lookupUt) this.utMisses.push(this.frames.length);
    this.frames.push({ rtp: frame.rtpTimestamp, dts, ut });
  }

  /** Keeps an SPS only when it parses: one that cannot be read cannot be muxed, so the clip waits for a good one. */
  private setSps(nal: Uint8Array): void {
    try {
      this.spsInfo = parseSps(nal);
      this.sps = nal.slice();
    } catch {
      this.sps = null;
      this.spsInfo = null;
      this.decodable = false;
    }
  }

  private frameNumOf(slice: Uint8Array, info: SpsInfo): number | null {
    try {
      return sliceFrameNum(slice, info);
    } catch {
      return null;
    }
  }

  /** Looks up the recent frames whose stamps had not arrived yet. */
  private retryUtMisses(): void {
    const lookup = this.lookupUt;
    if (!lookup || this.utMisses.length === 0) return;
    const oldest = this.frames.length - UT_RETRY_FRAMES;
    const still: number[] = [];
    for (const i of this.utMisses) {
      if (i < oldest) continue;
      const f = this.frames[i];
      f.ut = lookup(f.rtp)?.ut;
      if (f.ut == null) still.push(i);
    }
    this.utMisses = still;
  }

  /** Muxes everything collected so far. */
  finish(): CaptureClipResult {
    const frameUtSamples: UtSample[] = [];
    for (const f of this.frames) {
      /* A stamp can trail its frame; the client keeps seconds of them, so retry the misses. */
      f.ut ??= this.lookupUt?.(f.rtp)?.ut;
      if (f.ut != null) frameUtSamples.push({ t: (f.dts * 1000) / RTP_VIDEO_CLOCK_HZ, ut: f.ut });
    }
    const durations = this.writer.durations();
    const last = this.frames[this.frames.length - 1];
    const mediaTicks = last ? last.dts + durations[durations.length - 1] : 0;
    const blob = this.writer.finish();
    return {
      blob,
      mimeType: blob.type,
      frameUtSamples,
      firstFrameAt: this.firstFrameAt,
      frameCount: this.frames.length,
      mediaDurationMs: (mediaTicks * 1000) / RTP_VIDEO_CLOCK_HZ,
    };
  }

  /**
   * The frames between `startMs` and `endMs` of the clip as a file of their
   * own, opening on the keyframe at or before `startMs` so its first frame
   * decodes. Cut from the collected samples rather than by remuxing the
   * finished file, so every size the clip passes through keeps its own
   * parameter sets. Null when the window holds no frame. Call after
   * {@link finish}.
   */
  cut(startMs: number, endMs: number): CaptureClipCut | null {
    const times = this.writer.times();
    const startTicks = (startMs * RTP_VIDEO_CLOCK_HZ) / 1000;
    const endTicks = (endMs * RTP_VIDEO_CLOCK_HZ) / 1000;
    let first = -1;
    let to = 0;
    for (let i = 0; i < times.length; i++) {
      if (times[i] <= startTicks) first = i;
      if (times[i] <= endTicks) to = i + 1;
    }
    if (times.length === 0 || startTicks > times[times.length - 1]) return null;
    const from = this.writer.keyframeAtOrBefore(Math.max(first, 0));
    if (to <= from) return null;
    const base = times[from];
    const frameUtSamples: UtSample[] = [];
    for (let i = from; i < to; i++) {
      const f = this.frames[i];
      if (f.ut != null) frameUtSamples.push({ t: ((f.dts - base) * 1000) / RTP_VIDEO_CLOCK_HZ, ut: f.ut });
    }
    const durations = this.writer.durations();
    return {
      blob: this.writer.slice(from, to),
      frameUtSamples,
      startMs: (base * 1000) / RTP_VIDEO_CLOCK_HZ,
      mediaDurationMs: ((times[to - 1] - base + durations[to - 1]) * 1000) / RTP_VIDEO_CLOCK_HZ,
    };
  }
}
