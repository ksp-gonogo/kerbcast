/**
 * Pipeline-level tests for the stall presentation in `noise.ts`:
 *
 *  - a source that stalls after delivering frames gets the static ramped in
 *    over STALL_RAMP_MS on top of the held last frame (layer alpha 0 -> 1);
 *  - the first decoded frame drops the static immediately (no ramp out);
 *  - `showStatic: false` freezes the last frame with no static at all;
 *  - a source that never decoded a frame keeps the immediate full-static
 *    "waiting" look;
 *  - stall transitions surface through `onStallChange`.
 *
 * jsdom has no canvas/captureStream/requestVideoFrameCallback, so the test
 * installs a recording 2d-context fake, captures the rAF draw loop and the
 * rVFC frame callback, and drives time through a mocked `performance.now`.
 * Draw records carry the `globalAlpha` in effect at call time, which is the
 * opacity mechanic the ramp is implemented with.
 */

import { afterEach, describe, expect, it, vi } from "vitest";
import { tryCreateNoisePipeline } from "./noise";

interface DrawRecord {
  /*
   * getContext call order in the pipeline: 0 = the noise scratch canvas,
   * 1 = the OUTPUT canvas (what the processed stream shows), 2 = the
   * held-frame canvas (receives a copy of each decoded source frame).
   */
  ctx: number;
  kind: "drawImage" | "fillRect";
  src?: unknown;
  alpha: number;
}

/** The output canvas is the second 2d context the pipeline requests. */
const OUTPUT_CTX = 1;

interface StallEnv {
  setNow(t: number): void;
  /** Deliver one decoded frame (fires the captured rVFC callback). */
  fireFrame(metadata?: { rtpTimestamp?: number }): void;
  /** Run one draw-loop iteration; returns the OUTPUT-canvas records it made. */
  draw(): DrawRecord[];
  putImageData: ReturnType<typeof vi.fn>;
  /** `requestFrame` of the canvas-capture output track. */
  requestFrame: ReturnType<typeof vi.fn>;
  restore(): void;
}

function installStallEnv(): StallEnv {
  let now = 0;
  const records: DrawRecord[] = [];
  const putImageData = vi.fn();

  /*
   * Each canvas gets its own recording context (tagged by creation order) so
   * tests can tell an output-canvas composite from the stall-onset snapshot
   * copy into the held-frame canvas.
   */
  let ctxCount = 0;
  const makeCtx = () => {
    const id = ctxCount++;
    const ctx = {
      globalAlpha: 1,
      fillStyle: "",
      drawImage: (src: unknown) => {
        records.push({ ctx: id, kind: "drawImage", src, alpha: ctx.globalAlpha });
      },
      fillRect: () => {
        records.push({ ctx: id, kind: "fillRect", alpha: ctx.globalAlpha });
      },
      createImageData: (w: number, h: number) => ({
        data: new Uint8ClampedArray(w * h * 4),
        width: w,
        height: h,
      }),
      putImageData,
    };
    return ctx;
  };

  const origGetContext = HTMLCanvasElement.prototype.getContext;
  // @ts-expect-error -- jsdom augmentation
  HTMLCanvasElement.prototype.getContext = vi.fn(() => makeCtx());
  // @ts-expect-error -- jsdom augmentation
  const requestFrame = vi.fn();
  HTMLCanvasElement.prototype.captureStream = vi.fn(
    () => ({ getVideoTracks: () => [{ requestFrame }] }) as unknown as MediaStream,
  );

  let rafCb: FrameRequestCallback | null = null;
  const rafSpy = vi
    .spyOn(globalThis, "requestAnimationFrame")
    .mockImplementation((cb) => {
      rafCb = cb;
      return 1;
    });
  const cancelSpy = vi
    .spyOn(globalThis, "cancelAnimationFrame")
    .mockImplementation(() => {});

  type VfcCallback = (now: number, metadata: { rtpTimestamp?: number }) => void;
  let vfcCb: VfcCallback | null = null;
  const proto = HTMLVideoElement.prototype as HTMLVideoElement & {
    requestVideoFrameCallback?: (cb: VfcCallback) => number;
    cancelVideoFrameCallback?: (id: number) => void;
  };
  proto.requestVideoFrameCallback = (cb: VfcCallback) => {
    vfcCb = cb;
    return 1;
  };
  proto.cancelVideoFrameCallback = () => {};

  const origPlay = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = () => Promise.resolve();
  const origPause = HTMLMediaElement.prototype.pause;
  HTMLMediaElement.prototype.pause = () => {};

  const nowSpy = vi.spyOn(performance, "now").mockImplementation(() => now);

  return {
    setNow(t: number) {
      now = t;
    },
    fireFrame(metadata = {}) {
      vfcCb?.(now, metadata);
    },
    draw() {
      const start = records.length;
      rafCb?.(now);
      return records.slice(start).filter((r) => r.ctx === OUTPUT_CTX);
    },
    putImageData,
    requestFrame,
    restore() {
      HTMLCanvasElement.prototype.getContext = origGetContext;
      // @ts-expect-error -- cleanup
      delete HTMLCanvasElement.prototype.captureStream;
      delete proto.requestVideoFrameCallback;
      delete proto.cancelVideoFrameCallback;
      HTMLMediaElement.prototype.play = origPlay;
      HTMLMediaElement.prototype.pause = origPause;
      rafSpy.mockRestore();
      cancelSpy.mockRestore();
      nowSpy.mockRestore();
    },
  };
}

