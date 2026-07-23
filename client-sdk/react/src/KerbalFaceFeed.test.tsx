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
import { afterEach, describe, expect, it } from "vitest";

import { KerbalFaceFeed, type KerbalFeedState } from "./KerbalFaceFeed";
import { KerbcastProvider } from "./context";

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
