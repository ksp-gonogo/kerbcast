/**
 * Pipeline-level tests for the stall presentation in `noise.ts`:
 *
 *  - a source that stalls after delivering frames gets the static ramped in
 *    over STALL_RAMP_MS on top of the held last frame (layer alpha 0 -> 1);
 *  - the first decoded frame drops the static immediately (no ramp out);
 *  - `staticOnStall: false` freezes the last frame with no static at all;
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
   * held-last-frame canvas (created lazily at stall onset, receives the
   * snapshot copy).
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
  fireFrame(): void;
  /** Run one draw-loop iteration; returns the OUTPUT-canvas records it made. */
  draw(): DrawRecord[];
  putImageData: ReturnType<typeof vi.fn>;
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
  HTMLCanvasElement.prototype.captureStream = vi.fn(
    () => ({}) as MediaStream,
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

  let vfcCb: (() => void) | null = null;
  const proto = HTMLVideoElement.prototype as HTMLVideoElement & {
    requestVideoFrameCallback?: (cb: () => void) => number;
    cancelVideoFrameCallback?: (id: number) => void;
  };
  proto.requestVideoFrameCallback = (cb: () => void) => {
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
    fireFrame() {
      vfcCb?.();
    },
    draw() {
      const start = records.length;
      rafCb?.(now);
      return records.slice(start).filter((r) => r.ctx === OUTPUT_CTX);
    },
    putImageData,
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

  it("staticOnStall: false freezes the last frame with no static", () => {
    const env = installStallEnv();
    try {
      const onStallChange = vi.fn();
      const pipeline = tryCreateNoisePipeline(fakeStream(), 0.05, {
        staticOnStall: false,
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
      pipeline?.setStaticOnStall(true);
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
