import { beforeEach, describe, expect, it, vi } from "vitest";
import { ErrorSource, Layer } from "./__generated__/types";
import {
  BrowserKerbcamTransport,
  type KerbcamConnectionState,
  type KerbcamDataChannel,
  type KerbcamPeer,
  type KerbcamTransport,
  KerbcamClient,
} from "./client";
import { MockSidecar } from "./testing";
import * as noise from "./noise";

interface FakeChannel extends KerbcamDataChannel {
  sent: string[];
  _open: () => void;
  _msg: (raw: string) => void;
  _close: () => void;
}

function makeFakeTransport() {
  const captured: {
    dc?: FakeChannel;
    onTrack?: (t: MediaStreamTrack, idx: number) => void;
    onState?: (s: KerbcamConnectionState) => void;
    closed: boolean;
  } = { closed: false };

  function makeFakeChannel(): FakeChannel {
    const ch: FakeChannel = {
      sent: [],
      send: (s) => ch.sent.push(s),
      onOpen: (h) => {
        ch._open = h;
      },
      onMessage: (h) => {
        ch._msg = h;
      },
      onClose: (h) => {
        ch._close = h;
      },
      _open: () => {},
      _msg: () => {},
      _close: () => {},
    };
    return ch;
  }

  const transport: KerbcamTransport = {
    createPeer: () => {
      const peer: KerbcamPeer = {
        addRecvOnlyTransceiver: () => {},
        createDataChannel: () => {
          const ch = makeFakeChannel();
          captured.dc = ch;
          return ch;
        },
        onTrack: (h) => {
          captured.onTrack = h;
        },
        onStateChange: (h) => {
          captured.onState = h;
        },
        createOffer: async () => "v=0\r\n…fake-sdp…\r\n",
        setLocalDescription: async () => {},
        setRemoteAnswer: async () => {},
        waitForIceComplete: async () => {},
        localSdp: () => "v=0\r\n…fake-sdp…\r\n",
        close: () => {
          captured.closed = true;
        },
      };
      return peer;
    },
  };

  return { transport, captured };
}

function fakeAnswer(cameras: number[] = []) {
  return new Response(JSON.stringify({ sdp: "answer-sdp", cameras }), {
    status: 200,
  });
}

function fakeCameraState(flightId: number, overrides: Record<string, unknown> = {}) {
  return {
    flightId,
    partName: "navCam1",
    partTitle: "NavCam",
    cameraName: "NavCam",
    vesselName: "Perf Test 1",
    layers: [Layer.Near],
    operatorLayers: [Layer.Near, Layer.Scaled, Layer.Galaxy],
    renderWidth: 768,
    renderHeight: 768,
    operatorWidth: 768,
    operatorHeight: 768,
    supportsZoom: true,
    fov: 60,
    fovMin: 30,
    fovMax: 100,
    supportsPan: false,
    panYaw: 0,
    panPitch: 0,
    panYawMin: 0,
    panYawMax: 0,
    panPitchMin: 0,
    panPitchMax: 0,
    encoderBitrateBps: 1_500_000,
    targetBitrateBps: 0,
    degradeLevel: 0,
    ...overrides,
  };
}

/**
 * Install a stubbed canvas pipeline so `tryCreateNoisePipeline` succeeds in
 * jsdom (which has no real `captureStream`). Returns the spies + a `restore`
 * to call in a `finally`. Each `captureStream()` call yields a fresh
 * MediaStream identity so a destroy→recreate is observable, while a persistent
 * pipeline keeps emitting the same one across source swaps.
 */
function installNoisePipelineMock() {
  const captureStream = vi.fn(() => new MediaStream());
  const fakeCtx = {
    drawImage: vi.fn(),
    fillRect: vi.fn(),
    fillStyle: "",
    createImageData: vi
      .fn()
      .mockReturnValue({ data: new Uint8ClampedArray(4) }),
    putImageData: vi.fn(),
  };
  const origGetContext = HTMLCanvasElement.prototype.getContext;
  // @ts-expect-error — jsdom augmentation
  HTMLCanvasElement.prototype.getContext = vi.fn().mockReturnValue(fakeCtx);
  // @ts-expect-error — jsdom augmentation
  HTMLCanvasElement.prototype.captureStream = captureStream;
  const rafSpy = vi
    .spyOn(globalThis, "requestAnimationFrame")
    .mockReturnValue(0);
  const cancelSpy = vi
    .spyOn(globalThis, "cancelAnimationFrame")
    .mockImplementation(() => {});
  return {
    captureStream,
    rafSpy,
    cancelSpy,
    restore() {
      HTMLCanvasElement.prototype.getContext = origGetContext;
      // @ts-expect-error — cleanup
      delete HTMLCanvasElement.prototype.captureStream;
      rafSpy.mockRestore();
      cancelSpy.mockRestore();
    },
  };
}

