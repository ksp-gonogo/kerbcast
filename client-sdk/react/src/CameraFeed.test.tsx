/**
 * Tests for the `CameraFeed` component.
 *
 * Two halves:
 *  - the "SIGNAL LOST" overlay + zoom / pan / resize feedback controls;
 *  - the camera-selection layer (picker, Next/Previous buttons, handle API,
 *    status indicator and empty state).
 *
 * Everything drives the real `KerbcamClient` + real hooks through the SDK's
 * canonical `MockSidecar`. The only thing faked is the WebRTC transport,
 * because jsdom can't produce a real `MediaStream`. Multi-camera scenarios
 * are expressed by populating the sidecar's registry (`addCamera` /
 * `setCameras`); state changes go through `updateCamera` / `destroyCamera`;
 * client commands are inspected via the parsed `commands` array.
 *
 * Tests intentionally left behind from gonogo:
 *  - serial-input action tests (nextCamera/prevCamera/zoomIn/zoomOut/panYaw/
 *    panPitch via dispatchAction) -- the public surface is now the
 *    CameraFeedHandle ref; handle-based equivalents cover the same paths.
 *  - CameraFeedConfigPanel -- not part of this package.
 *  - CommNet degrade -- gonogo-only data source integration.
 *  - Station (brokered) mode -- gonogo-only KerbcamDataSource.attachBroker.
 */

import { KerbcamClient } from "@jonpepler/kerbcam";
import type { CameraLifecycle, ClientMessage } from "@jonpepler/kerbcam";
import { type MockCameraInit, MockSidecar } from "@jonpepler/kerbcam/testing";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { createRef } from "react";
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { CameraFeed, type CameraFeedHandle } from "./CameraFeed";
import { KerbcamProvider } from "./context";

// ---------------------------------------------------------------------------
// Camera-state fixture factory
// ---------------------------------------------------------------------------

type CameraStateLike = Record<string, unknown> & { flightId: number };

