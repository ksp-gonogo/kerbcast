import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  AnchorKind,
  CameraKind,
  CameraLifecycle,
  CommandAimKind,
  CommandVantageKind,
  ErrorCode,
  ErrorSource,
  Layer,
  QualityPreset,
  TrackMode,
} from "./__generated__/types";
import {
  BrowserKerbcastTransport,
  COMMAND_CAMERA_STATE_WAIT_MS,
  CommandLockedError,
  CommandTargetUnknownError,
  CONTROL_OPEN_TIMEOUT_MS,
  CONTACT_GAP_MS,
  type ContactGap,
  OfferRefusedError,
  type KerbcastConnectionState,
  type KerbcastDataChannel,
  type KerbcastPeer,
  type KerbcastPeerOptions,
  type KerbcastTransport,
  KerbcastClient,
  type KerbcastClientConfig,
} from "./client";
import { MockSidecar } from "./testing";
import { MAX_ANCHORS_PER_CAMERA } from "./anchors";
import type { DisplayedFrameUt } from "./frameUt";
import * as noise from "./noise";
import type { EncodedFrameCopy } from "./captureClip";

interface FakeChannel extends KerbcastDataChannel {
  sent: string[];
  _open: () => void;
  _msg: (raw: string) => void;
  _close: () => void;
}