beforeEach(() => {
  // jsdom doesn't ship MediaStream by default — provide the bare
  // minimum the client needs (constructor that accepts a track list).
  if (typeof MediaStream === "undefined") {
    // @ts-expect-error — augmenting globals for the test env
    globalThis.MediaStream = class FakeMediaStream {
      private _tracks: MediaStreamTrack[];
      constructor(tracks: MediaStreamTrack[] = []) {
        this._tracks = [...tracks];
      }
      getTracks() {
        return this._tracks;
      }
    };
  }
  if (typeof fetch === "undefined") {
    // @ts-expect-error — augmenting globals for the test env
    globalThis.fetch = vi.fn();
  }
});

describe("KerbcamClient", () => {
  it("starts disconnected and emits state-change on connect", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    expect(client.state).toBe("disconnected");

    const states: KerbcamConnectionState[] = [];
    client.on("state-change", (s) => states.push(s));

    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([42]));

    await client.connect([42]);
    captured.onState?.("connected");

    expect(states).toContain("connecting");
    expect(states).toContain("connected");
    expect(client.state).toBe("connected");
  });

  it("sends `hello` automatically once the control channel opens", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();

    expect(captured.dc?.sent).toContain(JSON.stringify({ type: "hello" }));
  });

  it("populates the camera registry from a snapshot push", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc?._msg(
      JSON.stringify({
        type: "camera-snapshot",
        content: {
          cameras: [
            {
              flightId: 42,
              partName: "navCam1",
              partTitle: "NavCam",
              cameraName: "NavCam",
              vesselName: "Perf Test 1",
              layers: [Layer.Near],
              operatorLayers: [Layer.Near, Layer.Scaled, Layer.Galaxy],
              renderWidth: 768,
              renderHeight: 768,
              operatorWidth: 768,
              operatorHeight: 768,
              supportsZoom: true,
              fov: 60,
              fovMin: 30,
              fovMax: 100,
              supportsPan: false,
              panYaw: 0,
              panPitch: 0,
              panYawMin: 0,
              panYawMax: 0,
              panPitchMin: 0,
              panPitchMax: 0,
              encoderBitrateBps: 1_500_000,
              targetBitrateBps: 0,
              degradeLevel: 0,
            },
          ],
        },
      }),
    );

    expect(client.cameras).toHaveLength(1);
    expect(client.camera(42).state?.partTitle).toBe("NavCam");
  });

  it("camera handles are stable across calls", () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    expect(client.camera(42)).toBe(client.camera(42));
  });

  it("set-degrade routes onto the control channel via the handle", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc!.sent.length = 0; // drop the hello

    await client.camera(42).setDegrade(0.7);

    expect(captured.dc?.sent[0]).toBe(
      JSON.stringify({
        type: "set-degrade",
        content: { flightId: 42, level: 0.7 },
      }),
    );
  });

  it("setFov / setLayers / setRenderSize routing", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc!.sent.length = 0;

    const cam = client.camera(7);
    await cam.setFov(35.5);
    await cam.setLayers([Layer.Near, Layer.Scaled]);
    await cam.setRenderSize(384, 384);

    expect(captured.dc?.sent).toEqual([
      JSON.stringify({ type: "set-fov", content: { flightId: 7, fov: 35.5 } }),
      JSON.stringify({
        type: "set-layers",
        content: { flightId: 7, layers: [Layer.Near, Layer.Scaled] },
      }),
      JSON.stringify({
        type: "set-render-size",
        content: { flightId: 7, width: 384, height: 384 },
      }),
    ]);
  });

  it("disconnect tears down peer + emits null MediaStream events", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([42]));

    await client.connect([42]);

    const streams: (MediaStream | null)[] = [];
    client.camera(42).on("stream", (s) => streams.push(s));

    // Simulate a track arrival so we have a non-null mediaStream first.
    const fakeTrack = {} as MediaStreamTrack;
    captured.onTrack?.(fakeTrack, 0);
    expect(client.camera(42).mediaStream).not.toBeNull();

    client.disconnect();
    expect(captured.closed).toBe(true);
    expect(client.state).toBe("disconnected");
    expect(streams[streams.length - 1]).toBeNull();
  });

  it("non-OK /offer response throws with status code and transitions to failed", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response("service unavailable", { status: 503 }),
    );

    const states: KerbcamConnectionState[] = [];
    client.on("state-change", (s) => states.push(s));

    await expect(client.connect()).rejects.toThrow("503");
    expect(states).toContain("failed");
    expect(client.state).toBe("failed");
  });

  it("discover() returns the camera list from /cameras", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    const mockCameras = [
      {
        flightId: 99,
        partName: "navCam1",
        partTitle: "NavCam",
        cameraName: "NavCam",
        vesselName: "Test Vessel",
        maxWidth: 1280,
        maxHeight: 720,
        supportsZoom: true,
        fov: 60,
        fovMin: 30,
        fovMax: 100,
        supportsPan: false,
      },
    ];
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ cameras: mockCameras }), { status: 200 }),
    );

    const result = await client.discover();
    expect(result).toHaveLength(1);
    expect(result[0].flightId).toBe(99);
    expect(result[0].partTitle).toBe("NavCam");
    expect(result[0].supportsZoom).toBe(true);
  });

  it("sidecar error message emits error event with correct payload including source", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();

    const errors: { message: string; source?: string }[] = [];
    client.on("error", (e) => errors.push(e));

    captured.dc?._msg(
      JSON.stringify({ type: "error", content: { message: "boom", source: "sidecar" } }),
    );

    expect(errors).toHaveLength(1);
    expect(errors[0].message).toBe("boom");
    expect(errors[0].source).toBe("sidecar");
  });

  it("malformed JSON emits error event with source: client", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();

    const errors: { message: string; source?: string }[] = [];
    client.on("error", (e) => errors.push(e));

    captured.dc?._msg("not valid json {{{{");

    expect(errors).toHaveLength(1);
    expect(errors[0].source).toBe(ErrorSource.Client);
    expect(typeof errors[0].message).toBe("string");
    expect(errors[0].message.length).toBeGreaterThan(0);
  });

  it("_send rejects when control channel is not open", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);

    // camera() and setFov() before connect — control channel is null
    await expect(client.camera(1).setFov(30)).rejects.toThrow(
      "[kerbcam] control channel not open",
    );
  });

  it("adaptive-shed message emits adaptive-shed event with correct payload", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();

    const events: { level: number; kspFps: number; reason: string }[] = [];
    client.on("adaptive-shed", (e) => events.push(e));

    captured.dc?._msg(
      JSON.stringify({
        type: "adaptive-shed",
        content: { level: 2, kspFps: 14.0, reason: "ksp-fps-low" },
      }),
    );

    expect(events).toHaveLength(1);
    expect(events[0].level).toBe(2);
    expect(events[0].kspFps).toBe(14.0);
    expect(events[0].reason).toBe("ksp-fps-low");
  });

  it("cameras-change fires and camera state updates on camera-state-changed", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();

    // Push initial snapshot with camera 42
    captured.dc?._msg(
      JSON.stringify({
        type: "camera-snapshot",
        content: { cameras: [fakeCameraState(42, { fov: 60 })] },
      }),
    );

    const camerasChanges: unknown[][] = [];
    client.on("cameras-change", (cams) => camerasChanges.push([...cams]));

    // Push a state change that updates fov to 45
    captured.dc?._msg(
      JSON.stringify({
        type: "camera-state-changed",
        content: { state: fakeCameraState(42, { fov: 45 }) },
      }),
    );

    expect(camerasChanges).toHaveLength(1);
    expect(client.cameras[0].fov).toBe(45);
  });

  it("setPan and requestKeyframe route correct JSON onto the control channel", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc!.sent.length = 0;

    const cam = client.camera(5);
    await cam.setPan(10.5, -3.2);
    await cam.requestKeyframe();

    expect(captured.dc?.sent[0]).toBe(
      JSON.stringify({ type: "set-pan", content: { flightId: 5, yaw: 10.5, pitch: -3.2 } }),
    );
    expect(captured.dc?.sent[1]).toBe(
      JSON.stringify({ type: "request-keyframe", content: { flightId: 5 } }),
    );
  });

  it("setPanRate and setZoomRate route correct JSON onto the control channel", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc!.sent.length = 0;

    const cam = client.camera(5);
    await cam.setPanRate(0.5, -1);
    await cam.setZoomRate(1);

    expect(captured.dc?.sent[0]).toBe(
      JSON.stringify({
        type: "set-pan-rate",
        content: { flightId: 5, yawRate: 0.5, pitchRate: -1 },
      }),
    );
    expect(captured.dc?.sent[1]).toBe(
      JSON.stringify({ type: "set-zoom-rate", content: { flightId: 5, rate: 1 } }),
    );
  });

  it("peer failed state emits failed and tears down streams", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcamClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([42]));

    await client.connect([42]);

    // Simulate track arrival to get a non-null mediaStream
    const fakeTrack = {} as MediaStreamTrack;
    captured.onTrack?.(fakeTrack, 0);
    expect(client.camera(42).mediaStream).not.toBeNull();

    const states: KerbcamConnectionState[] = [];
    client.on("state-change", (s) => states.push(s));

    // Trigger peer failed state
    captured.onState?.("failed");

    expect(states).toContain("failed");
    expect(client.state).toBe("failed");
    expect(client.camera(42).mediaStream).toBeNull();
  });

  describe("noise config", () => {
    // jsdom has no captureStream, so tryCreateNoisePipeline returns null and
    // the raw stream is surfaced. Tests cover the config resolution logic and
    // ensure that flipping noise on/off via configure() replaces the stream.

    it("noise is enabled by default (no explicit config)", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcamClient({ host: "h", port: 1 }, transport);
      // _resolveNoise with null override and no cfg.noise should return true
      expect(client._resolveNoise(null)).toBe(true);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([1]));
      await client.connect([1]);
      captured.onTrack?.({} as MediaStreamTrack, 0);
      // In jsdom: captureStream absent → pipeline skipped → raw stream exposed
      expect(client.camera(1).mediaStream).not.toBeNull();
    });

    it("noise can be disabled at the client level", () => {
      const { transport } = makeFakeTransport();
      const client = new KerbcamClient({ host: "h", port: 1, noise: { enabled: false } }, transport);
      expect(client._resolveNoise(null)).toBe(false);
    });

    it("per-camera configure() overrides client default", () => {
      const { transport } = makeFakeTransport();
      const clientOn = new KerbcamClient({ host: "h", port: 1 }, transport);
      const clientOff = new KerbcamClient({ host: "h", port: 1, noise: { enabled: false } }, transport);

      // Per-camera enable overrides client-off
      expect(clientOff._resolveNoise({ enabled: true })).toBe(true);
      // Per-camera disable overrides client-on (default)
      expect(clientOn._resolveNoise({ enabled: false })).toBe(false);
    });

    it("configure() triggers stream re-emit", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcamClient({ host: "h", port: 1 }, transport);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([5]));

      await client.connect([5]);
      const cam = client.camera(5);
      const streams: (MediaStream | null)[] = [];
      cam.on("stream", (s) => streams.push(s));

      captured.onTrack?.({} as MediaStreamTrack, 0);
      expect(streams).toHaveLength(1);
      expect(streams[0]).not.toBeNull();

      // Calling configure re-builds the pipeline → re-emits the stream
      cam.configure({ noise: { enabled: false } });
      expect(streams).toHaveLength(2);
      expect(streams[1]).not.toBeNull();
    });

    it("_setState updates noise intensity and does not break without a stream", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcamClient({ host: "h", port: 1 }, transport);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

      await client.connect();
      captured.dc?._open();

      // Push a state update before any track arrives — must not throw
      captured.dc?._msg(
        JSON.stringify({
          type: "camera-state-changed",
          content: { state: fakeCameraState(99, { degradeLevel: 0.6 }) },
        }),
      );

      expect(client.camera(99).state?.degradeLevel).toBe(0.6);
    });

    it("disconnect destroys noise pipeline and sets stream to null", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcamClient({ host: "h", port: 1 }, transport);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([7]));

      await client.connect([7]);
      captured.onTrack?.({} as MediaStreamTrack, 0);
      expect(client.camera(7).mediaStream).not.toBeNull();

      const streams: (MediaStream | null)[] = [];
      client.camera(7).on("stream", (s) => streams.push(s));

      client.disconnect();
      expect(client.camera(7).mediaStream).toBeNull();
      expect(streams[streams.length - 1]).toBeNull();
    });

    it("noise pipeline is used when captureStream is available", async () => {
      const fakeProcessed = new MediaStream();
      const mockCaptureStream = vi.fn().mockReturnValue(fakeProcessed);
      const fakeCtx = {
        drawImage: vi.fn(),
        createImageData: vi.fn().mockReturnValue({ data: new Uint8ClampedArray(4) }),
        putImageData: vi.fn(),
      };
      const origGetContext = HTMLCanvasElement.prototype.getContext;
      // @ts-expect-error — jsdom augmentation
      HTMLCanvasElement.prototype.getContext = vi.fn().mockReturnValue(fakeCtx);
      // @ts-expect-error — jsdom augmentation
      HTMLCanvasElement.prototype.captureStream = mockCaptureStream;
      const rafSpy = vi.spyOn(globalThis, "requestAnimationFrame").mockReturnValue(0);

      try {
        const { transport, captured } = makeFakeTransport();
        const client = new KerbcamClient({ host: "h", port: 1 }, transport);
        vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([3]));

        await client.connect([3]);
        captured.onTrack?.({} as MediaStreamTrack, 0);

        expect(client.camera(3).mediaStream).toBe(fakeProcessed);
        expect(mockCaptureStream).toHaveBeenCalledWith(30);
      } finally {
        HTMLCanvasElement.prototype.getContext = origGetContext;
        // @ts-expect-error — cleanup
        delete HTMLCanvasElement.prototype.captureStream;
        rafSpy.mockRestore();
      }
    });
  });

  describe("sourceless static (signal-loss / switch gap)", () => {
    it("keeps mediaStream non-null when a destroyed camera's source stops", async () => {
      const mock = installNoisePipelineMock();
      try {
        const sidecar = new MockSidecar();
        sidecar.addCamera({ flightId: 42 });
        const client = new KerbcamClient(
          { host: "h", port: 1 },
          sidecar.createTransport(),
        );
        vi.spyOn(globalThis, "fetch").mockImplementation(() =>
          Promise.resolve(MockSidecar.makeOfferResponse([42])),
        );

        await client.connect([42]);
        sidecar.open();
        sidecar.deliverTrack("0", {} as MediaStreamTrack);

        const live = client.camera(42).mediaStream;
        expect(live).not.toBeNull();

        // Destruction sends only camera-state-changed(Destroyed); the track
        // never ends. The handle must still go to live static, not null.
        sidecar.destroyCamera(42);
        expect(client.camera(42).mediaStream).not.toBeNull();
        // Persistent pipeline → same output stream, never a gap.
        expect(client.camera(42).mediaStream).toBe(live);
      } finally {
        mock.restore();
      }
    });

    it("drives static to full intensity on signal loss", async () => {
      const setIntensity = vi.fn();
      const setSource = vi.fn();
      const processedStream = new MediaStream();
      const createSpy = vi
        .spyOn(noise, "tryCreateNoisePipeline")
        .mockReturnValue({
          processedStream,
          setIntensity,
          setSource,
          destroy: vi.fn(),
        });
      try {
        const sidecar = new MockSidecar();
        sidecar.addCamera({ flightId: 42, degradeLevel: 0.3 });
        const client = new KerbcamClient(
          { host: "h", port: 1 },
          sidecar.createTransport(),
        );
        vi.spyOn(globalThis, "fetch").mockImplementation(() =>
          Promise.resolve(MockSidecar.makeOfferResponse([42])),
        );

        await client.connect([42]);
        sidecar.open();
        sidecar.deliverTrack("0", {} as MediaStreamTrack);

        // Live feed tracks degrade (0.3), not full.
        expect(setIntensity).toHaveBeenLastCalledWith(0.3);

        sidecar.destroyCamera(42);
        // Sourceless → full static, and the source was detached.
        expect(setSource).toHaveBeenLastCalledWith(null);
        expect(setIntensity).toHaveBeenLastCalledWith(1.0);

        // A later degrade=0 state update must NOT lower the full static.
        setIntensity.mockClear();
        sidecar.updateCamera(42, { degradeLevel: 0 });
        expect(setIntensity).not.toHaveBeenCalledWith(0.05);
      } finally {
        createSpy.mockRestore();
      }
    });

    it("restores video when a source returns after loss", async () => {
      const setSource = vi.fn();
      const processedStream = new MediaStream();
      const createSpy = vi
        .spyOn(noise, "tryCreateNoisePipeline")
        .mockReturnValue({
          processedStream,
          setIntensity: vi.fn(),
          setSource,
          destroy: vi.fn(),
        });
      try {
        const sidecar = new MockSidecar().withSlots(["a"]);
        sidecar.addCamera({ flightId: 1 });
        const client = new KerbcamClient(
          { host: "h", port: 1 },
          sidecar.createTransport(),
        );
        vi.spyOn(globalThis, "fetch").mockImplementation(() =>
          Promise.resolve(MockSidecar.makeOfferResponse([])),
        );

        await client.connect([], { slots: 1 });
        sidecar.open();
        await client.subscribe(1);
        sidecar.deliverTrack("a", {} as MediaStreamTrack);

        // Free the slot → outgoing camera goes sourceless (static).
        await client.unsubscribe(1);
        expect(setSource).toHaveBeenLastCalledWith(null);
        expect(client.camera(1).mediaStream).toBe(processedStream);

        // Resubscribe + new track → source restored on the SAME pipeline.
        await client.subscribe(1);
        sidecar.deliverTrack("a", {} as MediaStreamTrack);
        const last = setSource.mock.calls.at(-1)?.[0];
        expect(last).not.toBeNull();
        // Reused the persistent pipeline rather than building a new one.
        expect(createSpy).toHaveBeenCalledTimes(1);
      } finally {
        createSpy.mockRestore();
      }
    });

    it("sourceless draw() clears to black and composites static (no drawImage)", () => {
      const captureStream = vi.fn(() => new MediaStream());
      const fakeCtx = {
        drawImage: vi.fn(),
        fillRect: vi.fn(),
        fillStyle: "",
        createImageData: vi
          .fn()
          .mockReturnValue({ data: new Uint8ClampedArray(4) }),
        putImageData: vi.fn(),
      };
      const origGetContext = HTMLCanvasElement.prototype.getContext;
      // @ts-expect-error — jsdom augmentation
      HTMLCanvasElement.prototype.getContext = vi.fn().mockReturnValue(fakeCtx);
      // @ts-expect-error — jsdom augmentation
      HTMLCanvasElement.prototype.captureStream = captureStream;
      // Run the rAF callback exactly once so draw() executes its sourceless
      // branch, then stop (return 0, ignore the re-schedule).
      let ran = false;
      const rafSpy = vi
        .spyOn(globalThis, "requestAnimationFrame")
        .mockImplementation((cb) => {
          if (!ran) {
            ran = true;
            cb(0);
          }
          return 0;
        });
      try {
        const pipeline = noise.tryCreateNoisePipeline(null, 1.0);
        expect(pipeline).not.toBeNull();
        // No source → never drawImage the (absent) video.
        expect(fakeCtx.drawImage).toHaveBeenCalledTimes(1); // only the noise canvas
        // Sourceless branch clears the field to black.
        expect(fakeCtx.fillRect).toHaveBeenCalledTimes(1);
        expect(fakeCtx.putImageData).toHaveBeenCalledTimes(1);
        pipeline?.destroy();
      } finally {
        HTMLCanvasElement.prototype.getContext = origGetContext;
        // @ts-expect-error — cleanup
        delete HTMLCanvasElement.prototype.captureStream;
        rafSpy.mockRestore();
      }
    });

    it("shows static for a subscribed camera whose track hasn't arrived (switch gap)", async () => {
      const mock = installNoisePipelineMock();
      try {
        const sidecar = new MockSidecar().withSlots(["a"]);
        sidecar.addCamera({ flightId: 9 });
        const client = new KerbcamClient(
          { host: "h", port: 1 },
          sidecar.createTransport(),
        );
        vi.spyOn(globalThis, "fetch").mockImplementation(() =>
          Promise.resolve(MockSidecar.makeOfferResponse([])),
        );

        await client.connect([], { slots: 1 });
        sidecar.open();

        // Slot bound (slot-map arrives) but no track yet — the incoming
        // camera must show live static, not blank.
        await client.subscribe(9);
        expect(client.camera(9).mediaStream).not.toBeNull();

        // When the track lands the source is restored on the same pipeline.
        sidecar.deliverTrack("a", {} as MediaStreamTrack);
        expect(client.camera(9).mediaStream).not.toBeNull();
      } finally {
        mock.restore();
      }
    });

    it("teardown (disconnect) destroys the pipeline and nulls the stream", async () => {
      const destroy = vi.fn();
      const processedStream = new MediaStream();
      const createSpy = vi
        .spyOn(noise, "tryCreateNoisePipeline")
        .mockReturnValue({
          processedStream,
          setIntensity: vi.fn(),
          setSource: vi.fn(),
          destroy,
        });
      try {
        const sidecar = new MockSidecar();
        sidecar.addCamera({ flightId: 42 });
        const client = new KerbcamClient(
          { host: "h", port: 1 },
          sidecar.createTransport(),
        );
        vi.spyOn(globalThis, "fetch").mockImplementation(() =>
          Promise.resolve(MockSidecar.makeOfferResponse([42])),
        );

        await client.connect([42]);
        sidecar.open();
        sidecar.deliverTrack("0", {} as MediaStreamTrack);
        expect(client.camera(42).mediaStream).toBe(processedStream);

        client.disconnect();
        expect(destroy).toHaveBeenCalledTimes(1);
        expect(client.camera(42).mediaStream).toBeNull();
      } finally {
        createSpy.mockRestore();
      }
    });
  });
});

