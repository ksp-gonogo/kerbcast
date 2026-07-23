/**
 * Tests for the `KerbalFaceFeed` primitive. Drives the real `KerbcastClient`
 * through the SDK's canonical `MockSidecar` (only the WebRTC transport is
 * faked — jsdom can't produce a real `MediaStream`, so a mounted feed sits in
 * the no-signal state, which is exactly what the standby/never-remount
 * assertions need).
 */

import { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { act, cleanup, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { KerbalFaceFeed, type KerbalFeedState } from "./KerbalFaceFeed";
import { KerbcastProvider, type KerbcastDisplaySizes } from "./context";

const KERBAL_ID = 0x8000_002a; // a kerbal wire-id (top bit set)

async function buildConnected(): Promise<KerbcastClient> {
  const sidecar = new MockSidecar();
  sidecar.addCamera({ flightId: KERBAL_ID, cameraName: "Jebediah Kerman" });
  const client = new KerbcastClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  await act(async () => {
    await client.connect([], { slots: 4 });
  });
  await act(async () => {
    sidecar.open();
    sidecar.setConnectionState("connected");
  });
  return client;
}

afterEach(() => {
  cleanup();
});

describe("KerbalFaceFeed", () => {
  it("renders the square frame with a persistent video element", async () => {
    const client = await buildConnected();
    const { getByTestId, container } = render(
      <KerbcastProvider client={client}>
        <KerbalFaceFeed flightId={KERBAL_ID} size={96} />
      </KerbcastProvider>,
    );
    // The square frame primitive is present...
    expect(getByTestId("kerbal-face-feed")).toBeTruthy();
    // ...and it owns exactly one <video> for the face stream.
    expect(container.querySelectorAll("video")).toHaveLength(1);
  });

  it("shows the standby glyph and reports no-signal when there is no stream", async () => {
    const client = await buildConnected();
    const states: KerbalFeedState[] = [];
    const { getByTestId, container } = render(
      <KerbcastProvider client={client}>
        <KerbalFaceFeed
          flightId={KERBAL_ID}
          size={96}
          onFeedStateChange={(s) => states.push(s)}
        />
      </KerbcastProvider>,
    );
    // jsdom never yields a MediaStream, so the feed is out of signal.
    expect(getByTestId("kerbal-face-feed").getAttribute("data-feed-state")).toBe(
      "no-signal",
    );
    // The minimal standby glyph (an aria-hidden svg) is shown.
    expect(container.querySelector("svg[aria-hidden='true']")).toBeTruthy();
    // The state signal fired for the wrapper.
    expect(states).toContain("no-signal");
  });

  it("does not draw the standby glyph when showStandby is false", async () => {
    const client = await buildConnected();
    const { container } = render(
      <KerbcastProvider client={client}>
        <KerbalFaceFeed flightId={KERBAL_ID} size={96} showStandby={false} />
      </KerbcastProvider>,
    );
    expect(container.querySelector("svg[aria-hidden='true']")).toBeNull();
    // The video element is still mounted (never conditionally swapped out).
    expect(container.querySelectorAll("video")).toHaveLength(1);
  });

  it("never remounts the live video element across a prop change", async () => {
    const client = await buildConnected();
    const { container, rerender } = render(
      <KerbcastProvider client={client}>
        <KerbalFaceFeed flightId={KERBAL_ID} size={96} />
      </KerbcastProvider>,
    );
    const before = container.querySelector("video");
    expect(before).toBeTruthy();

    // A re-layout (size change) must NOT remount the <video> — same DOM node.
    rerender(
      <KerbcastProvider client={client}>
        <KerbalFaceFeed flightId={KERBAL_ID} size={220} />
      </KerbcastProvider>,
    );
    const after = container.querySelector("video");
    expect(after).toBe(before);
  });
});

// ---------------------------------------------------------------------------
// Display-size reporting + showActions
// ---------------------------------------------------------------------------

function recordingRegistry() {
  const reports: Array<{ flightId: number; w: number; h: number }> = [];
  const clears: Array<{ flightId: number }> = [];
  const displaySizes: KerbcastDisplaySizes = {
    report: (flightId, _id, w, h) => reports.push({ flightId, w, h }),
    clear: (flightId) => clears.push({ flightId }),
  };
  return { displaySizes, reports, clears };
}

describe("KerbalFaceFeed - reporting + showActions", () => {
  let resizeCallback:
    | ((entries: ResizeObserverEntry[], observer: ResizeObserver) => void)
    | undefined;
  const originalResizeObserver = globalThis.ResizeObserver;

  beforeEach(() => {
    globalThis.ResizeObserver = class {
      constructor(cb: (entries: ResizeObserverEntry[], observer: ResizeObserver) => void) {
        resizeCallback = cb;
      }
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
  });

  afterEach(() => {
    cleanup();
    globalThis.ResizeObserver = originalResizeObserver;
    resizeCallback = undefined;
  });

  it("reports a SQUARE self-measured display size for the face", async () => {
    const client = await buildConnected();
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={client} displaySizes={displaySizes}>
        <KerbalFaceFeed flightId={KERBAL_ID} />
      </KerbcastProvider>,
    );
    // A non-square measured box still reports square (min edge, bucketed): the
    // face capture is square, so 50x40 -> min 40 -> 48x48.
    act(() => {
      resizeCallback?.(
        [{ contentRect: { width: 50, height: 40 } }] as ResizeObserverEntry[],
        {} as ResizeObserver,
      );
    });
    expect(reports.at(-1)).toEqual({ flightId: KERBAL_ID, w: 48, h: 48 });
  });

  it("does not report when reportSize is false", async () => {
    const client = await buildConnected();
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={client} displaySizes={displaySizes}>
        <KerbalFaceFeed flightId={KERBAL_ID} reportSize={false} />
      </KerbcastProvider>,
    );
    expect(resizeCallback).toBeUndefined();
    expect(reports).toEqual([]);
  });

  it("renders supplied actions by default", async () => {
    const client = await buildConnected();
    const { getByLabelText } = render(
      <KerbcastProvider client={client}>
        <KerbalFaceFeed
          flightId={KERBAL_ID}
          actions={[{ id: "s", label: "Spotlight", icon: null, onClick: () => {} }]}
        />
      </KerbcastProvider>,
    );
    expect(getByLabelText("Spotlight")).toBeTruthy();
  });

  it("hides the action bar when showActions is false", async () => {
    const client = await buildConnected();
    const { queryByLabelText } = render(
      <KerbcastProvider client={client}>
        <KerbalFaceFeed
          flightId={KERBAL_ID}
          showActions={false}
          actions={[{ id: "s", label: "Spotlight", icon: null, onClick: () => {} }]}
        />
      </KerbcastProvider>,
    );
    expect(queryByLabelText("Spotlight")).toBeNull();
  });
});