function fakeStream(): MediaStream {
  return { getTracks: () => [] } as unknown as MediaStream;
}

/** Last drawImage of a draw: the noise-canvas composite. */
function noiseDraw(records: DrawRecord[]): DrawRecord | undefined {
  return [...records].reverse().find((r) => r.kind === "drawImage");
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("noise pipeline stall presentation", () => {
  it("ramps static in over the held last frame after a stall", () => {
    const env = installStallEnv();
    try {
      const onStallChange = vi.fn();
      const pipeline = tryCreateNoisePipeline(fakeStream(), 0.05, {
        onStallChange,
      });
      expect(pipeline).not.toBeNull();

      // Frames delivering: no stall.
      env.setNow(1000);
      env.fireFrame();
      env.setNow(1100);
      env.draw();
      expect(onStallChange).not.toHaveBeenCalled();

      // Stall onset (>500 ms since the last decoded frame): static layer
      // starts at alpha 0 over the restored last frame.
      env.setNow(1601);
      let records = env.draw();
      expect(onStallChange).toHaveBeenLastCalledWith(true);
      // First drawImage restores the held frame at full opacity.
      const restore = records.find((r) => r.kind === "drawImage");
      expect(restore?.alpha).toBe(1);
      expect(restore?.src).toBeInstanceOf(HTMLCanvasElement);
      // Scrim + static composite at the ramp position (0 at onset).
      expect(records.find((r) => r.kind === "fillRect")?.alpha).toBe(0);
      expect(noiseDraw(records)?.alpha).toBe(0);

      // Halfway through the 5 s ramp.
      env.setNow(1601 + 2500);
      records = env.draw();
      expect(records.find((r) => r.kind === "fillRect")?.alpha).toBeCloseTo(0.5);
      expect(noiseDraw(records)?.alpha).toBeCloseTo(0.5);

      // Fully ramped (and clamped thereafter).
      env.setNow(1601 + 7000);
      records = env.draw();
      expect(records.find((r) => r.kind === "fillRect")?.alpha).toBe(1);
      expect(noiseDraw(records)?.alpha).toBe(1);

      // One stall, one event.
      expect(onStallChange).toHaveBeenCalledTimes(1);
      pipeline?.destroy();
    } finally {
      env.restore();
    }
  });

  it("drops the static immediately when frames resume", () => {
    const env = installStallEnv();
    try {
      const onStallChange = vi.fn();
      const pipeline = tryCreateNoisePipeline(fakeStream(), 0.05, {
        onStallChange,
      });

      env.setNow(1000);
      env.fireFrame();
      env.setNow(2000);
      env.draw(); // stalled, mid-ramp
      expect(onStallChange).toHaveBeenLastCalledWith(true);

      // A decoded frame lands: the very next draw is back to the live path,
      // full-opacity degrade static, no scrim, no held-frame restore.
      env.fireFrame();
      const records = env.draw();
      expect(onStallChange).toHaveBeenLastCalledWith(false);
      expect(records.some((r) => r.kind === "fillRect")).toBe(false);
      expect(noiseDraw(records)?.alpha).toBe(1);
      pipeline?.destroy();
    } finally {
      env.restore();
    }
  });

  it("showStatic: false freezes the last frame with no static", () => {
    const env = installStallEnv();
    try {
      const onStallChange = vi.fn();
      const pipeline = tryCreateNoisePipeline(fakeStream(), 0.05, {
        showStatic: false,
        onStallChange,
      });

      env.setNow(1000);
      env.fireFrame();
      env.setNow(1601);
      env.putImageData.mockClear();
      const records = env.draw();

      // The stall is still detected and reported (consumers drive their own
      // staleness chrome from it) ...
      expect(onStallChange).toHaveBeenLastCalledWith(true);
      // ... but the draw only restores the held frame: no scrim, no noise.
      expect(records).toHaveLength(1);
      expect(records[0]?.kind).toBe("drawImage");
      expect(records[0]?.alpha).toBe(1);
      expect(env.putImageData).not.toHaveBeenCalled();

      // Runtime re-enable brings the static back on the next draw.
      pipeline?.setShowStatic(true);
      env.setNow(1700);
      env.draw();
      expect(env.putImageData).toHaveBeenCalled();
      pipeline?.destroy();
    } finally {
      env.restore();
    }
  });

  it("keeps the immediate full-static look for a source that never decoded", () => {
    const env = installStallEnv();
    try {
      const onStallChange = vi.fn();
      const pipeline = tryCreateNoisePipeline(fakeStream(), 0.05, {
        onStallChange,
      });

      // No frame ever fires; past the stall window the waiting look is the
      // hard full-static cut (black + static at alpha 1), not a ramp.
      env.setNow(601);
      const records = env.draw();
      expect(onStallChange).toHaveBeenLastCalledWith(true);
      expect(records.find((r) => r.kind === "fillRect")?.alpha).toBe(1);
      expect(noiseDraw(records)?.alpha).toBe(1);
      // Nothing restored under it: the only drawImage is the noise canvas.
      expect(records.filter((r) => r.kind === "drawImage")).toHaveLength(1);
      pipeline?.destroy();
    } finally {
      env.restore();
    }
  });

  it("showStatic: false skips noise on the sourceless path", () => {
    const env = installStallEnv();
    try {
      const pipeline = tryCreateNoisePipeline(null, 1.0, { showStatic: false });
      expect(pipeline).not.toBeNull();

      env.putImageData.mockClear();
      const records = env.draw();

      /* Sourceless + showStatic off: only the black fillRect, no noise composite. */
      expect(records).toHaveLength(1);
      expect(records[0]?.kind).toBe("fillRect");
      expect(env.putImageData).not.toHaveBeenCalled();

      /* Re-enable at runtime: noise appears on the next draw. */
      pipeline?.setShowStatic(true);
      env.draw();
      expect(env.putImageData).toHaveBeenCalled();

      pipeline?.destroy();
    } finally {
      env.restore();
    }
  });

  it("clears the stall state on source swap", () => {
    const env = installStallEnv();
    try {
      const onStallChange = vi.fn();
      const pipeline = tryCreateNoisePipeline(fakeStream(), 0.05, {
        onStallChange,
      });

      env.setNow(1000);
      env.fireFrame();
      env.setNow(2000);
      env.draw();
      expect(onStallChange).toHaveBeenLastCalledWith(true);

      // Going sourceless ends the stall (sourceless static takes over).
      pipeline?.setSource(null);
      expect(onStallChange).toHaveBeenLastCalledWith(false);
      pipeline?.destroy();
    } finally {
      env.restore();
    }
  });
});

describe("noise pipeline sizing", () => {
  it("resizes the output canvas when the source's decoded size changes mid-stream", () => {
    const env = installStallEnv();
    let vw = 784;
    let vh = 448;
    const proto = HTMLVideoElement.prototype;
    const origW = Object.getOwnPropertyDescriptor(proto, "videoWidth");
    const origH = Object.getOwnPropertyDescriptor(proto, "videoHeight");
    Object.defineProperty(proto, "videoWidth", { configurable: true, get: () => vw });
    Object.defineProperty(proto, "videoHeight", { configurable: true, get: () => vh });
    let output: HTMLCanvasElement | null = null;
    // @ts-expect-error -- jsdom augmentation
    HTMLCanvasElement.prototype.captureStream = vi.fn(function (this: HTMLCanvasElement) {
      output = this;
      return {} as MediaStream;
    });
    try {
      const pipeline = tryCreateNoisePipeline(fakeStream(), 0.05);
      expect(pipeline).not.toBeNull();

      env.setNow(1000);
      env.fireFrame();
      env.draw();
      expect([output!.width, output!.height]).toEqual([784, 448]);

      // The sidecar raises the render size; no second loadedmetadata fires.
      vw = 1024;
      vh = 576;
      env.setNow(1033);
      env.fireFrame();
      env.draw();
      expect([output!.width, output!.height]).toEqual([1024, 576]);
    } finally {
      if (origW) Object.defineProperty(proto, "videoWidth", origW);
      if (origH) Object.defineProperty(proto, "videoHeight", origH);
      env.restore();
    }
  });
});

/**
 * Fake MediaStreamTrackProcessor / MediaStreamTrackGenerator / VideoFrame.
 * `pushFrame` hands the pipeline one source frame; `written` collects what
 * the pipeline writes to its output track.
 */
function installInsertableStreams() {
  const written: { timestamp: number; src: unknown }[] = [];
  let pendingRead: ((r: { value?: unknown; done: boolean }) => void) | null = null;
  const closed: number[] = [];
  const pushFrame = async (rtpTimestamp: number) => {
    const frame = {
      rtp: rtpTimestamp,
      displayWidth: 640,
      displayHeight: 360,
      metadata: () => ({ rtpTimestamp }),
      close: () => closed.push(rtpTimestamp),
    };
    pendingRead?.({ value: frame, done: false });
    await Promise.resolve();
    await Promise.resolve();
    return frame;
  };
  const trackStop = vi.fn();
  const stream = {
    getTracks: () => [],
    getVideoTracks: () => [{ stop: trackStop }],
  } as unknown as MediaStream;
  const g = globalThis as Record<string, unknown>;
  const saved = {
    gen: g.MediaStreamTrackGenerator,
    proc: g.MediaStreamTrackProcessor,
    frame: g.VideoFrame,
    stream: g.MediaStream,
  };
  g.MediaStreamTrackProcessor = class {
    readable = {
      getReader: () => ({
        read: () => new Promise((resolve) => (pendingRead = resolve)),
        cancel: () => {
          pendingRead?.({ done: true });
          return Promise.resolve();
        },
      }),
    };
  };
  g.MediaStreamTrackGenerator = class {
    writable = {
      getWriter: () => ({
        write: (f: { timestamp: number; src: unknown }) => {
          written.push(f);
          return Promise.resolve();
        },
        close: () => Promise.resolve(),
      }),
    };
  };
  g.VideoFrame = class {
    timestamp: number;
    constructor(
      readonly src: unknown,
      init: { timestamp: number },
    ) {
      this.timestamp = init.timestamp;
    }
  };
  g.MediaStream = class {
    constructor(readonly tracks: unknown[]) {}
  };
  return {
    written,
    closed,
    pushFrame,
    stream,
    trackStop,
    restore() {
      g.MediaStreamTrackGenerator = saved.gen;
      g.MediaStreamTrackProcessor = saved.proc;
      g.VideoFrame = saved.frame;
      g.MediaStream = saved.stream;
    },
  };
}

describe("noise pipeline displayed frame", () => {
  it("estimates the oldest composite that could be on screen without a track generator", () => {
    const env = installStallEnv();
    try {
      const pipeline = tryCreateNoisePipeline(fakeStream(), 0.05)!;
      expect(pipeline.displayedFrame({ presentationTime: 0 })).toBeNull();

      env.setNow(1000);
      env.fireFrame({ rtpTimestamp: 9000 });
      env.draw();
      expect(env.requestFrame).toHaveBeenCalledTimes(1);
      env.setNow(1030);
      env.fireFrame({ rtpTimestamp: 12000 });
      env.draw();
      expect(env.requestFrame).toHaveBeenCalledTimes(2);

      /* Presented 35 ms after the second emit: too soon to rule out the first. */
      expect(pipeline.displayedFrame({ presentationTime: 1065 })).toEqual({
        rtpTimestamp: 9000,
        exact: false,
      });
      expect(pipeline.displayedFrame({ presentationTime: 1075 })).toEqual({
        rtpTimestamp: 12000,
        exact: false,
      });
      /* Nothing emitted long enough before it. */
      expect(pipeline.displayedFrame({ presentationTime: 1035 })).toBeNull();

      /* A stall holds the last frame, so its timestamp stays. */
      env.setNow(2000);
      env.draw();
      expect(pipeline.displayedFrame({ presentationTime: 2050 })?.rtpTimestamp).toBe(12000);

      pipeline.setSource(null);
      env.setNow(3000);
      env.draw();
      expect(pipeline.displayedFrame({ presentationTime: 3050 })).toEqual({
        rtpTimestamp: null,
        exact: false,
      });
      pipeline.destroy();
    } finally {
      env.restore();
    }
  });

  it("identifies each displayed frame exactly through a track processor and generator", async () => {
    const env = installStallEnv();
    const { written, closed, pushFrame, stream, trackStop, restore } = installInsertableStreams();
    try {
      const pipeline = tryCreateNoisePipeline(stream, 0.05)!;

      env.setNow(1000);
      await pushFrame(9000);
      env.draw();
      expect(written.map((f) => f.timestamp)).toEqual([1_000_000]);
      expect(pipeline.displayedFrame({ mediaTime: 1 })).toEqual({ rtpTimestamp: 9000, exact: true });
      expect(closed).toEqual([9000]);

      /* No new source frame and under the output interval: nothing emitted. */
      env.setNow(1010);
      env.draw();
      expect(written).toHaveLength(1);

      /* A new source frame is emitted straight away. */
      env.setNow(1016);
      await pushFrame(12000);
      env.draw();
      expect(written.map((f) => f.timestamp)).toEqual([1_000_000, 1_016_000]);
      expect(pipeline.displayedFrame({ mediaTime: 1.016 })).toEqual({ rtpTimestamp: 12000, exact: true });
      expect(pipeline.displayedFrame({ mediaTime: 1 })?.rtpTimestamp).toBe(9000);
      /* The presented frame's size must agree with the composite's. */
      expect(pipeline.displayedFrame({ mediaTime: 1.016, width: 640, height: 360 })?.exact).toBe(true);
      expect(pipeline.displayedFrame({ mediaTime: 1.016, width: 320, height: 180 })).toBeNull();

      /* Unknown frames are not guessed at. */
      expect(pipeline.displayedFrame({ mediaTime: 1.005 })).toBeNull();
      expect(pipeline.displayedFrame({})).toBeNull();

      /*
       * A source swap only unsubscribes: the shared reader keeps closing the
       * track's frames and the track is never stopped (processor teardown
       * leaks frames in Chrome).
       */
      pipeline.setSource(null);
      await pushFrame(15000);
      expect(closed).toEqual([9000, 12000, 15000]);
      env.setNow(1100);
      env.draw();
      expect(pipeline.displayedFrame({ mediaTime: 1.1 })).toEqual({ rtpTimestamp: null, exact: true });
      expect(trackStop).not.toHaveBeenCalled();
      pipeline.destroy();
    } finally {
      restore();
      env.restore();
    }
  });

  it("passthrough forwards source frames untouched and keeps them identifiable", async () => {
    const env = installStallEnv();
    const { written, closed, pushFrame, stream, restore } = installInsertableStreams();
    try {
      const pipeline = tryCreateNoisePipeline(stream, 0, { showStatic: false, passthrough: true })!;

      env.setNow(1000);
      const first = await pushFrame(9000);
      /* Written on arrival, re-stamped, from the source frame itself rather than a canvas. */
      expect(written).toHaveLength(1);
      expect(written[0]!.src).toBe(first);
      expect(closed).toEqual([9000]);
      expect(pipeline.displayedFrame({ mediaTime: written[0]!.timestamp / 1e6 })).toEqual({
        rtpTimestamp: 9000,
        exact: true,
      });

      /* Live frames come only from the source: the draw loop adds nothing. */
      env.setNow(1100);
      env.draw();
      expect(written).toHaveLength(1);

      /* Sourceless: the draw loop composites black again. */
      pipeline.setSource(null);
      env.setNow(1200);
      env.draw();
      expect(written).toHaveLength(2);
      expect(written[1]!.src).toBeInstanceOf(HTMLCanvasElement);
      pipeline.destroy();
    } finally {
      restore();
      env.restore();
    }
  });
});