describe("KerbcamClient — dynamic slot subscription", () => {
  it("subscribe binds a slot and routes its track; unsubscribe clears it", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcamClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );

    await client.connect([], { slots: 4 });
    sidecar.open();

    await client.subscribe(42);
    expect(sidecar.lastCommand("subscribe")?.content.flightId).toBe(42);
    const mid = sidecar.slotMidFor(42);
    expect(mid).toBeDefined();

    // Mapping known but media not arrived yet → still null.
    expect(client.camera(42).mediaStream).toBeNull();

    sidecar.deliverTrack(mid as string, {} as MediaStreamTrack);
    expect(client.camera(42).mediaStream).not.toBeNull();

    await client.unsubscribe(42);
    expect(client.camera(42).mediaStream).toBeNull();
  });

  it("reuses a freed slot for a different camera (the switch path)", async () => {
    const sidecar = new MockSidecar().withSlots(["a"]); // single slot
    const client = new KerbcamClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );

    await client.connect([], { slots: 1 });
    sidecar.open();

    await client.subscribe(1);
    const mid = sidecar.slotMidFor(1) as string;
    sidecar.deliverTrack(mid, {} as MediaStreamTrack);
    expect(client.camera(1).mediaStream).not.toBeNull();

    await client.unsubscribe(1);
    expect(client.camera(1).mediaStream).toBeNull();

    // The one slot is free again → camera 2 reuses it and inherits its track.
    await client.subscribe(2);
    expect(sidecar.slotMidFor(2)).toBe(mid);
    expect(client.camera(2).mediaStream).not.toBeNull();
    expect(client.camera(1).mediaStream).toBeNull();
  });

  it("surfaces a sidecar error when no slot is free", async () => {
    const sidecar = new MockSidecar().withSlots(["only"]);
    const client = new KerbcamClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );

    await client.connect([], { slots: 1 });
    sidecar.open();

    const errors: { message: string }[] = [];
    client.on("error", (e) => errors.push(e));

    await client.subscribe(1); // takes the only slot
    await client.subscribe(2); // no free slot → sidecar error
    expect(errors.some((e) => /no free slot/.test(e.message))).toBe(true);
  });

  it("uses an injected negotiate (signaling seam) instead of HTTP /offer", async () => {
    const sidecar = new MockSidecar();
    const negotiate = vi.fn(
      (offer: { sdp: string; cameras: number[]; slots?: number }) =>
        sidecar.negotiate(offer),
    );
    const client = new KerbcamClient(
      { host: "h", port: 1, negotiate },
      sidecar.createTransport(),
    );
    const fetchSpy = vi.spyOn(globalThis, "fetch");
    fetchSpy.mockClear(); // the global fetch spy accumulates across tests

    await client.connect([], { slots: 2 });
    sidecar.open();

    expect(negotiate).toHaveBeenCalledOnce();
    expect(negotiate.mock.calls[0][0].slots).toBe(2);
    expect(fetchSpy).not.toHaveBeenCalled(); // brokered — no HTTP /offer

    // The control channel + dynamic subscribe still work over the direct peer.
    await client.subscribe(7);
    const mid = sidecar.slotMidFor(7) as string;
    sidecar.deliverTrack(mid, {} as MediaStreamTrack);
    expect(client.camera(7).mediaStream).not.toBeNull();
  });
});

