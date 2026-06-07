/**
 * Tests for the kerbcam-web App.
 *
 * All tests drive the real KerbcamClient + real hooks through MockSidecar.
 * The DOM stubs are installed in src/test/setup.ts via installDomStubs().
 *
 * Pattern mirrors client-sdk/react/src/CameraFeed.test.tsx but with a key
 * difference: the App mounts its own ConnectionManager which calls
 * client.connect(). So tests do NOT pre-connect the client; instead they let
 * App mount, drive sidecar.open() + setConnectionState(), and assert the
 * resulting UI state.
 */

import { KerbcamClient } from "@jonpepler/kerbcam";
import type { CameraLifecycle } from "@jonpepler/kerbcam";
import { Layer } from "@jonpepler/kerbcam";
import type { MockCameraInit } from "@jonpepler/kerbcam/testing";
import { MockSidecar } from "@jonpepler/kerbcam/testing";
import { act, cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "./App";

// ---------------------------------------------------------------------------
// Camera fixture factory
// ---------------------------------------------------------------------------

function makeCamera(overrides: MockCameraInit): MockCameraInit {
  return {
    lifecycle: "active" as CameraLifecycle,
    partName: "mumech.MuMechModuleHullCamera",
    partTitle: "Hullcam Mk1",
    cameraName: "Camera",
    vesselName: "Kerbal X",
    layers: [Layer.Near, Layer.Scaled],
    operatorLayers: [Layer.Near, Layer.Scaled],
    renderWidth: 640,
    renderHeight: 360,
    operatorWidth: 640,
    operatorHeight: 360,
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

// ---------------------------------------------------------------------------
// Fixture: bare client + sidecar (NOT pre-connected)
//
// The App mounts its own ConnectionManager which drives client.connect().
// Tests render the App, wait for the connect promise to settle, then call
// sidecar.open() + setConnectionState() to complete the mock handshake.
// ---------------------------------------------------------------------------

const createdClients: KerbcamClient[] = [];

interface AppFixture {
  client: KerbcamClient;
  sidecar: MockSidecar;
  /** Open the mock channel and fire connected state. */
  openSidecar(): void;
}

function buildFixture(cameras: MockCameraInit[] = []): AppFixture {
  const sidecar = new MockSidecar();
  sidecar.withSlots(["0", "1", "2", "3", "4", "5", "6", "7"]);
  for (const cam of cameras) sidecar.addCamera(cam);

  const client = new KerbcamClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  createdClients.push(client);

  return {
    client,
    sidecar,
    openSidecar: () => {
      sidecar.open();
      sidecar.setConnectionState("connected");
    },
  };
}

async function renderApp(client: KerbcamClient) {
  let result: ReturnType<typeof render>;
  await act(async () => {
    result = render(<App client={client} />);
  });
  return result!;
}

// ---------------------------------------------------------------------------
// Storage helpers
// ---------------------------------------------------------------------------

function clearStorage() {
  localStorage.clear();
}

// ---------------------------------------------------------------------------
// Setup / teardown
// ---------------------------------------------------------------------------

beforeEach(() => {
  clearStorage();
  vi.restoreAllMocks();
  // Stub fetch so DevPanel /profile poll and any connect-time fetches don't throw
  vi.stubGlobal("fetch", async (url: unknown) => {
    const urlStr = String(typeof url === "string" ? url : (url as { url?: string }).url ?? url);
    if (urlStr.endsWith("/profile")) {
      return new Response(
        JSON.stringify({ staggerBudget: 3, kerbcamFrameMs: 0.15, kspFps: 60 }),
        { status: 200, headers: { "Content-Type": "application/json" } },
      );
    }
    return new Response("not found", { status: 404 });
  });
});

afterEach(() => {
  cleanup();
  for (const c of createdClients) {
    try { c.disconnect(); } catch { /* ignore */ }
  }
  createdClients.length = 0;
  vi.restoreAllMocks();
  clearStorage();
  // Restore data-theme if set
  document.documentElement.removeAttribute("data-theme");
});

// ---------------------------------------------------------------------------
// Connect flow
// ---------------------------------------------------------------------------

describe("App - connect flow", () => {
  it("shows connected status after sidecar opens", async () => {
    const { client, openSidecar } = buildFixture([]);
    await renderApp(client);

    // Let connection manager's _connect() fire, then open the sidecar
    await act(async () => {
      openSidecar();
    });

    await waitFor(() => {
      expect(screen.getByText(/connected/i)).toBeTruthy();
    });
  });

  it("renders one tile per camera (cap 4) after snapshot with 4 cameras", async () => {
    const cameras = [
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
      makeCamera({ flightId: 2, cameraName: "Bravo" }),
      makeCamera({ flightId: 3, cameraName: "Charlie" }),
      makeCamera({ flightId: 4, cameraName: "Delta" }),
    ];
    const { client, openSidecar } = buildFixture(cameras);
    await renderApp(client);

    await act(async () => {
      openSidecar();
    });

    await waitFor(() => {
      expect(screen.getAllByRole("button", { name: /remove tile/i })).toHaveLength(4);
    });
  });

  it("caps auto-seeded tiles at 4 when 6 cameras arrive", async () => {
    const cameras = Array.from({ length: 6 }, (_, i) =>
      makeCamera({ flightId: i + 1, cameraName: `Cam ${i + 1}` }),
    );
    const { client, openSidecar } = buildFixture(cameras);
    await renderApp(client);

    await act(async () => {
      openSidecar();
    });

    await waitFor(() => {
      expect(screen.getAllByRole("button", { name: /remove tile/i })).toHaveLength(4);
    });
  });

  it("shows add-tile button when there are fewer than 8 tiles", async () => {
    const { client, openSidecar } = buildFixture([
      makeCamera({ flightId: 1, cameraName: "Solo" }),
    ]);
    await renderApp(client);

    await act(async () => {
      openSidecar();
    });

    await waitFor(() => {
      expect(screen.getByRole("button", { name: /add tile/i })).toBeTruthy();
    });
  });

  it("header shows sidecar version and encoder backend after connect", async () => {
    const { client, openSidecar } = buildFixture([]);
    await renderApp(client);

    await act(async () => {
      openSidecar();
    });

    // MockSidecar sends sidecarVersion="0.0.1-mock" and encoderBackend="mock"
    // in its hello message; these appear in the header after cameras-change fires.
    await waitFor(() => {
      expect(screen.getByText(/0\.0\.1-mock/)).toBeTruthy();
    });
  });
});

// ---------------------------------------------------------------------------
// Tile management
// ---------------------------------------------------------------------------

describe("App - tile management", () => {
  it("adding a tile persists to localStorage and renders a new tile", async () => {
    const { client, openSidecar } = buildFixture([
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
    ]);
    await renderApp(client);

    await act(async () => {
      openSidecar();
    });

    await waitFor(() => {
      expect(screen.getAllByRole("button", { name: /remove tile/i })).toHaveLength(1);
    });

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /add tile/i }));
    });

    expect(screen.getAllByRole("button", { name: /remove tile/i })).toHaveLength(2);
    const stored = JSON.parse(localStorage.getItem("kerbcam:tiles") ?? "[]") as unknown[];
    expect(stored).toHaveLength(2);
  });

  it("removing a tile persists to localStorage", async () => {
    const { client, openSidecar } = buildFixture([
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
      makeCamera({ flightId: 2, cameraName: "Bravo" }),
    ]);
    await renderApp(client);

    await act(async () => {
      openSidecar();
    });

    await waitFor(() => {
      expect(screen.getAllByRole("button", { name: /remove tile/i })).toHaveLength(2);
    });

    const removeButtons = screen.getAllByRole("button", { name: /remove tile/i });
    await act(async () => {
      fireEvent.click(removeButtons[0]);
    });

    expect(screen.getAllByRole("button", { name: /remove tile/i })).toHaveLength(1);
    const stored = JSON.parse(localStorage.getItem("kerbcam:tiles") ?? "[]") as unknown[];
    expect(stored).toHaveLength(1);
  });

  it("does not reseed tiles when localStorage already has a value", async () => {
    localStorage.setItem(
      "kerbcam:tiles",
      JSON.stringify([{ flightId: 1 }, { flightId: 2 }]),
    );

    const cameras = [
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
      makeCamera({ flightId: 2, cameraName: "Bravo" }),
      makeCamera({ flightId: 3, cameraName: "Charlie" }),
    ];
    const { client, openSidecar } = buildFixture(cameras);
    await renderApp(client);

    await act(async () => {
      openSidecar();
    });

    await waitFor(() => {
      // Only 2 tiles from storage, not 3 from cameras
      expect(screen.getAllByRole("button", { name: /remove tile/i })).toHaveLength(2);
    });
  });

  it("respects a stored empty-tile array (user removed all)", async () => {
    localStorage.setItem("kerbcam:tiles", JSON.stringify([]));

    const { client, openSidecar } = buildFixture([
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
    ]);
    await renderApp(client);

    await act(async () => {
      openSidecar();
    });

    // Wait for camera list to arrive (seeder would run if not suppressed)
    await new Promise((r) => setTimeout(r, 50));

    expect(screen.queryAllByRole("button", { name: /remove tile/i })).toHaveLength(0);
    expect(screen.getByRole("button", { name: /add tile/i })).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------
// Settings: theme + debug
// ---------------------------------------------------------------------------

describe("App - settings", () => {
  it("gear button opens the settings panel", async () => {
    const { client, openSidecar } = buildFixture([]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    fireEvent.click(screen.getByRole("button", { name: /settings/i }));
    await waitFor(() => {
      expect(screen.getByRole("dialog", { name: /settings/i })).toBeTruthy();
    });
  });

  it("selecting dark theme sets data-theme=dark on html and persists", async () => {
    const { client, openSidecar } = buildFixture([]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    fireEvent.click(screen.getByRole("button", { name: /settings/i }));
    await waitFor(() => screen.getByRole("dialog"));

    const select = screen.getByRole("combobox") as HTMLSelectElement;
    await act(async () => {
      fireEvent.change(select, { target: { value: "dark" } });
    });

    expect(document.documentElement.getAttribute("data-theme")).toBe("dark");
    expect(localStorage.getItem("kerbcam:theme")).toBe("dark");
  });

  it("selecting auto theme removes data-theme from html", async () => {
    document.documentElement.setAttribute("data-theme", "dark");
    localStorage.setItem("kerbcam:theme", "dark");

    const { client, openSidecar } = buildFixture([]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    fireEvent.click(screen.getByRole("button", { name: /settings/i }));
    await waitFor(() => screen.getByRole("dialog"));

    const select = screen.getByRole("combobox") as HTMLSelectElement;
    await act(async () => {
      fireEvent.change(select, { target: { value: "auto" } });
    });

    expect(document.documentElement.getAttribute("data-theme")).toBeNull();
  });

  it("debug toggle reveals the developer panel", async () => {
    const { client, openSidecar } = buildFixture([
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
    ]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    // No debug panel initially
    expect(screen.queryByRole("region", { name: /developer panel/i })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: /settings/i }));
    await waitFor(() => screen.getByRole("dialog"));

    const debugCb = screen.getByRole("checkbox", { name: /show debug info/i });
    await act(async () => {
      fireEvent.click(debugCb);
    });

    await waitFor(() => {
      expect(screen.getByRole("region", { name: /developer panel/i })).toBeTruthy();
    });
  });

  it("debug setting persists to localStorage", async () => {
    const { client, openSidecar } = buildFixture([]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    fireEvent.click(screen.getByRole("button", { name: /settings/i }));
    await waitFor(() => screen.getByRole("dialog"));

    const debugCb = screen.getByRole("checkbox", { name: /show debug info/i });
    await act(async () => {
      fireEvent.click(debugCb);
    });

    expect(localStorage.getItem("kerbcam:debug")).toBe("true");
  });
});

// ---------------------------------------------------------------------------
// Dev panel: layer checkboxes + degrade slider
// ---------------------------------------------------------------------------

describe("App - dev panel controls", () => {
  async function openDebugWithCamera() {
    const { client, sidecar, openSidecar } = buildFixture([
      makeCamera({
        flightId: 42,
        cameraName: "Test Cam",
        layers: [Layer.Near, Layer.Scaled],
        operatorLayers: [Layer.Near, Layer.Scaled],
      }),
    ]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    // Open settings and enable debug
    fireEvent.click(screen.getByRole("button", { name: /settings/i }));
    await waitFor(() => screen.getByRole("dialog"));
    await act(async () => {
      fireEvent.click(screen.getByRole("checkbox", { name: /show debug info/i }));
    });

    await waitFor(() => screen.getByRole("region", { name: /developer panel/i }));
    return { client, sidecar };
  }

  it("layer checkbox sends set-layers when unchecked", async () => {
    const { sidecar } = await openDebugWithCamera();

    const nearCb = screen.getByRole("checkbox", { name: /near layer/i }) as HTMLInputElement;
    expect(nearCb.checked).toBe(true);

    await act(async () => {
      fireEvent.click(nearCb);
    });

    const cmd = sidecar.lastCommand("set-layers", 42);
    expect(cmd).toBeTruthy();
    expect(cmd?.content.layers).not.toContain(Layer.Near);
  });

  it("auto-shed marker appears when operatorLayers has a layer not in layers", async () => {
    const { sidecar } = await openDebugWithCamera();

    // Push a state where SCALED is requested but not effective (shed)
    await act(async () => {
      sidecar.updateCamera(42, {
        layers: [Layer.Near],
        operatorLayers: [Layer.Near, Layer.Scaled],
      });
    });

    await waitFor(() => {
      expect(screen.getAllByLabelText("auto-shed").length).toBeGreaterThan(0);
    });
  });

  it("degrade slider sends set-degrade on change", async () => {
    const { sidecar } = await openDebugWithCamera();

    const slider = screen.getByRole("slider", { name: /degrade/i });
    await act(async () => {
      fireEvent.change(slider, { target: { value: "50" } });
    });

    const cmd = sidecar.lastCommand("set-degrade", 42);
    expect(cmd).toBeTruthy();
    expect(cmd?.content.level).toBeCloseTo(0.5, 1);
  });
});

// ---------------------------------------------------------------------------
// Adaptive-shed banner
// ---------------------------------------------------------------------------

describe("App - adaptive-shed banner", () => {
  it("shows banner when level > 0", async () => {
    const { client, sidecar, openSidecar } = buildFixture([]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    await act(async () => {
      sidecar.fireAdaptiveShed({ level: 1, kspFps: 28, reason: "ksp-fps-low" });
    });

    await waitFor(() => {
      expect(screen.getByRole("alert")).toBeTruthy();
      expect(screen.getByText(/quality reduced/i)).toBeTruthy();
    });
  });

  it("banner is dismissed on the x button", async () => {
    const { client, sidecar, openSidecar } = buildFixture([]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    await act(async () => {
      sidecar.fireAdaptiveShed({ level: 1, kspFps: 28, reason: "ksp-fps-low" });
    });

    await waitFor(() => screen.getByRole("alert"));

    await act(async () => {
      fireEvent.click(screen.getByRole("button", { name: /dismiss quality warning/i }));
    });

    expect(screen.queryByRole("alert")).toBeNull();
  });

  it("banner auto-clears when level 0 event arrives", async () => {
    const { client, sidecar, openSidecar } = buildFixture([]);
    await renderApp(client);
    await act(async () => { openSidecar(); });

    await act(async () => {
      sidecar.fireAdaptiveShed({ level: 1, kspFps: 28, reason: "ksp-fps-low" });
    });
    await waitFor(() => screen.getByRole("alert"));

    await act(async () => {
      sidecar.fireAdaptiveShed({ level: 0, kspFps: 58, reason: "ksp-fps-recovered" });
    });

    await waitFor(() => {
      expect(screen.queryByRole("alert")).toBeNull();
    });
  });
});

// ---------------------------------------------------------------------------
// Reconnect behaviour
// ---------------------------------------------------------------------------

describe("App - reconnect", () => {
  it("shows reconnecting status when peer state fails", async () => {
    const { client, sidecar, openSidecar } = buildFixture([]);
    await renderApp(client);
    await act(async () => { openSidecar(); });
    await waitFor(() => screen.getByText(/connected/i));

    await act(async () => {
      sidecar.setConnectionState("failed");
    });

    await waitFor(() => {
      const text = document.body.textContent ?? "";
      expect(text).toMatch(/reconnecting|disconnected/i);
    });
  });
});

// ---------------------------------------------------------------------------
// Watchdog
// ---------------------------------------------------------------------------

describe("App - ping watchdog", () => {
  it("no ping for 15s triggers disconnect + scheduled reconnect", async () => {
    vi.useFakeTimers();

    const { client, openSidecar } = buildFixture([]);

    // Render + connect with fake timers active
    await act(async () => {
      render(<App client={client} />);
    });
    await act(async () => {
      openSidecar();
    });

    // Flush all timers + microtasks to reach "connected"
    await act(async () => {
      vi.runAllTicks();
    });

    // Now advance past watchdog (15s)
    await act(async () => {
      vi.advanceTimersByTime(15_001);
      vi.runAllTicks();
    });

    // Status should now be reconnecting or disconnected
    const text = document.body.textContent ?? "";
    expect(text).toMatch(/reconnecting|disconnected/i);

    vi.useRealTimers();
  });

  it("ping resets the watchdog timer so connection stays alive", async () => {
    vi.useFakeTimers();

    const { client, sidecar, openSidecar } = buildFixture([]);

    await act(async () => {
      render(<App client={client} />);
    });
    await act(async () => {
      openSidecar();
    });
    await act(async () => {
      vi.runAllTicks();
    });

    // Advance 10s (well within 15s watchdog), fire a ping, advance 10 more
    await act(async () => { vi.advanceTimersByTime(10_000); });
    await act(async () => { sidecar.firePing(); });
    await act(async () => { vi.advanceTimersByTime(10_000); });

    // Still connected since last ping was 10s ago (watchdog resets on ping)
    const text = document.body.textContent ?? "";
    expect(text).toMatch(/connected/i);
    expect(text).not.toMatch(/reconnecting/i);

    vi.useRealTimers();
  });
});

// ---------------------------------------------------------------------------
// Shared-subscribe refcount (carried-forward gap from design doc)
//
// These tests verify the KerbcamProvider refcounting at the CameraFeed level.
// The App is not used here; we test the shared-subscription behaviour directly
// via KerbcamProvider + multiple CameraFeed tiles, which is the component layer
// where refcounting lives. This mirrors the CameraFeed.test.tsx fixture pattern.
// ---------------------------------------------------------------------------

import { CameraFeed } from "@jonpepler/kerbcam-react";
import { KerbcamProvider } from "@jonpepler/kerbcam-react";

async function buildConnectedFixture(cameras: MockCameraInit[] = []) {
  const sidecar = new MockSidecar();
  sidecar.withSlots(["0", "1", "2", "3", "4", "5", "6", "7"]);
  for (const cam of cameras) sidecar.addCamera(cam);

  const client = new KerbcamClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  createdClients.push(client);

  await act(async () => {
    await client.connect([], { slots: 8 });
  });
  await act(async () => {
    sidecar.open();
    sidecar.setConnectionState("connected");
  });

  return { client, sidecar };
}

describe("Subscribe refcount - two tiles, one subscribe", () => {
  it("two CameraFeed tiles for the same camera share one subscribe call", async () => {
    const { client, sidecar } = await buildConnectedFixture([
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
    ]);

    await act(async () => {
      render(
        <KerbcamProvider client={client}>
          <CameraFeed flightId={1} />
          <CameraFeed flightId={1} />
        </KerbcamProvider>,
      );
    });

    // Both feeds mounted; refcount is 2, but only 1 subscribe should be sent
    const subscribeCmds = sidecar.commands.filter(
      (c) =>
        c.type === "subscribe" &&
        (c as { content: { flightId: number } }).content.flightId === 1,
    );
    expect(subscribeCmds).toHaveLength(1);
  });

  it("removing one of two mounted feeds does not unsubscribe (refcount stays > 0)", async () => {
    const { client, sidecar } = await buildConnectedFixture([
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
    ]);

    function TwoFeeds({ showBoth }: { showBoth: boolean }) {
      return (
        <KerbcamProvider client={client}>
          <CameraFeed flightId={1} />
          {showBoth && <CameraFeed flightId={1} />}
        </KerbcamProvider>
      );
    }

    let r: ReturnType<typeof render>;
    await act(async () => {
      r = render(<TwoFeeds showBoth={true} />);
    });

    // Both feeds mounted; 1 subscribe
    expect(
      sidecar.commands.filter(
        (c) =>
          c.type === "subscribe" &&
          (c as { content: { flightId: number } }).content.flightId === 1,
      ),
    ).toHaveLength(1);

    // Unmount one feed (rerender with showBoth=false)
    await act(async () => {
      r!.rerender(<TwoFeeds showBoth={false} />);
    });

    // Still no unsubscribe (refcount went 2 -> 1)
    const unsubscribeCmds = sidecar.commands.filter((c) => c.type === "unsubscribe");
    expect(unsubscribeCmds).toHaveLength(0);
  });
});
