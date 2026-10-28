/*
 * Integration test: the mock's simulated force-full-resolution + resolution
 * bump (MockSidecar), driving the REAL RecordingController, not a
 * hand-rolled fake, through a REAL KerbcastClient + MockSidecar pair: the
 * same stack the app's `?mock=1` harness runs (see mock/driver.ts). Proves
 * the mock's `set-force-full-resolution` handling actually satisfies the
 * SDK's arm-and-wait / grouped-trim logic end to end: a forced single feed
 * really arms then records (not stuck arming forever) and a forced group
 * really arms every member, then starts them together once all are ready.
 * jsdom has no decoder, so the decoded-size gate follows the mock's camera
 * state here (stateFollowingDecodedSizeProbe); the gap between the two is
 * covered in the SDK's own recording tests.
 *
 * Client-level, not App/component-level: `client.recording` is driven
 * directly. The web app's own option-wiring (Settings toggle governs the
 * single-feed REC button, the REC+ banner checkbox governs the group) is
 * covered separately in recordingFlow.test.tsx, which fakes the recordings
 * store to sidestep jsdom's MediaRecorder/track limitations; this file
 * instead proves the real controller and real mock cooperate correctly.
 */

import { ARM_TIMEOUT_MS, KerbcastClient, utToRecordingTimeMs } from "@ksp-gonogo/kerbcast";
import type { GroupTrimmer } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { afterEach, describe, expect, it, vi } from "vitest";
import { ConnectionManager } from "./connectionManager";
import { stateFollowingDecodedSizeProbe } from "./mock/decodedSizeProbe";

function fakeVideoTrack(): MediaStreamTrack {
  return { kind: "video" } as MediaStreamTrack;
}

/**
 * Wires a KerbcastClient to `sidecar` the way mock/driver.ts does: dynamic
 * slots, a track delivered on every subscribe, `/cameras` intercepted so
 * `discover()` populates `maxRenderSize`, then connects, opens, and
 * subscribes each of `flightIds`.
 */
async function connectedClientWithTracks(
  sidecar: MockSidecar,
  flightIds: number[],
  loadTrimmer: () => Promise<GroupTrimmer | null>,
): Promise<KerbcastClient> {
  sidecar.withSlots(["0", "1", "2", "3"]);
  sidecar.onSubscribe((_flightId, mid) => sidecar.deliverTrack(mid, fakeVideoTrack()));

  vi.spyOn(globalThis, "fetch").mockImplementation((input: RequestInfo | URL) => {
    const url =
      typeof input === "string"
        ? input
        : input instanceof URL
          ? input.toString()
          : (input as Request).url;
    if (url.endsWith("/cameras")) {
      return Promise.resolve(
        new Response(JSON.stringify({ cameras: sidecar.discoveredCameras() }), { status: 200 }),
      );
    }
    return Promise.reject(new Error(`unexpected fetch: ${url}`));
  });

  const client: KerbcastClient = new KerbcastClient(
    {
      host: "h",
      port: 1,
      negotiate: (o) => sidecar.negotiate(o),
      recording: { loadTrimmer, probeDecodedSize: stateFollowingDecodedSizeProbe(() => client) },
    },
    sidecar.createTransport(),
  );
  await client.discover();
  await client.connect([], { slots: 4 });
  sidecar.open();
  sidecar.setConnectionState("connected");
  for (const flightId of flightIds) {
    await client.subscribe(flightId);
  }
  return client;
}

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

describe("force-full-resolution through the mock: single feed", () => {
  it("arms, sends force, then records once the mock bumps resolution, and clears force on stop", async () => {
    vi.useFakeTimers();
    const sidecar = new MockSidecar({ forceBumpDelayMs: 200 });
    sidecar.addCamera({
      flightId: 42,
      renderWidth: 640,
      renderHeight: 360,
      operatorWidth: 640,
      operatorHeight: 360,
      maxWidth: 1920,
      maxHeight: 1080,
    });
    const client = await connectedClientWithTracks(sidecar, [42], () => Promise.resolve(null));

    // maxRenderSize came from discover(): the "known ceiling" ready check,
    // not the weaker "operator size increased" fallback.
    expect(client.camera(42).maxRenderSize).toEqual({ width: 1920, height: 1080 });

    const id = client.recording.startRecording(42, { forceFullResolution: true });

    expect(sidecar.lastCommand("set-force-full-resolution", 42)?.content.force).toBe(true);
    expect(client.recording.getSnapshot().active.find((a) => a.recordingId === id)?.arming).toBe(
      true,
    );

    // Not yet: the mock hasn't landed the bump.
    await vi.advanceTimersByTimeAsync(50);
    expect(client.recording.getSnapshot().active.find((a) => a.recordingId === id)?.arming).toBe(
      true,
    );

    await vi.advanceTimersByTimeAsync(150);
    expect(client.recording.getSnapshot().active.find((a) => a.recordingId === id)?.arming).toBe(
      false,
    );

    await client.recording.stopRecording(id);
    expect(sidecar.lastCommand("set-force-full-resolution", 42)?.content.force).toBe(false);
  });

  it("never arms and sends no force message when forceFullResolution is not set", async () => {
    const sidecar = new MockSidecar();
    sidecar.addCamera({ flightId: 42 });
    const client = await connectedClientWithTracks(sidecar, [42], () => Promise.resolve(null));

    const id = client.recording.startRecording(42);

    expect(sidecar.lastCommand("set-force-full-resolution", 42)).toBeUndefined();
    expect(client.recording.getSnapshot().active.find((a) => a.recordingId === id)?.arming).toBe(
      false,
    );
  });
});

