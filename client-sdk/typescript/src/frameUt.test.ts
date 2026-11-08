import { describe, expect, it } from "vitest";
import { FRAME_STAMP_HISTORY, FrameHistory, FrameStampStore, extrapolateCaptureUt } from "./frameUt";

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

  it("carries the live bound, boundary and command state as extras", () => {
    const store = new FrameStampStore();
    const command = {
      hasFix: false,
      frameHeightPx: 720,
      framingFits: true,
      occluded: false,
      occluder: "",
      sunlit: false,
      subjectPersistentId: 0,
    };
    store.record({ flightId: 1, rtpTimestamp: 5, captureUt: 1, captureEpoch: 0, liveLightTimeS: 0, boundaryM: 0.05, command });
    expect(store.lookup(1, 5)).toEqual({ ut: 1, epoch: 0, liveLightTimeS: 0, boundaryM: 0.05, command });
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

describe("FrameHistory", () => {
  const stamp = (ut: number, extra: object = {}, epoch = 0) => ({
    flightId: 1,
    rtpTimestamp: Math.round(ut * 1000),
    captureUt: ut,
    captureEpoch: epoch,
    ...extra,
  });

  it("finds the newest frame at or before a UT, with its extras", () => {
    const history = new FrameHistory();
    history.record(stamp(10, { fov: 30 }));
    history.record(stamp(11, { fov: 20 }));
    history.record(stamp(12, { fov: 10 }));
    expect(history.at(1, 9.9)).toBeNull();
    expect(history.at(1, 11.5)).toEqual({
      ut: 11,
      epoch: 0,
      source: "exact",
      flightId: 1,
      rtpTimestamp: null,
      presentedAt: 0,
      fov: 20,
    });
    expect(history.at(1, 100)?.fov).toBe(10);
    expect(history.at(1, 11)).toBe(history.at(1, 11.9));
    expect(history.at(2, 11)).toBeNull();
  });

  it("keeps frames back to the UT span, with one at or before its start", () => {
    const history = new FrameHistory(10, 1000);
    for (let ut = 0; ut <= 30; ut++) history.record(stamp(ut));
    expect(history.at(1, 19)).toBeNull();
    expect(history.at(1, 20.5)?.ut).toBe(20);
    expect(history.size(1)).toBe(11);
  });

  it("keeps at most its entry cap, dropping the oldest", () => {
    const history = new FrameHistory(1e9, 5);
    for (let ut = 0; ut < 100; ut++) history.record(stamp(ut));
    expect(history.size(1)).toBe(5);
    expect(history.at(1, 94.5)).toBeNull();
    expect(history.at(1, 95)?.ut).toBe(95);
  });

  it("drops what came after a frame at an earlier UT, and starts afresh on a new epoch", () => {
    const history = new FrameHistory();
    for (let ut = 0; ut < 10; ut++) history.record(stamp(ut));
    history.record(stamp(5.5));
    expect(history.at(1, 9)?.ut).toBe(5.5);
    history.record(stamp(50, {}, 1));
    expect(history.at(1, 49)).toBeNull();
    expect(history.size(1)).toBe(1);
  });
});
