/*
 * Capture-timed recording through RecordingController: a camera whose
 * encoded frames can be copied records them as an RTP-timed MP4 instead of
 * running MediaRecorder; one whose frames can't falls back to arrival timing.
 */

import { afterEach, describe, expect, it, vi } from "vitest";
import { accessUnits } from "../fixtures/h264";
import type { EncodedFrameCopy } from "./captureClip";
import {
  KEYFRAME_RETRY_MS,
  RecordingController,
  type RecordingClient,
  type RecordingClientCamera,
} from "./recording";
import type { CameraState } from "./__generated__/types";
import { installDomStubs, StubMediaRecorder } from "./testing/index";

installDomStubs();

function liveStream(): MediaStream {
  const track = { kind: "video" } as MediaStreamTrack;
  return { getVideoTracks: () => [track], getTracks: () => [track] } as unknown as MediaStream;
}

class TapCamera implements RecordingClientCamera {
  mediaStream: MediaStream | null = liveStream();
  state = null;
  maxRenderSize = null;
  /** False models a client built without capture timing: no copies. */
  tappable = true;
  tap: ((frame: EncodedFrameCopy) => void) | null = null;
  keyframeRequests = 0;
  readonly holds: boolean[] = [];

  async setForceFullResolution(): Promise<void> {}
  async setSizeHold(hold: boolean): Promise<void> {
    this.holds.push(hold);
  }
  on(): () => void {
    return () => {};
  }
  canTapEncodedFrames(): boolean {
    return this.tappable;
  }
  tapEncodedFrames(onFrame: (frame: EncodedFrameCopy) => void): (() => void) | null {
    if (!this.tappable) return null;
    this.tap = onFrame;
    return () => {
      if (this.tap === onFrame) this.tap = null;
    };
  }
  async requestKeyframe(): Promise<void> {
    this.keyframeRequests++;
  }

  /** Test hook: one fixture frame arrives. */
  send(au: Uint8Array, rtpTimestamp: number, receivedAt = 0): void {
    this.tap?.({ data: au.slice().buffer, rtpTimestamp, ssrc: 1, receivedAt });
  }
}

class TapClient implements RecordingClient {
  captureUt: number | null = 50;
  private camerasChange: ((cameras: CameraState[]) => void) | null = null;
  readonly cams = new Map<number, TapCamera>();
  /** rtpTimestamp -> UT for frames with a capture stamp. */
  readonly stamps = new Map<number, number>();

  camera(flightId: number): TapCamera {
    let cam = this.cams.get(flightId);
    if (!cam) {
      cam = new TapCamera();
      this.cams.set(flightId, cam);
    }
    return cam;
  }
  get clock(): { captureUt: number | null } {
    return { captureUt: this.captureUt };
  }
  on(event: string, handler: (data: never) => void): () => void {
    if (event === "cameras-change") this.camerasChange = handler as (cameras: CameraState[]) => void;
    return () => {};
  }
  /** Test hook: a new camera list arrives. */
  emitCameras(cameras: CameraState[]): void {
    this.camerasChange?.(cameras);
  }
  frameUt(_flightId: number, rtp: number): { ut: number; source: "exact" | "estimated" } | null {
    const ut = this.stamps.get(rtp);
    return ut == null ? { ut: 0, source: "estimated" } : { ut, source: "exact" };
  }
}

afterEach(() => {
  vi.useRealTimers();
});