describe("force-full-resolution through the mock: grouped", () => {
  async function forcedGroupClient(forceBumpDelayMs: number): Promise<{
    sidecar: MockSidecar;
    client: KerbcastClient;
    trimCalls: { startMs: number; endMs: number }[];
  }> {
    const sidecar = new MockSidecar({ forceBumpDelayMs });
    for (const flightId of [1, 2]) {
      sidecar.addCamera({
        flightId,
        renderWidth: 720,
        renderHeight: 400,
        operatorWidth: 720,
        operatorHeight: 400,
        maxWidth: 1024,
        maxHeight: 576,
      });
    }
    const trimCalls: { startMs: number; endMs: number }[] = [];
    const fakeTrimmer: GroupTrimmer = {
      trim: async (blob, _mimeType, startMs, endMs) => {
        trimCalls.push({ startMs, endMs });
        return blob;
      },
    };
    const client = await connectedClientWithTracks(sidecar, [1, 2], () =>
      Promise.resolve(fakeTrimmer),
    );
    return { sidecar, client, trimCalls };
  }

  const armingOf = (client: KerbcastClient, ids: string[]): (boolean | undefined)[] =>
    ids.map((id) => client.recording.getSnapshot().active.find((a) => a.recordingId === id)?.arming);

  it("arms every member, then starts them together once the mock lands both bumps", async () => {
    vi.useFakeTimers();
    const { sidecar, client, trimCalls } = await forcedGroupClient(200);

    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 500 });
    const { groupId, recordingIds } = client.recording.startGroupedRecording([1, 2], {
      forceFullResolution: true,
    });

    expect(sidecar.lastCommand("set-force-full-resolution", 1)?.content.force).toBe(true);
    expect(sidecar.lastCommand("set-force-full-resolution", 2)?.content.force).toBe(true);
    expect(armingOf(client, recordingIds)).toEqual([true, true]);

    /* UT moves on while the feeds are still at tile size: none of this
       lead-in may reach a clip. */
    await vi.advanceTimersByTimeAsync(100);
    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 506 });
    expect(armingOf(client, recordingIds)).toEqual([true, true]);

    await vi.advanceTimersByTimeAsync(100);
    expect(armingOf(client, recordingIds)).toEqual([false, false]);

    await vi.advanceTimersByTimeAsync(100);
    sidecar.fireSettingsState({ throttleMainScreen: false, captureUt: 510 });
    const handle = await client.recording.stopGroupedRecording(groupId);

    /* Both clips open at the UT the recorders started on, after the bump. */
    expect(handle.recordings.map((r) => r.utStart)).toEqual([506, 506]);
    expect(handle.commonUtWindow).toEqual([506, 510]);
    expect(trimCalls).toHaveLength(2);
    for (const call of trimCalls) expect(call.startMs).toBe(0);
    expect(sidecar.lastCommand("set-force-full-resolution", 1)?.content.force).toBe(false);
    expect(sidecar.lastCommand("set-force-full-resolution", 2)?.content.force).toBe(false);
  });

  it("starts every member after ARM_TIMEOUT_MS when the bumps never land", async () => {
    vi.useFakeTimers();
    /* A bump far beyond the arm bound models a feed out of flight or wedged. */
    const { client } = await forcedGroupClient(60_000);

    const { groupId, recordingIds } = client.recording.startGroupedRecording([1, 2], {
      forceFullResolution: true,
    });
    await vi.advanceTimersByTimeAsync(ARM_TIMEOUT_MS - 100);
    expect(armingOf(client, recordingIds)).toEqual([true, true]);

    await vi.advanceTimersByTimeAsync(100);
    expect(armingOf(client, recordingIds)).toEqual([false, false]);

    const handle = await client.recording.stopGroupedRecording(groupId);
    for (const rec of handle.recordings) expect(rec.byteSize).toBeGreaterThan(0);
  });
});