// ---------------------------------------------------------------------------
// BrowserKerbcamTransport: ICE gathering timeout
// ---------------------------------------------------------------------------

/**
 * Minimal RTCPeerConnection stub with controllable iceGatheringState.
 * Tracks event listeners so tests can fire or skip the gathering event.
 */
function makeFakeRTCPeerConnection(
  initialState: RTCIceGatheringState = "gathering",
) {
  const listeners = new Map<string, (() => void)[]>();
  const pc = {
    iceGatheringState: initialState as RTCIceGatheringState,
    localDescription: null as { sdp: string } | null,
    connectionState: "new" as RTCPeerConnectionState,
    ontrack: null as ((ev: RTCTrackEvent) => void) | null,
    onconnectionstatechange: null as (() => void) | null,
    addTransceiver: () => {},
    createDataChannel: () => ({
      onopen: null,
      onclose: null,
      onmessage: null,
      send: () => {},
    }),
    createOffer: async () => ({ sdp: "fake-sdp", type: "offer" as RTCSdpType }),
    setLocalDescription: async () => {
      pc.localDescription = { sdp: "fake-sdp" };
    },
    setRemoteDescription: async () => {},
    close: () => {},
    addEventListener(type: string, handler: () => void) {
      const list = listeners.get(type) ?? [];
      list.push(handler);
      listeners.set(type, list);
    },
    removeEventListener(type: string, handler: () => void) {
      const list = listeners.get(type) ?? [];
      listeners.set(
        type,
        list.filter((h) => h !== handler),
      );
    },
    /** Test helper: simulate gathering state change. */
    fireGatheringComplete() {
      pc.iceGatheringState = "complete";
      for (const h of listeners.get("icegatheringstatechange") ?? []) h();
    },
    listenerCount(type: string) {
      return (listeners.get(type) ?? []).length;
    },
  };
  return pc;
}

