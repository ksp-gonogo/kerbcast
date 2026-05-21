import { beforeEach, describe, expect, it, vi } from "vitest";
import { Layer } from "./__generated__/types";
import {
  type KerbcamConnectionState,
  type KerbcamDataChannel,
  type KerbcamPeer,
  type KerbcamTransport,
  KerbcamClient,
} from "./client";

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
});