function makeCamera(overrides: CameraStateLike): CameraStateLike {
  return {
    lifecycle: "active",
    partName: "mumech.MuMechModuleHullCamera",
    partTitle: "Hullcam Mk1",
    cameraName: "Camera",
    vesselName: "Kerbal X",
    layers: ["NEAR", "SCALED"],
    operatorLayers: ["NEAR", "SCALED"],
    renderWidth: 384,
    renderHeight: 384,
    operatorWidth: 384,
    operatorHeight: 384,
    supportsZoom: false,
    fov: 60,
    fovMin: 10,
    fovMax: 90,
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

function toInit(c: CameraStateLike): MockCameraInit {
  return {
    flightId: c.flightId,
    lifecycle: c.lifecycle as CameraLifecycle | undefined,
    partName: c.partName as string | undefined,
    partTitle: c.partTitle as string | undefined,
    cameraName: c.cameraName as string | undefined,
    vesselName: c.vesselName as string | undefined,
    renderWidth: c.renderWidth as number | undefined,
    renderHeight: c.renderHeight as number | undefined,
    operatorWidth: c.operatorWidth as number | undefined,
    operatorHeight: c.operatorHeight as number | undefined,
    supportsZoom: c.supportsZoom as boolean | undefined,
    fov: c.fov as number | undefined,
    fovMin: c.fovMin as number | undefined,
    fovMax: c.fovMax as number | undefined,
    supportsPan: c.supportsPan as boolean | undefined,
    panYaw: c.panYaw as number | undefined,
    panPitch: c.panPitch as number | undefined,
    panYawMin: c.panYawMin as number | undefined,
    panYawMax: c.panYawMax as number | undefined,
    panPitchMin: c.panPitchMin as number | undefined,
    panPitchMax: c.panPitchMax as number | undefined,
    encoderBitrateBps: c.encoderBitrateBps as number | undefined,
    targetBitrateBps: c.targetBitrateBps as number | undefined,
    degradeLevel: c.degradeLevel as number | undefined,
  };
}

// ---------------------------------------------------------------------------
// Fixture: connected KerbcamClient + MockSidecar
// ---------------------------------------------------------------------------

const createdClients: KerbcamClient[] = [];

async function buildConnectedSource(
  cameras: CameraStateLike[] = [
    makeCamera({ flightId: 42, cameraName: "Starboard Cam" }),
  ],
): Promise<{ client: KerbcamClient; sidecar: MockSidecar }> {
  const sidecar = new MockSidecar();
  for (const c of cameras) {
    sidecar.addCamera(toInit(c));
  }
  const client = new KerbcamClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  createdClients.push(client);

  await act(async () => {
    await client.connect([], { slots: 4 });
  });
  await act(async () => {
    sidecar.open();
    sidecar.setConnectionState("connected");
  });
  return { client, sidecar };
}

// ---------------------------------------------------------------------------
// Render helpers
// ---------------------------------------------------------------------------

function renderFeed(
  client: KerbcamClient,
  props: {
    flightId: number | null;
    onSelectCamera?: (id: number) => void;
    showDebugInfo?: boolean;
    renderSize?: "auto" | "none";
    ref?: React.Ref<CameraFeedHandle>;
  },
) {
  const { ref, ...rest } = props;
  return render(
    <KerbcamProvider client={client}>
      <CameraFeed ref={ref ?? null} {...rest} />
    </KerbcamProvider>,
  );
}

// A stateful wrapper: holds `flightId` in state, feeds its own setter as
// `onSelectCamera`. Lets selection tests exercise the real round-trip.
function renderStatefulFeed(
  client: KerbcamClient,
  initial: number | null,
  ref?: React.Ref<CameraFeedHandle>,
) {
  function Harness() {
    const [flightId, setFlightId] = useState<number | null>(initial);
    return (
      <KerbcamProvider client={client}>
        <CameraFeed
          ref={ref ?? null}
          flightId={flightId}
          onSelectCamera={setFlightId}
        />
      </KerbcamProvider>
    );
  }
  return render(<Harness />);
}

// ---------------------------------------------------------------------------
// Global ResizeObserver stub for tests that don't need the controllable one.
// ---------------------------------------------------------------------------

if (typeof globalThis.ResizeObserver === "undefined") {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
}

// ---------------------------------------------------------------------------
// Setup / teardown
// ---------------------------------------------------------------------------

afterEach(() => {
  cleanup();
  // Disconnect tracked clients AFTER cleanup so the widget is unmounted first.
  for (const c of createdClients) {
    try { c.disconnect(); } catch { /* ignore */ }
  }
  createdClients.length = 0;
  vi.restoreAllMocks();
});

// ---------------------------------------------------------------------------
// Camera selection
// ---------------------------------------------------------------------------

describe("CameraFeed - camera selection", () => {
  const THREE_CAMERAS = [
    makeCamera({ flightId: 42, cameraName: "Starboard Cam", vesselName: "Kerbal X" }),
    makeCamera({ flightId: 43, cameraName: "Nose Cam", vesselName: "Kerbal X" }),
    makeCamera({ flightId: 44, cameraName: "Tail Cam", vesselName: "Kerbal X" }),
  ];

  it("lists every available camera in the menu", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    renderFeed(client, { flightId: null });

    fireEvent.click(screen.getByRole("button", { name: /starboard cam/i }));

    const labels = screen
      .getAllByRole("menuitemradio")
      .map((item) => item.textContent);
    expect(labels).toEqual([
      "Starboard Cam (Kerbal X)",
      "Nose Cam (Kerbal X)",
      "Tail Cam (Kerbal X)",
    ]);
  });

  it("caps the menu height and scrolls instead of overflowing", async () => {
    const many = Array.from({ length: 20 }, (_, i) =>
      makeCamera({ flightId: 100 + i, cameraName: `Cam ${String(i + 1).padStart(2, "0")}` }),
    );
    const { client } = await buildConnectedSource(many);

    renderFeed(client, { flightId: null });

    fireEvent.click(screen.getByRole("button", { name: /cam 01/i }));

    const style = getComputedStyle(screen.getByRole("menu"));
    expect(style.overflowY).toBe("auto");
    expect(style.maxHeight).toBe("min(40vh, 300px)");
  });

  it("portals the open menu to document.body so tile clipping cannot cut it off", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    const { container } = renderFeed(client, { flightId: null });

    fireEvent.click(screen.getByRole("button", { name: /starboard cam/i }));

    const menu = screen.getByRole("menu");
    // The menu lives on document.body, outside the tile's DOM subtree...
    expect(menu.parentElement).toBe(document.body);
    expect(container.contains(menu)).toBe(false);
    // ...with fixed positioning so no ancestor overflow can clip it.
    expect(getComputedStyle(menu).position).toBe("fixed");
  });

  it("pointer-down inside the portaled menu keeps it open; outside dismisses it", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    renderFeed(client, { flightId: null });

    fireEvent.click(screen.getByRole("button", { name: /starboard cam/i }));
    expect(screen.getByRole("menu")).toBeTruthy();

    // A press inside the portaled menu is not "outside" even though it is
    // outside the tile's subtree.
    fireEvent.pointerDown(
      screen.getByRole("menuitemradio", { name: /nose cam/i }),
    );
    expect(screen.getByRole("menu")).toBeTruthy();

    // A press anywhere else dismisses.
    fireEvent.pointerDown(document.body);
    expect(screen.queryByRole("menu")).toBeNull();
  });

  it("disambiguates same-named cameras by part title", async () => {
    const { client } = await buildConnectedSource([
      makeCamera({ flightId: 42, cameraName: "NavCam", vesselName: "Kerbal X", partTitle: "NavCam" }),
      makeCamera({ flightId: 43, cameraName: "NavCam", vesselName: "Kerbal X", partTitle: "Clamp-O-Tron Docking Port Jr." }),
      makeCamera({ flightId: 44, cameraName: "Tail Cam", vesselName: "Kerbal X", partTitle: "Some Other Part" }),
    ]);

    renderFeed(client, { flightId: null });

    fireEvent.click(screen.getByRole("button", { name: /navcam/i }));
    const labels = screen
      .getAllByRole("menuitemradio")
      .map((item) => item.textContent);
    expect(labels).toEqual([
      "NavCam (Kerbal X)",
      "NavCam - Clamp-O-Tron Docking Port Jr. (Kerbal X)",
      "Tail Cam (Kerbal X)",
    ]);
  });

  it("auto-selects the first available camera when flightId is null", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    renderFeed(client, { flightId: null });

    expect(screen.getByRole("heading", { name: "Starboard Cam" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /starboard cam/i }));
    const checked = screen.getByRole("menuitemradio", { checked: true });
    expect(checked.textContent).toBe("Starboard Cam (Kerbal X)");
  });

  it("auto-latch does NOT fire onSelectCamera", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    const onSelect = vi.fn();
    renderFeed(client, { flightId: null, onSelectCamera: onSelect });

    // Auto-latch picks Starboard Cam but must never call onSelectCamera.
    expect(screen.getByRole("heading", { name: "Starboard Cam" })).toBeTruthy();
    expect(onSelect).not.toHaveBeenCalled();
  });

  it("honours an explicitly-configured non-top camera", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    renderFeed(client, { flightId: 44 });

    expect(screen.getByRole("heading", { name: "Tail Cam" })).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /tail cam/i }));
    const checked = screen.getByRole("menuitemradio", { checked: true });
    expect(checked.textContent).toBe("Tail Cam (Kerbal X)");
  });

  it("selecting a different camera in the menu persists the choice and switches", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    renderStatefulFeed(client, 42);

    expect(screen.getByRole("heading", { name: "Starboard Cam" })).toBeTruthy();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /starboard cam/i }));
    });
    await act(async () => {
      fireEvent.click(screen.getByRole("menuitemradio", { name: /nose cam/i }));
    });

    expect(screen.getByRole("heading", { name: "Nose Cam" })).toBeTruthy();
    expect(screen.queryByRole("menu")).toBeNull();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /nose cam/i }));
    });
    expect(
      screen.getByRole("menuitemradio", { checked: true }).textContent,
    ).toBe("Nose Cam (Kerbal X)");
  });

  it("Next button advances to the next camera and wraps round", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    renderStatefulFeed(client, 42);

    const next = screen.getByRole("button", { name: /next camera/i });

    await act(async () => { fireEvent.click(next); });
    expect(screen.getByRole("heading", { name: "Nose Cam" })).toBeTruthy();

    await act(async () => { fireEvent.click(next); });
    expect(screen.getByRole("heading", { name: "Tail Cam" })).toBeTruthy();

    // Wrap: third -> first.
    await act(async () => { fireEvent.click(next); });
    expect(screen.getByRole("heading", { name: "Starboard Cam" })).toBeTruthy();
  });

  it("Previous button steps backward and wraps round", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    renderStatefulFeed(client, 42);

    const prev = screen.getByRole("button", { name: /previous camera/i });

    await act(async () => { fireEvent.click(prev); });
    expect(screen.getByRole("heading", { name: "Tail Cam" })).toBeTruthy();
  });

  it("handle.stepCamera(1) advances camera", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    const handleRef = createRef<CameraFeedHandle>();
    renderStatefulFeed(client, 42, handleRef);

    await act(async () => {
      handleRef.current?.stepCamera(1);
    });
    expect(screen.getByRole("heading", { name: "Nose Cam" })).toBeTruthy();

    await act(async () => {
      handleRef.current?.stepCamera(-1);
    });
    expect(screen.getByRole("heading", { name: "Starboard Cam" })).toBeTruthy();
  });

  it("falls back to the first camera when the configured one disappears", async () => {
    const { client, sidecar } = await buildConnectedSource(THREE_CAMERAS);

    renderFeed(client, { flightId: 44 });
    expect(screen.getByRole("heading", { name: "Tail Cam" })).toBeTruthy();

    await act(async () => {
      sidecar.setCameras([
        toInit(makeCamera({ flightId: 42, cameraName: "Starboard Cam" })),
      ]);
    });

    expect(screen.getByRole("heading", { name: "Starboard Cam" })).toBeTruthy();
  });

  it("step buttons are disabled when only one camera is available", async () => {
    const { client } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    expect(
      screen.getByRole<HTMLButtonElement>("button", { name: /next camera/i }).disabled,
    ).toBe(true);
    expect(
      screen.getByRole<HTMLButtonElement>("button", { name: /previous camera/i }).disabled,
    ).toBe(true);
  });

  it("Escape closes the open camera menu", async () => {
    const { client } = await buildConnectedSource(THREE_CAMERAS);

    renderFeed(client, { flightId: 42 });

    fireEvent.click(screen.getByRole("button", { name: /starboard cam/i }));
    expect(screen.getByRole("menu")).toBeTruthy();

    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("menu")).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Debug-info toggle
// ---------------------------------------------------------------------------

describe("CameraFeed - debug info toggle", () => {
  it("hides the resolution/bitrate readout by default", async () => {
    const { client } = await buildConnectedSource([
      makeCamera({ flightId: 42, cameraName: "Starboard Cam", vesselName: "Kerbal X", renderWidth: 640, renderHeight: 360 }),
    ]);

    renderFeed(client, { flightId: 42 });

    expect(screen.queryByText(/640×360/)).toBeNull();
  });

  it("shows the resolution/bitrate readout when showDebugInfo is true", async () => {
    const { client } = await buildConnectedSource([
      makeCamera({ flightId: 42, cameraName: "Starboard Cam", vesselName: "Kerbal X", renderWidth: 640, renderHeight: 360 }),
    ]);

    renderFeed(client, { flightId: 42, showDebugInfo: true });

    expect(screen.getByText(/640×360/)).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------
// Empty state and status
// ---------------------------------------------------------------------------

describe("CameraFeed - empty state and status", () => {
  it("shows the no-cameras empty state and hides the menu trigger when connected with no cameras", async () => {
    const { client } = await buildConnectedSource([]);

    renderFeed(client, { flightId: null });

    expect(screen.queryByRole("button", { name: /camera feed/i })).toBeNull();
    expect(document.querySelector("video")).toBeNull();
    expect(screen.getByText(/start a vessel with hullcam parts/i)).toBeTruthy();
  });

  it("renders the empty state gracefully when the source is disconnected", async () => {
    const sidecar = new MockSidecar();
    const client = new KerbcamClient(
      { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
      sidecar.createTransport(),
    );
    createdClients.push(client);

    render(
      <KerbcamProvider client={client}>
        <CameraFeed flightId={null} />
      </KerbcamProvider>,
    );

    expect(screen.getByText(/start a vessel with hullcam parts/i)).toBeTruthy();
    expect(
      screen.queryByRole("status", { name: /connected|disconnected/i }),
    ).toBeNull();
  });

  it("shows a custom emptyMessage when provided", async () => {
    const { client } = await buildConnectedSource([]);

    renderFeed(client, { flightId: null, emptyMessage: "Custom empty message" });

    expect(screen.getByText("Custom empty message")).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------
// SIGNAL LOST overlay
// ---------------------------------------------------------------------------

describe("CameraFeed - SIGNAL LOST overlay", () => {
  it("does not render SIGNAL LOST overlay when camera is active", async () => {
    const { client } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    expect(screen.queryByRole("status", { name: /signal lost/i })).toBeNull();
  });

  it("renders SIGNAL LOST overlay when camera lifecycle transitions to destroyed", async () => {
    const { client, sidecar } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    expect(screen.queryByRole("status", { name: /signal lost/i })).toBeNull();

    await act(async () => {
      sidecar.destroyCamera(42);
    });

    const overlay = screen.getByRole("status", { name: /signal lost/i });
    expect(overlay).toBeTruthy();
    expect(overlay.textContent).toMatch(/SIGNAL LOST/i);
  });

  it("renders SIGNAL LOST overlay from initial snapshot when camera starts destroyed", async () => {
    const { client } = await buildConnectedSource([
      makeCamera({
        flightId: 99,
        lifecycle: "destroyed",
        partTitle: "Hullcam Mk2",
        cameraName: "Nose Cam",
        vesselName: "Debris Field",
        layers: [],
        operatorLayers: [],
        renderWidth: 0,
        renderHeight: 0,
        operatorWidth: 0,
        operatorHeight: 0,
        fov: 0,
        encoderBitrateBps: 0,
      }),
    ]);

    renderFeed(client, { flightId: 99 });

    const overlay = await screen.findByRole("status", { name: /signal lost/i });
    expect(overlay).toBeTruthy();
    expect(overlay.textContent).toMatch(/SIGNAL LOST/i);
  });
});

// ---------------------------------------------------------------------------
// Zoom controls
// ---------------------------------------------------------------------------

describe("CameraFeed - zoom controls", () => {
  it("zoom controls appear when camera supportsZoom", async () => {
    const { client, sidecar } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    await act(async () => {
      sidecar.updateCamera(42, { supportsZoom: true });
    });

    expect(screen.getByRole("button", { name: /zoom in/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /zoom out/i })).toBeTruthy();
  });

  it("zoom controls absent when camera does not support zoom", async () => {
    const { client } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    expect(screen.queryByRole("button", { name: /zoom in/i })).toBeNull();
  });

  it("clicking the on-screen + button fires a discrete set-fov step", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, { supportsZoom: true, fov: 60 });
    });

    renderFeed(client, { flightId: 42 });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }));
    });
    expect(sidecar.lastCommand("set-fov")?.content).toMatchObject({
      flightId: 42,
      fov: 55, // 60 - 5 (zoom in, FoV decreases)
    });
    expect(sidecar.lastCommand("set-zoom-rate")).toBeUndefined();

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /zoom out/i }));
    });
    // Accumulates against the optimistic FoV (55 -> 60).
    expect(sidecar.lastCommand("set-fov")?.content.fov).toBe(60);
  });

  it("keyboard-activating a zoom button (no pointer events) fires a discrete nudge", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, { supportsZoom: true, fov: 60 });
    });

    renderFeed(client, { flightId: 42 });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /zoom in/i }), { detail: 0 });
    });
    expect(sidecar.lastCommand("set-fov")?.content).toMatchObject({
      flightId: 42,
      fov: 55,
    });
    expect(sidecar.lastCommand("set-zoom-rate")).toBeUndefined();
  });

  it("handle.setZoomRate(1) holds a zoom-in rate; setZoomRate(0) stops it", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, { supportsZoom: true, fov: 60 });
    });

    const handleRef = createRef<CameraFeedHandle>();
    renderFeed(client, { flightId: 42, ref: handleRef });

    await act(async () => {
      handleRef.current?.setZoomRate(1);
    });
    expect(sidecar.lastCommand("set-zoom-rate")?.content).toMatchObject({
      flightId: 42,
      rate: 1,
    });

    await act(async () => {
      handleRef.current?.setZoomRate(0);
    });
    expect(sidecar.lastCommand("set-zoom-rate")?.content.rate).toBe(0);
  });

  it("handle.setZoomRate is a no-op when camera does not support zoom", async () => {
    const { client, sidecar } = await buildConnectedSource();

    const handleRef = createRef<CameraFeedHandle>();
    renderFeed(client, { flightId: 42, ref: handleRef });

    await act(async () => {
      handleRef.current?.setZoomRate(1);
    });

    expect(sidecar.lastCommand("set-zoom-rate")).toBeUndefined();
  });

  it("a release with no prior press emits no command (rate already 0)", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, { supportsZoom: true, fov: 60 });
    });

    const handleRef = createRef<CameraFeedHandle>();
    renderFeed(client, { flightId: 42, ref: handleRef });

    await act(async () => {
      handleRef.current?.setZoomRate(0);
    });

    // sendZoomRate dedupes against the last-sent rate (0).
    expect(sidecar.lastCommand("set-zoom-rate")).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// Vertical FoV (zoom) slider
