const NOISE_MAX_W = 320;
const NOISE_MAX_H = 180;

/*
 * A live source that hasn't presented a decoded frame for this long is
 * treated as stalled: the pipeline composites the same black + full-static
 * look it uses when sourceless. Without this, a starved or mid-GOP WebRTC
 * track shows the BROWSER DECODER's output — grey-green macroblock smear —
 * which reads as a second, inconsistent "static" style. Long enough that a
 * single late frame at 30 fps never flickers the overlay.
 */
const SOURCE_STALL_MS = 500;

/*
 * When a source that WAS delivering frames stalls, the full-static look
 * ramps in over this window on top of the held last frame instead of
 * cutting hard. Only degradation is softened: the first decoded frame
 * drops the static outright, so recovery feels instant. Sources that
 * never decoded a frame (fresh attach) keep the immediate full-static
 * "waiting" look: there is no last frame to soften from.
 */
const STALL_RAMP_MS = 5000;

/*
 * Output frame pacing. A composite holding a new source frame is always
 * emitted; otherwise (static animating over a held or absent frame) the
 * output runs at OUTPUT_FPS. The slack keeps a 60 Hz rAF from missing every
 * other 30 fps slot to jitter.
 */
const OUTPUT_FPS = 30;
const OUTPUT_SLACK_MS = 8;

/* Emitted composites remembered for mapping a displayed frame back to its source frame: ~4 s at 30 fps. */
const COMPOSITE_HISTORY = 128;

/*
 * Without MediaStreamTrackGenerator a displayed frame can't be identified,
 * so it is taken to be the newest composite emitted at least this long
 * before the element presented it: the oldest that could be on screen if
 * capture-to-presentation takes up to this long. Chrome's canvas capture
 * measured about 1 ms (10 ms worst), so the guess is usually one emitted
 * frame behind the picture and leads it only past this bound.
 */
const FALLBACK_OUTPUT_LATENCY_MS = 40;

/* The slice of the displayed element's `requestVideoFrameCallback` metadata the pipeline reads. */
export interface DisplayedFrameMetadata {
  mediaTime?: number;
  presentationTime?: number;
  width?: number;
  height?: number;
}

/** The source frame a displayed output frame shows. */
export interface DisplayedSourceFrame {
  /** RTP timestamp of the source frame, or `null` when the output frame held none (static only). */
  rtpTimestamp: number | null;
  /**
   * True when the output frame and its source frame were both identified
   * exactly. False when either was inferred: the reported source frame may
   * then be older than the pixels but should never be newer.
   */
  exact: boolean;
}

export interface NoisePipelineOptions {
  /**
   * Composite animated static over stalled and sourceless states (default
   * true). When false: a stalled source holds its last decoded frame with no
   * static (the consumer provides its own staleness chrome), and a sourceless
   * path clears to black without noise (only the text overlay renders).
   */
  showStatic?: boolean;
  /** Observe stall transitions (frames stopped / frames resumed). */
  onStallChange?: (stalled: boolean) => void;
  /**
   * Forward each source frame to the output untouched, with no static and
   * no canvas work, keeping only the frame identity `displayedFrame` needs
   * (default false). Needs MediaStreamTrackProcessor and
   * MediaStreamTrackGenerator; elsewhere it composites as usual. A stalled
   * source holds its last frame and a sourceless one shows black.
   */
  passthrough?: boolean;
}

export interface NoisePipeline {
  readonly processedStream: MediaStream;
  setIntensity(level: number): void;
  /**
   * Swap (or remove) the source video. With a non-null stream the pipeline
   * composites static over the live frame; with `null` it goes *sourceless*
   * — `draw()` skips `drawImage` and renders pure static. The output
   * `processedStream` stays valid across the swap, so consumers never see a
   * gap.
   */
  setSource(stream: MediaStream | null): void;
  /** Runtime switch for {@link NoisePipelineOptions.showStatic}. */
  setShowStatic(enabled: boolean): void;
  /** Runtime switch for {@link NoisePipelineOptions.passthrough}. */
  setPassthrough(enabled: boolean): void;
  /**
   * The source frame shown by an element presenting `processedStream`, from
   * that element's `requestVideoFrameCallback` metadata. A held frame under
   * stall static keeps its timestamp. `null` when the frame is unknown (no
   * composite emitted yet, or one older than the history).
   */
  displayedFrame(metadata: DisplayedFrameMetadata): DisplayedSourceFrame | null;
  destroy(): void;
}