describe("BrowserKerbcamTransport: waitForIceComplete", () => {
  it("resolves immediately when already complete", async () => {
    const pc = makeFakeRTCPeerConnection("complete");
    // @ts-expect-error -- stub replaces the real constructor
    globalThis.RTCPeerConnection = class { constructor() { return pc; } };
    try {
      const transport = new BrowserKerbcamTransport({ iceGatheringTimeoutMs: 500 });
      const peer = transport.createPeer([]);
      const start = Date.now();
      await peer.waitForIceComplete();
      expect(Date.now() - start).toBeLessThan(100);
    } finally {
      // @ts-expect-error -- cleanup
      delete globalThis.RTCPeerConnection;
    }
  });

  it("resolves after the timeout when gathering never completes", async () => {
    vi.useFakeTimers();
    const pc = makeFakeRTCPeerConnection("gathering");
    // @ts-expect-error -- stub replaces the real constructor
    globalThis.RTCPeerConnection = class { constructor() { return pc; } };
    try {
      const transport = new BrowserKerbcamTransport({ iceGatheringTimeoutMs: 2000 });
      const peer = transport.createPeer([]);
      const done = vi.fn();
      void peer.waitForIceComplete().then(done);

      expect(done).not.toHaveBeenCalled();
      await vi.advanceTimersByTimeAsync(2000);
      expect(done).toHaveBeenCalledOnce();
      // Listener must have been cleaned up after the timeout fires.
      expect(pc.listenerCount("icegatheringstatechange")).toBe(0);
    } finally {
      vi.useRealTimers();
      // @ts-expect-error -- cleanup
      delete globalThis.RTCPeerConnection;
    }
  });

  it("resolves when gathering completes before the timeout and cleans up the timer", async () => {
    vi.useFakeTimers();
    const pc = makeFakeRTCPeerConnection("gathering");
    // @ts-expect-error -- stub replaces the real constructor
    globalThis.RTCPeerConnection = class { constructor() { return pc; } };
    try {
      const transport = new BrowserKerbcamTransport({ iceGatheringTimeoutMs: 2000 });
      const peer = transport.createPeer([]);
      const done = vi.fn();
      void peer.waitForIceComplete().then(done);

      // Gathering completes before the timeout.
      await vi.advanceTimersByTimeAsync(500);
      pc.fireGatheringComplete();
      await Promise.resolve(); // flush microtasks
      expect(done).toHaveBeenCalledOnce();
      // No listener remains; timer was cancelled.
      expect(pc.listenerCount("icegatheringstatechange")).toBe(0);
      // Advancing past the original timeout must not call done again.
      await vi.advanceTimersByTimeAsync(2000);
      expect(done).toHaveBeenCalledOnce();
    } finally {
      vi.useRealTimers();
      // @ts-expect-error -- cleanup
      delete globalThis.RTCPeerConnection;
    }
  });
});