/*
 * Regression coverage for the production connect path: connectedClientWithTracks
 * above calls discover() manually (a test-only convenience), but the real web
 * page's ConnectionManager is what actually drives connect() in the browser.
 * Before this fix, ConnectionManager never called discover(), so
 * maxRenderSize stayed null for every feed and a force-full-resolution arm on
 * a feed already at its render ceiling had no known target to check against;
 * it fell back to the weaker "operator size increased" heuristic, which can
 * never fire for a feed that never changes, so the single-recording
 * arm-and-wait ran out the full ARM_TIMEOUT_MS before recording started,
 * losing the first ~3s of the clip.
 */
describe("force-full-resolution through the real production connect flow (ConnectionManager)", () => {
  it("arms promptly for a feed already at its render ceiling, because ConnectionManager wires discover() into connect()", async () => {
    const sidecar = new MockSidecar();
    sidecar.withSlots(["0", "1", "2", "3", "4", "5", "6", "7"]);
    sidecar.onSubscribe((_flightId, mid) => sidecar.deliverTrack(mid, fakeVideoTrack()));
    sidecar.addCamera({
      flightId: 7,
      renderWidth: 1280,
      renderHeight: 720,
      operatorWidth: 1280,
      operatorHeight: 720,
    });

    vi.spyOn(globalThis, "fetch").mockImplementation((input: RequestInfo | URL) => {
      const url =
        typeof input === "string"
          ? input
          : input instanceof URL
            ? input.toString()
            : (input as Request).url;
      if (url.endsWith("/cameras")) {
        return Promise.resolve(
          new Response(JSON.stringify({ cameras: sidecar.discoveredCameras() }), { status: 200 }),
        );
      }
      return Promise.reject(new Error(`unexpected fetch: ${url}`));
    });

    const client: KerbcastClient = new KerbcastClient(
      {
        host: "h",
        port: 1,
        negotiate: (o) => sidecar.negotiate(o),
        recording: { probeDecodedSize: stateFollowingDecodedSizeProbe(() => client) },
      },
      sidecar.createTransport(),
    );

    const manager = new ConnectionManager(client);
    manager.start();
    await new Promise((resolve) => setTimeout(resolve, 0));
    sidecar.open();
    sidecar.setConnectionState("connected");
    await new Promise((resolve) => setTimeout(resolve, 0));

    await client.subscribe(7);

    expect(client.camera(7).maxRenderSize).toEqual({ width: 1280, height: 720 });

    const id = client.recording.startRecording(7, { forceFullResolution: true });
    expect(
      client.recording.getSnapshot().active.find((a) => a.recordingId === id)?.arming,
    ).toBe(true);

    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(
      client.recording.getSnapshot().active.find((a) => a.recordingId === id)?.arming,
    ).toBe(false);

    await client.recording.stopRecording(id);
    manager.stop();
  });

  it("arms promptly for a feed discover() never saw, from the ceiling in its live state", async () => {
    /* A camera that appears after the page's one discovery (a fresh flight
       scene) used to have no known ceiling and stalled the full
       ARM_TIMEOUT_MS when already at it. */
    const sidecar = new MockSidecar();
    sidecar.withSlots(["0", "1", "2", "3"]);
    sidecar.onSubscribe((_flightId, mid) => sidecar.deliverTrack(mid, fakeVideoTrack()));
    sidecar.addCamera({
      flightId: 7,
      renderWidth: 1280,
      renderHeight: 720,
      operatorWidth: 1280,
      operatorHeight: 720,
    });

    const client: KerbcastClient = new KerbcastClient(
      {
        host: "h",
        port: 1,
        negotiate: (o) => sidecar.negotiate(o),
        recording: { probeDecodedSize: stateFollowingDecodedSizeProbe(() => client) },
      },
      sidecar.createTransport(),
    );
    await client.connect([], { slots: 4 });
    sidecar.open();
    sidecar.setConnectionState("connected");
    await client.subscribe(7);

    expect(client.camera(7).maxRenderSize).toEqual({ width: 1280, height: 720 });

    const id = client.recording.startRecording(7, { forceFullResolution: true });
    await new Promise((resolve) => setTimeout(resolve, 0));
    expect(
      client.recording.getSnapshot().active.find((a) => a.recordingId === id)?.arming,
    ).toBe(false);

    await client.recording.stopRecording(id);
  });
});