function makeFakeTransport() {
  const captured: {
    dc?: FakeChannel;
    onTrack?(t: MediaStreamTrack, idx: number, mid?: string): void;
    onState?: (s: KerbcastConnectionState) => void;
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

  const transport: KerbcastTransport = {
    createPeer: () => {
      const peer: KerbcastPeer = {
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
      getVideoTracks() {
        return this._tracks.filter((t) => t.kind === "video");
      }
    };
  }
  if (typeof fetch === "undefined") {
    // @ts-expect-error — augmenting globals for the test env
    globalThis.fetch = vi.fn();
  }
});

describe("KerbcastClient", () => {
  it("starts disconnected and emits state-change on connect", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    expect(client.state).toBe("disconnected");

    const states: KerbcastConnectionState[] = [];
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
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();

    expect(captured.dc?.sent).toContain(JSON.stringify({ type: "hello" }));
  });

  it("asks for frame stamps only from a sidecar that offers them", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
    await client.connect();
    captured.dc?._open();
    const optIns = () => (captured.dc?.sent ?? []).filter((m) => JSON.parse(m).type === "set-frame-stamps");

    captured.dc?._msg(
      JSON.stringify({ type: "hello", content: { sidecarVersion: "1.10.3", encoderBackend: "x" } }),
    );
    expect(optIns()).toEqual([]);

    captured.dc?._msg(
      JSON.stringify({
        type: "hello",
        content: { sidecarVersion: "1.11.0", encoderBackend: "x", frameStamps: true },
      }),
    );
    expect(optIns()).toEqual([JSON.stringify({ type: "set-frame-stamps", content: { enabled: true } })]);
  });

  it("emits `control-open` once per control channel open, after hello is sent", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockImplementation(async () => fakeAnswer());
    const sentAtOpen: string[][] = [];
    client.on("control-open", () => sentAtOpen.push([...(captured.dc?.sent ?? [])]));

    await client.connect();
    expect(sentAtOpen).toHaveLength(0);
    captured.dc?._open();
    expect(sentAtOpen).toEqual([[JSON.stringify({ type: "hello" })]]);

    await client.connect();
    captured.dc?._open();
    expect(sentAtOpen).toHaveLength(2);
  });

  describe("commands sent before the control channel opens", () => {
    it("wait for it and go out after hello, in send order", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
      await client.connect();

      const fov = client.camera(7).setFov(40);
      const sub = client.subscribe(7);
      await Promise.resolve();
      expect(captured.dc?.sent).toEqual([]);

      captured.dc?._open();
      await expect(Promise.all([fov, sub])).resolves.toBeDefined();
      expect(captured.dc?.sent.map((m) => JSON.parse(m).type)).toEqual([
        "hello",
        "set-fov",
        "subscribe",
      ]);
    });

    it("reject when the channel does not open in time", async () => {
      vi.useFakeTimers();
      try {
        const { transport } = makeFakeTransport();
        const client = new KerbcastClient({ host: "h", port: 1 }, transport);
        vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
        await client.connect();
        const outcome = client.subscribe(7).then(
          () => "sent",
          (e: Error) => e.message,
        );
        await vi.advanceTimersByTimeAsync(CONTROL_OPEN_TIMEOUT_MS + 1);
        expect(await outcome).toMatch(/did not open within/);
      } finally {
        vi.useRealTimers();
      }
    });

    it("reject when the client disconnects first", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
      await client.connect();
      const pending = client.subscribe(7);
      client.disconnect();
      await expect(pending).rejects.toThrow(/disconnected before the control channel opened/);
      expect(captured.dc?.sent).toEqual([]);
    });

    it("reject when the channel closes before opening", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
      await client.connect();
      const pending = client.subscribe(7);
      captured.dc?._close();
      await expect(pending).rejects.toThrow(/closed before it opened/);
    });
  });

  it("populates the camera registry from a snapshot push", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    expect(client.camera(42)).toBe(client.camera(42));
  });

  it("set-degrade routes onto the control channel via the handle", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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

  it("set-quality routes onto the control channel; null preset means auto", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc!.sent.length = 0; // drop the hello

    await client.camera(42).setQuality(QualityPreset.Half);
    await client.camera(42).setQuality(null);

    expect(captured.dc?.sent).toEqual([
      JSON.stringify({
        type: "set-quality",
        content: { flightId: 42, preset: "half" },
      }),
      // Auto: the preset field is omitted entirely (sidecar reads None).
      JSON.stringify({ type: "set-quality", content: { flightId: 42 } }),
    ]);
  });

  it("set-quality roundtrips through MockSidecar into camera state", async () => {
    const sidecar = new MockSidecar();
    sidecar.addCamera({
      flightId: 42,
      operatorWidth: 1024,
      operatorHeight: 576,
      renderWidth: 1024,
      renderHeight: 576,
    });
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      MockSidecar.makeOfferResponse([42]),
    );

    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    await client.connect([42]);
    sidecar.open();

    await client.camera(42).setQuality(QualityPreset.Half);
    expect(sidecar.lastCommand("set-quality", 42)?.content.preset).toBe(
      QualityPreset.Half,
    );
    // The mock models the honored case: effective dims follow the preset
    // and the change event carries the authoritative quality state.
    const state = client.camera(42).state;
    expect(state?.viewerQuality).toBe(QualityPreset.Half);
    expect(state?.renderWidth).toBe(512);
    expect(state?.renderHeight).toBe(288);
    expect(state?.qualityLimitedBy).toBeUndefined();

    // Throttled-below-request is simulated by the test driving the state
    // push directly, exactly like the real sidecar would broadcast it.
    sidecar.updateCamera(42, {
      renderWidth: 256,
      renderHeight: 144,
      qualityLimitedBy: "throttled",
    });
    expect(client.camera(42).state?.qualityLimitedBy).toBe("throttled");

    await client.camera(42).setQuality(null);
    expect(client.camera(42).state?.viewerQuality).toBeUndefined();
    expect(client.camera(42).state?.renderWidth).toBe(1024);
  });

  it("setFov / setLayers / setRenderSize routing", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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

  it("set-force-full-resolution routes onto the control channel via the handle", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc!.sent.length = 0; // drop the hello

    await client.camera(42).setForceFullResolution(true);
    await client.camera(42).setForceFullResolution(false);

    expect(captured.dc?.sent).toEqual([
      JSON.stringify({
        type: "set-force-full-resolution",
        content: { flightId: 42, force: true },
      }),
      JSON.stringify({
        type: "set-force-full-resolution",
        content: { flightId: 42, force: false },
      }),
    ]);
  });

  it("setSizeHold routes onto the control channel via the handle", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc?._msg(
      JSON.stringify({ type: "hello", content: { sidecarVersion: "1.12.0", encoderBackend: "x", sizeHold: true } }),
    );
    captured.dc!.sent.length = 0;

    await client.camera(42).setSizeHold(true);
    await client.camera(42).setSizeHold(false);

    expect(captured.dc?.sent).toEqual([
      JSON.stringify({ type: "set-size-hold", content: { flightId: 42, hold: true } }),
      JSON.stringify({ type: "set-size-hold", content: { flightId: 42, hold: false } }),
    ]);
  });

  it("reportDisplaySize sends a report-display-size command", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc!.sent.length = 0;

    await client.reportDisplaySize(3, 40, 40);

    expect(captured.dc?.sent).toEqual([
      JSON.stringify({
        type: "report-display-size",
        content: { flightId: 3, width: 40, height: 40 },
      }),
    ]);
  });

  it("setTrackTarget sends a set-track-target command", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc!.sent.length = 0;

    await client.setTrackTarget(5, TrackMode.ActiveVessel);

    expect(captured.dc?.sent).toEqual([
      JSON.stringify({
        type: "set-track-target",
        content: { flightId: 5, mode: TrackMode.ActiveVessel },
      }),
    ]);
  });

  describe("command cameras", () => {
    /* `reported` adds the game's first status report: a target, a target list and commandTrack. */
    function commandCamera(flightId: number, token?: string, reported = true) {
      return {
        flightId,
        kind: CameraKind.Command,
        partName: "kerbcast.commandCamera",
        partTitle: "Command camera",
        cameraName: "Launchpad camera",
        vesselName: "Launch Pad",
        layers: [Layer.Near],
        operatorLayers: [Layer.Near],
        renderWidth: 768,
        renderHeight: 768,
        operatorWidth: 768,
        operatorHeight: 768,
        supportsZoom: false,
        fov: 30,
        fovMin: 30,
        fovMax: 30,
        supportsPan: false,
        panYaw: 0,
        panPitch: 0,
        panYawMin: 0,
        panYawMax: 0,
        panPitchMin: 0,
        panPitchMax: 0,
        encoderBitrateBps: 0,
        targetBitrateBps: 0,
        degradeLevel: 0,
        commandCentre: {
          stationId: "ground:LaunchPad",
          nodeName: "LaunchPad",
          displayName: "Launch Pad",
          body: "Kerbin",
          latitude: 0,
          longitude: 0,
          altitude: 0,
          subjectPersistentId: reported ? 42 : 0,
          trackingLevel: 1,
          targets: reported
            ? [
                {
                  persistentId: 42,
                  vesselName: "Kerbal X",
                  vesselType: "Ship",
                  rangeM: 519,
                  eligibility: "ok",
                  eligibilityText: "Trackable",
                },
              ]
            : [],
          created: token !== undefined,
          ...(token !== undefined ? { token } : {}),
        },
        ...(reported
          ? {
              commandTrack: {
                ut: 1,
                hasFix: true,
                rangeM: 519,
                occluded: false,
                occluder: "",
                sunlit: true,
                frameHeightPx: 768,
                framingFits: true,
                events: [],
              },
            }
          : {}),
      };
    }

    async function connected() {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
      await client.connect();
      captured.dc?._open();
      captured.dc!.sent.length = 0;
      const snapshot = (cameras: unknown[]) =>
        captured.dc?._msg(JSON.stringify({ type: "camera-snapshot", content: { cameras } }));
      return { client, captured, snapshot };
    }

    it("createCommandCamera sends the request and resolves with the camera carrying its token", async () => {
      const { client, captured, snapshot } = await connected();

      const created = client.createCommandCamera(
        { kind: CommandVantageKind.LaunchSite, site: "LaunchPad" },
        { token: "tok-1", name: "Pad cam" },
      );
      await Promise.resolve();
      expect(captured.dc?.sent).toEqual([
        JSON.stringify({
          type: "create-command-camera",
          content: {
            token: "tok-1",
            vantage: { kind: "launchSite", site: "LaunchPad" },
            name: "Pad cam",
          },
        }),
      ]);

      snapshot([commandCamera(100), commandCamera(200, "someone-else")]);
      snapshot([commandCamera(100), commandCamera(200, "someone-else"), commandCamera(300, "tok-1")]);
      const cam = await created;
      expect(cam.flightId).toBe(300);
      expect(cam.commandCentre?.created).toBe(true);
    });

    it("createCommandCamera generates a token when none is given and passes target and look-at", async () => {
      const { client, captured, snapshot } = await connected();
      const aim = {
        kind: CommandAimKind.Surface,
        body: "Kerbin",
        latitude: -0.0972,
        longitude: -74.5577,
        altitude: 70,
        framingMarginM: 40,
      };
      const created = client.createCommandCamera(
        { kind: CommandVantageKind.Surface, body: "Kerbin", latitude: -0.05, longitude: -74.7 },
        { aim },
      );
      await Promise.resolve();
      const sent = JSON.parse(captured.dc!.sent[0]);
      expect(sent.type).toBe("create-command-camera");
      expect(typeof sent.content.token).toBe("string");
      expect(sent.content.token.length).toBeGreaterThan(0);
      expect(sent.content.aim).toEqual(aim);
      expect("name" in sent.content).toBe(false);

      snapshot([commandCamera(5, sent.content.token)]);
      expect((await created).flightId).toBe(5);
    });

    it("createCommandCamera rejects when the camera never appears", async () => {
      vi.useFakeTimers();
      try {
        const { client } = await connected();
        const created = client.createCommandCamera(
          { kind: CommandVantageKind.Station, station: "Nowhere" },
          { token: "t", timeoutMs: 1000 },
        );
        const outcome = created.then(
          () => "resolved",
          (e: Error) => e.message,
        );
        await vi.advanceTimersByTimeAsync(1001);
        expect(await outcome).toMatch(/did not appear within 1000 ms/);
      } finally {
        vi.useRealTimers();
      }
    });

    it("createCommandCamera works straight after connect resolves", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
      vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
      await client.connect();
      const created = client.createCommandCamera(
        { kind: CommandVantageKind.LaunchSite },
        { token: "early" },
      );
      captured.dc?._open();
      await Promise.resolve();
      expect(captured.dc?.sent.map((m) => JSON.parse(m).type)).toEqual([
        "hello",
        "create-command-camera",
      ]);
      captured.dc?._msg(
        JSON.stringify({
          type: "camera-snapshot",
          content: { cameras: [commandCamera(9, "early")] },
        }),
      );
      expect((await created).flightId).toBe(9);
    });

    it("createCommandCamera resolves only once the camera's target is reported", async () => {
      const { client, snapshot } = await connected();
      let resolved = false;
      const created = client.createCommandCamera(
        { kind: CommandVantageKind.LaunchSite },
        { token: "tok-2" },
      );
      void created.then(() => {
        resolved = true;
      });

      snapshot([commandCamera(300, "tok-2", false)]);
      await Promise.resolve();
      expect(resolved).toBe(false);

      snapshot([commandCamera(300, "tok-2")]);
      const cam = await created;
      expect(cam.commandCentre?.subjectPersistentId).toBe(42);
      expect(cam.commandCentre?.targets.map((t) => t.persistentId)).toEqual([42]);
    });

    it("createCommandCamera resolves with what is known when no report comes", async () => {
      vi.useFakeTimers();
      try {
        const { client, snapshot } = await connected();
        const created = client.createCommandCamera(
          { kind: CommandVantageKind.LaunchSite },
          { token: "bare", timeoutMs: 1000 },
        );
        const outcome = created.then(
          (c) => c,
          (e: Error) => e.message,
        );
        await vi.advanceTimersByTimeAsync(10);
        snapshot([commandCamera(300, "bare", false)]);
        await vi.advanceTimersByTimeAsync(COMMAND_CAMERA_STATE_WAIT_MS + 1);
        const cam = await outcome;
        expect(typeof cam).not.toBe("string");
        expect((cam as { flightId: number }).flightId).toBe(300);
      } finally {
        vi.useRealTimers();
      }
    });

    it("createCommandCamera throws when the control channel is not open", async () => {
      const client = new KerbcastClient({ host: "h", port: 1 }, makeFakeTransport().transport);
      await expect(
        client.createCommandCamera({ kind: CommandVantageKind.LaunchSite }, { timeoutMs: 10 }),
      ).rejects.toThrow(/control channel not open/);
    });

    it("destroyCommandCamera and setCommandAim send their commands", async () => {
      const { client, captured } = await connected();
      await client.destroyCommandCamera(2147483905);
      await client.setCommandAim(2147483905, {
        kind: CommandAimKind.Vessel,
        persistentId: 4000000001,
      });
      await client.setCommandAim(2147483905, {
        kind: CommandAimKind.Direction,
        azimuth: 90,
        elevation: 30,
        framingMarginM: 50,
      });
      await client.setCommandAim(2147483905, {
        kind: CommandAimKind.Vessel,
        persistentId: 4000000002,
        allowUncertain: true,
      });
      await client.setCommandAim(2147483905, null);
      expect(captured.dc?.sent).toEqual([
        JSON.stringify({ type: "destroy-command-camera", content: { flightId: 2147483905 } }),
        JSON.stringify({
          type: "set-command-aim",
          content: { flightId: 2147483905, aim: { kind: "vessel", persistentId: 4000000001 } },
        }),
        JSON.stringify({
          type: "set-command-aim",
          content: {
            flightId: 2147483905,
            aim: { kind: "direction", azimuth: 90, elevation: 30, framingMarginM: 50 },
          },
        }),
        JSON.stringify({
          type: "set-command-aim",
          content: {
            flightId: 2147483905,
            aim: { kind: "vessel", persistentId: 4000000002, allowUncertain: true },
          },
        }),
        JSON.stringify({ type: "set-command-aim", content: { flightId: 2147483905 } }),
      ]);
    });

    it("setCommandAim rejects while a command authority holds the lock", async () => {
      const { client, captured, snapshot } = await connected();
      const locked = {
        ...commandCamera(300),
        commandTrack: { ...commandCamera(300).commandTrack, authority: { owner: "mission-control" } },
      };
      snapshot([locked]);
      const err = await client
        .setCommandAim(300, { kind: CommandAimKind.Vessel, persistentId: 7 })
        .catch((e: unknown) => e);
      expect(err).toBeInstanceOf(CommandLockedError);
      expect((err as CommandLockedError).owner).toBe("mission-control");
      expect((err as CommandLockedError).code).toBe(ErrorCode.CommandLocked);
      const search = await client
        .setCommandAim(300, { kind: CommandAimKind.Vessel, persistentId: 7, allowUncertain: true })
        .catch((e: unknown) => e);
      expect(search).toBeInstanceOf(CommandLockedError);
      expect(captured.dc?.sent).toEqual([]);

      snapshot([commandCamera(300)]);
      await client.setCommandAim(300, null);
      expect(captured.dc?.sent).toEqual([
        JSON.stringify({ type: "set-command-aim", content: { flightId: 300 } }),
      ]);
    });

    it("setCommandAim rejects an aim at a craft missing from the target list", async () => {
      const { client, captured, snapshot } = await connected();
      snapshot([commandCamera(300)]);

      const err = await client
        .setCommandAim(300, { kind: CommandAimKind.Vessel, persistentId: 99 })
        .catch((e: unknown) => e);
      expect(err).toBeInstanceOf(CommandTargetUnknownError);
      expect((err as CommandTargetUnknownError).code).toBe(ErrorCode.CommandTargetUnknown);
      expect((err as CommandTargetUnknownError).persistentId).toBe(99);
      expect((err as CommandTargetUnknownError).flightId).toBe(300);
      await expect(
        client.setCommandAim(300, {
          kind: CommandAimKind.PositionAt,
          body: "Kerbin",
          ut: 0,
          position: [0, 0, 7e5],
          velocity: [0, 0, 0],
          subjectPersistentId: 99,
        }),
      ).rejects.toBeInstanceOf(CommandTargetUnknownError);
      expect(captured.dc?.sent).toEqual([]);

      await client.setCommandAim(300, { kind: CommandAimKind.Vessel, persistentId: 42 });
      await client.setCommandAim(300, { kind: CommandAimKind.Direction, azimuth: 0, elevation: 10 });
      expect(captured.dc?.sent).toHaveLength(2);

      /* No list yet: the game decides. */
      snapshot([commandCamera(301, undefined, false)]);
      await client.setCommandAim(301, { kind: CommandAimKind.Vessel, persistentId: 99 });
      expect(captured.dc?.sent).toHaveLength(3);

      /* A list cut to fit the message may leave the craft out: the sidecar decides. */
      const cut = commandCamera(302);
      snapshot([{ ...cut, commandCentre: { ...cut.commandCentre!, targetsOmitted: 400 } }]);
      await client.setCommandAim(302, { kind: CommandAimKind.Vessel, persistentId: 99 });
      expect(captured.dc?.sent).toHaveLength(4);
    });
  });

  it("disconnect tears down peer + emits null MediaStream events", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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

  it("a failed connection closes its peer, freeing any relay allocations", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([42]));

    await client.connect([42]);
    captured.onState?.("disconnected");
    expect(captured.closed).toBe(false);
    captured.onState?.("failed");
    expect(captured.closed).toBe(true);
    expect(client.state).toBe("failed");
  });

  it("a failed /offer closes the peer it gathered", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response("nope", { status: 500 }));

    await expect(client.connect([42])).rejects.toThrow();
    expect(captured.closed).toBe(true);
  });

  it("non-OK /offer response throws with status code and transitions to failed", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response("service unavailable", { status: 503 }),
    );

    const states: KerbcastConnectionState[] = [];
    client.on("state-change", (s) => states.push(s));

    await expect(client.connect()).rejects.toThrow("503");
    expect(states).toContain("failed");
    expect(client.state).toBe("failed");
  });

  it("a refused /offer surfaces the sidecar's reason and code", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(
        JSON.stringify({
          message: "the browser offered no H.264 video",
          source: ErrorSource.Sidecar,
          code: ErrorCode.CodecUnsupported,
        }),
        { status: 422, headers: { "content-type": "application/json" } },
      ),
    );

    const err = await client.connect().catch((e: unknown) => e);
    expect(err).toBeInstanceOf(OfferRefusedError);
    expect((err as OfferRefusedError).status).toBe(422);
    expect((err as OfferRefusedError).code).toBe(ErrorCode.CodecUnsupported);
    expect((err as Error).message).toMatch(/no H\.264 video/);
    expect(client.state).toBe("failed");
  });

  it("discover() returns the camera list from /cameras", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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

  it("maxRenderSize reads the live camera state for a camera discover() never saw", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(JSON.stringify({ cameras: [] }), { status: 200 }),
    );
    await client.discover();
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
    await client.connect();
    captured.dc?._open();
    expect(client.camera(42).maxRenderSize).toBeNull();

    captured.dc?._msg(
      JSON.stringify({
        type: "camera-state-changed",
        content: { state: fakeCameraState(42, { maxWidth: 1296, maxHeight: 730 }) },
      }),
    );
    expect(client.camera(42).maxRenderSize).toEqual({ width: 1296, height: 730 });
  });

  it("maxRenderSize falls back to discover() when the camera state omits the ceiling", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValueOnce(
      new Response(
        JSON.stringify({ cameras: [{ flightId: 42, maxWidth: 1280, maxHeight: 720 }] }),
        { status: 200 },
      ),
    );
    await client.discover();
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
    await client.connect();
    captured.dc?._open();
    captured.dc?._msg(
      JSON.stringify({
        type: "camera-state-changed",
        content: { state: fakeCameraState(42) },
      }),
    );
    expect(client.camera(42).maxRenderSize).toEqual({ width: 1280, height: 720 });
  });

  it("discover() surfaces per-camera rates and the held display size", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    const base = {
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
    };
    const mockCameras = [
      {
        ...base,
        flightId: 1,
        targetCaptureFps: 30,
        displayed: true,
        captureFps: 29.5,
        sentFps: 23,
        keyframesSent: 12,
        keyframeRequestsReceived: 4,
        displaySize: { width: 640, height: 360, ageMs: 250 },
      },
      { ...base, flightId: 2, targetCaptureFps: 2, displayed: false, displaySize: null },
      { ...base, flightId: 3 },
    ];
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      new Response(JSON.stringify({ cameras: mockCameras }), { status: 200 }),
    );

    const [watched, background, old] = await client.discover();
    expect(watched.targetCaptureFps).toBe(30);
    expect(watched.displayed).toBe(true);
    expect(watched.captureFps).toBe(29.5);
    expect(watched.sentFps).toBe(23);
    expect(watched.keyframesSent).toBe(12);
    expect(watched.keyframeRequestsReceived).toBe(4);
    expect(watched.displaySize).toEqual({ width: 640, height: 360, ageMs: 250 });
    expect(background.targetCaptureFps).toBe(2);
    expect(background.displaySize).toBeNull();
    // An older sidecar sends none of the rate fields.
    expect(old.targetCaptureFps).toBeUndefined();
    expect(old.displaySize).toBeUndefined();
  });

  it("camera state from the snapshot carries the rate fields when the sidecar sends them", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());

    await client.connect();
    captured.dc?._open();
    captured.dc?._msg(
      JSON.stringify({
        type: "camera-snapshot",
        content: {
          cameras: [
            fakeCameraState(1, {
              targetCaptureFps: 2,
              displayed: false,
              captureFps: 2,
              sentFps: 2,
              keyframesSent: 3,
              keyframeRequestsReceived: 0,
            }),
            fakeCameraState(2),
          ],
        },
      }),
    );

    const [withRates, legacy] = client.cameras;
    expect(withRates.targetCaptureFps).toBe(2);
    expect(withRates.displayed).toBe(false);
    expect(withRates.sentFps).toBe(2);
    expect(withRates.keyframesSent).toBe(3);
    expect(withRates.displaySize).toBeUndefined();
    expect(legacy.targetCaptureFps).toBeUndefined();
    expect(legacy.captureFps).toBeUndefined();
  });

  it("sidecar error message emits error event with correct payload including source", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);

    // camera() and setFov() before connect — control channel is null
    await expect(client.camera(1).setFov(30)).rejects.toThrow(
      "[kerbcast] control channel not open",
    );
  });

  it("adaptive-shed message emits adaptive-shed event with correct payload", async () => {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([42]));

    await client.connect([42]);

    // Simulate track arrival to get a non-null mediaStream
    const fakeTrack = {} as MediaStreamTrack;
    captured.onTrack?.(fakeTrack, 0);
    expect(client.camera(42).mediaStream).not.toBeNull();

    const states: KerbcastConnectionState[] = [];
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
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
      const client = new KerbcastClient({ host: "h", port: 1, noise: { enabled: false } }, transport);
      expect(client._resolveNoise(null)).toBe(false);
    });

    it("per-camera configure() overrides client default", () => {
      const { transport } = makeFakeTransport();
      const clientOn = new KerbcastClient({ host: "h", port: 1 }, transport);
      const clientOff = new KerbcastClient({ host: "h", port: 1, noise: { enabled: false } }, transport);

      // Per-camera enable overrides client-off
      expect(clientOff._resolveNoise({ enabled: true })).toBe(true);
      // Per-camera disable overrides client-on (default)
      expect(clientOn._resolveNoise({ enabled: false })).toBe(false);
    });

    it("configure() triggers stream re-emit", async () => {
      const { transport, captured } = makeFakeTransport();
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
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

    it("frameLock keeps a noise-disabled camera on the pipeline with no static", async () => {
      const fakeProcessed = new MediaStream();
      const mockCaptureStream = vi.fn().mockReturnValue(fakeProcessed);
      const fakeCtx = {
        drawImage: vi.fn(),
        fillRect: vi.fn(),
        createImageData: vi.fn().mockReturnValue({ data: new Uint8ClampedArray(4) }),
        putImageData: vi.fn(),
      };
      const origGetContext = HTMLCanvasElement.prototype.getContext;
      // @ts-expect-error -- jsdom augmentation
      HTMLCanvasElement.prototype.getContext = vi.fn().mockReturnValue(fakeCtx);
      // @ts-expect-error -- jsdom augmentation
      HTMLCanvasElement.prototype.captureStream = mockCaptureStream;
      let draw: FrameRequestCallback | null = null;
      const rafSpy = vi.spyOn(globalThis, "requestAnimationFrame").mockImplementation((cb) => {
        draw = cb;
        return 0;
      });

      try {
        const { transport, captured } = makeFakeTransport();
        const client = new KerbcastClient(
          { host: "h", port: 1, noise: { enabled: false }, frameLock: true },
          transport,
        );
        vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([3]));

        await client.connect([3]);
        captured.onTrack?.({} as MediaStreamTrack, 0);
        const cam = client.camera(3);
        expect(cam.mediaStream).toBe(fakeProcessed);

        /* showStatic can't bring static back, and the waiting frame is plain black. */
        cam.setShowStatic(true);
        (draw as FrameRequestCallback | null)?.(0);
        expect(fakeCtx.putImageData).not.toHaveBeenCalled();
      } finally {
        HTMLCanvasElement.prototype.getContext = origGetContext;
        // @ts-expect-error -- cleanup
        delete HTMLCanvasElement.prototype.captureStream;
        rafSpy.mockRestore();
      }
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
        const client = new KerbcastClient({ host: "h", port: 1 }, transport);
        vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([3]));

        await client.connect([3]);
        captured.onTrack?.({} as MediaStreamTrack, 0);

        expect(client.camera(3).mediaStream).toBe(fakeProcessed);
        /* Frames are captured on demand, one per emitted composite. */
        expect(mockCaptureStream).toHaveBeenCalledWith(0);
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
        const client = new KerbcastClient(
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

    it("re-sources a resurrected camera from its retained track (no refresh)", async () => {
      // KSP reuses part.flightID across revert, so the sidecar tombstones a
      // camera (handle goes sourceless / static) then resurrects the SAME id
      // and rebinds the SAME track WITHOUT a new slot-map. The handle only
      // re-sources on a slot-map, so it must re-source itself when the
      // lifecycle flips back to Active, or the feed is stuck on static until a
      // page refresh.
      const setIntensity = vi.fn();
      const setSource = vi.fn();
      const processedStream = new MediaStream();
      const createSpy = vi
        .spyOn(noise, "tryCreateNoisePipeline")
        .mockReturnValue({
          processedStream,
          setIntensity,
          setSource,
          setShowStatic: vi.fn(),
          setPassthrough: vi.fn(),
          destroy: vi.fn(),
        });
      try {
        const sidecar = new MockSidecar().withSlots(["a"]);
        sidecar.addCamera({ flightId: 42 });
        const client = new KerbcastClient(
          { host: "h", port: 1 },
          sidecar.createTransport(),
        );
        vi.spyOn(globalThis, "fetch").mockImplementation(() =>
          Promise.resolve(MockSidecar.makeOfferResponse([])),
        );

        await client.connect([], { slots: 1 });
        sidecar.open();
        await client.subscribe(42);
        sidecar.deliverTrack("a", {} as MediaStreamTrack);

        // Live: sourced from the delivered track.
        expect(setSource.mock.calls.at(-1)?.[0]).not.toBeNull();

        // Part destroyed: source detaches, feed shows full static. The slot is
        // NOT freed (no slot-map), so the client keeps the track for the mid.
        sidecar.destroyCamera(42);
        expect(setSource).toHaveBeenLastCalledWith(null);

        // Resurrection: lifecycle flips back to Active with no new slot-map.
        // The handle must re-source from the track it still holds.
        setSource.mockClear();
        sidecar.updateCamera(42, { lifecycle: CameraLifecycle.Active });
        expect(setSource).toHaveBeenCalledTimes(1);
        expect(setSource.mock.calls[0][0]).not.toBeNull();
      } finally {
        createSpy.mockRestore();
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
          setShowStatic: vi.fn(),
          setPassthrough: vi.fn(),
          destroy: vi.fn(),
        });
      try {
        const sidecar = new MockSidecar();
        sidecar.addCamera({ flightId: 42, degradeLevel: 0.3 });
        const client = new KerbcastClient(
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
          setShowStatic: vi.fn(),
          setPassthrough: vi.fn(),
          destroy: vi.fn(),
        });
      try {
        const sidecar = new MockSidecar().withSlots(["a"]);
        sidecar.addCamera({ flightId: 1 });
        const client = new KerbcastClient(
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
        const client = new KerbcastClient(
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
          setShowStatic: vi.fn(),
          setPassthrough: vi.fn(),
          destroy,
        });
      try {
        const sidecar = new MockSidecar();
        sidecar.addCamera({ flightId: 42 });
        const client = new KerbcastClient(
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

  describe("stall surface (handle mirror of the pipeline's stall detector)", () => {
    /*
     * Stub pipeline that captures the options the handle creates it with, so
     * tests can fire the onStallChange callback and inspect the showStatic
     * seed without a real canvas.
     */
    function makePipelineSpy() {
      const setShowStatic = vi.fn();
      const pipeline = {
        processedStream: new MediaStream(),
        setIntensity: vi.fn(),
        setSource: vi.fn(),
        setShowStatic,
        destroy: vi.fn(),
      };
      let opts: Parameters<typeof noise.tryCreateNoisePipeline>[2];
      const createSpy = vi
        .spyOn(noise, "tryCreateNoisePipeline")
        .mockImplementation((_raw, _intensity, options) => {
          opts = options;
          return pipeline;
        });
      return {
        setShowStatic,
        createSpy,
        fireStall: (stalled: boolean) => opts?.onStallChange?.(stalled),
        creationOptions: () => opts,
      };
    }

    /** Connected client with camera 42 registered; track not yet delivered. */
    async function connectCamera42(
      sidecar: MockSidecar,
    ): Promise<KerbcastClient> {
      sidecar.addCamera({ flightId: 42 });
      const client = new KerbcastClient(
        { host: "h", port: 1 },
        sidecar.createTransport(),
      );
      vi.spyOn(globalThis, "fetch").mockImplementation(() =>
        Promise.resolve(MockSidecar.makeOfferResponse([42])),
      );
      await client.connect([42]);
      sidecar.open();
      return client;
    }

    it("stalled getter and stall events mirror the pipeline's transitions", async () => {
      const spy = makePipelineSpy();
      try {
        const sidecar = new MockSidecar();
        const client = await connectCamera42(sidecar);
        sidecar.deliverTrack("0", {} as MediaStreamTrack);

        const cam = client.camera(42);
        expect(cam.stalled).toBe(false);
        const events: boolean[] = [];
        cam.on("stall", (s) => events.push(s));

        spy.fireStall(true);
        expect(cam.stalled).toBe(true);
        expect(events).toEqual([true]);

        // Repeating the same state is deduped: no extra event.
        spy.fireStall(true);
        expect(events).toEqual([true]);

        spy.fireStall(false);
        expect(cam.stalled).toBe(false);
        expect(events).toEqual([true, false]);
      } finally {
        spy.createSpy.mockRestore();
      }
    });

    it("setShowStatic seeds pipeline creation and forwards live changes", async () => {
      const spy = makePipelineSpy();
      try {
        const sidecar = new MockSidecar();
        const client = await connectCamera42(sidecar);
        const cam = client.camera(42);

        // Default on; flipping before any pipeline exists just stores it.
        expect(cam.showStatic).toBe(true);
        cam.setShowStatic(false);
        expect(cam.showStatic).toBe(false);
        expect(spy.creationOptions()).toBeUndefined();

        // The pipeline built on track arrival carries the stored setting.
        sidecar.deliverTrack("0", {} as MediaStreamTrack);
        expect(spy.creationOptions()?.showStatic).toBe(false);

        // Runtime flips forward to the live pipeline.
        cam.setShowStatic(true);
        expect(cam.showStatic).toBe(true);
        expect(spy.setShowStatic).toHaveBeenLastCalledWith(true);
      } finally {
        spy.createSpy.mockRestore();
      }
    });

    it("teardown (disconnect) clears a latched stall", async () => {
      const spy = makePipelineSpy();
      try {
        const sidecar = new MockSidecar();
        const client = await connectCamera42(sidecar);
        sidecar.deliverTrack("0", {} as MediaStreamTrack);

        const cam = client.camera(42);
        const events: boolean[] = [];
        cam.on("stall", (s) => events.push(s));

        spy.fireStall(true);
        expect(cam.stalled).toBe(true);

        // The pipeline (and its stall detector) dies with the connection; a
        // handle must not report a stale stall afterwards.
        client.disconnect();
        expect(cam.stalled).toBe(false);
        expect(events).toEqual([true, false]);
      } finally {
        spy.createSpy.mockRestore();
      }
    });
  });
});

describe("KerbcastClient — dynamic slot subscription", () => {
  it("subscribe binds a slot and routes its track; unsubscribe clears it", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
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

  it("sends a camera's force, hold and display size again when it returns to the list", async () => {
    const sidecar = new MockSidecar();
    sidecar.addCamera({ flightId: 42 });
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect([], { slots: 4 });
    sidecar.open();
    await client.subscribe(42);
    await client.camera(42).setForceFullResolution(true);
    await client.camera(42).setSizeHold(true);
    await client.reportDisplaySize(42, 640, 360);
    const sentSince = (n: number) => sidecar.commands.slice(n).map((m) => JSON.stringify(m));

    sidecar.setCameras([]);
    const gone = sidecar.commands.length;
    sidecar.setCameras([{ flightId: 42 }]);
    await vi.waitFor(() =>
      expect(sentSince(gone)).toEqual([
        JSON.stringify({ type: "set-force-full-resolution", content: { flightId: 42, force: true } }),
        JSON.stringify({ type: "set-size-hold", content: { flightId: 42, hold: true } }),
        JSON.stringify({ type: "report-display-size", content: { flightId: 42, width: 640, height: 360 } }),
      ]),
    );

    const listed = sidecar.commands.length;
    sidecar.setCameras([{ flightId: 42 }]);
    await new Promise((r) => setTimeout(r, 0));
    expect(sentSince(listed)).toEqual([]);
  });

  it("sends nothing again for a camera this client unsubscribed before it left the list", async () => {
    const sidecar = new MockSidecar();
    sidecar.addCamera({ flightId: 42 });
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect([], { slots: 4 });
    sidecar.open();
    await client.subscribe(42);
    await client.camera(42).setForceFullResolution(true);
    await client.reportDisplaySize(42, 1280, 720);
    await client.unsubscribe(42);

    sidecar.setCameras([]);
    const gone = sidecar.commands.length;
    sidecar.setCameras([{ flightId: 42 }]);
    await new Promise((r) => setTimeout(r, 0));
    expect(sidecar.commands.slice(gone)).toEqual([]);

    await client.subscribe(42);
    sidecar.setCameras([]);
    const again = sidecar.commands.length;
    sidecar.setCameras([{ flightId: 42 }]);
    await new Promise((r) => setTimeout(r, 0));
    expect(sidecar.commands.slice(again).map((m) => m.type)).toEqual(["set-force-full-resolution"]);
  });

  /* The mock announces every bound slot again on recovery, as the sidecar does,
  so this also proves a repeated slot map for the playing track changes nothing. */
  it("keeps every feed's stream through a disconnect the peer recovers from", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect([], { slots: 4 });
    sidecar.open();
    await client.subscribe(42);
    sidecar.deliverTrack(sidecar.slotMidFor(42) as string, {} as MediaStreamTrack);
    const cam = client.camera(42);
    const stream = cam.mediaStream;
    expect(stream).not.toBeNull();
    const streams: (MediaStream | null)[] = [];
    cam.on("stream", (s) => streams.push(s));

    sidecar.setConnectionState("disconnected");
    expect(client.state).toBe("disconnected");
    sidecar.setConnectionState("connected");

    expect(cam.mediaStream).toBe(stream);
    expect(cam.hasSource).toBe(true);
    expect(streams).toEqual([]);
  });

  it("re-sources a feed that lost its stream when the sidecar announces its slot again on recovery", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect([], { slots: 4 });
    sidecar.open();
    await client.subscribe(42);
    const track = {} as MediaStreamTrack;
    sidecar.deliverTrack(sidecar.slotMidFor(42) as string, track);
    const cam = client.camera(42);
    /* What a client that tears down on disconnect is left with. */
    cam._setMediaStream(null);
    expect(cam.mediaStream).toBeNull();

    sidecar.setConnectionState("disconnected");
    sidecar.setConnectionState("connected");

    expect(cam.mediaStream?.getTracks()).toEqual([track]);
    expect(cam.hasSource).toBe(true);
  });

  it("re-sources a feed whose track ended when its slot is announced again", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect([], { slots: 4 });
    sidecar.open();
    await client.subscribe(42);
    const track = { readyState: "live" } as { readyState: MediaStreamTrackState };
    sidecar.deliverTrack(sidecar.slotMidFor(42) as string, track as MediaStreamTrack);
    const cam = client.camera(42);
    const streams: (MediaStream | null)[] = [];
    cam.on("stream", (s) => streams.push(s));

    sidecar.setConnectionState("disconnected");
    sidecar.setConnectionState("connected");
    expect(streams).toEqual([]);

    track.readyState = "ended";
    sidecar.setConnectionState("disconnected");
    sidecar.setConnectionState("connected");
    expect(streams).toHaveLength(1);
    expect(streams[0]?.getTracks()).toEqual([track]);
  });

  it("drops every feed's stream when the peer fails", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect([], { slots: 4 });
    sidecar.open();
    await client.subscribe(42);
    sidecar.deliverTrack(sidecar.slotMidFor(42) as string, {} as MediaStreamTrack);

    sidecar.setConnectionState("disconnected");
    sidecar.setConnectionState("failed");

    expect(client.camera(42).mediaStream).toBeNull();
    expect(client.camera(42).hasSource).toBe(false);
  });

  it("reports whether the camera has a live source as its slot binds, frees, ends and disconnects", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect([], { slots: 4 });
    sidecar.open();
    const cam = client.camera(42);
    const events: boolean[] = [];
    cam.on("source", (has) => events.push(has));

    await client.subscribe(42);
    const mid = sidecar.slotMidFor(42) as string;
    expect(cam.hasSource).toBe(false);
    let onEnded: (() => void) | undefined;
    const track = {
      kind: "video",
      readyState: "live",
      addEventListener: (type: string, cb: () => void) => {
        if (type === "ended") onEnded = cb;
      },
    } as unknown as MediaStreamTrack;
    sidecar.deliverTrack(mid, track);
    expect(cam.hasSource).toBe(true);

    await client.unsubscribe(42);
    expect(cam.hasSource).toBe(false);

    await client.subscribe(42);
    expect(cam.hasSource).toBe(true);
    onEnded?.();
    expect(cam.hasSource).toBe(false);

    await client.unsubscribe(42);
    await client.subscribe(42);
    expect(cam.hasSource).toBe(true);
    client.disconnect();
    expect(cam.hasSource).toBe(false);
    expect(events).toEqual([true, false, true, false, true, false]);
  });

  it("reuses a freed slot for a different camera (the switch path)", async () => {
    const sidecar = new MockSidecar().withSlots(["a"]); // single slot
    const client = new KerbcastClient(
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
    const client = new KerbcastClient(
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

  /** Let the client's re-offer round trip and its follow-up sends settle. */
  async function settle(): Promise<void> {
    for (let i = 0; i < 5; i++) await new Promise((r) => setTimeout(r, 0));
  }

  function growable(sidecar: MockSidecar) {
    const renegotiate = vi.fn((o: { session: string; sdp: string; slots: number }) =>
      sidecar.renegotiate(o),
    );
    const client = new KerbcastClient(
      { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
      sidecar.createTransport(),
    );
    return { client, renegotiate };
  }

  it("adds slots with a re-offer when every slot is taken, keeping running feeds", async () => {
    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
    const { client, renegotiate } = growable(sidecar);
    await client.connect([], { slots: 1 });
    sidecar.open();
    const errors: { message: string }[] = [];
    client.on("error", (e) => errors.push(e));

    await client.subscribe(1);
    await client.subscribe(2);
    await settle();

    expect(renegotiate).toHaveBeenCalledOnce();
    expect(renegotiate.mock.calls[0][0]).toMatchObject({ session: "mock-session", slots: 5 });
    expect(sidecar.slotMids).toHaveLength(5);
    expect(sidecar.slotMidFor(1)).toBe("0");
    expect(sidecar.slotMidFor(2)).toBeDefined();
    expect(client.camera(2).waitingForSlot).toBe(false);
    expect(errors).toEqual([]);
  });

  it("marks a camera waiting when the pool cannot grow, and hands it the next freed slot", async () => {
    const sidecar = new MockSidecar().withSlots(["only"]);
    const client = new KerbcastClient(
      { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
      sidecar.createTransport(),
    );
    await client.connect([], { slots: 1 });
    sidecar.open();
    const waiting: boolean[] = [];
    client.camera(2).on("waiting", (w) => waiting.push(w));

    await client.subscribe(1);
    await client.subscribe(2);
    expect(client.camera(2).waitingForSlot).toBe(true);

    await client.unsubscribe(1);
    await settle();
    expect(sidecar.slotMidFor(2)).toBe("only");
    expect(client.camera(2).waitingForSlot).toBe(false);
    expect(waiting).toEqual([true, false]);
  });

  it("does not grow through a brokered negotiate that has no renegotiate", async () => {
    const sidecar = new MockSidecar().withSlots(["only"]).withRenegotiation();
    const client = new KerbcastClient(
      { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
      sidecar.createTransport(),
    );
    await client.connect([], { slots: 1 });
    sidecar.open();

    await client.subscribe(1);
    await client.subscribe(2);
    await settle();
    expect(sidecar.slotMids).toHaveLength(1);
    expect(client.camera(2).waitingForSlot).toBe(true);
  });

  it("stops growing at the sidecar's slot ceiling", async () => {
    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation(2);
    const { client } = growable(sidecar);
    await client.connect([], { slots: 1 });
    sidecar.open();

    await client.subscribe(1);
    await client.subscribe(2);
    await settle();
    await client.subscribe(3);
    await settle();

    expect(sidecar.slotMids).toHaveLength(2);
    expect(client.camera(2).waitingForSlot).toBe(false);
    expect(client.camera(3).waitingForSlot).toBe(true);
  });

  it("falls back to waiting when a re-offer fails", async () => {
    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
    const client = new KerbcastClient(
      {
        host: "h",
        port: 1,
        negotiate: (o) => sidecar.negotiate(o),
        renegotiate: () => Promise.reject(new Error("boom")),
      },
      sidecar.createTransport(),
    );
    await client.connect([], { slots: 1 });
    sidecar.open();
    const errors: { message: string }[] = [];
    client.on("error", (e) => errors.push(e));

    await client.subscribe(1);
    await client.subscribe(2);
    await settle();

    expect(client.camera(2).waitingForSlot).toBe(true);
    expect(errors.some((e) => /could not add video slots/.test(e.message))).toBe(true);
  });

  function noFreeSlot(flightId: number) {
    return {
      message: "no free slot",
      source: ErrorSource.Sidecar,
      flightId,
      code: ErrorCode.NoFreeSlot,
    };
  }

  it("treats a camera refused at connect like a refused subscribe", async () => {
    const fixed = new MockSidecar().withSlots(["0"]);
    const plain = new KerbcastClient(
      { host: "h", port: 1, negotiate: (o) => fixed.negotiate(o) },
      fixed.createTransport(),
    );
    await plain.connect([1, 2], { slots: 1 });
    fixed.open();
    const errors: unknown[] = [];
    plain.on("error", (e) => errors.push(e));
    fixed.fireError(noFreeSlot(2));
    expect(plain.camera(2).waitingForSlot).toBe(true);
    expect(errors).toHaveLength(1);

    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
    const { client, renegotiate } = growable(sidecar);
    await client.connect([1, 2], { slots: 1 });
    sidecar.open();
    sidecar.fireError(noFreeSlot(2));
    await settle();
    expect(renegotiate).toHaveBeenCalledOnce();
    expect(sidecar.slotMidFor(2)).toBeDefined();
  });

  it("batches refusals that land while a re-offer is in flight", async () => {
    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => (release = r));
    const renegotiate = vi.fn(async (o: { session: string; sdp: string; slots: number }) => {
      await gate;
      return sidecar.renegotiate(o);
    });
    const client = new KerbcastClient(
      { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
      sidecar.createTransport(),
    );
    await client.connect([], { slots: 1 });
    sidecar.open();

    for (const id of [1, 2, 3, 4, 5, 6, 7]) await client.subscribe(id);
    expect(renegotiate).toHaveBeenCalledOnce();
    release();
    await settle();

    expect(renegotiate).toHaveBeenCalledTimes(2);
    expect(renegotiate.mock.calls.map((c) => c[0].slots)).toEqual([5, 10]);
    for (const id of [1, 2, 3, 4, 5, 6, 7]) expect(sidecar.slotMidFor(id)).toBeDefined();
  });

  it("rolls back when a re-offer's answer is lost, and never uses the slots the sidecar grew", async () => {
    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
    const client = new KerbcastClient(
      {
        host: "h",
        port: 1,
        negotiate: (o) => sidecar.negotiate(o),
        renegotiate: async (o) => {
          await sidecar.renegotiate(o);
          throw new Error("response lost");
        },
      },
      sidecar.createTransport(),
    );
    await client.connect([], { slots: 1 });
    sidecar.open();
    const errors: { message: string; code?: ErrorCode }[] = [];
    client.on("error", (e) => errors.push(e));

    await client.subscribe(1);
    await client.subscribe(2);
    await settle();
    await client.subscribe(3);
    await settle();

    expect(sidecar.rollbacks).toBe(1);
    expect(sidecar.slotMids).toHaveLength(5);
    expect(sidecar.lastCommand("confirm-slots")).toBeUndefined();
    expect(sidecar.slotMidFor(2)).toBeUndefined();
    expect(sidecar.slotMidFor(3)).toBeUndefined();
    expect(client.camera(2).waitingForSlot).toBe(true);
    expect(client.camera(3).waitingForSlot).toBe(true);
    expect(errors.filter((e) => /could not add video slots/.test(e.message))).toHaveLength(1);
  });

  it("rolls back and confirms nothing when the browser refuses a re-offer's answer", async () => {
    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
    const base = sidecar.createTransport();
    let answers = 0;
    const transport = {
      createPeer: (...args: Parameters<typeof base.createPeer>) => {
        const peer = base.createPeer(...args);
        return {
          ...peer,
          setRemoteAnswer: async (sdp: string) => {
            if (++answers > 1) throw new Error("answer refused");
            await peer.setRemoteAnswer(sdp);
          },
        };
      },
    };
    const client = new KerbcastClient(
      {
        host: "h",
        port: 1,
        negotiate: (o) => sidecar.negotiate(o),
        renegotiate: (o) => sidecar.renegotiate(o),
      },
      transport,
    );
    await client.connect([], { slots: 1 });
    sidecar.open();

    await client.subscribe(1);
    await client.subscribe(2);
    await settle();

    expect(sidecar.rollbacks).toBe(1);
    expect(sidecar.lastCommand("confirm-slots")).toBeUndefined();
    expect(sidecar.slotMidFor(2)).toBeUndefined();
    expect(client.camera(2).waitingForSlot).toBe(true);
  });

  it("drops a grow whose peer was replaced while its answer was applied", async () => {
    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
    const base = sidecar.createTransport();
    let answers = 0;
    let release: () => void = () => {};
    const gate = new Promise<void>((r) => (release = r));
    const transport = {
      createPeer: (...args: Parameters<typeof base.createPeer>) => {
        const peer = base.createPeer(...args);
        return {
          ...peer,
          setRemoteAnswer: async (sdp: string) => {
            if (++answers === 2) await gate;
            await peer.setRemoteAnswer(sdp);
          },
        };
      },
    };
    const client = new KerbcastClient(
      {
        host: "h",
        port: 1,
        negotiate: (o) => sidecar.negotiate(o),
        renegotiate: (o) => sidecar.renegotiate(o),
      },
      transport,
    );
    await client.connect([], { slots: 1 });
    sidecar.open();
    await client.subscribe(1);
    await client.subscribe(2);
    await settle();
    expect(answers).toBe(2);

    await client.connect([], { slots: 1 });
    sidecar.open();
    const sent = sidecar.commands.length;
    release();
    await settle();

    expect(sidecar.lastCommand("confirm-slots")).toBeUndefined();
    expect(sidecar.commands.slice(sent).some((c) => c.type === "subscribe")).toBe(false);
  });

  it("treats a /renegotiate that never answers as a failed grow", async () => {
    vi.useFakeTimers();
    try {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      vi.spyOn(globalThis, "fetch").mockImplementation((url, init) => {
        if (String(url).endsWith("/offer")) {
          return Promise.resolve(
            new Response(JSON.stringify({ sdp: "v=0\r\n", cameras: [], session: "s" }), {
              status: 200,
              headers: { "Content-Type": "application/json" },
            }),
          );
        }
        return new Promise<Response>((_, reject) => {
          init?.signal?.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError")));
        });
      });
      const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
      await client.connect([], { slots: 1 });
      sidecar.open();
      const errors: { message: string }[] = [];
      client.on("error", (e) => errors.push(e));

      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(10_000);

      expect(sidecar.rollbacks).toBe(1);
      expect(client.camera(2).waitingForSlot).toBe(true);
      expect(errors.some((e) => /no answer within/.test(e.message))).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  describe("retrying a failed grow", () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    /** A transport over `sidecar` that counts the transceivers the client adds. */
    function countingTransport(sidecar: MockSidecar) {
      const base = sidecar.createTransport();
      const counts = { transceivers: 0 };
      const transport = {
        createPeer: (...args: Parameters<typeof base.createPeer>) => {
          const peer = base.createPeer(...args);
          return {
            ...peer,
            addRecvOnlyTransceiver: () => {
              counts.transceivers++;
              peer.addRecvOnlyTransceiver();
            },
          };
        },
      };
      return { transport, counts };
    }

    it("recovers the waiting cameras after a 503, without reconnecting", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      let renegotiates = 0;
      vi.spyOn(globalThis, "fetch").mockImplementation(async (url, init) => {
        const json = (body: unknown) =>
          new Response(JSON.stringify(body), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          });
        if (String(url).endsWith("/offer")) {
          return json({ sdp: "v=0\r\n", cameras: [], session: "s" });
        }
        if (++renegotiates === 1) return new Response("busy", { status: 503 });
        return json(await sidecar.renegotiate(JSON.parse(String(init?.body))));
      });
      const { transport, counts } = countingTransport(sidecar);
      const client = new KerbcastClient({ host: "h", port: 1 }, transport);
      const offers = vi.mocked(globalThis.fetch);
      offers.mockClear();
      await client.connect([], { slots: 1 });
      sidecar.open();

      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(0);
      expect(client.camera(2).waitingForSlot).toBe(true);
      expect(sidecar.slotMidFor(1)).toBe("0");

      await vi.advanceTimersByTimeAsync(2_000);
      expect(renegotiates).toBe(2);
      expect(sidecar.slotMidFor(1)).toBe("0");
      expect(sidecar.slotMidFor(2)).toBeDefined();
      expect(client.camera(2).waitingForSlot).toBe(false);
      expect(offers.mock.calls.filter(([u]) => String(u).endsWith("/offer"))).toHaveLength(1);
      expect(counts.transceivers).toBe(5);
    });

    it("recovers after a lost answer, re-offering the transceivers it rolled back", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      const renegotiate = vi.fn(async (o: { session: string; sdp: string; slots: number }) => {
        const answer = await sidecar.renegotiate(o);
        if (renegotiate.mock.calls.length === 1) throw new Error("response lost");
        return answer;
      });
      const { transport, counts } = countingTransport(sidecar);
      const client = new KerbcastClient(
        { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
        transport,
      );
      await client.connect([], { slots: 1 });
      sidecar.open();

      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(0);
      expect(sidecar.rollbacks).toBe(1);
      expect(client.camera(2).waitingForSlot).toBe(true);

      await vi.advanceTimersByTimeAsync(2_000);
      expect(renegotiate.mock.calls.map((c) => c[0].slots)).toEqual([5, 5]);
      expect(counts.transceivers).toBe(5);
      expect(sidecar.slotMids).toHaveLength(5);
      expect(sidecar.lastCommand("confirm-slots")?.content).toEqual({ slots: 5 });
      expect(sidecar.slotMidFor(1)).toBe("0");
      expect(sidecar.slotMidFor(2)).toBeDefined();
      expect(client.camera(2).waitingForSlot).toBe(false);
    });

    it("backs off between retries, then gives up after six failures", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      const calls: number[] = [];
      const renegotiate = vi.fn(async () => {
        calls.push(Date.now());
        throw new Error("down");
      });
      const client = new KerbcastClient(
        { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
        sidecar.createTransport(),
      );
      const errors: { message: string }[] = [];
      client.on("error", (e) => errors.push(e));
      await client.connect([], { slots: 1 });
      sidecar.open();
      await client.subscribe(1);
      const start = Date.now();
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(300_000);

      expect(calls.map((t) => t - start)).toEqual([0, 2_000, 7_000, 22_000, 52_000, 82_000]);
      /* The first failure and the giving up, nothing in between. */
      expect(errors.filter((e) => /could not add video slots/.test(e.message))).toHaveLength(2);
      expect(client.camera(2).waitingForSlot).toBe(true);
    });

    it("never retries an answer the browser refused", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      const base = sidecar.createTransport();
      let answers = 0;
      const transport = {
        createPeer: (...args: Parameters<typeof base.createPeer>) => {
          const peer = base.createPeer(...args);
          return {
            ...peer,
            setRemoteAnswer: async (sdp: string) => {
              if (++answers > 1) throw new Error("answer refused");
              await peer.setRemoteAnswer(sdp);
            },
          };
        },
      };
      const renegotiate = vi.fn((o: { session: string; sdp: string; slots: number }) =>
        sidecar.renegotiate(o),
      );
      const client = new KerbcastClient(
        { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
        transport,
      );
      await client.connect([], { slots: 1 });
      sidecar.open();
      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(120_000);
      expect(renegotiate).toHaveBeenCalledOnce();
      expect(client.camera(2).waitingForSlot).toBe(true);
    });

    it("starts afresh after a run of failures ends with nothing waiting", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      const calls: number[] = [];
      const renegotiate = vi.fn(async () => {
        calls.push(Date.now());
        throw new Error("down");
      });
      const client = new KerbcastClient(
        { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
        sidecar.createTransport(),
      );
      const errors: { message: string }[] = [];
      client.on("error", (e) => {
        if (/could not add video slots/.test(e.message)) errors.push(e);
      });
      await client.connect([], { slots: 1 });
      sidecar.open();
      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(10_000);
      expect(calls).toHaveLength(3);
      await client.unsubscribe(2);
      await vi.advanceTimersByTimeAsync(60_000);
      expect(calls).toHaveLength(3);

      const again = Date.now();
      await client.subscribe(3);
      await vi.advanceTimersByTimeAsync(2_000);
      expect(calls.slice(3).map((t) => t - again)).toEqual([0, 2_000]);
      expect(errors).toHaveLength(2);
    });

    it("stops retrying once the peer fails", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      const renegotiate = vi.fn(async () => {
        throw new Error("down");
      });
      const client = new KerbcastClient(
        { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
        sidecar.createTransport(),
      );
      await client.connect([], { slots: 1 });
      sidecar.open();
      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(0);
      sidecar.setConnectionState("failed");
      await vi.advanceTimersByTimeAsync(120_000);
      expect(renegotiate).toHaveBeenCalledOnce();
    });

    it("stops retrying once nothing waits", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      const renegotiate = vi.fn(async () => {
        throw new Error("down");
      });
      const client = new KerbcastClient(
        { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
        sidecar.createTransport(),
      );
      await client.connect([], { slots: 1 });
      sidecar.open();
      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(0);
      await client.unsubscribe(2);
      await vi.advanceTimersByTimeAsync(120_000);
      expect(renegotiate).toHaveBeenCalledOnce();
    });

    it("stops retrying on disconnect", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      const renegotiate = vi.fn(async () => {
        throw new Error("down");
      });
      const client = new KerbcastClient(
        { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o), renegotiate },
        sidecar.createTransport(),
      );
      await client.connect([], { slots: 1 });
      sidecar.open();
      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(0);
      client.disconnect();
      await vi.advanceTimersByTimeAsync(120_000);
      expect(renegotiate).toHaveBeenCalledOnce();
    });

    it("stops retrying when the sidecar refuses the re-offer", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      let renegotiates = 0;
      vi.spyOn(globalThis, "fetch").mockImplementation(async (url) => {
        if (String(url).endsWith("/offer")) {
          return new Response(JSON.stringify({ sdp: "v=0\r\n", cameras: [], session: "s" }), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          });
        }
        renegotiates++;
        return new Response("no", { status: 422 });
      });
      const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
      await client.connect([], { slots: 1 });
      sidecar.open();
      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(120_000);
      expect(renegotiates).toBe(1);
      expect(client.camera(2).waitingForSlot).toBe(true);
    });

    it("never grows over a brokered negotiate without renegotiate", async () => {
      const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
      const client = new KerbcastClient(
        { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
        sidecar.createTransport(),
      );
      await client.connect([], { slots: 1 });
      sidecar.open();
      await client.subscribe(1);
      await client.subscribe(2);
      await vi.advanceTimersByTimeAsync(120_000);
      expect(sidecar.rollbacks).toBe(0);
      expect(sidecar.slotMids).toHaveLength(1);
      expect(client.camera(2).waitingForSlot).toBe(true);
    });
  });

  it("confirms grown slots before subscribing into them", async () => {
    const sidecar = new MockSidecar().withSlots(["0"]).withRenegotiation();
    const { client } = growable(sidecar);
    await client.connect([], { slots: 1 });
    sidecar.open();
    await client.subscribe(1);
    await client.subscribe(2);
    await settle();
    const types = sidecar.commands.map((c) => c.type);
    const confirm = types.indexOf("confirm-slots");
    expect(confirm).toBeGreaterThan(-1);
    expect(types.lastIndexOf("subscribe")).toBeGreaterThan(confirm);
  });

  it("skips a destroyed waiter when a slot frees, and says no-free-slot once per waiter", async () => {
    const sidecar = new MockSidecar().withSlots(["only"]);
    for (const id of [1, 2, 3]) sidecar.addCamera({ flightId: id });
    const client = new KerbcastClient(
      { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
      sidecar.createTransport(),
    );
    await client.connect([], { slots: 1 });
    sidecar.open();
    const refusals: unknown[] = [];
    client.on("error", (e) => {
      if (e.code === ErrorCode.NoFreeSlot) refusals.push(e);
    });

    await client.subscribe(1);
    await client.subscribe(2);
    await client.subscribe(3);
    sidecar.fireError(noFreeSlot(2));
    expect(refusals).toHaveLength(2);

    sidecar.destroyCamera(2);
    await client.unsubscribe(1);
    await settle();
    expect(sidecar.slotMidFor(3)).toBe("only");
    expect(client.camera(2).waitingForSlot).toBe(false);
    expect(client.camera(3).waitingForSlot).toBe(false);
  });

  it("clears the waiting state when the camera is unsubscribed", async () => {
    const sidecar = new MockSidecar().withSlots(["only"]);
    const client = new KerbcastClient(
      { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
      sidecar.createTransport(),
    );
    await client.connect([], { slots: 1 });
    sidecar.open();

    await client.subscribe(1);
    await client.subscribe(2);
    expect(client.camera(2).waitingForSlot).toBe(true);
    await client.unsubscribe(2);
    expect(client.camera(2).waitingForSlot).toBe(false);
  });

  it("uses an injected negotiate (signaling seam) instead of HTTP /offer", async () => {
    const sidecar = new MockSidecar();
    const negotiate = vi.fn(
      (offer: { sdp: string; cameras: number[]; slots?: number }) =>
        sidecar.negotiate(offer),
    );
    const client = new KerbcastClient(
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
// BrowserKerbcastTransport: ICE gathering timeout
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

describe("BrowserKerbcastTransport: waitForIceComplete", () => {
  it("resolves immediately when already complete", async () => {
    const pc = makeFakeRTCPeerConnection("complete");
    // @ts-expect-error -- stub replaces the real constructor
    globalThis.RTCPeerConnection = class { constructor() { return pc; } };
    try {
      const transport = new BrowserKerbcastTransport({ iceGatheringTimeoutMs: 500 });
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
      const transport = new BrowserKerbcastTransport({ iceGatheringTimeoutMs: 2000 });
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
      const transport = new BrowserKerbcastTransport({ iceGatheringTimeoutMs: 2000 });
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

// ---------------------------------------------------------------------------
// KerbcastClient.inboundVideoStats
// ---------------------------------------------------------------------------

describe("BrowserKerbcastTransport: script-path passthrough", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("keeps a receiver's frame tap when its passthrough is renewed", async () => {
    const transceivers: { mid: string; receiver: { transform: unknown } }[] = [];
    const made: { options?: { port?: MessagePort } }[] = [];
    vi.stubGlobal("RTCRtpReceiver", { prototype: { transform: null } });
    vi.stubGlobal(
      "RTCRtpScriptTransform",
      class {
        constructor(_worker: unknown, readonly options?: { port?: MessagePort }) {
          made.push(this);
        }
      },
    );
    vi.stubGlobal("Worker", class { terminate() {} });
    vi.stubGlobal(
      "RTCPeerConnection",
      class {
        addTransceiver() {
          const t = { mid: String(transceivers.length), receiver: { transform: null as unknown } };
          transceivers.push(t);
          return t;
        }
        getTransceivers() {
          return transceivers;
        }
        close() {}
      },
    );
    const peer = new BrowserKerbcastTransport().createPeer([], { frameTaps: true });
    peer.addRecvOnlyTransceiver();
    const got: number[] = [];
    peer.setReceiverTap?.("0", (f) => got.push(f.rtpTimestamp));
    peer.setReceiverTransform?.("0", null);
    expect(made).toHaveLength(2);
    expect(transceivers[0].receiver.transform).toBe(made[1]);

    const port = made[1].options?.port as MessagePort;
    const tapOn = await new Promise((resolve) => {
      port.onmessage = (m) => resolve(m.data);
    });
    expect(tapOn).toBe(true);
    port.postMessage({ data: new ArrayBuffer(4), rtpTimestamp: 5 });
    await vi.waitFor(() => expect(got).toEqual([5]));
    peer.close();
    port.close();
  });
});

describe("KerbcastClient.inboundVideoStats", () => {
  it("returns empty map when disconnected", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    const stats = await client.inboundVideoStats();
    expect(stats.size).toBe(0);
  });

  it("returns empty map when the transport has no getStats", async () => {
    const { transport } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([42]));
    await client.connect([42]);
    // makeFakeTransport does not implement getStats, so the peer lacks it.
    const stats = await client.inboundVideoStats();
    expect(stats.size).toBe(0);
  });

  it("returns stats keyed by flightId in legacy mode (trackIdentifier match)", async () => {
    const sidecar = new MockSidecar();
    sidecar.addCamera({ flightId: 42 });
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([42])),
    );

    await client.connect([42]);
    sidecar.open();
    // Deliver a track with a known id so the client records it in flightByTrackId.
    const fakeTrack = { id: "track-42" } as MediaStreamTrack;
    sidecar.deliverTrack("0", fakeTrack);

    sidecar.setInboundStats(42, {
      packetsReceived: 1200,
      bytesReceived: 500_000,
      framesDecoded: 360,
      jitter: 0.002,
      framesPerSecond: 30,
    });

    const stats = await client.inboundVideoStats();
    expect(stats.size).toBe(1);
    const cam42 = stats.get(42);
    expect(cam42?.packetsReceived).toBe(1200);
    expect(cam42?.bytesReceived).toBe(500_000);
    expect(cam42?.framesDecoded).toBe(360);
    expect(cam42?.jitter).toBeCloseTo(0.002);
    expect(cam42?.framesPerSecond).toBe(30);
  });

  it("returns stats keyed by flightId in dynamic mode (mid match)", async () => {
    const sidecar = new MockSidecar().withSlots(["a"]);
    sidecar.addCamera({ flightId: 7 });
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );

    await client.connect([], { slots: 1 });
    sidecar.open();
    await client.subscribe(7);
    // Deliver a track; dynamic mode matches by mid, not trackIdentifier.
    sidecar.deliverTrack("a", {} as MediaStreamTrack);

    sidecar.setInboundStats(7, {
      packetsReceived: 800,
      bytesReceived: 200_000,
      framesDecoded: 240,
    });

    const stats = await client.inboundVideoStats();
    expect(stats.size).toBe(1);
    const cam7 = stats.get(7);
    expect(cam7?.packetsReceived).toBe(800);
    expect(cam7?.framesDecoded).toBe(240);
    // Fields not set default to 0 or undefined.
    expect(cam7?.framesPerSecond).toBeUndefined();
  });

  it("returns empty map after disconnect", async () => {
    const sidecar = new MockSidecar();
    sidecar.addCamera({ flightId: 5 });
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([5])),
    );

    await client.connect([5]);
    sidecar.open();
    sidecar.deliverTrack("0", { id: "track-5" } as MediaStreamTrack);
    sidecar.setInboundStats(5, { packetsReceived: 100 });

    client.disconnect();
    const stats = await client.inboundVideoStats();
    expect(stats.size).toBe(0);
  });

  it("throttleMainScreen defaults to false before hello", () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    expect(client.throttleMainScreen).toBe(false);
  });

  it("throttleMainScreen reflects the settings-state pushed on hello", async () => {
    const sidecar = new MockSidecar();
    /* Seed the mock sidecar with throttle on before opening. */
    sidecar.fireSettingsState({ throttleMainScreen: true });
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect();
    sidecar.open();
    expect(client.throttleMainScreen).toBe(true);
  });

  it("tracks inFlight from scene-state-changed and emits scene-change", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    const seen: (boolean | undefined)[] = [];
    client.on("scene-change", (v) => seen.push(v));
    await client.connect();
    sidecar.open();
    expect(client.inFlight).toBeUndefined();
    sidecar.fireSceneState(false);
    expect(client.inFlight).toBe(false);
    sidecar.fireSceneState(true);
    expect(client.inFlight).toBe(true);
    expect(seen).toEqual([false, true]);
  });

  it("keeps when the sidecar was last heard from across a disconnect", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect();
    expect(client.lastContactAt).toBeNull();
    sidecar.open();
    const before = Date.now();
    sidecar.fireSceneState(true);
    const heard = client.lastContactAt;
    expect(heard).not.toBeNull();
    expect(heard!).toBeGreaterThanOrEqual(before);
    client.disconnect();
    expect(client.lastContactAt).toBe(heard);
  });

  it("reports a silence when messages held back by a paused page arrive", async () => {
    vi.useFakeTimers();
    try {
      const sidecar = new MockSidecar();
      const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
      vi.spyOn(globalThis, "fetch").mockImplementation(() =>
        Promise.resolve(MockSidecar.makeOfferResponse([])),
      );
      const gaps: ContactGap[] = [];
      client.on("contact-gap", (g) => gaps.push(g));
      await client.connect();
      sidecar.open();
      sidecar.fireSceneState(true);
      const quietFrom = Date.now();
      vi.advanceTimersByTime(CONTACT_GAP_MS - 1);
      sidecar.fireSceneState(true);
      expect(gaps).toEqual([]);

      vi.setSystemTime(Date.now() + 75_000);
      sidecar.fireSceneState(true);
      sidecar.fireSceneState(true);
      expect(gaps).toEqual([{ from: quietFrom + CONTACT_GAP_MS - 1, to: Date.now() }]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("setThrottleMainScreen sends the correct command", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect();
    sidecar.open();

    await client.setThrottleMainScreen(true);

    const cmd = sidecar.lastCommand("set-throttle-main-screen");
    expect(cmd).toBeDefined();
    expect(cmd?.content.enabled).toBe(true);
  });

  it("settings-change event fires with SettingsState payload", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect();
    sidecar.open();

    const events: { throttleMainScreen: boolean }[] = [];
    client.on("settings-change", (p) => events.push(p));

    await client.setThrottleMainScreen(true);

    expect(events.length).toBeGreaterThan(0);
    expect(events[events.length - 1].throttleMainScreen).toBe(true);
    expect(client.throttleMainScreen).toBe(true);
  });

  it("clock.captureUt is null before any settings-state", () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    expect(client.clock.captureUt).toBeNull();
    expect(client.clock.epoch).toBe(0);
    expect(client.clock.warpRate).toBe(1);
  });

  it("clock reflects a fired capture clock", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect();
    sidecar.open();

    sidecar.fireSettingsState({
      throttleMainScreen: false,
      captureUt: 12345.5,
      captureEpoch: 7,
      timeWarpRate: 4,
    });

    expect(client.clock).toEqual({
      captureUt: 12345.5,
      epoch: 7,
      warpRate: 4,
    });
  });

  it("clock warpRate defaults to 1 and epoch is retained when fields are absent", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect();
    sidecar.open();

    sidecar.fireSettingsState({
      throttleMainScreen: false,
      captureUt: 100,
      captureEpoch: 5,
      timeWarpRate: 50,
    });
    /* A later push omitting epoch/warp: epoch retained, warp back to 1. */
    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 101 });

    expect(client.clock.captureUt).toBe(101);
    expect(client.clock.epoch).toBe(5);
    expect(client.clock.warpRate).toBe(1);
  });

  it("MockSidecar.throttleMainScreen reflects set-throttle-main-screen command", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect();
    sidecar.open();

    expect(sidecar.throttleMainScreen).toBe(false);
    await client.setThrottleMainScreen(true);
    expect(sidecar.throttleMainScreen).toBe(true);
    await client.setThrottleMainScreen(false);
    expect(sidecar.throttleMainScreen).toBe(false);
  });

  it("fireSettingsState pushes settings-change to the client", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient(
      { host: "h", port: 1 },
      sidecar.createTransport(),
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    /* Register before open() so the open-time settings-state is also captured. */
    const events: { throttleMainScreen: boolean }[] = [];
    client.on("settings-change", (p) => events.push(p));

    await client.connect();
    sidecar.open();

    /* One event from open() (false), then one more from fireSettingsState (true). */
    expect(events).toHaveLength(1);
    expect(events[0].throttleMainScreen).toBe(false);

    sidecar.fireSettingsState({ throttleMainScreen: true });
    expect(events).toHaveLength(2);
    expect(events[1].throttleMainScreen).toBe(true);
    expect(client.throttleMainScreen).toBe(true);
  });
});

describe("KerbcastClient frame UT", () => {
  async function connected() {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    await client.connect();
    sidecar.open();
    return { sidecar, client };
  }

  /** A video element whose frame callbacks the test fires by hand. */
  function fakeVideo() {
    const callbacks = new Map<number, (now: number, meta: object) => void>();
    let next = 1;
    const video = {
      requestVideoFrameCallback: (cb: (now: number, meta: object) => void) => {
        callbacks.set(next, cb);
        return next++;
      },
      cancelVideoFrameCallback: (id: number) => callbacks.delete(id),
    } as unknown as HTMLVideoElement;
    const present = (meta: object, now = 0) => {
      const pending = [...callbacks.values()];
      callbacks.clear();
      for (const cb of pending) cb(now, meta);
    };
    return { video, present, armed: () => callbacks.size };
  }

  it("is null with neither a stamp nor a clock", async () => {
    const { client } = await connected();
    expect(client.frameUt(7, 1234)).toBeNull();
  });

  it("is exact for a stamped RTP timestamp and estimated otherwise", async () => {
    const { sidecar, client } = await connected();
    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 500, captureEpoch: 2 });
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 90_000, captureUt: 498.25, captureEpoch: 2 });

    expect(client.frameUt(7, 90_000)).toEqual({ ut: 498.25, epoch: 2, source: "exact" });
    expect(client.frameUt(7, 93_000)?.source).toBe("estimated");
    expect(client.frameUt(8, 90_000)?.source).toBe("estimated");
    expect(client.frameUt(7)?.source).toBe("estimated");
  });

  it("forgets stamps on reconnect, since RTP timestamps restart per peer", async () => {
    const { sidecar, client } = await connected();
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 1, captureUt: 10, captureEpoch: 0 });
    expect(client.frameUt(7, 1)?.source).toBe("exact");
    await client.connect();
    expect(client.frameUt(7, 1)).toBeNull();
  });

  it("watchDisplayedFrames reports each presented frame with its source", async () => {
    const { sidecar, client } = await connected();
    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 500 });
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 3000, captureUt: 499.5, captureEpoch: 0 });
    const { video, present, armed } = fakeVideo();
    const frames: unknown[] = [];
    const stop = client.watchDisplayedFrames(7, video, (f) => frames.push(f));

    present({ rtpTimestamp: 3000, expectedDisplayTime: 42 });
    present({ rtpTimestamp: 6000, expectedDisplayTime: 75 });
    expect(frames).toEqual([
      { ut: 499.5, epoch: 0, source: "exact", flightId: 7, rtpTimestamp: 3000, presentedAt: 42 },
      /* Unstamped: bounded by the last exactly identified frame. */
      { ut: 499.5, epoch: 0, source: "estimated", flightId: 7, rtpTimestamp: 3000, presentedAt: 75 },
    ]);

    stop();
    expect(armed()).toBe(0);
  });

  it("watchDisplayedFrames resolves noise-pipeline frames and keeps inexact ones estimated", async () => {
    const { sidecar, client } = await connected();
    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 500 });
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 3000, captureUt: 499.5, captureEpoch: 0 });
    const handle = client.camera(7) as unknown as {
      _displayedSourceFrame(m: object): { rtpTimestamp: number | null; exact: boolean } | null;
    };
    const resolve = vi.spyOn(handle, "_displayedSourceFrame");
    const { video, present } = fakeVideo();
    const frames: unknown[] = [];
    const stop = client.watchDisplayedFrames(7, video, (f) => frames.push(f));

    resolve.mockReturnValueOnce({ rtpTimestamp: 3000, exact: true });
    present({ mediaTime: 1.5, expectedDisplayTime: 42 });
    resolve.mockReturnValueOnce({ rtpTimestamp: 3000, exact: false });
    present({ presentationTime: 70, expectedDisplayTime: 75 });
    expect(resolve).toHaveBeenNthCalledWith(1, video, { mediaTime: 1.5, expectedDisplayTime: 42 });
    expect(frames).toEqual([
      { ut: 499.5, epoch: 0, source: "exact", flightId: 7, rtpTimestamp: 3000, presentedAt: 42 },
      { ut: 499.5, epoch: 0, source: "estimated", flightId: 7, rtpTimestamp: 3000, presentedAt: 75 },
    ]);
    stop();
  });

  it("watchDisplayedFrames estimates unidentified frames from a stamp old enough to be on screen", async () => {
    const { sidecar, client } = await connected();
    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 500 });
    const now = vi.spyOn(performance, "now");
    now.mockReturnValue(1000);
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 3000, captureUt: 499.5, captureEpoch: 0 });
    now.mockReturnValue(1100);
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 6000, captureUt: 499.6, captureEpoch: 0 });
    now.mockRestore();
    const { video, present } = fakeVideo();
    const frames: { ut: number; source: string; rtpTimestamp: number | null }[] = [];
    const stop = client.watchDisplayedFrames(7, video, (f) => frames.push(f));

    /* No rtpTimestamp in the metadata and no pipeline: never exact, never newer than the screen. */
    present({ expectedDisplayTime: 1200 });
    present({ expectedDisplayTime: 1260 });
    /* A presented rtp the sidecar never stamped is estimated the same way. */
    present({ rtpTimestamp: 9000, expectedDisplayTime: 1300 });
    expect(frames.map((f) => [f.ut, f.source, f.rtpTimestamp])).toEqual([
      [499.5, "estimated", 3000],
      [499.6, "estimated", 6000],
      [499.6, "estimated", 6000],
    ]);
    stop();
  });

  it("watchDisplayedFrames distrusts a pipeline identification that runs backwards", async () => {
    const { sidecar, client } = await connected();
    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 500 });
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 3000, captureUt: 499.5, captureEpoch: 0 });
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 6000, captureUt: 499.6, captureEpoch: 0 });
    const handle = client.camera(7) as unknown as {
      _displayedSourceFrame(v: unknown, m: object): { rtpTimestamp: number | null; exact: boolean } | null;
    };
    const resolve = vi.spyOn(handle, "_displayedSourceFrame");
    const { video, present } = fakeVideo();
    const frames: { ut: number; source: string }[] = [];
    const stop = client.watchDisplayedFrames(7, video, (f) => frames.push(f));

    resolve.mockReturnValueOnce({ rtpTimestamp: 6000, exact: true });
    present({ mediaTime: 2, expectedDisplayTime: 100 });
    resolve.mockReturnValueOnce({ rtpTimestamp: 3000, exact: true });
    present({ mediaTime: 1.9, expectedDisplayTime: 133 });
    resolve.mockReturnValueOnce(null);
    present({ mediaTime: 2.1, expectedDisplayTime: 166 });
    expect(frames.map((f) => [f.ut, f.source])).toEqual([
      [499.6, "exact"],
      [499.6, "estimated"],
      [499.6, "estimated"],
    ]);
    stop();
  });

  it("watchDisplayedFrames falls back to timed estimates without frame callbacks", async () => {
    vi.useFakeTimers();
    try {
      const { sidecar, client } = await connected();
      sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 500 });
      const frames: { source: string; rtpTimestamp: number | null }[] = [];
      const stop = client.watchDisplayedFrames(7, {} as HTMLVideoElement, (f) => frames.push(f));
      vi.advanceTimersByTime(250);
      stop();
      vi.advanceTimersByTime(500);
      expect(frames).toHaveLength(2);
      expect(frames.every((f) => f.source === "estimated" && f.rtpTimestamp === null)).toBe(true);
    } finally {
      vi.useRealTimers();
    }
  });

  it("watchDisplayedFrames carries each frame's anchors, fov and size from its own stamp", async () => {
    const { sidecar, client } = await connected();
    const port = {
      id: "port",
      x: 320,
      y: 180,
      inFrame: true,
      behind: false,
      distanceM: 40,
      apparentRadiusPx: 9,
      angleOffAxis: 0.2,
      forward: [0, 0, -1],
      up: [0, 1, 0],
    };
    sidecar.fireFrameStamp({
      flightId: 7,
      rtpTimestamp: 3000,
      captureUt: 499.5,
      captureEpoch: 0,
      fov: 30,
      width: 640,
      height: 360,
      anchors: [port],
    });
    sidecar.fireFrameStamp({ flightId: 7, rtpTimestamp: 6000, captureUt: 499.6, captureEpoch: 0 });
    const { video, present } = fakeVideo();
    const frames: DisplayedFrameUt[] = [];
    const stop = client.watchDisplayedFrames(7, video, (f) => frames.push(f));

    present({ rtpTimestamp: 3000, expectedDisplayTime: 10 });
    present({ rtpTimestamp: 9000, expectedDisplayTime: 20 });
    present({ rtpTimestamp: 6000, expectedDisplayTime: 30 });
    expect(frames[0]).toMatchObject({ source: "exact", anchors: [port], fov: 30, frameWidth: 640, frameHeight: 360 });
    /* An unstamped frame reuses the last exact frame, anchors and all: they describe that picture. */
    expect(frames[1]).toMatchObject({ source: "estimated", rtpTimestamp: 3000, anchors: [port] });
    /* A stamp without anchors gives a frame without the key, not stale anchors. */
    expect(frames[2].source).toBe("exact");
    expect("anchors" in frames[2]).toBe(false);
    stop();
  });
});

