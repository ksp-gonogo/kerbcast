import { describe, expect, it } from "vitest";
import { FRAME_STAMP_HISTORY, FrameStampStore, extrapolateCaptureUt } from "./frameUt";

describe("FrameStampStore", () => {
  it("keeps the newest stamps per camera and evicts the oldest", () => {
    const store = new FrameStampStore();
    for (let i = 0; i <= FRAME_STAMP_HISTORY; i++) {
      store.record({ flightId: 1, rtpTimestamp: i, captureUt: 100 + i, captureEpoch: 3 });
    }
    expect(store.lookup(1, 0)).toBeNull();
    expect(store.lookup(1, 1)).toEqual({ ut: 101, epoch: 3 });
    expect(store.lookup(1, FRAME_STAMP_HISTORY)).toEqual({ ut: 100 + FRAME_STAMP_HISTORY, epoch: 3 });
    expect(store.lookup(2, 1)).toBeNull();
  });

  it("finds the newest stamp that arrived by a given time", () => {
    const store = new FrameStampStore();
    store.record({ flightId: 1, rtpTimestamp: 10, captureUt: 1, captureEpoch: 0 }, 100);
    store.record({ flightId: 1, rtpTimestamp: 20, captureUt: 2, captureEpoch: 0 }, 133);
    expect(store.newestArrivedBy(1, 99)).toBeNull();
    expect(store.newestArrivedBy(1, 120)).toEqual({ ut: 1, epoch: 0, rtpTimestamp: 10 });
    expect(store.newestArrivedBy(1, 133)).toEqual({ ut: 2, epoch: 0, rtpTimestamp: 20 });
    expect(store.newestArrivedBy(2, 500)).toBeNull();
  });

  it("clear forgets everything", () => {
    const store = new FrameStampStore();
    store.record({ flightId: 1, rtpTimestamp: 5, captureUt: 1, captureEpoch: 0 });
    store.clear();
    expect(store.lookup(1, 5)).toBeNull();
  });
});

describe("extrapolateCaptureUt", () => {
  it("advances at the warp rate and caps the extrapolation", () => {
    expect(extrapolateCaptureUt(100, 4, 0, 500)).toBe(102);
    expect(extrapolateCaptureUt(100, 1, 0, 60_000)).toBe(102);
    expect(extrapolateCaptureUt(100, 1, 1000, 0)).toBe(100);
  });
});
