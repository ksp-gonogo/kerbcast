import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BrowserKerbcastTransport, KerbcastClient } from "./client";

/*
 * One peer, three video slots, driven end to end through the browser
 * transport over a stand-in RTCPeerConnection. Every write to a receiver's
 * `transform` is logged, so a release or a size report on one slot that
 * reaches another slot's receiver shows up as a write to it.
 */
describe("a slot release on a shared peer", () => {
  type Receiver = { transform: unknown };
  type Transceiver = { mid: string | null; receiver: Receiver };

  let writes: { mid: string | null; value: unknown }[];
  let transceivers: Transceiver[];
  let channel: {
    onopen: (() => void) | null;
    onmessage: ((ev: { data: string }) => void) | null;
    onclose: (() => void) | null;
    send: (s: string) => void;
    sent: string[];
  };
  let pc: { ontrack: ((ev: unknown) => void) | null };
  const packets = new Map<string, number>();
  const frames = new Map<string, number>();

  beforeEach(() => {
    vi.useFakeTimers();
    writes = [];
    transceivers = [];
    packets.clear();
    frames.clear();
    if (typeof MediaStream === "undefined") {
      vi.stubGlobal(
        "MediaStream",
        class {
          constructor(readonly tracks: unknown[] = []) {}
          getTracks() {
            return this.tracks;
          }
          getVideoTracks() {
            return this.tracks;
          }
        },
      );
    }
    vi.stubGlobal("RTCRtpReceiver", { prototype: { transform: null } });
    vi.stubGlobal("RTCRtpScriptTransform", class {});
    vi.stubGlobal("Worker", class { terminate() {} });
    vi.stubGlobal(
      "RTCPeerConnection",
      class {
        ontrack: ((ev: unknown) => void) | null = null;
        onconnectionstatechange: (() => void) | null = null;
        connectionState = "new";
        iceGatheringState = "complete";
        localDescription: { sdp: string } | null = null;
        constructor() {
          pc = this;
        }
        addTransceiver() {
          let current: unknown = null;
          const t: Transceiver = { mid: null, receiver: {} as Receiver };
          Object.defineProperty(t.receiver, "transform", {
            get: () => current,
            set: (value: unknown) => {
              writes.push({ mid: t.mid, value });
              current = value;
            },
          });
          transceivers.push(t);
          return t;
        }
        getTransceivers() {
          return transceivers;
        }
        createDataChannel() {
          channel = {
            onopen: null,
            onmessage: null,
            onclose: null,
            sent: [],
            send: (s: string) => channel.sent.push(s),
          };
          return channel;
        }
        async createOffer() {
          return { type: "offer", sdp: "offer-sdp" };
        }
        async setLocalDescription(d: { sdp: string }) {
          this.localDescription = { sdp: d.sdp };
        }
        async setRemoteDescription() {
          transceivers.forEach((t, i) => {
            t.mid ??= String(i);
          });
        }
        addEventListener() {}
        removeEventListener() {}
        async getStats() {
          const report = new Map<string, unknown>();
          for (const [mid, n] of packets) {
            report.set(`in-${mid}`, {
              id: `in-${mid}`,
              type: "inbound-rtp",
              kind: "video",
              mid,
              packetsReceived: n,
              framesDecoded: frames.get(mid) ?? 0,
            });
          }
          return report;
        }
        close() {}
      },
    );
    vi.spyOn(globalThis, "fetch").mockImplementation(() =>
      Promise.resolve(new Response(JSON.stringify({ sdp: "answer-sdp", cameras: [] }), { status: 200 })),
    );
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  function server(type: string, content: unknown) {
    channel.onmessage?.({ data: JSON.stringify({ type, content }) });
  }

  /* Packets and decoded frames keep arriving on each mid named, as they did on the phone. */
  async function flow(mids: string[], ms: number) {
    for (let t = 0; t < ms; t += 250) {
      for (const mid of mids) {
        packets.set(mid, (packets.get(mid) ?? 0) + 30);
        frames.set(mid, (frames.get(mid) ?? 0) + 7);
      }
      await vi.advanceTimersByTimeAsync(250);
    }
  }

  const LAUNCHPAD = 3790468094;
  const HULL = 767722819;
  const FACE = 2613378959;

  /* Three cameras bound to slots 0 to 2, the first two with a delay installed, every heal watch stood down. */
  async function threeSlots(recordingTiming: "arrival" | "capture") {
    const client = new KerbcastClient(
      { host: "h", port: 1, encodedTransforms: true, recordingTiming, noise: { enabled: false } },
      new BrowserKerbcastTransport(),
    );
    await client.connect([], { slots: 3 });
    channel.onopen?.();
    expect(transceivers.map((t) => t.mid)).toEqual(["0", "1", "2"]);

    /* The host's delay: a fresh script transform per call, as a delay pipeline builds one. */
    const built = new Map<number, unknown[]>();
    const delay = (flightId: number) => () => {
      const t = new RTCRtpScriptTransform({} as Worker);
      built.set(flightId, [...(built.get(flightId) ?? []), t]);
      return t;
    };
    client.camera(LAUNCHPAD).setEncodedTransform(delay(LAUNCHPAD), { holdMs: 500 });
    client.camera(HULL).setEncodedTransform(delay(HULL), { holdMs: 500 });
    const bind: [number, string][] = [
      [LAUNCHPAD, "0"],
      [HULL, "1"],
      [FACE, "2"],
    ];
    for (const [flightId, mid] of bind) {
      const t = transceivers[Number(mid)];
      pc.ontrack?.({ transceiver: t, track: { id: `track-${mid}`, kind: "video" } });
      server("slot-map", { mid, flightId });
    }
    if (recordingTiming === "capture") {
      client.camera(FACE).tapEncodedFrames(() => {});
    }
    await flow(["0", "1", "2"], 30_000);
    return { client, built };
  }

  for (const recordingTiming of ["arrival", "capture"] as const) {
    it(`leaves another slot's receiver transform alone (recordingTiming ${recordingTiming})`, async () => {
      const { client, built } = await threeSlots(recordingTiming);
      const warn = vi.spyOn(console, "warn").mockImplementation(() => {});

      const hull = transceivers[1].receiver.transform;
      expect(built.get(HULL)).toContain(hull);
      const hullBuilt = built.get(HULL)?.length;
      const hullStream = client.camera(HULL).mediaStream;
      writes.length = 0;

      server("slot-map", { mid: "2", flightId: null });
      await client.reportDisplaySize(LAUNCHPAD, 368, 208);
      await flow(["0", "1"], 120_000);

      expect(writes.filter((w) => w.mid === "1")).toEqual([]);
      expect(transceivers[1].receiver.transform).toBe(hull);
      expect(built.get(HULL)?.length).toBe(hullBuilt);
      expect(client.camera(HULL).mediaStream).toBe(hullStream);
      expect(writes.every((w) => w.mid === "2")).toBe(true);
      expect(warn.mock.calls.flat().filter((m) => String(m).includes("slot 1"))).toEqual([]);
    });
  }

  /*
   * A host clearing the camera's transform, for example a teardown that runs
   * after a newer attach for the same camera, puts the passthrough on the
   * receiver: frames then reach the decoder undelayed while the host's own
   * pipeline reads nothing. The console names the slot and camera.
   */
  it("says on the console when a camera's transform leaves its receiver", async () => {
    const { client, built } = await threeSlots("arrival");
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    writes.length = 0;

    client.camera(HULL).setEncodedTransform(null);

    expect(writes).toHaveLength(1);
    expect(writes[0].mid).toBe("1");
    expect(built.get(HULL)).not.toContain(writes[0].value);
    expect(transceivers[1].receiver.transform).toBe(writes[0].value);
    expect(warn).toHaveBeenCalledWith(
      `[kerbcast] slot 1, camera ${HULL}: its encoded transform leaves the receiver: setEncodedTransform(null) removed it, so the receiver runs the passthrough`,
    );
  });

  it("says on the console when it stops renewing a transform that decodes nothing", async () => {
    const { client } = await threeSlots("arrival");
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    client.camera(HULL).setEncodedTransform(() => new RTCRtpScriptTransform({} as Worker), { holdMs: 500 });
    for (let t = 0; t < 120_000; t += 250) {
      packets.set("1", (packets.get("1") ?? 0) + 30);
      await vi.advanceTimersByTimeAsync(250);
    }
    const said = warn.mock.calls.flat().map(String);
    expect(said.filter((m) => m.includes("is renewed (renewal"))).toHaveLength(4);
    expect(said).toContain(
      `[kerbcast] slot 1, camera ${HULL}: no frame decoded after 4 renewals of its encoded transform; it is no longer watched`,
    );
  });
});