describe("KerbcastClient force and size hold", () => {
  async function connected() {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
    await client.connect();
    captured.dc?._open();
    const sent = () =>
      (captured.dc?.sent ?? [])
        .map((m) => JSON.parse(m))
        .filter((m) => ["set-size-hold", "set-force-full-resolution", "resume"].includes(m.type));
    const sayHello = (extra: Record<string, unknown>) =>
      captured.dc?._msg(
        JSON.stringify({ type: "hello", content: { sidecarVersion: "1.12.0", encoderBackend: "x", ...extra } }),
      );
    const clear = () => {
      captured.dc!.sent.length = 0;
    };
    return { client, sent, sayHello, clear };
  }

  it("never sends a size hold to a sidecar whose hello does not offer it", async () => {
    const { client, sent, sayHello } = await connected();
    sayHello({});
    await client.camera(4).setSizeHold(true);
    await client.camera(4).setForceFullResolution(true);
    expect(sent()).toEqual([
      { type: "set-force-full-resolution", content: { flightId: 4, force: true } },
    ]);
  });

  it("sends the forces and holds still in place to every new connection", async () => {
    const { client, sent, sayHello, clear } = await connected();
    sayHello({ sizeHold: true });
    await client.camera(4).setSizeHold(true);
    await client.camera(4).setForceFullResolution(true);
    await client.camera(5).setSizeHold(true);
    await client.camera(5).setSizeHold(false);
    clear();
    sayHello({ sizeHold: true });
    expect(sent()).toEqual([
      { type: "set-force-full-resolution", content: { flightId: 4, force: true } },
      { type: "set-size-hold", content: { flightId: 4, hold: true } },
    ]);
    clear();
    sayHello({});
    expect(sent()).toEqual([
      { type: "set-force-full-resolution", content: { flightId: 4, force: true } },
    ]);
  });

  it("names itself first to a sidecar that takes resumes, the same name on every connection", async () => {
    const { client, sent, sayHello, clear } = await connected();
    sayHello({ sizeHold: true, resume: true });
    const [first] = sent();
    expect(first).toMatchObject({ type: "resume", content: { forced: [], held: [] } });
    expect(typeof first.content.clientId).toBe("string");

    await client.camera(4).setForceFullResolution(true);
    await client.camera(4).setSizeHold(true);
    clear();
    sayHello({ sizeHold: true, resume: true });
    expect(sent()).toEqual([
      { type: "resume", content: { clientId: first.content.clientId, forced: [4], held: [4] } },
      { type: "set-force-full-resolution", content: { flightId: 4, force: true } },
      { type: "set-size-hold", content: { flightId: 4, hold: true } },
    ]);
  });

  it("keeps its name to itself with a sidecar that does not take resumes", async () => {
    const { sent, sayHello } = await connected();
    sayHello({ sizeHold: true });
    expect(sent()).toEqual([]);
  });
});