/* Chrome's window-exposed insertable-streams generator; absent from the DOM lib. */
interface TrackGenerator extends MediaStreamTrack {
  readonly writable: WritableStream<VideoFrame>;
}
type TrackGeneratorCtor = new (init: { kind: "video" }) => TrackGenerator;
type TrackProcessorCtor = new (init: { track: MediaStreamTrack; maxBufferSize?: number }) => {
  readonly readable: ReadableStream<VideoFrame>;
};

type FrameListener = (frame: VideoFrame) => void;

/*
 * One MediaStreamTrackProcessor per source track, shared by every pipeline
 * and kept reading until the track ends. Chrome (148 to 156 measured) leaks
 * a VideoFrame it never hands to JS in about 1 to 2% of processor
 * teardowns, whether the reader is cancelled or the track stopped, so a
 * source swap or pipeline teardown only unsubscribes. Frames nobody listens
 * to are closed straight away. The same leak hits about 5% of tracks that
 * end under a processor (a peer closing), which JS can't prevent.
 */
const frameFeeds = new WeakMap<MediaStreamTrack, Set<FrameListener>>();

function subscribeFrames(
  Processor: TrackProcessorCtor,
  track: MediaStreamTrack,
  listener: FrameListener,
): () => void {
  let listeners = frameFeeds.get(track);
  if (!listeners) {
    const feed = new Set<FrameListener>();
    listeners = feed;
    frameFeeds.set(track, feed);
    const reader = new Processor({ track, maxBufferSize: 1 }).readable.getReader();
    void (async () => {
      try {
        for (;;) {
          const { value: frame, done } = await reader.read();
          if (done) break;
          try {
            for (const l of feed) {
              try {
                l(frame);
              } catch {
                /* One listener failing to copy a frame doesn't starve the others. */
              }
            }
          } finally {
            frame.close();
          }
        }
      } catch {
        /* The stream errored; treated like the track ending. */
      }
      if (frameFeeds.get(track) === feed) frameFeeds.delete(track);
    })();
  }
  const feed = listeners;
  feed.add(listener);
  return () => feed.delete(listener);
}

/** One emitted output frame: its timestamp (µs), size, and the source frame it shows. */
interface Composite {
  at: number;
  w: number;
  h: number;
  rtp: number | null;
}

/**
 * Wraps a (possibly absent) `MediaStream` in a canvas pipeline that
 * composites digital static over the video. Pass `null` to run the pipeline
 * sourceless — it emits pure static until a source is attached via
 * {@link NoisePipeline.setSource}. Returns null when `captureStream` is
 * unavailable (SSR, test environments without canvas support).
 *
 * Every output frame is tied to the source frame it shows (see the source
 * frames note below). Where `MediaStreamTrackGenerator` exists the output is
 * written frame by frame with a unique timestamp, which a displaying element
 * reports back as `mediaTime`, so a displayed frame maps to its composite
 * exactly. Elsewhere it falls back to on-demand `canvas.captureStream`,
 * where a displayed frame can only be estimated.
 */