describe("capture-timed recording", () => {
  it("records copied frames into an MP4 timed by RTP, from the first keyframe", async () => {
    const client = new TapClient();
    const controller = new RecordingController(client);
    const cam = client.camera(1);
    const aus = accessUnits();

    const id = controller.startRecording(1);
    expect(cam.keyframeRequests).toBe(1);
    expect(cam.tap).not.toBeNull();
    /* P-frames before the requested keyframe can't open the clip. */
    for (let i = 3; i < 12; i++) {
      client.stamps.set(i * 3000, 100 + i / 30);
      cam.send(aus[i], i * 3000);
    }
    const handle = await controller.stopRecording(id);

    expect(handle.timing).toBe("capture");
    expect(handle.mimeType).toMatch(/^video\/mp4;codecs=avc1\./);
    expect(handle.byteSize).toBeGreaterThan(0);
    expect(handle.utSamples).toHaveLength(6);
    expect(handle.utSamples[0]).toEqual({ t: 0, ut: 100 + 6 / 30 });
    expect(handle.utSamples[5].t).toBeCloseTo(5 * (100 / 3));
    expect(handle.utStart).toBeCloseTo(100.2);
    expect(handle.utEnd).toBeCloseTo(100 + 11 / 30);
    /* Media length: six frames at 3000 ticks. */
    expect(handle.durationMs).toBeCloseTo(200);
    expect(cam.tap).toBeNull();
  });

  it("saves the frames it has when the camera goes away mid-clip", async () => {
    vi.spyOn(console, "info").mockImplementation(() => {});
    const client = new TapClient();
    const controller = new RecordingController(client);
    const cam = client.camera(1);
    const aus = accessUnits();
    const id = controller.startRecording(1);
    for (let i = 0; i < 6; i++) cam.send(aus[i], i * 3000);

    client.emitCameras([]);
    await vi.waitFor(() => expect(controller.fetchRecording(id)).toBeDefined());

    const handle = controller.fetchRecording(id)!;
    expect(handle.timing).toBe("capture");
    expect(handle.endedBy).toBe("camera-gone");
    expect(handle.byteSize).toBeGreaterThan(0);
    expect(handle.durationMs).toBeGreaterThan(0);
    expect(cam.tap).toBeNull();
    expect(controller.isRecording(1)).toBe(false);
  });

  it("falls back to the clock UT track when frames carry no stamps", async () => {
    const client = new TapClient();
    const controller = new RecordingController(client);
    const cam = client.camera(1);
    const id = controller.startRecording(1);
    for (const [i, au] of accessUnits().slice(0, 4).entries()) cam.send(au, i * 3000);
    const handle = await controller.stopRecording(id);
    expect(handle.timing).toBe("capture");
    expect(handle.utStart).toBe(50);
    expect(handle.utSamples.every((s) => s.t >= 0 && s.ut === 50)).toBe(true);
  });

  it("asks for a keyframe again while none has arrived", () => {
    vi.useFakeTimers();
    const client = new TapClient();
    const controller = new RecordingController(client);
    const cam = client.camera(1);
    const id = controller.startRecording(1);
    vi.advanceTimersByTime(KEYFRAME_RETRY_MS * 2);
    expect(cam.keyframeRequests).toBe(3);
    cam.send(accessUnits()[0], 0);
    vi.advanceTimersByTime(KEYFRAME_RETRY_MS * 2);
    expect(cam.keyframeRequests).toBe(3);
    controller.discardRecording(id);
    expect(cam.tap).toBeNull();
    vi.advanceTimersByTime(KEYFRAME_RETRY_MS * 2);
    expect(cam.keyframeRequests).toBe(3);
  });

  it("ends with an empty capture clip when stopped before any keyframe", async () => {
    const client = new TapClient();
    const controller = new RecordingController(client);
    const id = controller.startRecording(1);
    const handle = await controller.stopRecording(id);
    expect(handle).toMatchObject({ timing: "capture", byteSize: 0, mimeType: "video/mp4" });
  });

  it("falls back to arrival timing when the camera's frames can't be copied", async () => {
    const original = (globalThis as { MediaRecorder?: unknown }).MediaRecorder;
    (globalThis as { MediaRecorder?: unknown }).MediaRecorder = StubMediaRecorder;
    try {
      const client = new TapClient();
      client.camera(1).tappable = false;
      const controller = new RecordingController(client);
      const id = controller.startRecording(1);
      expect(controller.getSnapshot().active[0].stopping).toBe(false);
      const stopped = controller.stopRecording(id);
      expect(controller.getSnapshot().active[0].stopping).toBe(true);
      const handle = await stopped;
      expect(controller.getSnapshot().active).toEqual([]);
      expect(handle.timing).toBe("arrival");
      expect(client.camera(1).keyframeRequests).toBe(0);
    } finally {
      (globalThis as { MediaRecorder?: unknown }).MediaRecorder = original;
    }
  });

  it("only copies frames once the recorder starts, so an arming clip holds no tap", async () => {
    const client = new TapClient();
    const controller = new RecordingController(client, {
      probeDecodedSize: () => () => {},
    });
    const cam = client.camera(1);
    const id = controller.startRecording(1, { forceFullResolution: true });
    expect(cam.tap).toBeNull();
    const handle = await controller.stopRecording(id);
    expect(handle.timing).toBe("capture");
    expect(cam.tap).toBeNull();
  });

  it("records a group together, each member capture-timed", async () => {
    const client = new TapClient();
    const controller = new RecordingController(client, { loadTrimmer: async () => null });
    const { groupId } = controller.startGroupedRecording([1, 2]);
    const aus = accessUnits();
    for (const flightId of [1, 2]) {
      aus.slice(0, 6).forEach((au, i) => client.camera(flightId).send(au, i * 3000));
    }
    const group = await controller.stopGroupedRecording(groupId);
    expect(group.recordings.map((r) => r.timing)).toEqual(["capture", "capture"]);
    expect(group.recordings.every((r) => r.byteSize > 0)).toBe(true);
    expect(group.commonUtWindow).toEqual([50, 50]);
  });

  it("holds the feed's size from the recorder's start until it stops or is discarded", async () => {
    const client = new TapClient();
    const controller = new RecordingController(client);
    const id = controller.startRecording(1);
    expect(client.camera(1).holds).toEqual([true]);
    await controller.stopRecording(id);
    expect(client.camera(1).holds).toEqual([true, false]);

    const discarded = controller.startRecording(2);
    controller.discardRecording(discarded);
    expect(client.camera(2).holds).toEqual([true, false]);
  });

  it("holds a forced feed only once arming lets the recorder start", async () => {
    vi.useFakeTimers();
    const client = new TapClient();
    const controller = new RecordingController(client, { probeDecodedSize: () => () => {} });
    const id = controller.startRecording(1, { forceFullResolution: true });
    expect(client.camera(1).holds).toEqual([]);
    await vi.advanceTimersByTimeAsync(3000);
    expect(client.camera(1).holds).toEqual([true]);
    const stopped = controller.stopRecording(id);
    await vi.advanceTimersByTimeAsync(100);
    await stopped;
    expect(client.camera(1).holds).toEqual([true, false]);
  });

  it("cuts a group's capture-timed clips from their own frames, not through the trimmer", async () => {
    const client = new TapClient();
    const trim = vi.fn();
    const controller = new RecordingController(client, { loadTrimmer: async () => ({ trim }) });
    const { groupId } = controller.startGroupedRecording([1, 2]);
    const aus = accessUnits();
    aus.forEach((au, i) => {
      client.stamps.set(i * 3000, 100 + i);
      client.stamps.set(1_000_000 + i * 3000, 100 + i);
    });
    /* Feed 2 starts six frames late, so the common window opens at its first keyframe. */
    aus.forEach((au, i) => client.camera(1).send(au, i * 3000));
    aus.slice(6).forEach((au, i) => client.camera(2).send(au, 1_000_000 + (i + 6) * 3000));
    const group = await controller.stopGroupedRecording(groupId);
    expect(trim).not.toHaveBeenCalled();
    expect(group.commonUtWindow).toEqual([106, 123]);
    const [first, second] = group.recordings;
    expect(first.utStart).toBe(106);
    expect(first.utSamples[0]).toEqual({ t: 0, ut: 106 });
    expect(first.durationMs).toBeCloseTo(18 * (100 / 3));
    expect(second.utStart).toBe(106);
    expect(controller.fetchRecording(first.recordingId)).toBe(first);
  });

  it("shows the stopping state before the mux runs, and muxes REC+ members one per frame", async () => {
    const frames: (() => void)[] = [];
    /* After the fake timers, which bring their own animation frames. */
    vi.useFakeTimers();
    vi.stubGlobal("requestAnimationFrame", (cb: () => void) => frames.push(cb));
    try {
      const { CaptureClip } = await import("./captureClip");
      const finish = vi.spyOn(CaptureClip.prototype, "finish");
      const client = new TapClient();
      const controller = new RecordingController(client, { loadTrimmer: async () => null });
      const { groupId } = controller.startGroupedRecording([1, 2]);
      const stopped = controller.stopGroupedRecording(groupId);
      await vi.advanceTimersByTimeAsync(0);

      expect(controller.getSnapshot().active.map((a) => a.stopping)).toEqual([true, true]);
      expect(finish).not.toHaveBeenCalled();

      frames.shift()!();
      await vi.advanceTimersByTimeAsync(0);
      expect(finish).toHaveBeenCalledTimes(1);
      frames.shift()!();
      await vi.advanceTimersByTimeAsync(0);
      expect(finish).toHaveBeenCalledTimes(2);
      expect((await stopped).recordings).toHaveLength(2);
      finish.mockRestore();
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("still saves when no frame is painted, as in a hidden tab", async () => {
    vi.useFakeTimers();
    vi.stubGlobal("requestAnimationFrame", () => 0);
    try {
      const client = new TapClient();
      const controller = new RecordingController(client);
      const id = controller.startRecording(1);
      const stopped = controller.stopRecording(id);
      await vi.advanceTimersByTimeAsync(100);
      await expect(stopped).resolves.toMatchObject({ recordingId: id, timing: "capture" });
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it("clears a recording whose clip fails to finish, so the feed can record again", async () => {
    const client = new TapClient();
    const controller = new RecordingController(client);
    const id = controller.startRecording(1);
    const cam = client.camera(1);
    accessUnits().slice(0, 6).forEach((au, i) => cam.send(au, i * 3000));
    const error = vi.spyOn(console, "error").mockImplementation(() => {});
    const finish = vi
      .spyOn((await import("./captureClip")).CaptureClip.prototype, "finish")
      .mockImplementation(() => {
        throw new RangeError("Maximum call stack size exceeded");
      });
    await expect(controller.stopRecording(id)).rejects.toThrow(RangeError);
    finish.mockRestore();
    error.mockRestore();
    expect(controller.isRecording(1)).toBe(false);
    expect(controller.getSnapshot().active).toEqual([]);
    expect(() => controller.startRecording(1)).not.toThrow();
  });
});