describe("KerbcastClient anchors", () => {
  function ship(id: string) {
    return { id, kind: AnchorKind.Vessel, vessel: 42 };
  }

  async function connectedTo(hello: Record<string, unknown>) {
    const { transport, captured } = makeFakeTransport();
    const client = new KerbcastClient({ host: "h", port: 1 }, transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer());
    await client.connect();
    captured.dc?._open();
    const sent = (type: string) =>
      (captured.dc?.sent ?? []).map((m) => JSON.parse(m)).filter((m) => m.type === type);
    const sayHello = () =>
      captured.dc?._msg(
        JSON.stringify({ type: "hello", content: { sidecarVersion: "1.12.0", encoderBackend: "x", ...hello } }),
      );
    const push = (msg: object) => captured.dc?._msg(JSON.stringify(msg));
    const list = (...flightIds: number[]) =>
      push({ type: "camera-snapshot", content: { cameras: flightIds.map(listedCamera) } });
    return { client, sent, sayHello, push, list };
  }

  function listedCamera(flightId: number) {
    return {
      flightId, kind: "part", partName: "", partTitle: "", cameraName: "", vesselName: "",
      layers: [], operatorLayers: [], renderWidth: 64, renderHeight: 64, operatorWidth: 64, operatorHeight: 64,
      supportsZoom: false, fov: 30, fovMin: 30, fovMax: 30, supportsPan: false, panYaw: 0, panPitch: 0,
      panYawMin: 0, panYawMax: 0, panPitchMin: 0, panPitchMax: 0,
    };
  }

  it("asks for anchor results only from a sidecar that offers anchors", async () => {
    const { sent, sayHello } = await connectedTo({ frameStamps: true, anchors: true });
    sayHello();
    expect(sent("set-frame-stamps")).toEqual([
      { type: "set-frame-stamps", content: { enabled: true, anchors: true } },
    ]);
  });

  it("asks for per-frame command state from a sidecar that offers it", async () => {
    const { client, sent, sayHello } = await connectedTo({ frameStamps: true, anchors: true, frameCommand: true });
    sayHello();
    expect(sent("set-frame-stamps")).toEqual([
      { type: "set-frame-stamps", content: { enabled: true, anchors: true, command: true } },
    ]);
    expect(client.commandCentreAt(1)).toBeNull();
  });

  it("holds a command camera's targets list to the displayed UT, and shows none undisplayed under a lock", async () => {
    const { client, sayHello, push } = await connectedTo({ frameStamps: true, frameCommand: true });
    sayHello();
    const centre = (ut: number, rangeM: number) => ({
      stationId: "ground:KSC", nodeName: "KSC", siteKey: "KSC", siteKind: "station", vantagePersistentId: 0,
      displayName: "KSC", body: "Kerbin", latitude: 0, longitude: 0, altitude: 0,
      subjectPersistentId: 42, rangeM, trackingLevel: 3, targets: [], created: false, ut,
    });
    const camera = (ut: number, rangeM: number, owner?: string) => ({
      flightId: 9, kind: "command", partName: "", partTitle: "", cameraName: "", vesselName: "",
      layers: [], operatorLayers: [], renderWidth: 64, renderHeight: 64, operatorWidth: 64, operatorHeight: 64,
      supportsZoom: false, fov: 30, fovMin: 30, fovMax: 30, supportsPan: false, panYaw: 0, panPitch: 0,
      panYawMin: 0, panYawMax: 0, panPitchMin: 0, panPitchMax: 0,
      commandCentre: centre(ut, rangeM),
      commandTrack: { ut, hasFix: false, occluded: false, occluder: "", sunlit: false, frameHeightPx: 0, framingFits: true, events: [], ...(owner ? { authority: { owner } } : {}) },
    });
    push({ type: "camera-snapshot", content: { cameras: [camera(100, 1000)] } });
    push({ type: "camera-state-changed", content: { state: camera(200, 2000) } });
    expect(client.commandCentreAt(9, 99)).toBeNull();
    expect(client.commandCentreAt(9, 150)?.rangeM).toBe(1000);
    expect(client.commandCentreAt(9)?.rangeM).toBe(2000);
    push({ type: "camera-state-changed", content: { state: camera(300, 3000, "mission") } });
    expect(client.commandCentreAt(9)).toBeNull();
    expect(client.commandCentreAt(9, 250)?.rangeM).toBe(2000);
    push({ type: "camera-snapshot", content: { cameras: [] } });
    expect(client.commandCentreAt(9, 250)).toBeNull();
  });

  it("keeps anchors set before hello and sends them once the sidecar offers anchors and lists the camera", async () => {
    const { client, sent, sayHello, list } = await connectedTo({ frameStamps: true, anchors: true });
    await client.setAnchors(7, [ship("a")]);
    expect(sent("set-anchors")).toEqual([]);
    sayHello();
    expect(sent("set-anchors")).toEqual([]);
    list(7);
    expect(sent("set-anchors")).toEqual([
      { type: "set-anchors", content: { flightId: 7, anchors: [ship("a")] } },
    ]);
    expect(client.anchorsSupported).toBe(true);

    await client.setAnchors(7, null);
    expect(sent("set-anchors").at(-1)).toEqual({ type: "set-anchors", content: { flightId: 7 } });
    expect(client.anchorsFor(7)).toEqual([]);
    /* Clearing what is already clear sends nothing. */
    const before = sent("set-anchors").length;
    await client.setAnchors(7, []);
    expect(sent("set-anchors")).toHaveLength(before);
  });

  it("never sends anchors to a sidecar that predates them", async () => {
    const { client, sent, sayHello } = await connectedTo({ frameStamps: true });
    sayHello();
    await client.setAnchors(7, [ship("a")]);
    expect(sent("set-anchors")).toEqual([]);
    expect(sent("set-frame-stamps")).toEqual([{ type: "set-frame-stamps", content: { enabled: true } }]);
    expect(client.anchorsFor(7)).toEqual([ship("a")]);
  });

  it("refuses more than the cap and repeated ids before sending", async () => {
    const { client, sayHello } = await connectedTo({ frameStamps: true, anchors: true });
    sayHello();
    expect(client.maxAnchorsPerCamera).toBe(MAX_ANCHORS_PER_CAMERA);
    const seventeen = Array.from({ length: MAX_ANCHORS_PER_CAMERA + 1 }, (_, i) => ship(`a${i}`));
    await expect(client.setAnchors(7, seventeen)).rejects.toThrow(RangeError);
    await expect(client.setAnchors(7, [ship("a"), ship("a")])).rejects.toThrow(/twice/);
    expect(client.anchorsFor(7)).toEqual([]);
  });

  it("takes the anchor cap the sidecar announces", async () => {
    const { client, sayHello } = await connectedTo({ frameStamps: true, anchors: true, maxAnchorsPerCamera: 2 });
    sayHello();
    expect(client.maxAnchorsPerCamera).toBe(2);
    await expect(client.setAnchors(7, [ship("a"), ship("b"), ship("c")])).rejects.toThrow(/at most 2/);
  });

  it("holds anchors for a camera the sidecar does not list until it appears", async () => {
    const { client, sent, sayHello, list, push } = await connectedTo({ frameStamps: true, anchors: true });
    sayHello();
    list(1);
    await client.setAnchors(7, [ship("a")]);
    expect(sent("set-anchors")).toEqual([]);
    push({ type: "camera-state-changed", content: { state: listedCamera(7) } });
    expect(sent("set-anchors")).toEqual([
      { type: "set-anchors", content: { flightId: 7, anchors: [ship("a")] } },
    ]);
    /* Gone and back on one connection: the sidecar kept them. */
    list(1);
    list(1, 7);
    expect(sent("set-anchors")).toHaveLength(1);
  });

  it("applies what it has of a split snapshot when another message cuts it short", async () => {
    const { client, sayHello, push } = await connectedTo({ frameStamps: true });
    sayHello();
    push({ type: "camera-snapshot", content: { cameras: [listedCamera(1)], follows: 3 } });
    push({ type: "camera-state-changed", content: { state: listedCamera(2) } });
    expect(client.cameras).toEqual([]);
    push({ type: "ping" });
    expect(client.cameras.map((c) => c.flightId)).toEqual([1, 2]);
  });

  it("sends no clear for a camera the sidecar no longer lists", async () => {
    const { client, sent, sayHello, list } = await connectedTo({ frameStamps: true, anchors: true });
    sayHello();
    list(7);
    await client.setAnchors(7, [ship("a")]);
    list(8);
    await client.setAnchors(7, null);
    expect(sent("set-anchors")).toHaveLength(1);
    expect(client.anchorsFor(7)).toEqual([]);
  });

  it("does not send anchors held from an earlier connection for a camera that has gone", async () => {
    const { client, sent, sayHello, list } = await connectedTo({ frameStamps: true, anchors: true });
    sayHello();
    list(7);
    await client.setAnchors(7, [ship("a")]);
    expect(sent("set-anchors")).toHaveLength(1);
    /* The sidecar restarts without camera 7. */
    sayHello();
    list(1);
    expect(sent("set-anchors")).toHaveLength(1);
    expect(client.anchorsFor(7)).toEqual([ship("a")]);
    /* Clearing anchors this sidecar never had sends nothing. */
    await client.setAnchors(7, null);
    expect(sent("set-anchors")).toHaveLength(1);
  });

  it("applies a snapshot split across messages once, when its last camera arrives", async () => {
    const { client, sayHello, push } = await connectedTo({ frameStamps: true });
    sayHello();
    const changes: number[][] = [];
    client.on("cameras-change", (cams) => changes.push(cams.map((c) => c.flightId)));
    push({ type: "camera-snapshot", content: { cameras: [listedCamera(1), listedCamera(2)], follows: 2 } });
    push({ type: "camera-state-changed", content: { state: listedCamera(3) } });
    expect(changes).toEqual([]);
    push({ type: "camera-state-changed", content: { state: listedCamera(4) } });
    expect(changes).toEqual([[1, 2, 3, 4]]);
    expect(client.cameras.map((c) => c.flightId)).toEqual([1, 2, 3, 4]);
    /* Later state changes apply one by one again. */
    push({ type: "camera-state-changed", content: { state: listedCamera(5) } });
    expect(changes.at(-1)).toEqual([1, 2, 3, 4, 5]);
  });

  it("sends its anchors again on every new connection", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcastClient({ host: "h", port: 1 }, sidecar.createTransport());
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(MockSidecar.makeOfferResponse([])),
    );
    sidecar.addCamera({ flightId: 7 });
    await client.connect();
    sidecar.open();
    await client.setAnchors(7, [ship("a")]);
    expect(sidecar.anchorsFor(7)).toEqual([ship("a")]);

    const fresh = new MockSidecar();
    fresh.addCamera({ flightId: 7 });
    const again = new KerbcastClient({ host: "h", port: 1 }, fresh.createTransport());
    await again.setAnchors(7, [ship("b")]);
    await again.connect();
    fresh.open();
    expect(fresh.anchorsFor(7)).toEqual([ship("b")]);
  });
});

