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

export interface NoisePipelineOptions {
  /**
   * Composite static over a stalled-but-attached source (default true).
   * When false a stalled source holds its last decoded frame with no
   * static, so the consumer can mark staleness in its own chrome instead.
   * Sourceless static (signal loss / camera-switch gap) is unaffected.
   */
  staticOnStall?: boolean;
  /** Observe stall transitions (frames stopped / frames resumed). */
  onStallChange?: (stalled: boolean) => void;
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
  /** Runtime switch for {@link NoisePipelineOptions.staticOnStall}. */
  setStaticOnStall(enabled: boolean): void;
  destroy(): void;
}

/**
 * Wraps a (possibly absent) `MediaStream` in a canvas pipeline that
 * composites digital static over the video. Pass `null` to run the pipeline
 * sourceless — it emits pure static until a source is attached via
 * {@link NoisePipeline.setSource}. Returns null when `captureStream` is
 * unavailable (SSR, test environments without canvas support).
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
  if (!noiseCtx || !ctx) return null;

  // Size the output canvas to video dimensions once metadata loads.
  // Until then (and while sourceless), use a placeholder so captureStream
  // has something to output.
  canvas.width = NOISE_MAX_W;
  canvas.height = NOISE_MAX_H;
  noiseCanvas.width = Math.ceil(NOISE_MAX_W / 2);
  noiseCanvas.height = Math.ceil(NOISE_MAX_H / 2);

  let intensity = initialIntensity;
  let staticOnStall = options?.staticOnStall ?? true;
  const onStallChange = options?.onStallChange;
  let rafId: number | null = null;
  let destroyed = false;

  const video = document.createElement("video");
  video.muted = true;
  video.playsInline = true;

  /*
   * Frame-stall tracking via requestVideoFrameCallback (per decoded frame).
   * lastFrameTs = 0 on (re)attach, so a freshly connected track shows the
   * full-static "waiting" look until its first real frame decodes. Where
   * rVFC is unsupported (jsdom, old engines) stall detection is disabled
   * and behaviour is unchanged.
   */
  const videoVfc = video as HTMLVideoElement & {
    requestVideoFrameCallback?: (cb: () => void) => number;
    cancelVideoFrameCallback?: (id: number) => void;
  };
  const vfcSupported = typeof videoVfc.requestVideoFrameCallback === "function";
  let lastFrameTs = 0;
  let vfcId: number | null = null;
  const armFrameCallback = () => {
    if (destroyed || !vfcSupported) return;
    vfcId = videoVfc.requestVideoFrameCallback!(() => {
      lastFrameTs = performance.now();
      armFrameCallback();
    });
  };

  /*
   * Stall presentation state. stallStartTs anchors the static ramp;
   * lastFrameCanvas holds a copy of the final decoded frame so each stalled
   * draw can restore it pristine before compositing the ramping scrim and
   * static (painting onto an unrestored canvas would accumulate to black
   * within a few frames regardless of the ramp position).
   */
  let stallStartTs: number | null = null;
  let stallHasFrame = false;
  let lastFrameCanvas: HTMLCanvasElement | null = null;
  let lastFrameCtx: CanvasRenderingContext2D | null = null;

  const snapshotLastFrame = (): boolean => {
    if (!lastFrameCanvas) {
      lastFrameCanvas = document.createElement("canvas");
      lastFrameCtx = lastFrameCanvas.getContext("2d");
    }
    if (!lastFrameCtx) return false;
    lastFrameCanvas.width = canvas.width;
    lastFrameCanvas.height = canvas.height;
    lastFrameCtx.drawImage(canvas, 0, 0);
    return true;
  };

  const setStalled = (next: boolean, now: number) => {
    if (next === (stallStartTs !== null)) return;
    if (next) {
      stallStartTs = now;
      stallHasFrame = lastFrameTs > 0 && snapshotLastFrame();
    } else {
      stallStartTs = null;
      stallHasFrame = false;
    }
    onStallChange?.(next);
  };

  const onMeta = () => {
    const vw = video.videoWidth;
    const vh = video.videoHeight;
    if (!vw || !vh) return;
    canvas.width = vw;
    canvas.height = vh;
    const scale = Math.min(1, NOISE_MAX_W / vw, NOISE_MAX_H / vh);
    noiseCanvas.width = Math.max(1, Math.round(vw * scale));
    noiseCanvas.height = Math.max(1, Math.round(vh * scale));
  };
  video.addEventListener("loadedmetadata", onMeta);

  const setSource = (stream: MediaStream | null) => {
    if (destroyed) return;
    // Stall state belongs to the outgoing source; a swap starts clean.
    setStalled(false, performance.now());
    if (stream) {
      video.srcObject = stream;
      lastFrameTs = 0; // full static until the first frame actually decodes
      armFrameCallback();
      void video.play();
    } else {
      // Sourceless: detach the video so draw() composites pure static. Reset
      // the canvas to the placeholder size so a stale source's dimensions
      // don't linger.
      video.pause();
      video.srcObject = null;
      canvas.width = NOISE_MAX_W;
      canvas.height = NOISE_MAX_H;
      noiseCanvas.width = Math.ceil(NOISE_MAX_W / 2);
      noiseCanvas.height = Math.ceil(NOISE_MAX_H / 2);
    }
  };
  setSource(rawStream);

  const draw = () => {
    if (destroyed) return;
    const now = performance.now();
    const cw = canvas.width;
    const ch = canvas.height;
    const nw = noiseCanvas.width;
    const nh = noiseCanvas.height;

    // A source that exists but hasn't decoded a frame recently (or yet) is
    // treated as stalled; otherwise the decoder's starved output
    // (macroblock smear) leaks through as a second static style.
    const stalled =
      vfcSupported &&
      video.srcObject !== null &&
      now - lastFrameTs > SOURCE_STALL_MS;
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

    if (video.srcObject && !stalled && video.readyState >= 2) {
      ctx.drawImage(video, 0, 0, cw, ch);
    } else if (!video.srcObject) {
      // Sourceless: clear to black so static composites over a blank field
      // rather than the last live frame.
      ctx.fillStyle = "#000";
      ctx.fillRect(0, 0, cw, ch);
    } else if (stalled) {
      // Stall pins the noise to full strength; the handle's degrade-driven
      // intensity (possibly near zero) belongs to a feed that is delivering.
      eff = 1;
      if (stallHasFrame && lastFrameCanvas) {
        // Restore the held last frame, then ramp a black scrim in step with
        // the static so the fully-ramped look matches the sourceless one.
        ctx.drawImage(lastFrameCanvas, 0, 0, cw, ch);
        if (!staticOnStall) {
          // Frozen last frame, no static: the consumer's chrome carries the
          // staleness indicator instead.
          rafId = requestAnimationFrame(draw);
          return;
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
        if (!staticOnStall) {
          rafId = requestAnimationFrame(draw);
          return;
        }
      }
    }

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

    rafId = requestAnimationFrame(draw);
  };
  rafId = requestAnimationFrame(draw);

  let processedStream: MediaStream;
  try {
    processedStream = (
      canvas as HTMLCanvasElement & { captureStream(fps: number): MediaStream }
    ).captureStream(30);
  } catch {
    destroyed = true;
    if (rafId !== null) cancelAnimationFrame(rafId);
    video.removeEventListener("loadedmetadata", onMeta);
    video.pause();
    video.srcObject = null;
    return null;
  }

  return {
    processedStream,
    setIntensity(level: number) {
      intensity = level;
    },
    setSource,
    setStaticOnStall(enabled: boolean) {
      staticOnStall = enabled;
    },
    destroy() {
      destroyed = true;
      if (rafId !== null) cancelAnimationFrame(rafId);
      if (vfcId !== null) videoVfc.cancelVideoFrameCallback?.(vfcId);
      video.removeEventListener("loadedmetadata", onMeta);
      video.pause();
      video.srcObject = null;
    },
  };
}
