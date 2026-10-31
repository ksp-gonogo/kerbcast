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
import { afterEach, describe, expect, it, vi } from "vitest";
import { KERBCAST_RELEASE_GRACE_MS, KerbcastProvider } from "../context";
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
  vi.useRealTimers();
});

/** Fake only the timers the release grace uses, so the client's own async setup is untouched. */
function fakeGraceTimers(): void {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
}

function countCommands(sidecar: MockSidecar, type: string, flightId: number): number {
  return sidecar.commands.filter(
    (c) => c.type === type && (c.content as { flightId?: number }).flightId === flightId,
  ).length;
}

describe("useKerbcastStream - slot subscription lifecycle", () => {
  it("subscribes the camera on mount and releases it on unmount", async () => {
    const { client, sidecar } = await connectedSource();

    fakeGraceTimers();

    const { unmount } = render(<Wrapper client={client} flightId={42} />);

    expect(sidecar.lastCommand("subscribe", 42)).toBeTruthy();
    expect(sidecar.slotMidFor(42)).toBeDefined();

    act(() => {
      unmount();
    });
    act(() => {
      vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS);
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
    fakeGraceTimers();

    const { rerender, unmount } = render(
      <Wrapper client={client} flightId={42} />,
    );
    expect(sidecar.slotMidFor(42)).toBeDefined();

    act(() => {
      rerender(<Wrapper client={client} flightId={43} />);
    });
    act(() => {
      vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS);
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

  it("keeps the slot and stream for a feed that remounts inside the grace period", async () => {
    const { client, sidecar } = await connectedSource();
    fakeGraceTimers();

    const first = render(<Wrapper client={client} flightId={42} />);
    const mid = sidecar.slotMidFor(42);
    expect(mid).toBeDefined();
    await act(async () => {
      sidecar.deliverTrack(mid as string, {} as MediaStreamTrack);
    });
    const stream = client.camera(42).mediaStream;
    expect(stream).not.toBeNull();
    act(() => {
      first.unmount();
    });

    act(() => {
      vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS - 1);
    });
    let seen: MediaStream | null = null;
    function Seen(): null {
      seen = useKerbcastStream(42);
      return null;
    }
    const second = render(
      <KerbcastProvider client={client}>
        <Seen />
      </KerbcastProvider>,
    );
    act(() => {
      vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 3);
    });

    expect(seen).toBe(stream);
    expect(sidecar.slotMidFor(42)).toBe(mid);
    expect(countCommands(sidecar, "subscribe", 42)).toBe(1);
    expect(countCommands(sidecar, "unsubscribe", 42)).toBe(0);

    act(() => {
      second.unmount();
    });
    await act(async () => {
      await client.disconnect();
    });
  });

  it("sends exactly one unsubscribe once the grace period runs out", async () => {
    const { client, sidecar } = await connectedSource();
    fakeGraceTimers();

    const { unmount } = render(<Wrapper client={client} flightId={42} />);
    act(() => {
      unmount();
    });
    act(() => {
      vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS - 1);
    });
    expect(countCommands(sidecar, "unsubscribe", 42)).toBe(0);
    expect(sidecar.slotMidFor(42)).toBeDefined();

    act(() => {
      vi.advanceTimersByTime(50);
    });
    expect(countCommands(sidecar, "unsubscribe", 42)).toBe(1);
    expect(sidecar.slotMidFor(42)).toBeUndefined();

    act(() => {
      vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 3);
    });
    expect(countCommands(sidecar, "unsubscribe", 42)).toBe(1);

    await act(async () => {
      await client.disconnect();
    });
  });
});