// ---------------------------------------------------------------------------

describe("CameraFeed - vertical FoV (zoom) slider", () => {
  const PAN_ZOOM_CAMERA = {
    flightId: 42,
    cameraName: "Gimbal Cam",
    supportsPan: true,
    panYaw: 0,
    panPitch: 0,
    panYawMin: -45,
    panYawMax: 45,
    panPitchMin: -30,
    panPitchMax: 30,
    supportsZoom: true,
    fov: 60,
    fovMin: 10,
    fovMax: 90,
  };

  it("shows a single zoom slider (no yaw/pitch sliders) when zoom is supported", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_ZOOM_CAMERA);
    });

    renderFeed(client, { flightId: 42 });

    expect(screen.getByRole("slider", { name: /zoom/i })).toBeTruthy();
    expect(screen.queryByRole("slider", { name: /pan/i })).toBeNull();
  });

  it("does not show the zoom slider when the camera has no zoom", async () => {
    const { client } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    expect(screen.queryByRole("slider", { name: /zoom/i })).toBeNull();
  });

  it("zoom slider min/max reflect the camera FoV range and initial value", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_ZOOM_CAMERA);
    });

    renderFeed(client, { flightId: 42 });

    const fovSlider = screen.getByRole<HTMLInputElement>("slider", { name: /zoom/i });
    expect(Number(fovSlider.min)).toBe(10);
    expect(Number(fovSlider.max)).toBe(90);
    expect(Number(fovSlider.value)).toBe(60);
  });

  it("zoom slider commits the settled absolute set-fov on release (debounced)", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_ZOOM_CAMERA);
    });

    renderFeed(client, { flightId: 42 });

    const fovSlider = screen.getByRole("slider", { name: /zoom/i });

    await act(async () => {
      fireEvent.pointerDown(fovSlider);
      fireEvent.change(fovSlider, { target: { value: "50" } });
      fireEvent.change(fovSlider, { target: { value: "30" } });
    });
    expect(sidecar.lastCommand("set-fov")).toBeUndefined();

    await act(async () => {
      fireEvent.pointerUp(fovSlider);
    });
    expect(sidecar.lastCommand("set-fov")?.content).toMatchObject({
      flightId: 42,
      fov: 30,
    });
  });

  it("zoom slider commits the settled value after a pause (no pointer release)", async () => {
    vi.useFakeTimers();
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_ZOOM_CAMERA);
    });

    renderFeed(client, { flightId: 42 });

    const fovSlider = screen.getByRole("slider", { name: /zoom/i });

    await act(async () => {
      fireEvent.change(fovSlider, { target: { value: "42" } });
    });
    expect(sidecar.lastCommand("set-fov")).toBeUndefined();

    await act(async () => {
      vi.advanceTimersByTime(150); // past FOV_SLIDER_DEBOUNCE_MS (120)
    });
    expect(sidecar.lastCommand("set-fov")?.content.fov).toBe(42);

    vi.useRealTimers();
  });

  it("holding a zoom button sends a constant rate; releasing stops it", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, { supportsZoom: true, fov: 60 });
    });

    renderFeed(client, { flightId: 42 });

    const zoomInBtn = screen.getByRole("button", { name: /zoom in/i });

    await act(async () => {
      fireEvent.pointerDown(zoomInBtn);
    });
    expect(sidecar.lastCommand("set-zoom-rate")?.content).toMatchObject({
      flightId: 42,
      rate: 1,
    });

    await act(async () => {
      fireEvent.pointerUp(zoomInBtn);
    });
    expect(sidecar.lastCommand("set-zoom-rate")?.content.rate).toBe(0);
  });

  it("zoom slider thumb tracks the camera echo when not dragging", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_ZOOM_CAMERA);
    });

    renderFeed(client, { flightId: 42 });

    await act(async () => {
      sidecar.updateCamera(42, { fov: 45 });
    });

    const fovSlider = screen.getByRole<HTMLInputElement>("slider", { name: /zoom/i });
    expect(Number(fovSlider.value)).toBe(45);
  });
});

