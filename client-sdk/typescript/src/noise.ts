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
    const cw = canvas.width;
    const ch = canvas.height;
    const nw = noiseCanvas.width;
    const nh = noiseCanvas.height;

    // A source that exists but hasn't decoded a frame recently (or yet) is
    // shown exactly like no source at all — otherwise the decoder's starved
    // output (macroblock smear) leaks through as a second static style.
    const stalled =
      vfcSupported &&
      video.srcObject !== null &&
      performance.now() - lastFrameTs > SOURCE_STALL_MS;

    // Only draw the source frame when one is attached and decodable;
    // otherwise the canvas keeps its prior contents and we composite static
    // on top — which, sourceless, is the full-static signal-loss look.
    if (video.srcObject && !stalled && video.readyState >= 2) {
      ctx.drawImage(video, 0, 0, cw, ch);
    } else if (!video.srcObject || stalled) {
      // Sourceless or stalled: clear to black so static composites over a
      // blank field rather than the last live frame.
      ctx.fillStyle = "#000";
      ctx.fillRect(0, 0, cw, ch);
    }

    // Stall pins the noise to full strength — the handle's degrade-driven
    // intensity (possibly near zero) belongs to a feed that is delivering.
    const eff = stalled ? 1 : intensity;

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
    ctx.drawImage(noiseCanvas, 0, 0, cw, ch);

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