describe("KerbcastCameraHandle.setEncodedTransform", () => {
  type Path = "script" | "streams" | null;

  /* The fake transport plus an encoded-transform probe and an install log. */
  function makeTransformTransport(probe: (optedIn: boolean) => Path) {
    const base = makeFakeTransport();
    const installs: { mid: string; transform: unknown }[] = [];
    const peerOpts: (KerbcastPeerOptions | undefined)[] = [];
    const transport: KerbcastTransport = {
      createPeer: (ice, opts) => {
        peerOpts.push(opts);
        return {
          ...base.transport.createPeer(ice),
          setReceiverTransform: (mid, transform) => {
            installs.push({ mid, transform });
          },
        };
      },
      encodedTransformPath: (opts) => probe(opts.encodedTransforms === true),
    };
    return { transport, captured: base.captured, installs, peerOpts };
  }

  function slotMap(captured: { dc?: FakeChannel }, mid: string, flightId: number | null) {
    captured.dc?._msg(JSON.stringify({ type: "slot-map", content: { mid, flightId } }));
  }

  /* Each call yields a distinct instance so tests can tell fresh ones apart. */
  function countingFactory() {
    const made: { flightId: number; path: string; n: number }[] = [];
    const factory = vi.fn((ctx: { flightId: number; path: string }) => {
      const t = { ...ctx, n: made.length };
      made.push(t);
      return t as unknown as TransformStream<RTCEncodedVideoFrame, RTCEncodedVideoFrame>;
    });
    return { factory, made };
  }

  const optedInScript = (optedIn: boolean): Path => (optedIn ? "script" : null);

  async function connectDynamic() {
    const t = makeTransformTransport(optedInScript);
    const client = new KerbcastClient({ host: "h", port: 1, encodedTransforms: true }, t.transport);
    vi.spyOn(globalThis, "fetch").mockImplementation(() => Promise.resolve(fakeAnswer([])));
    await client.connect([], { slots: 2 });
    return { ...t, client };
  }

  it("installs a fresh instance when the camera is bound to a slot", async () => {
    const { client, captured, installs } = await connectDynamic();
    const { factory, made } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    expect(installs).toHaveLength(0);

    slotMap(captured, "0", 7);
    expect(factory).toHaveBeenCalledWith({ flightId: 7, path: "script" });
    expect(installs).toEqual([{ mid: "0", transform: made[0] }]);
  });

  it("installs at once when set on a camera that is already bound", async () => {
    const { client, captured, installs } = await connectDynamic();
    slotMap(captured, "1", 7);
    const { factory, made } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    expect(installs).toEqual([{ mid: "1", transform: made[0] }]);
  });

  it("keeps the instance when the slot's track arrives", async () => {
    const { client, captured, installs } = await connectDynamic();
    const { factory } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    slotMap(captured, "0", 7);
    captured.onTrack?.({ kind: "video" } as MediaStreamTrack, 0, "0");
    expect(client.camera(7).mediaStream).not.toBeNull();
    expect(factory).toHaveBeenCalledOnce();
    expect(installs).toHaveLength(1);
  });

  it("moves to the new slot with a fresh instance and clears the old one", async () => {
    const { client, captured, installs } = await connectDynamic();
    const { factory, made } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    slotMap(captured, "0", 7);
    slotMap(captured, "1", 7);
    slotMap(captured, "0", null);
    expect(installs).toEqual([
      { mid: "0", transform: made[0] },
      { mid: "1", transform: made[1] },
      { mid: "0", transform: null },
    ]);
  });

  it("swaps instances when another camera takes the slot", async () => {
    const { client, captured, installs } = await connectDynamic();
    const a = countingFactory();
    const b = countingFactory();
    client.camera(1).setEncodedTransform(a.factory);
    client.camera(2).setEncodedTransform(b.factory);
    slotMap(captured, "0", 1);
    slotMap(captured, "0", 2);
    expect(installs).toEqual([
      { mid: "0", transform: a.made[0] },
      { mid: "0", transform: null },
      { mid: "0", transform: b.made[0] },
    ]);
  });

  it("null detaches, and a new factory replaces the instance", async () => {
    const { client, captured, installs } = await connectDynamic();
    const first = countingFactory();
    const second = countingFactory();
    const cam = client.camera(7);
    slotMap(captured, "0", 7);
    cam.setEncodedTransform(first.factory);
    cam.setEncodedTransform(second.factory);
    cam.setEncodedTransform(null);
    expect(installs).toEqual([
      { mid: "0", transform: first.made[0] },
      { mid: "0", transform: null },
      { mid: "0", transform: second.made[0] },
      { mid: "0", transform: null },
    ]);
  });

  function snapshot(captured: { dc?: FakeChannel }, cameras: unknown[]) {
    captured.dc?._msg(JSON.stringify({ type: "camera-snapshot", content: { cameras } }));
  }

  it("gives a camera back after a scene change a fresh instance on the slot it kept", async () => {
    const { client, captured, installs } = await connectDynamic();
    captured.dc?._open();
    const { factory, made } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    snapshot(captured, [fakeCameraState(7)]);
    slotMap(captured, "0", 7);
    snapshot(captured, []);
    snapshot(captured, [fakeCameraState(7)]);
    expect(installs).toEqual([
      { mid: "0", transform: made[0] },
      { mid: "0", transform: made[1] },
    ]);
  });

  it("gives a camera back from destroyed a fresh instance", async () => {
    const { client, captured, installs } = await connectDynamic();
    captured.dc?._open();
    const { factory, made } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    snapshot(captured, [fakeCameraState(7)]);
    slotMap(captured, "0", 7);
    const changed = (lifecycle: string) =>
      captured.dc?._msg(
        JSON.stringify({ type: "camera-state-changed", content: { state: fakeCameraState(7, { lifecycle }) } }),
      );
    changed("destroyed");
    changed("active");
    expect(installs).toEqual([
      { mid: "0", transform: made[0] },
      { mid: "0", transform: made[1] },
    ]);
  });

  it("keeps the instance while the camera stays listed", async () => {
    const { client, captured, installs } = await connectDynamic();
    captured.dc?._open();
    const { factory } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    snapshot(captured, [fakeCameraState(7)]);
    slotMap(captured, "0", 7);
    snapshot(captured, [fakeCameraState(7), fakeCameraState(8)]);
    snapshot(captured, [fakeCameraState(7)]);
    expect(factory).toHaveBeenCalledOnce();
    expect(installs).toHaveLength(1);
  });

  it("gives a camera reattached in place a fresh instance when its attach changes", async () => {
    const { client, captured, installs } = await connectDynamic();
    captured.dc?._open();
    const { factory, made } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    snapshot(captured, [fakeCameraState(7, { attach: 1 })]);
    slotMap(captured, "0", 7);
    snapshot(captured, [fakeCameraState(7, { attach: 2 })]);
    captured.dc?._msg(
      JSON.stringify({ type: "camera-state-changed", content: { state: fakeCameraState(7, { attach: 3 }) } }),
    );
    expect(installs).toEqual([
      { mid: "0", transform: made[0] },
      { mid: "0", transform: made[1] },
      { mid: "0", transform: made[2] },
    ]);
  });

  it("keeps the instance while the attach stays the same or is missing", async () => {
    const { client, captured, installs } = await connectDynamic();
    captured.dc?._open();
    const { factory } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    snapshot(captured, [fakeCameraState(7, { attach: 1 })]);
    slotMap(captured, "0", 7);
    snapshot(captured, [fakeCameraState(7, { attach: 1 })]);
    captured.dc?._msg(
      JSON.stringify({ type: "camera-state-changed", content: { state: fakeCameraState(7, { attach: 1 }) } }),
    );
    snapshot(captured, [fakeCameraState(7)]);
    snapshot(captured, [fakeCameraState(7, { attach: 1 })]);
    snapshot(captured, [fakeCameraState(7)]);
    expect(factory).toHaveBeenCalledOnce();
    expect(installs).toHaveLength(1);
  });

  it("renews once when a camera comes back to the list with a new attach", async () => {
    const { client, captured, installs } = await connectDynamic();
    captured.dc?._open();
    const { factory, made } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    snapshot(captured, [fakeCameraState(7, { attach: 1 })]);
    slotMap(captured, "0", 7);
    snapshot(captured, []);
    snapshot(captured, [fakeCameraState(7, { attach: 2 })]);
    expect(installs).toEqual([
      { mid: "0", transform: made[0] },
      { mid: "0", transform: made[1] },
    ]);
  });

  it("removes the instance at disconnect and reinstalls after reconnect", async () => {
    const { client, captured, installs } = await connectDynamic();
    const { factory, made } = countingFactory();
    client.camera(7).setEncodedTransform(factory);
    slotMap(captured, "0", 7);
    client.disconnect();
    expect(installs).toEqual([
      { mid: "0", transform: made[0] },
      { mid: "0", transform: null },
    ]);

    await client.connect([], { slots: 2 });
    slotMap(captured, "1", 7);
    expect(installs.at(-1)).toEqual({ mid: "1", transform: made[1] });
  });

  it("removes the instance when the peer fails", async () => {
    const { client, captured, installs } = await connectDynamic();
    client.camera(7).setEncodedTransform(countingFactory().factory);
    slotMap(captured, "0", 7);
    captured.onState?.("failed");
    expect(installs.at(-1)).toEqual({ mid: "0", transform: null });
  });

  it("works in legacy mode, routing by track index", async () => {
    const t = makeTransformTransport(optedInScript);
    const client = new KerbcastClient({ host: "h", port: 1, encodedTransforms: true }, t.transport);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([10, 20]));
    const { factory, made } = countingFactory();
    client.camera(20).setEncodedTransform(factory);
    await client.connect([10, 20]);
    t.captured.onTrack?.({ kind: "video" } as MediaStreamTrack, 0, "0");
    t.captured.onTrack?.({ kind: "video" } as MediaStreamTrack, 1, "1");
    expect(t.installs).toEqual([{ mid: "1", transform: made[0] }]);
    expect(made[0].flightId).toBe(20);
  });

  it("surfaces a throwing factory as a client error and installs nothing", async () => {
    const { client, captured, installs } = await connectDynamic();
    const errors: { message: string; flightId?: number }[] = [];
    client.on("error", (e) => errors.push(e));
    client.camera(7).setEncodedTransform(() => {
      throw new Error("boom");
    });
    slotMap(captured, "0", 7);
    expect(installs).toHaveLength(0);
    expect(errors).toHaveLength(1);
    expect(errors[0].flightId).toBe(7);
    expect(errors[0].message).toMatch(/boom/);
  });

  it("requires the encodedTransforms opt-in", async () => {
    const t = makeTransformTransport((optedIn) => (optedIn ? "streams" : null));
    const plain = new KerbcastClient({ host: "h", port: 1 }, t.transport);
    expect(plain.encodedTransformPath).toBeNull();
    expect(() => plain.camera(1).setEncodedTransform(countingFactory().factory)).toThrow(
      /encodedTransforms: true/,
    );
    expect(() => plain.camera(1).setEncodedTransform(null)).not.toThrow();

    const optedIn = new KerbcastClient({ host: "h", port: 1, encodedTransforms: true }, t.transport);
    expect(optedIn.encodedTransformPath).toBe("streams");
    const { factory } = countingFactory();
    optedIn.camera(1).setEncodedTransform(factory);
    vi.spyOn(globalThis, "fetch").mockResolvedValue(fakeAnswer([]));
    await optedIn.connect([], { slots: 1 });
    expect(t.peerOpts.at(-1)).toEqual({ encodedTransforms: true });
    slotMap(t.captured, "0", 1);
    expect(factory).toHaveBeenCalledWith({ flightId: 1, path: "streams" });
  });

  it("throws when the browser has no install path", () => {
    const t = makeTransformTransport(() => null);
    const client = new KerbcastClient({ host: "h", port: 1, encodedTransforms: true }, t.transport);
    expect(() => client.camera(1).setEncodedTransform(countingFactory().factory)).toThrow(
      /neither RTCRtpScriptTransform nor createEncodedStreams/,
    );
    const bare = new KerbcastClient({ host: "h", port: 1 }, makeFakeTransport().transport);
    expect(() => bare.camera(1).setEncodedTransform(countingFactory().factory)).toThrow(/unavailable/);
  });

  /*
   * Chromium can leave a script transform handed over on a receiver with no
   * packets arriving without frames, so each install is renewed once when
   * packets next arrive, and again while packets arrive with none decoded.
   */
  describe("heal once packets arrive", () => {
    beforeEach(() => vi.useFakeTimers());
    afterEach(() => vi.useRealTimers());

    /*
     * `packets` and `frames` give each mid one inbound-rtp entry; `extraStats`
     * adds more, such as an old stream's entry lingering on a mid. While
     * `hangStats` is set, stats calls never settle; `stats.delayMs` makes them
     * settle late, with the counts as they were when asked. `stats.failNull`
     * makes handing a receiver back to passthrough throw.
     */
    async function connectWithStats(path: Path = "script", cfg: Partial<KerbcastClientConfig> = {}) {
      const t = makeTransformTransport(() => path);
      const packets = new Map<string, number>();
      const frames = new Map<string, number>();
      const extraStats: { id: string; mid: string; packetsReceived: number }[] = [];
      const taps = new Map<string, ((frame: EncodedFrameCopy) => void) | null>();
      const stats = { calls: 0, hang: false, delayMs: 0, failNull: false };
      const transport: KerbcastTransport = {
        ...t.transport,
        createPeer: (ice, opts) => {
          const base = t.transport.createPeer(ice, opts);
          return {
          ...base,
          setReceiverTransform: (mid, transform) => {
            if (transform === null && stats.failNull) throw new Error("no receiver");
            base.setReceiverTransform?.(mid, transform);
          },
          setReceiverTap: (mid, onFrame) => {
            taps.set(mid, onFrame);
          },
          getStats: () => {
            stats.calls++;
            if (stats.hang) return new Promise<RTCStatsReport>(() => {});
            const entries = [
              ...[...packets].map(([mid, n]) => ({
                id: `in-${mid}`,
                mid,
                packetsReceived: n,
                framesDecoded: frames.get(mid) ?? 0,
              })),
              ...extraStats,
            ];
            const report = new Map(entries.map((e) => [e.id, { type: "inbound-rtp", kind: "video", ...e }]));
            if (stats.delayMs === 0) return Promise.resolve(report as unknown as RTCStatsReport);
            return new Promise<RTCStatsReport>((resolve) => {
              setTimeout(() => resolve(report as unknown as RTCStatsReport), stats.delayMs);
            });
          },
          };
        },
      };
      const client = new KerbcastClient({ host: "h", port: 1, encodedTransforms: true, ...cfg }, transport);
      vi.spyOn(globalThis, "fetch").mockImplementation(() => Promise.resolve(fakeAnswer([])));
      await client.connect([], { slots: 2 });
      t.captured.dc?._open();
      const keyframeRequests = () =>
        (t.captured.dc?.sent ?? []).map((m) => JSON.parse(m)).filter((m) => m.type === "request-keyframe");
      return {
        ...t,
        client,
        packets,
        frames,
        stats,
        extraStats,
        taps,
        keyframeRequests,
        statsCalls: () => stats.calls,
        hangStats: (hang: boolean) => {
          stats.hang = hang;
        },
      };
    }

    it("renews the instance once from the factory when packets start arriving, then asks for a keyframe", async () => {
      const { client, captured, installs, packets, keyframeRequests } = await connectWithStats();
      const { factory, made } = countingFactory();
      client.camera(7).setEncodedTransform(factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(1000);
      expect(installs).toEqual([{ mid: "0", transform: made[0] }]);

      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(installs).toEqual([
        { mid: "0", transform: made[0] },
        { mid: "0", transform: made[1] },
      ]);
      expect(keyframeRequests()).toEqual([{ type: "request-keyframe", content: { flightId: 7 } }]);

      packets.set("0", 300);
      await vi.advanceTimersByTimeAsync(5000);
      expect(factory).toHaveBeenCalledTimes(2);
      expect(keyframeRequests()).toHaveLength(1);
    });

    it("heals an instance installed while packets were already arriving, once", async () => {
      const { client, captured, installs, packets, frames } = await connectWithStats();
      packets.set("0", 500);
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory);
      slotMap(captured, "0", 7);
      for (let n = 530; n < 1000; n += 30) {
        packets.set("0", n);
        frames.set("0", n / 10);
        await vi.advanceTimersByTimeAsync(250);
      }
      expect(factory).toHaveBeenCalledTimes(2);
      expect(installs).toHaveLength(2);
    });

    it("samples a heal started during another's stats call only from a later report", async () => {
      const { client, captured, packets, stats } = await connectWithStats();
      stats.delayMs = 100;
      packets.set("0", 100);
      client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(captured, "1", 7);
      const b = countingFactory();
      client.camera(8).setEncodedTransform(b.factory);
      slotMap(captured, "0", 8);
      packets.set("0", 105);
      await vi.advanceTimersByTimeAsync(2000);
      expect(b.factory).toHaveBeenCalledOnce();
    });

    it("renews again while packets arrive with none decoded, and stops once frames decode", async () => {
      const { client, captured, packets, frames, statsCalls, keyframeRequests } = await connectWithStats();
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory, { holdMs: 0 });
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      let n = 0;
      const flow = async (ms: number) => {
        for (let t = 0; t < ms; t += 250) {
          packets.set("0", (n += 30));
          await vi.advanceTimersByTimeAsync(250);
        }
      };
      await flow(500);
      expect(factory).toHaveBeenCalledTimes(2);
      await flow(1500);
      expect(factory).toHaveBeenCalledTimes(2);
      await flow(1500);
      expect(factory).toHaveBeenCalledTimes(3);
      expect(keyframeRequests()).toHaveLength(2);

      frames.set("0", 5);
      await flow(1000);
      const calls = statsCalls();
      await flow(20_000);
      expect(factory).toHaveBeenCalledTimes(3);
      expect(statsCalls()).toBe(calls);
    });

    it("renews a transform with no hold given only once, however long packets arrive undecoded", async () => {
      const { client, captured, packets, statsCalls, keyframeRequests } = await connectWithStats();
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      for (let n = 1; n < 240; n++) {
        packets.set("0", n * 30);
        await vi.advanceTimersByTimeAsync(250);
      }
      expect(factory).toHaveBeenCalledTimes(2);
      expect(keyframeRequests()).toHaveLength(1);
      const calls = statsCalls();
      await vi.advanceTimersByTimeAsync(5000);
      expect(statsCalls()).toBe(calls);
    });

    /* Flows packets with none decoded, returning the ms after the first renewal at which each later one came. */
    async function flowUndecoded(
      t: Awaited<ReturnType<typeof connectWithStats>>,
      factory: { mock: { calls: unknown[] } },
      ms: number,
    ): Promise<number[]> {
      let healedAt: number | null = null;
      const later: number[] = [];
      for (let n = 1; n * 250 <= ms; n++) {
        t.packets.set("0", (t.packets.get("0") ?? 0) + 30);
        await vi.advanceTimersByTimeAsync(250);
        const renewals = factory.mock.calls.length - 1;
        if (renewals >= 1) healedAt ??= n * 250;
        if (healedAt !== null) while (later.length < renewals - 1) later.push(n * 250 - healedAt);
      }
      return later;
    }

    it("renews a transform holding 5 s again only after 7 s of packets with none decoded", async () => {
      const t = await connectWithStats();
      const { factory } = countingFactory();
      t.client.camera(7).setEncodedTransform(factory, { holdMs: 5000 });
      slotMap(t.captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      const later = await flowUndecoded(t, factory, 10_000);
      expect(later).toHaveLength(1);
      expect(later[0]).toBeGreaterThanOrEqual(7000);
      expect(later[0]).toBeLessThan(8000);
    });

    it("reads a hold given as a function at each check", async () => {
      const t = await connectWithStats();
      const { factory } = countingFactory();
      let hold = 60_000;
      const holdMs = vi.fn(() => hold);
      t.client.camera(7).setEncodedTransform(factory, { holdMs });
      slotMap(t.captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      expect(await flowUndecoded(t, factory, 5000)).toEqual([]);
      hold = 0;
      expect(await flowUndecoded(t, factory, 1000)).toHaveLength(1);
      expect(holdMs.mock.calls.length).toBeGreaterThan(2);
    });

    it("renews the passthrough again while packets arrive with none decoded", async () => {
      const t = await connectWithStats();
      t.client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(t.captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      slotMap(t.captured, "0", 8);
      for (let n = 1; n <= 16; n++) {
        t.packets.set("0", n * 30);
        await vi.advanceTimersByTimeAsync(250);
      }
      expect(t.installs.map((i) => i.transform === null)).toEqual([false, true, true, true]);
    });

    it("counts a stall only while packets keep arriving, not across an idle receiver", async () => {
      for (const kind of ["passthrough", "held"] as const) {
        const t = await connectWithStats();
        const { factory } = countingFactory();
        if (kind === "passthrough") {
          t.client.camera(7).setEncodedTransform(factory);
          slotMap(t.captured, "0", 7);
          await vi.advanceTimersByTimeAsync(300);
          slotMap(t.captured, "0", 8);
        } else {
          t.client.camera(7).setEncodedTransform(factory, { holdMs: 1000 });
          slotMap(t.captured, "0", 7);
        }
        const renewals = () => t.installs.length - 1;
        await vi.advanceTimersByTimeAsync(300);
        t.packets.set("0", 30);
        await vi.advanceTimersByTimeAsync(250);
        const healed = renewals();
        expect(healed).toBeGreaterThanOrEqual(1);
        await vi.advanceTimersByTimeAsync(300);
        t.packets.set("0", 33);
        await vi.advanceTimersByTimeAsync(30_000);
        expect(renewals()).toBe(healed);

        for (let n = 1; n <= 24; n++) {
          t.packets.set("0", 33 + n * 30);
          await vi.advanceTimersByTimeAsync(250);
        }
        expect(renewals()).toBe(healed + 1);
        t.client.disconnect();
        vi.restoreAllMocks();
      }
    });

    it("renews at most three more times when frames never decode, waiting longer each time", async () => {
      const { client, captured, packets, statsCalls, keyframeRequests } = await connectWithStats();
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory, { holdMs: 0 });
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      const renewedAt: number[] = [];
      for (let n = 1; n < 240; n++) {
        packets.set("0", n * 30);
        await vi.advanceTimersByTimeAsync(250);
        while (renewedAt.length < factory.mock.calls.length - 1) renewedAt.push(n * 250);
      }
      const gaps = renewedAt.slice(1).map((t, i) => t - renewedAt[i]);
      expect(gaps).toHaveLength(3);
      expect(gaps[1]).toBeGreaterThan(gaps[0]);
      expect(gaps[2]).toBeGreaterThan(gaps[1]);
      expect(factory).toHaveBeenCalledTimes(5);
      expect(keyframeRequests()).toHaveLength(4);
      const calls = statsCalls();
      await vi.advanceTimersByTimeAsync(5000);
      expect(statsCalls()).toBe(calls);
    });

    it("renews again when packets resume undecoded after a burst spent the first renewal", async () => {
      const { client, captured, packets } = await connectWithStats();
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory, { holdMs: 0 });
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(factory).toHaveBeenCalledTimes(2);
      await vi.advanceTimersByTimeAsync(10_000);
      expect(factory).toHaveBeenCalledTimes(2);
      for (let n = 60; n < 600; n += 30) {
        packets.set("0", n);
        await vi.advanceTimersByTimeAsync(250);
      }
      expect(factory).toHaveBeenCalledTimes(3);
    });

    it("names the camera bound to the receiver when renewing its passthrough fails", async () => {
      const { client, captured, packets, stats } = await connectWithStats();
      const errors: { flightId?: number }[] = [];
      client.on("error", (e) => errors.push(e));
      client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      slotMap(captured, "0", 8);
      await vi.advanceTimersByTimeAsync(300);
      stats.failNull = true;
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(errors.map((e) => e.flightId)).toEqual([8]);
    });

    it("polls a new peer at once while the old peer's stats call is still pending", async () => {
      const { client, captured, installs, packets, hangStats } = await connectWithStats();
      client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      hangStats(true);
      await vi.advanceTimersByTimeAsync(250);
      await client.connect([], { slots: 2 });
      hangStats(false);
      const before = installs.length;
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(installs.length - before).toBe(2);
    });

    it("keeps watching an idle receiver past its first minute, more slowly, and renews when packets come", async () => {
      const { client, captured, installs, packets, frames, statsCalls, keyframeRequests } =
        await connectWithStats();
      packets.set("0", 12);
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(10_000);
      const fastCalls = statsCalls();
      await vi.advanceTimersByTimeAsync(10_000);
      expect(statsCalls() - fastCalls).toBe(40);

      await vi.advanceTimersByTimeAsync(100_000);
      expect(installs).toHaveLength(1);
      const slowCalls = statsCalls();
      await vi.advanceTimersByTimeAsync(10_000);
      expect(statsCalls() - slowCalls).toBe(5);

      packets.set("0", 40);
      await vi.advanceTimersByTimeAsync(2000);
      expect(factory).toHaveBeenCalledTimes(2);
      expect(installs).toHaveLength(2);
      expect(keyframeRequests()).toHaveLength(1);
      for (let n = 1; n <= 4; n++) {
        frames.set("0", n);
        await vi.advanceTimersByTimeAsync(250);
      }
      const done = statsCalls();
      await vi.advanceTimersByTimeAsync(10_000);
      expect(statsCalls()).toBe(done);
    });

    it("renews the passthrough a removed transform leaves, for the camera bound next, once packets come", async () => {
      const { client, captured, installs, packets, keyframeRequests } = await connectWithStats();
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      slotMap(captured, "0", null);
      await vi.advanceTimersByTimeAsync(5000);
      slotMap(captured, "0", 8);
      await vi.advanceTimersByTimeAsync(300);
      expect(installs.map((i) => i.transform === null)).toEqual([false, true]);

      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(installs.map((i) => i.transform === null)).toEqual([false, true, true]);
      expect(installs.at(-1)?.mid).toBe("0");
      expect(keyframeRequests()).toEqual([{ type: "request-keyframe", content: { flightId: 8 } }]);
      packets.set("0", 300);
      await vi.advanceTimersByTimeAsync(2000);
      expect(installs).toHaveLength(3);
      expect(factory).toHaveBeenCalledOnce();
    });

    it("renews the passthrough left when a bound camera's factory is removed", async () => {
      const { client, captured, installs, packets } = await connectWithStats();
      client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      client.camera(7).setEncodedTransform(null);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(installs.map((i) => i.transform === null)).toEqual([false, true, true]);
    });

    it("keeps a camera's frame taps across the passthrough's renewal", async () => {
      const { client, captured, installs, packets, taps } = await connectWithStats("script", {
        recordingTiming: "capture",
      });
      client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      slotMap(captured, "0", null);
      const got: number[] = [];
      client.camera(8).tapEncodedFrames((f) => got.push(f.rtpTimestamp));
      slotMap(captured, "0", 8);
      await vi.advanceTimersByTimeAsync(300);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(installs.map((i) => i.transform === null)).toEqual([false, true, true]);
      taps.get("0")?.({ data: new ArrayBuffer(4), rtpTimestamp: 9, receivedAt: 0 });
      expect(got).toEqual([9]);
    });

    it("keeps the instance in place when the factory throws on renewal, and asks for no keyframe", async () => {
      const { client, captured, installs, packets, keyframeRequests } = await connectWithStats();
      const errors: { message: string }[] = [];
      client.on("error", (e) => errors.push(e));
      let calls = 0;
      const factory = vi.fn(() => {
        if (++calls === 2) throw new Error("no worker");
        return { n: calls } as unknown as TransformStream<RTCEncodedVideoFrame, RTCEncodedVideoFrame>;
      });
      client.camera(7).setEncodedTransform(factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(errors.map((e) => e.message)).toEqual([expect.stringMatching(/camera 7: no worker/)]);
      expect(keyframeRequests()).toHaveLength(0);
      expect(installs).toHaveLength(1);

      slotMap(captured, "1", 8);
      expect(factory).toHaveBeenCalledTimes(2);
      expect(installs).toHaveLength(1);
    });

    it("asks again for stats when a call never settles", async () => {
      const { client, captured, installs, packets, hangStats } = await connectWithStats();
      client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      hangStats(true);
      await vi.advanceTimersByTimeAsync(300);
      hangStats(false);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(10_000);
      expect(installs).toHaveLength(2);
    });

    it("sees packets on a receiver whose old stream's stats entry lingers", async () => {
      const { client, captured, installs, packets, extraStats } = await connectWithStats();
      extraStats.push({ id: "in-old", mid: "0", packetsReceived: 500 });
      client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(installs).toHaveLength(2);
    });

    it("drops the heal when the camera leaves the receiver or its factory is removed first", async () => {
      const { client, captured, installs, packets, frames, statsCalls } = await connectWithStats();
      const a = countingFactory();
      client.camera(7).setEncodedTransform(a.factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      slotMap(captured, "0", null);
      const b = countingFactory();
      client.camera(8).setEncodedTransform(b.factory);
      slotMap(captured, "1", 8);
      await vi.advanceTimersByTimeAsync(300);
      client.camera(8).setEncodedTransform(null);
      packets.set("0", 30);
      packets.set("1", 30);
      await vi.advanceTimersByTimeAsync(1000);
      expect(a.factory).toHaveBeenCalledOnce();
      expect(b.factory).toHaveBeenCalledOnce();
      expect(installs.filter((i) => i.transform !== null)).toHaveLength(2);
      frames.set("0", 3);
      frames.set("1", 3);
      await vi.advanceTimersByTimeAsync(1000);
      const calls = statsCalls();
      await vi.advanceTimersByTimeAsync(2000);
      expect(statsCalls()).toBe(calls);
    });

    it("heals a renewed instance again, since a renewal is a new install", async () => {
      const { client, captured, packets } = await connectWithStats();
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory);
      snapshot(captured, [fakeCameraState(7)]);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(300);
      expect(factory).toHaveBeenCalledTimes(2);

      snapshot(captured, []);
      snapshot(captured, [fakeCameraState(7)]);
      expect(factory).toHaveBeenCalledTimes(3);
      await vi.advanceTimersByTimeAsync(300);
      packets.set("0", 60);
      await vi.advanceTimersByTimeAsync(300);
      expect(factory).toHaveBeenCalledTimes(4);
    });

    it("leaves the streams path alone", async () => {
      const { client, captured, packets, statsCalls } = await connectWithStats("streams");
      const { factory } = countingFactory();
      client.camera(7).setEncodedTransform(factory);
      slotMap(captured, "0", 7);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(1000);
      expect(factory).toHaveBeenCalledOnce();
      expect(statsCalls()).toBe(0);
    });

    it("costs a host with no factory nothing", async () => {
      const { captured, packets, statsCalls } = await connectWithStats();
      slotMap(captured, "0", 7);
      packets.set("0", 30);
      await vi.advanceTimersByTimeAsync(1000);
      expect(statsCalls()).toBe(0);
    });

    it("stops watching at disconnect, and watches nothing on the next peer", async () => {
      const { client, captured, installs, packets, statsCalls } = await connectWithStats();
      client.camera(7).setEncodedTransform(countingFactory().factory);
      slotMap(captured, "0", 7);
      await vi.advanceTimersByTimeAsync(300);
      slotMap(captured, "0", null);
      slotMap(captured, "1", 7);
      await vi.advanceTimersByTimeAsync(300);
      client.disconnect();
      const calls = statsCalls();
      const before = installs.length;
      await client.connect([], { slots: 2 });
      packets.set("0", 30);
      packets.set("1", 30);
      await vi.advanceTimersByTimeAsync(1000);
      expect(statsCalls()).toBe(calls);
      expect(installs).toHaveLength(before);
    });
  });
});

