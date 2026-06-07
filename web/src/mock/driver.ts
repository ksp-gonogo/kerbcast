/**
 * Mock driver for dev/browser review.
 *
 * Loaded dynamically only when location.search contains mock=1.
 * Never imported statically from the app entry.
 *
 * Creates a KerbcamClient backed by MockSidecar. Registers 4 plausible
 * cameras and delivers animated canvas captureStream tracks to each slot.
 *
 * One camera starts destroyed to demonstrate SIGNAL LOST.
 * /profile fetch is intercepted to return plausible stagger numbers.
 */

import { CameraLifecycle, KerbcamClient } from "@jonpepler/kerbcam";
import type { MockCameraInit } from "@jonpepler/kerbcam/testing";
import { MockSidecar } from "@jonpepler/kerbcam/testing";

const MOCK_CAMERAS: MockCameraInit[] = [
  {
    flightId: 101,
    cameraName: "NavCam",
    vesselName: "Kerbal X",
    partName: "mumech.MuMechModuleHullCamera",
    partTitle: "NavCam",
    lifecycle: CameraLifecycle.Active,
    supportsPan: true,
    panYawMin: -45,
    panYawMax: 45,
    panPitchMin: -30,
    panPitchMax: 30,
    supportsZoom: true,
    fov: 60,
    fovMin: 15,
    fovMax: 90,
    renderWidth: 1280,
    renderHeight: 720,
    operatorWidth: 1280,
    operatorHeight: 720,
    encoderBitrateBps: 2_000_000,
  },
  {
    flightId: 102,
    cameraName: "Booster Cam",
    vesselName: "Kerbal X",
    partName: "mumech.MuMechModuleHullCamera",
    partTitle: "Booster Cam",
    lifecycle: CameraLifecycle.Active,
    supportsZoom: false,
    supportsPan: false,
    renderWidth: 1280,
    renderHeight: 720,
    operatorWidth: 1280,
    operatorHeight: 720,
    encoderBitrateBps: 1_500_000,
  },
  {
    flightId: 103,
    cameraName: "NavCam",
    vesselName: "Kerbal X",
    partName: "mumech.MuMechModuleHullCamera",
    partTitle: "Clamp-O-Tron Docking Port Jr.",
    lifecycle: CameraLifecycle.Active,
    supportsZoom: false,
    supportsPan: false,
    renderWidth: 1280,
    renderHeight: 720,
    operatorWidth: 1280,
    operatorHeight: 720,
    encoderBitrateBps: 1_200_000,
  },
  {
    flightId: 104,
    cameraName: "Launchpad West",
    vesselName: "Ground",
    partName: "mumech.MuMechModuleHullCamera",
    partTitle: "Launchpad Camera",
    lifecycle: CameraLifecycle.Destroyed,
    supportsZoom: false,
    supportsPan: false,
    renderWidth: 0,
    renderHeight: 0,
    operatorWidth: 0,
    operatorHeight: 0,
    encoderBitrateBps: 0,
  },
];

/** HSL hue for each camera's test pattern. */
const CAMERA_HUES = [210, 30, 140, 0];

/**
 * Create a KerbcamClient backed by a MockSidecar.
 * The client is already connected and the channel is open when this resolves.
 */
export async function createMockClient(): Promise<KerbcamClient> {
  const sidecar = new MockSidecar();
  sidecar.withSlots(["0", "1", "2", "3", "4", "5", "6", "7"]);

  for (const cam of MOCK_CAMERAS) {
    sidecar.addCamera(cam);
  }

  const mockClient = new KerbcamClient(
    {
      host: "mock",
      port: 0,
      negotiate: (offer) => sidecar.negotiate(offer),
    },
    sidecar.createTransport(),
  );

  // Intercept /profile before the app's DevPanel starts polling
  interceptProfileFetch();

  // Connect, then open the channel (mirrors the fixture two-act pattern)
  await mockClient.connect([], { slots: 8 });
  sidecar.open();
  sidecar.setConnectionState("connected");

  // Deliver canvas tracks for each live camera
  let slotIdx = 0;
  for (let i = 0; i < MOCK_CAMERAS.length; i++) {
    const cam = MOCK_CAMERAS[i];
    if ((cam.lifecycle ?? CameraLifecycle.Active) === CameraLifecycle.Destroyed) {
      slotIdx++;
      continue;
    }
    const mid = String(slotIdx);
    const hue = CAMERA_HUES[i] ?? 0;
    const track = buildCanvasTrack(cam.cameraName ?? "Camera", hue);
    sidecar.deliverTrack(mid, track);
    slotIdx++;
  }

  // Periodic state variation
  setInterval(() => {
    const variation = 0.8 + Math.random() * 0.4;
    sidecar.updateCamera(101, {
      encoderBitrateBps: Math.round(2_000_000 * variation),
    });
  }, 3000);

  // Periodic pings (keeps the watchdog alive in mock mode)
  setInterval(() => {
    sidecar.firePing();
  }, 5000);

  return mockClient;
}

// ---------------------------------------------------------------------------
// Animated canvas track
// ---------------------------------------------------------------------------

function buildCanvasTrack(label: string, hue: number): MediaStreamTrack {
  const canvas = document.createElement("canvas");
  canvas.width = 640;
  canvas.height = 360;
  const ctx = canvas.getContext("2d");

  let frame = 0;
  setInterval(() => {
    if (!ctx) return;
    frame = (frame + 1) % 360;
    const t = frame / 360;

    // Background gradient sweep
    ctx.fillStyle = `hsl(${hue}, 60%, ${15 + t * 10}%)`;
    ctx.fillRect(0, 0, 640, 360);

    // Moving brightness bar
    const barY = Math.round(t * 360);
    ctx.fillStyle = `hsl(${hue}, 80%, 70%)`;
    ctx.fillRect(0, barY, 640, 8);

    // Camera label
    ctx.fillStyle = "#ffffff";
    ctx.font = "bold 24px sans-serif";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText(label, 320, 180);
  }, 1000 / 24);

  // captureStream is stubbed by installDomStubs in test environments
  // and real in a browser; return the first video track.
  type HasCaptureStream = HTMLCanvasElement & { captureStream(fps?: number): MediaStream };
  const stream = (canvas as HasCaptureStream).captureStream(24);
  const track = stream.getVideoTracks()[0];
  if (!track) throw new Error("captureStream returned no video tracks");
  return track;
}

// ---------------------------------------------------------------------------
// /profile intercept (fetch wrapper for this URL only)
// ---------------------------------------------------------------------------

function interceptProfileFetch(): void {
  const original = globalThis.fetch.bind(globalThis);
  globalThis.fetch = (
    input: RequestInfo | URL,
    init?: RequestInit,
  ): Promise<Response> => {
    const url =
      typeof input === "string"
        ? input
        : input instanceof URL
          ? input.toString()
          : (input as Request).url;

    if (url === "/profile" || url.endsWith("/profile")) {
      const data = {
        staggerBudget: 3 + Math.floor(Math.random() * 3),
        kerbcamFrameMs: 0.1 + Math.random() * 0.2,
        kspFps: 55 + Math.random() * 10,
      };
      return Promise.resolve(
        new Response(JSON.stringify(data), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        }),
      );
    }
    return original(input, init);
  };
}