export function tryCreateNoisePipeline(
  rawStream: MediaStream | null,
  initialIntensity: number,
  options?: NoisePipelineOptions,
): NoisePipeline | null {
  const canvas = document.createElement("canvas");
  if (typeof (canvas as HTMLCanvasElement & { captureStream?: unknown }).captureStream !== "function") {
    return null;
  }

  const noiseCanvas = document.createElement("canvas");
  const noiseCtx = noiseCanvas.getContext("2d");
  const ctx = canvas.getContext("2d");
  const heldCanvas = document.createElement("canvas");
  const heldCtx = heldCanvas.getContext("2d");
  if (!noiseCtx || !ctx || !heldCtx) return null;

  // Size the output canvas to video dimensions once frames arrive.
  // Until then (and while sourceless), use a placeholder so the output
  // has something to show.
  canvas.width = NOISE_MAX_W;
  canvas.height = NOISE_MAX_H;
  noiseCanvas.width = Math.ceil(NOISE_MAX_W / 2);
  noiseCanvas.height = Math.ceil(NOISE_MAX_H / 2);

  let intensity = initialIntensity;
  let showStatic = options?.showStatic ?? true;
  let passthrough = options?.passthrough ?? false;
  const onStallChange = options?.onStallChange;
  let rafId: number | null = null;
  let destroyed = false;

  /*
   * Source frames. Each decoded frame is copied into heldCanvas together
   * with its RTP timestamp, and the draw loop composites only that copy, so
   * the pixels of a composite and the timestamp reported for it always
   * belong to the same frame. Where MediaStreamTrackProcessor exists the
   * frames are read from the source track (see subscribeFrames), each
   * VideoFrame carrying its own timestamp. Elsewhere a hidden video element
   * copies its image inside requestVideoFrameCallback, which Chrome was measured to
   * get one frame ahead of the metadata about 1% of the time; that path's
   * timestamps are never reported as exact. Where neither exists (jsdom,
   * old engines) stall detection is disabled, the draw loop composites the
   * live video and no timestamps are known.
   *
   * lastFrameTs = 0 on (re)attach, so a freshly connected track shows the
   * full-static "waiting" look until its first real frame decodes.
   */
  const Processor = (globalThis as { MediaStreamTrackProcessor?: TrackProcessorCtor })
    .MediaStreamTrackProcessor;
  const useProcessor = typeof Processor === "function";
  const video = document.createElement("video");
  video.muted = true;
  video.playsInline = true;
  const videoVfc = video as HTMLVideoElement & {
    requestVideoFrameCallback?: (
      cb: (now: number, metadata?: { rtpTimestamp?: number; width?: number; height?: number }) => void,
    ) => number;
    cancelVideoFrameCallback?: (id: number) => void;
  };
  const vfcSupported = typeof videoVfc.requestVideoFrameCallback === "function";
  const framesTracked = useProcessor || vfcSupported;
  let hasSource = false;
  let lastFrameTs = 0;
  let vfcId: number | null = null;
  let stopReader: (() => void) | null = null;
  /* Whether heldCanvas holds a frame of the current source, its RTP timestamp, and a count of frames copied in. */
  let hasHeld = false;
  let heldRtp: number | null = null;
  let heldVersion = 0;

  const holdFrame = (image: CanvasImageSource, w: number, h: number, rtp: unknown) => {
    lastFrameTs = performance.now();
    if (heldCanvas.width !== w || heldCanvas.height !== h) {
      heldCanvas.width = w;
      heldCanvas.height = h;
    }
    heldCtx.drawImage(image, 0, 0, w, h);
    heldRtp = typeof rtp === "number" ? rtp : null;
    hasHeld = true;
    heldVersion++;
  };

  const armFrameCallback = () => {
    if (destroyed || !vfcSupported) return;
    vfcId = videoVfc.requestVideoFrameCallback!((_now, metadata) => {
      holdFrame(
        video,
        metadata?.width || video.videoWidth || canvas.width,
        metadata?.height || video.videoHeight || canvas.height,
        metadata?.rtpTimestamp,
      );
      armFrameCallback();
    });
  };

  const readTrack = (stream: MediaStream) => {
    const track = stream.getVideoTracks?.()[0];
    if (!track) return;
    stopReader = subscribeFrames(Processor!, track, (frame) => {
      if (destroyed) return;
      const meta = (frame as VideoFrame & { metadata?: () => { rtpTimestamp?: number } }).metadata?.();
      if (passthrough && writer) {
        lastFrameTs = performance.now();
        hasHeld = false;
        forward(frame, meta?.rtpTimestamp);
      } else {
        holdFrame(frame, frame.displayWidth, frame.displayHeight, meta?.rtpTimestamp);
      }
    });
  };

  /*
   * Stall presentation state. stallStartTs anchors the static ramp; each
   * stalled draw restores the held frame pristine before compositing the
   * ramping scrim and static (painting onto an unrestored canvas would
   * accumulate to black within a few frames regardless of the ramp
   * position).
   */
  let stallStartTs: number | null = null;
  let stallHasFrame = false;

  const setStalled = (next: boolean, now: number) => {
    if (next === (stallStartTs !== null)) return;
    if (next) {
      stallStartTs = now;
      stallHasFrame = hasHeld;
    } else {
      stallStartTs = null;
      stallHasFrame = false;
    }
    onStallChange?.(next);
  };

  const sizeTo = (vw: number, vh: number) => {
    canvas.width = vw;
    canvas.height = vh;
    const scale = Math.min(1, NOISE_MAX_W / vw, NOISE_MAX_H / vh);
    noiseCanvas.width = Math.max(1, Math.round(vw * scale));
    noiseCanvas.height = Math.max(1, Math.round(vh * scale));
  };

  const detachSource = () => {
    stopReader?.();
    stopReader = null;
    if (vfcId !== null) videoVfc.cancelVideoFrameCallback?.(vfcId);
    vfcId = null;
    video.pause();
    video.srcObject = null;
  };

  const setSource = (stream: MediaStream | null) => {
    if (destroyed) return;
    // Stall state and the held frame belong to the outgoing source; a swap starts clean.
    setStalled(false, performance.now());
    detachSource();
    hasHeld = false;
    heldRtp = null;
    hasSource = stream !== null;
    if (stream) {
      lastFrameTs = 0; // full static until the first frame actually decodes
      if (useProcessor) {
        readTrack(stream);
      } else {
        video.srcObject = stream;
        armFrameCallback();
        void video.play();
      }
    } else {
      // Sourceless: draw() composites pure static. Reset the canvas to the
      // placeholder size so a stale source's dimensions don't linger.
      canvas.width = NOISE_MAX_W;
      canvas.height = NOISE_MAX_H;
      noiseCanvas.width = Math.ceil(NOISE_MAX_W / 2);
      noiseCanvas.height = Math.ceil(NOISE_MAX_H / 2);
    }
  };

  /*
   * Output. With a generator, each emitted composite becomes a VideoFrame
   * stamped with a unique, increasing timestamp; without one the canvas is
   * captured on demand. `history` maps emitted frames back to their source
   * frame, oldest first.
   */
  const Generator = (globalThis as { MediaStreamTrackGenerator?: TrackGeneratorCtor })
    .MediaStreamTrackGenerator;
  const useGenerator = typeof Generator === "function" && typeof VideoFrame === "function";
  let generator: TrackGenerator | null = null;
  let writer: WritableStreamDefaultWriter<VideoFrame> | null = null;
  let pendingWrites = 0;
  let captureTrack: { requestFrame?: () => void } | undefined;
  const history: Composite[] = [];
  let lastEmitAt = -Infinity;
  let lastTimestampUs = 0;
  let emittedVersion = -1;

  /* A successful write hands the frame to the track (Chrome closes it); a rejected one is ours to close. */
  const write = (frame: VideoFrame) => {
    pendingWrites++;
    writer!.write(frame).then(
      () => pendingWrites--,
      () => {
        pendingWrites--;
        frame.close();
      },
    );
  };

  const record = (now: number, at: number, w: number, h: number, rtp: number | null) => {
    lastTimestampUs = at;
    lastEmitAt = now;
    history.push({ at, w, h, rtp });
    if (history.length > COMPOSITE_HISTORY) history.shift();
  };

  const emit = (now: number, rtp: number | null, version: number) => {
    if (version === emittedVersion && now - lastEmitAt < 1000 / OUTPUT_FPS - OUTPUT_SLACK_MS) {
      return;
    }
    let at: number;
    if (writer) {
      /* A sink that stops draining drops frames here rather than queueing them. */
      if (pendingWrites >= 2) return;
      at = Math.max(lastTimestampUs + 1, Math.round(now * 1000));
      let frame: VideoFrame;
      try {
        frame = new VideoFrame(canvas, { timestamp: at, alpha: "discard" });
      } catch {
        return;
      }
      write(frame);
    } else {
      at = Math.round(now * 1000);
      captureTrack?.requestFrame?.();
    }
    emittedVersion = version;
    record(now, at, canvas.width, canvas.height, rtp);
  };

  /** Passthrough: re-stamp a source frame and write it straight to the output. */
  const forward = (frame: VideoFrame, rtp: unknown) => {
    if (pendingWrites >= 2) return;
    const now = performance.now();
    const at = Math.max(lastTimestampUs + 1, Math.round(now * 1000));
    let out: VideoFrame;
    try {
      out = new VideoFrame(frame, { timestamp: at });
    } catch {
      return;
    }
    write(out);
    record(now, at, frame.displayWidth, frame.displayHeight, typeof rtp === "number" ? rtp : null);
  };

  const displayedFrame = (metadata: DisplayedFrameMetadata): DisplayedSourceFrame | null => {
    if (useGenerator) {
      if (typeof metadata.mediaTime !== "number") return null;
      const at = Math.round(metadata.mediaTime * 1e6);
      /*
       * Composites are at least a rAF apart, so a match within 1 µs is
       * unambiguous. A size that disagrees with the presented frame means the
       * match is not this frame after all, so nothing is reported.
       */
      for (let i = history.length - 1; i >= 0; i--) {
        const c = history[i]!;
        if (Math.abs(c.at - at) > 1) continue;
        if (metadata.width && metadata.height && (metadata.width !== c.w || metadata.height !== c.h)) {
          return null;
        }
        return { rtpTimestamp: c.rtp, exact: useProcessor };
      }
      return null;
    }
    const presentedUs = (metadata.presentationTime ?? performance.now()) * 1000;
    const cutoff = presentedUs - FALLBACK_OUTPUT_LATENCY_MS * 1000;
    for (let i = history.length - 1; i >= 0; i--) {
      if (history[i]!.at <= cutoff) return { rtpTimestamp: history[i]!.rtp, exact: false };
    }
    return null;
  };

  const draw = () => {
    if (destroyed) return;
    const now = performance.now();

    /*
     * Follow mid-stream size changes. Without this the canvas (and the
     * processed stream every consumer displays and records) stays at the
     * first decoded size forever, even after the sidecar raises the render
     * size.
     */
    const vw = hasHeld ? heldCanvas.width : video.videoWidth;
    const vh = hasHeld ? heldCanvas.height : video.videoHeight;
    if (hasSource && vw > 0 && vh > 0 && (vw !== canvas.width || vh !== canvas.height)) {
      sizeTo(vw, vh);
    }
    /*
     * Passthrough with frames flowing (or held on the element through a
     * stall): the output is fed by forward(), so only stall detection runs.
     * Sourceless and waiting states still composite black.
     */
    if (passthrough && writer && hasSource && lastFrameTs > 0) {
      setStalled(now - lastFrameTs > SOURCE_STALL_MS, now);
      rafId = requestAnimationFrame(draw);
      return;
    }
    const version = heldVersion;
    const rtp = composite(now);
    emit(now, rtp, version);
    rafId = requestAnimationFrame(draw);
  };

  /** Paint one output frame; returns the RTP timestamp of the source frame it shows. */
  const composite = (now: number): number | null => {
    const cw = canvas.width;
    const ch = canvas.height;
    const nw = noiseCanvas.width;
    const nh = noiseCanvas.height;

    // A source that exists but hasn't decoded a frame recently (or yet) is
    // treated as stalled; otherwise the decoder's starved output
    // (macroblock smear) leaks through as a second static style.
    const stalled = framesTracked && hasSource && now - lastFrameTs > SOURCE_STALL_MS;
    setStalled(stalled, now);

    /*
     * Static strength and layer opacity for this frame. A delivering feed
     * draws degrade-driven static at full layer opacity; a stall pins the
     * static to full strength but ramps the LAYER in over the held last
     * frame, so degradation eases in while recovery (the first decoded
     * frame) is instant.
     */
    let eff = intensity;
    let layerAlpha = 1;
    let shownRtp: number | null = null;

    if (hasSource && !stalled && hasHeld) {
      ctx.drawImage(heldCanvas, 0, 0, cw, ch);
      shownRtp = heldRtp;
    } else if (hasSource && !framesTracked && video.readyState >= 2) {
      ctx.drawImage(video, 0, 0, cw, ch);
    } else if (!hasSource) {
      // Sourceless: clear to black so static composites over a blank field
      // rather than the last live frame.
      ctx.fillStyle = "#000";
      ctx.fillRect(0, 0, cw, ch);
      if (!showStatic) return null;
    } else if (stalled) {
      // Stall pins the noise to full strength; the handle's degrade-driven
      // intensity (possibly near zero) belongs to a feed that is delivering.
      eff = 1;
      if (stallHasFrame) {
        // Restore the held last frame, then ramp a black scrim in step with
        // the static so the fully-ramped look matches the sourceless one.
        ctx.drawImage(heldCanvas, 0, 0, cw, ch);
        shownRtp = heldRtp;
        if (!showStatic) {
          // Frozen last frame, no static: the consumer's chrome carries the
          // staleness indicator instead.
          return shownRtp;
        }
        layerAlpha = Math.min(1, (now - (stallStartTs ?? now)) / STALL_RAMP_MS);
        ctx.globalAlpha = layerAlpha;
        ctx.fillStyle = "#000";
        ctx.fillRect(0, 0, cw, ch);
        ctx.globalAlpha = 1;
      } else {
        // Never decoded a frame (waiting after attach): the established
        // immediate full-static look; nothing to soften from.
        ctx.fillStyle = "#000";
        ctx.fillRect(0, 0, cw, ch);
        if (!showStatic) return null;
      }
    }

    /* No static to draw (frame-lock passthrough): the noise layer would be fully transparent. */
    if (eff <= 0) return shownRtp;

    // Build noise at reduced resolution; composited up to full canvas size.
    const imageData = noiseCtx.createImageData(nw, nh);
    const d = imageData.data;
    const dropThreshold = (eff - 0.45) * 0.35;
    const dropAlpha = Math.round(Math.min(eff * 1.8, 1) * 230);
    const speckleAlpha = Math.round(eff * 210);

    for (let row = 0; row < nh; row++) {
      const dropped = eff > 0.45 && Math.random() < dropThreshold;
      for (let col = 0; col < nw; col++) {
        const i = (row * nw + col) * 4;
        if (dropped) {
          d[i] = d[i + 1] = d[i + 2] = 0;
          d[i + 3] = dropAlpha;
        } else if (Math.random() < eff * 0.45) {
          const v = Math.floor(Math.random() * 155 + 100);
          d[i] = d[i + 1] = d[i + 2] = v;
          d[i + 3] = speckleAlpha;
        }
        // else: fully transparent (ImageData is zeroed on creation)
      }
    }
    noiseCtx.putImageData(imageData, 0, 0);
    ctx.globalAlpha = layerAlpha;
    ctx.drawImage(noiseCanvas, 0, 0, cw, ch);
    ctx.globalAlpha = 1;
    return shownRtp;
  };

  let processedStream: MediaStream;
  try {
    if (useGenerator) {
      generator = new Generator!({ kind: "video" });
      writer = generator.writable.getWriter();
      processedStream = new MediaStream([generator]);
    } else {
      processedStream = (
        canvas as HTMLCanvasElement & { captureStream(fps: number): MediaStream }
      ).captureStream(0);
      captureTrack = processedStream.getVideoTracks?.()[0] as typeof captureTrack;
    }
  } catch {
    return null;
  }

  setSource(rawStream);
  rafId = requestAnimationFrame(draw);

  return {
    processedStream,
    setIntensity(level: number) {
      intensity = level;
    },
    setSource,
    setShowStatic(enabled: boolean) {
      showStatic = enabled;
    },
    setPassthrough(enabled: boolean) {
      passthrough = enabled;
    },
    displayedFrame,
    destroy() {
      destroyed = true;
      if (rafId !== null) cancelAnimationFrame(rafId);
      detachSource();
      writer?.close().catch(() => {});
    },
  };
}