// ---------------------------------------------------------------------------
// ResizeObserver render-size feedback
// ---------------------------------------------------------------------------

describe("CameraFeed - ResizeObserver render-size feedback", () => {
  let resizeCallback:
    | ((entries: ResizeObserverEntry[], observer: ResizeObserver) => void)
    | undefined;
  const originalResizeObserver = globalThis.ResizeObserver;

  beforeEach(() => {
    vi.useFakeTimers();
    globalThis.ResizeObserver = class {
      constructor(
        cb: (entries: ResizeObserverEntry[], observer: ResizeObserver) => void,
      ) {
        resizeCallback = cb;
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
  });

  afterEach(() => {
    vi.useRealTimers();
    globalThis.ResizeObserver = originalResizeObserver;
    resizeCallback = undefined;
  });

  it("render-size observer fires set-render-size command", async () => {
    const { client, sidecar } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    await act(async () => {
      resizeCallback?.(
        [{ contentRect: { width: 400, height: 400 } }] as ResizeObserverEntry[],
        {} as ResizeObserver,
      );
    });

    await act(async () => {
      vi.advanceTimersByTime(501);
    });

    const renderSizeMsg = sidecar.lastCommand("set-render-size");
    expect(renderSizeMsg).toBeTruthy();
    // Width as-is (already even); height derived 16:9 then rounded to even px:
    // 400 * 9/16 = 225 -> 226.
    expect(renderSizeMsg?.content.width).toBe(400);
    expect(renderSizeMsg?.content.height).toBe(226);
  });
});

// ---------------------------------------------------------------------------
// Pan reticle
// ---------------------------------------------------------------------------

describe("CameraFeed - pan reticle", () => {
  const PAN_FLIP = {
    supportsPan: true,
    panYawMin: -45,
    panYawMax: 45,
    panPitchMin: -30,
    panPitchMax: 30,
  };

  it("pan controls appear when camera supportsPan", async () => {
    const { client, sidecar } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    await act(async () => {
      sidecar.updateCamera(42, PAN_FLIP);
    });

    expect(screen.getByRole("button", { name: /pan left/i })).toBeTruthy();
    expect(
      (screen.getByRole("button", { name: /pan up/i }) as HTMLButtonElement).disabled,
    ).toBe(false);
  });

  it("pan controls absent when camera does not support pan", async () => {
    const { client } = await buildConnectedSource();

    renderFeed(client, { flightId: 42 });

    expect(screen.queryByRole("button", { name: /pan left/i })).toBeNull();
  });

  const panRateMsgs = (sidecar: MockSidecar) =>
    sidecar.commands.filter(
      (c): c is Extract<ClientMessage, { type: "set-pan-rate" }> =>
        c.type === "set-pan-rate",
    );

  it("clicking a pan arrow moves one discrete step (absolute set-pan, no rate)", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_FLIP);
    });

    renderFeed(client, { flightId: 42 });

    const leftArrow = screen.getByRole("button", { name: /pan left/i });

    await act(async () => {
      fireEvent.click(leftArrow);
    });
    expect(panRateMsgs(sidecar)).toHaveLength(0);
    expect(sidecar.lastCommand("set-pan")?.content).toMatchObject({
      flightId: 42,
      yaw: -5, // left = negative yaw
      pitch: 0,
    });

    await act(async () => {
      fireEvent.click(leftArrow);
    });
    expect(sidecar.lastCommand("set-pan")?.content.yaw).toBe(-10);
  });

  it("dragging the pan ball sends a proportional set-pan-rate; release sends 0", async () => {
    const origCapture = HTMLElement.prototype.setPointerCapture;
    HTMLElement.prototype.setPointerCapture = vi.fn();

    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_FLIP);
    });

    renderFeed(client, { flightId: 42 });

    const ball = screen.getByTitle("Drag to pan");

    await act(async () => {
      fireEvent.pointerDown(ball, { clientX: 100, clientY: 100 });
    });
    expect(panRateMsgs(sidecar)).toHaveLength(0);

    // Deflect fully right (40px >= PAN_BALL_RADIUS => rate clamps to +1).
    await act(async () => {
      fireEvent.pointerMove(ball, { clientX: 140, clientY: 100 });
    });
    const moved = panRateMsgs(sidecar);
    expect(moved.length).toBeGreaterThan(0);
    expect(moved.at(-1)?.content).toMatchObject({ flightId: 42, yawRate: 1 });
    expect(moved.at(-1)?.content.pitchRate).toBe(0);

    await act(async () => {
      fireEvent.pointerUp(ball);
    });
    expect(panRateMsgs(sidecar).at(-1)?.content).toMatchObject({
      yawRate: 0,
      pitchRate: 0,
    });

    HTMLElement.prototype.setPointerCapture = origCapture;
  });

  it("handle.setPanAxis('yaw', 0.5) sends set-pan-rate on the yaw axis", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_FLIP);
    });

    const handleRef = createRef<CameraFeedHandle>();
    renderFeed(client, { flightId: 42, ref: handleRef });

    await act(async () => {
      handleRef.current?.setPanAxis("yaw", 0.5);
    });

    expect(sidecar.lastCommand("set-pan-rate")?.content).toMatchObject({
      flightId: 42,
      yawRate: 0.5,
      pitchRate: 0,
    });
  });

  it("handle.setPanAxis('pitch', 0.5) sends set-pan-rate on the pitch axis", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_FLIP);
    });

    const handleRef = createRef<CameraFeedHandle>();
    renderFeed(client, { flightId: 42, ref: handleRef });

    await act(async () => {
      handleRef.current?.setPanAxis("pitch", 0.5);
    });

    expect(sidecar.lastCommand("set-pan-rate")?.content).toMatchObject({
      flightId: 42,
      yawRate: 0,
      pitchRate: 0.5,
    });
  });

  it("yaw and pitch axes compose -- setting one preserves the other", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_FLIP);
    });

    const handleRef = createRef<CameraFeedHandle>();
    renderFeed(client, { flightId: 42, ref: handleRef });

    await act(async () => {
      handleRef.current?.setPanAxis("yaw", 1);
    });
    await act(async () => {
      handleRef.current?.setPanAxis("pitch", 0.5);
    });

    expect(sidecar.lastCommand("set-pan-rate")?.content).toMatchObject({
      yawRate: 1,
      pitchRate: 0.5,
    });
  });

  it("a tiny analog deflection inside the deadzone sends no command", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_FLIP);
    });

    const handleRef = createRef<CameraFeedHandle>();
    renderFeed(client, { flightId: 42, ref: handleRef });

    // 0.02 < ANALOG_DEADZONE (0.05) -> snapped to 0 -> deduped against 0 -> no traffic.
    await act(async () => {
      handleRef.current?.setPanAxis("yaw", 0.02);
    });

    expect(panRateMsgs(sidecar)).toHaveLength(0);
  });

  it("handle.setPanAxis is a no-op when camera does not support pan", async () => {
    // Default camera has supportsPan: false.
    const { client, sidecar } = await buildConnectedSource();

    const handleRef = createRef<CameraFeedHandle>();
    renderFeed(client, { flightId: 42, ref: handleRef });

    await act(async () => {
      handleRef.current?.setPanAxis("yaw", 1);
    });

    expect(panRateMsgs(sidecar)).toHaveLength(0);
  });

  it("unmounting mid-pan stops the rate so the plugin doesn't run away", async () => {
    const { client, sidecar } = await buildConnectedSource();

    await act(async () => {
      sidecar.updateCamera(42, PAN_FLIP);
    });

    const handleRef = createRef<CameraFeedHandle>();
    const { unmount } = renderFeed(client, { flightId: 42, ref: handleRef });

    await act(async () => {
      handleRef.current?.setPanAxis("yaw", 1);
    });
    expect(panRateMsgs(sidecar).at(-1)?.content.yawRate).toBe(1);

    await act(async () => {
      unmount();
    });
    expect(panRateMsgs(sidecar).at(-1)?.content).toMatchObject({
      flightId: 42,
      yawRate: 0,
      pitchRate: 0,
    });
  });
});

// ---------------------------------------------------------------------------
// client prop override
// ---------------------------------------------------------------------------

describe("CameraFeed - client prop override", () => {
  it("accepts a client prop instead of a provider", async () => {
    const { client } = await buildConnectedSource([
      makeCamera({ flightId: 42, cameraName: "Override Cam", vesselName: "Test" }),
    ]);

    // Render without a KerbcamProvider -- client prop provides it implicitly.
    render(<CameraFeed client={client} flightId={42} />);

    await waitFor(() => {
      expect(screen.getByRole("heading", { name: "Override Cam" })).toBeTruthy();
    });
  });
});