describe("KerbcastCameraHandle.tapEncodedFrames", () => {
  type Path = "script" | "streams" | null;
  type Tap = ((frame: EncodedFrameCopy) => void) | null;

  function makeTapTransport(path: Path) {
    const base = makeFakeTransport();
    const taps = new Map<string, Tap>();
    const tapCalls: { mid: string; on: boolean }[] = [];
    const peerOpts: (KerbcastPeerOptions | undefined)[] = [];
    const transport: KerbcastTransport = {
      createPeer: (ice, opts) => {
        peerOpts.push(opts);
        return {
          ...base.transport.createPeer(ice),
          setReceiverTransform: () => {},
          setReceiverTap: (mid, onFrame) => {
            taps.set(mid, onFrame);
            tapCalls.push({ mid, on: onFrame !== null });
          },
        };
      },
      encodedTransformPath: (opts) =>
        opts.encodedTransforms === true || opts.frameTaps === true ? path : null,
    };
    return { transport, captured: base.captured, taps, tapCalls, peerOpts };
  }

  function slotMap(captured: { dc?: FakeChannel }, mid: string, flightId: number | null) {
    captured.dc?._msg(JSON.stringify({ type: "slot-map", content: { mid, flightId } }));
  }

  function frame(rtpTimestamp: number): EncodedFrameCopy {
    return { data: new ArrayBuffer(4), rtpTimestamp, receivedAt: 0 };
  }

  async function connect(path: Path, cfg: Partial<KerbcastClientConfig> = {}) {
    const t = makeTapTransport(path);
    const client = new KerbcastClient(
      { host: "h", port: 1, recordingTiming: "capture", ...cfg },
      t.transport,
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() => Promise.resolve(fakeAnswer([])));
    await client.connect([], { slots: 2 });
    return { ...t, client };
  }

  it("is unavailable without recordingTiming capture", async () => {
    const { client, peerOpts } = await connect("script", { recordingTiming: undefined });
    expect(peerOpts.at(-1)).toEqual({ encodedTransforms: false });
    expect(client.camera(7).canTapEncodedFrames()).toBe(false);
    expect(client.camera(7).tapEncodedFrames(() => {})).toBeNull();
  });

  it("asks the peer for frame taps and does not unlock setEncodedTransform", async () => {
    const { client, peerOpts } = await connect("script");
    expect(peerOpts.at(-1)).toEqual({ encodedTransforms: false, frameTaps: true });
    expect(() => client.camera(7).setEncodedTransform(() => new TransformStream())).toThrow(
      /encodedTransforms: true/,
    );
  });

  it("delivers the bound receiver's frames and follows the camera to a new slot", async () => {
    const { client, captured, taps, tapCalls } = await connect("script");
    const got: number[] = [];
    const untap = client.camera(7).tapEncodedFrames((f) => got.push(f.rtpTimestamp));
    expect(untap).not.toBeNull();
    expect(tapCalls).toHaveLength(0);

    slotMap(captured, "0", 7);
    taps.get("0")?.(frame(1));
    slotMap(captured, "0", null);
    slotMap(captured, "1", 7);
    taps.get("1")?.(frame(2));
    expect(got).toEqual([1, 2]);
    expect(tapCalls).toEqual([
      { mid: "0", on: true },
      { mid: "0", on: false },
      { mid: "1", on: true },
    ]);

    untap?.();
    expect(tapCalls.at(-1)).toEqual({ mid: "1", on: false });
  });

  it("is unavailable on the script path while the camera runs its own transform", async () => {
    const { client } = await connect("script", { encodedTransforms: true });
    const cam = client.camera(7);
    expect(cam.canTapEncodedFrames()).toBe(true);
    cam.setEncodedTransform(() => ({}) as RTCRtpScriptTransform);
    expect(cam.canTapEncodedFrames()).toBe(false);
    expect(cam.tapEncodedFrames(() => {})).toBeNull();
  });

  it("warns when the camera's own transform displaces a live tap on the script path", async () => {
    const { client } = await connect("script", { encodedTransforms: true });
    const errors: { message: string; flightId?: number }[] = [];
    client.on("error", (e) => errors.push(e));
    const cam = client.camera(7);
    cam.tapEncodedFrames(() => {});
    cam.setEncodedTransform(() => ({}) as RTCRtpScriptTransform);
    expect(errors).toHaveLength(1);
    expect(errors[0].flightId).toBe(7);
    expect(errors[0].message).toMatch(/stops its frame copies/);
  });

  it("keeps delivering to other taps when one throws", async () => {
    const { client, captured, taps } = await connect("streams");
    const got: number[] = [];
    const errSpy = vi.spyOn(console, "error").mockImplementation(() => {});
    client.camera(7).tapEncodedFrames(() => {
      throw new Error("boom");
    });
    client.camera(7).tapEncodedFrames((f) => got.push(f.rtpTimestamp));
    slotMap(captured, "0", 7);
    taps.get("0")?.(frame(5));
    expect(got).toEqual([5]);
    expect(errSpy).toHaveBeenCalled();
    errSpy.mockRestore();
  });

  it("stays available on the streams path alongside the camera's own transform", async () => {
    const { client } = await connect("streams", { encodedTransforms: true });
    const cam = client.camera(7);
    cam.setEncodedTransform(() => new TransformStream());
    expect(cam.tapEncodedFrames(() => {})).not.toBeNull();
  });

  it("keeps the tap when a camera back after a scene change gets a fresh transform", async () => {
    const { client, captured, taps, tapCalls } = await connect("streams", { encodedTransforms: true });
    captured.dc?._open();
    const cam = client.camera(7);
    const factory = vi.fn(() => new TransformStream());
    cam.setEncodedTransform(factory);
    const got: number[] = [];
    cam.tapEncodedFrames((f) => got.push(f.rtpTimestamp));
    const snapshot = (cameras: unknown[]) =>
      captured.dc?._msg(JSON.stringify({ type: "camera-snapshot", content: { cameras } }));
    snapshot([fakeCameraState(7)]);
    slotMap(captured, "0", 7);
    snapshot([]);
    snapshot([fakeCameraState(7)]);
    taps.get("0")?.(frame(1));
    expect(factory).toHaveBeenCalledTimes(2);
    expect(tapCalls).toEqual([{ mid: "0", on: true }]);
    expect(got).toEqual([1]);
  });

  it("drops the receiver's tap on disconnect and taps again after reconnect", async () => {
    const { client, captured, tapCalls } = await connect("streams");
    client.camera(7).tapEncodedFrames(() => {});
    slotMap(captured, "0", 7);
    client.disconnect();
    await client.connect([], { slots: 2 });
    slotMap(captured, "1", 7);
    expect(tapCalls).toEqual([
      { mid: "0", on: true },
      { mid: "1", on: true },
    ]);
  });
});
