/**
 * Tests for the Tile's missing-camera ("reconnecting / gone") state.
 *
 * A tile keyed to a flightId that is no longer present in the live cameras
 * list (a fresh launch, a different craft, a destroyed vessel) must not mount a
 * dead CameraFeed. Instead it shows a reconnecting affordance with a way to
 * repoint or remove the tile.
 *
 * The fixture mirrors App.test.tsx: a real KerbcamClient + MockSidecar wired
 * through KerbcamProvider so useKerbcamCameras() returns the live list.
 */

import { KerbcamClient } from "@jonpepler/kerbcam";
import type { CameraLifecycle } from "@jonpepler/kerbcam";
import { Layer } from "@jonpepler/kerbcam";
import type { MockCameraInit } from "@jonpepler/kerbcam/testing";
import { MockSidecar } from "@jonpepler/kerbcam/testing";
import { KerbcamProvider } from "@jonpepler/kerbcam-react";
import { act, cleanup, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { Tile } from "./Tile";

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

const createdClients: KerbcamClient[] = [];

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

function renderTile(client: KerbcamClient, flightId: number | null) {
  return render(
    <KerbcamProvider client={client}>
      <Tile
        flightId={flightId}
        index={0}
        showDebugInfo={false}
        showStatic={false}
        spotlit={false}
        onSelectCamera={() => {}}
        onRemove={() => {}}
        onToggleSpotlight={() => {}}
      />
    </KerbcamProvider>,
  );
}

beforeEach(() => {
  vi.restoreAllMocks();
});

afterEach(() => {
  cleanup();
  for (const c of createdClients) {
    try { c.disconnect(); } catch { /* ignore */ }
  }
  createdClients.length = 0;
  vi.restoreAllMocks();
});

describe("Tile - missing camera state", () => {
  it("shows a reconnecting affordance and no live feed when the camera is gone", async () => {
    // Live list has flightId 1; the tile is keyed to 2 (gone).
    const { client } = await buildConnectedFixture([
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
    ]);

    let container: HTMLElement;
    await act(async () => {
      ({ container } = renderTile(client, 2));
    });

    // Reconnecting / gone affordance is shown.
    expect(screen.getByText(/camera reconnecting/i)).toBeTruthy();
    // A way to repoint/remove the tile is present.
    expect(screen.getByRole("button", { name: /remove tile/i })).toBeTruthy();
    // No live CameraFeed video element mounted.
    expect(container!.querySelector("video")).toBeNull();
  });

  it("renders the live feed when the camera is present", async () => {
    const { client } = await buildConnectedFixture([
      makeCamera({ flightId: 1, cameraName: "Alpha" }),
    ]);

    let container: HTMLElement;
    await act(async () => {
      ({ container } = renderTile(client, 1));
    });

    // The feed mounts (its video element is present); no reconnecting text.
    expect(container!.querySelector("video")).not.toBeNull();
    expect(screen.queryByText(/reconnecting|camera gone/i)).toBeNull();
  });
});
