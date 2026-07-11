/**
 * useKerbcastStream slot-subscription lifecycle.
 *
 * Renders a probe component through the real hook + real KerbcastClient,
 * with only the WebRTC transport faked by the SDK's canonical MockSidecar.
 * Asserts the hook drives the dynamic-mode subscription: a slot binds while a
 * camera is on screen, switches when the selected flightId changes, and frees
 * on unmount.
 */

import { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { act, cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { KerbcastProvider } from "../context";
import { useKerbcastStream } from "./useKerbcastStream";

function StreamProbe({ flightId }: { flightId: number | null }): null {
  useKerbcastStream(flightId);
  return null;
}

function Wrapper({
  client,
  flightId,
}: {
  client: KerbcastClient;
  flightId: number | null;
}): React.JSX.Element {
  return (
    <KerbcastProvider client={client}>
      <StreamProbe flightId={flightId} />
    </KerbcastProvider>
  );
}

async function connectedSource(
  flightIds: number[] = [42, 43],
): Promise<{ client: KerbcastClient; sidecar: MockSidecar }> {
  const sidecar = new MockSidecar();
  for (const flightId of flightIds) {
    sidecar.addCamera({ flightId });
  }
  const client = new KerbcastClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  await act(async () => {
    await client.connect([], { slots: 4 });
    sidecar.open();
    sidecar.setConnectionState("connected");
  });
  return { client, sidecar };
}

afterEach(() => {
  cleanup();
});

describe("useKerbcastStream - slot subscription lifecycle", () => {
  it("subscribes the camera on mount and releases it on unmount", async () => {
    const { client, sidecar } = await connectedSource();

    const { unmount } = render(<Wrapper client={client} flightId={42} />);

    expect(sidecar.lastCommand("subscribe", 42)).toBeTruthy();
    expect(sidecar.slotMidFor(42)).toBeDefined();

    act(() => {
      unmount();
    });

    // Disconnect after unmount so stream-null emission hits no mounted widget.
    await act(async () => {
      await client.disconnect();
    });

    expect(sidecar.lastCommand("unsubscribe", 42)).toBeTruthy();
    expect(sidecar.slotMidFor(42)).toBeUndefined();
  });

  it("switches slots when the selected flightId changes", async () => {
    const { client, sidecar } = await connectedSource();

    const { rerender, unmount } = render(
      <Wrapper client={client} flightId={42} />,
    );
    expect(sidecar.slotMidFor(42)).toBeDefined();

    act(() => {
      rerender(<Wrapper client={client} flightId={43} />);
    });

    expect(sidecar.slotMidFor(42)).toBeUndefined();
    expect(sidecar.slotMidFor(43)).toBeDefined();

    act(() => {
      unmount();
    });
    await act(async () => {
      await client.disconnect();
    });
  });

  it("does not subscribe when no camera is selected", async () => {
    const { client, sidecar } = await connectedSource();

    const { unmount } = render(<Wrapper client={client} flightId={null} />);

    expect(sidecar.commands.some((c) => c.type === "subscribe")).toBe(false);

    act(() => {
      unmount();
    });
    await act(async () => {
      await client.disconnect();
    });
  });
});
