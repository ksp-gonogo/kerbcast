import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { act, render } from "@testing-library/react";
import { useEffect } from "react";
import {
  KERBCAST_RELEASE_GRACE_MS,
  KerbcastProvider,
  createClientDisplaySizes,
  createClientSubscriptions,
  useKerbcastSubscriptions,
} from "./context";

/**
 * A client stub that only records reportDisplaySize calls. The registry is the
 * per-consumer MAX collapse across every mounted reporter for one flightId, so
 * a spotlight and a tiny avatar of the same camera resolve to one report = max,
 * and a departing big reporter relaxes the stream back down (the v1.6.3
 * "don't pin large" discipline at the client edge).
 */
function fakeClient() {
  const reports: Array<{ flightId: number; width: number; height: number }> = [];
  const handlers = new Map<string, Array<() => void>>();
  const client = {
    on: (event: string, h: () => void) => {
      handlers.set(event, [...(handlers.get(event) ?? []), h]);
      return () => {};
    },
    reportDisplaySize: vi.fn(async (flightId: number, width: number, height: number) => {
      reports.push({ flightId, width, height });
    }),
  } as unknown as KerbcastClient;
  /** Simulate the control channel (re)opening on a fresh peer. */
  const openControl = () => handlers.get("control-open")?.forEach((h) => h());
  return { client, reports, openControl };
}

describe("createClientDisplaySizes", () => {
  it("reports the measured size for a single reporter", () => {
    const { client, reports } = fakeClient();
    const reg = createClientDisplaySizes(client);
    reg.report(1, "a", 40, 40);
    expect(reports).toEqual([{ flightId: 1, width: 40, height: 40 }]);
  });

  it("collapses two reporters of one camera to a single MAX report", () => {
    const { client, reports } = fakeClient();
    const reg = createClientDisplaySizes(client);
    reg.report(1, "small", 40, 40);
    reg.report(1, "big", 512, 512);
    // 40 first, then bumped to the max 512 when the big one mounts.
    expect(reports).toEqual([
      { flightId: 1, width: 40, height: 40 },
      { flightId: 1, width: 512, height: 512 },
    ]);
  });

  it("maxes width and height independently across reporters", () => {
    const { client, reports } = fakeClient();
    const reg = createClientDisplaySizes(client);
    reg.report(1, "a", 640, 360);
    reg.report(1, "b", 320, 400);
    expect(reports.at(-1)).toEqual({ flightId: 1, width: 640, height: 400 });
  });

  it("does not re-report when the max is unchanged", () => {
    const { client, reports } = fakeClient();
    const reg = createClientDisplaySizes(client);
    reg.report(1, "big", 512, 512);
    reg.report(1, "small", 40, 40); // below current max -> no new report
    reg.report(1, "small", 44, 44); // still below -> no new report
    expect(reports).toEqual([{ flightId: 1, width: 512, height: 512 }]);
  });

  it("relaxes to the next-largest reporter when the big one departs", () => {
    const { client, reports } = fakeClient();
    const reg = createClientDisplaySizes(client);
    reg.report(1, "small", 40, 40);
    reg.report(1, "big", 512, 512);
    reg.clear(1, "big");
    expect(reports.at(-1)).toEqual({ flightId: 1, width: 40, height: 40 });
  });

  it("sends nothing more once the last reporter departs (backend forget clears it)", () => {
    const { client, reports } = fakeClient();
    const reg = createClientDisplaySizes(client);
    reg.report(1, "a", 40, 40);
    const countBefore = reports.length;
    reg.clear(1, "a");
    expect(reports.length).toBe(countBefore); // no extra report on empty
  });

  it("keeps separate maxes per flightId", () => {
    const { client, reports } = fakeClient();
    const reg = createClientDisplaySizes(client);
    reg.report(1, "a", 40, 40);
    reg.report(2, "b", 512, 512);
    expect(reports).toEqual([
      { flightId: 1, width: 40, height: 40 },
      { flightId: 2, width: 512, height: 512 },
    ]);
  });

  it("clearing an unknown reporter is a no-op", () => {
    const { client, reports } = fakeClient();
    const reg = createClientDisplaySizes(client);
    reg.clear(99, "ghost");
    expect(reports).toEqual([]);
  });

  describe("reconnect", () => {
    it("re-sends an unchanged size when the control channel reopens", () => {
      const { client, reports, openControl } = fakeClient();
      const reg = createClientDisplaySizes(client);
      reg.report(1, "a", 640, 360);
      openControl();
      expect(reports).toEqual([
        { flightId: 1, width: 640, height: 360 },
        { flightId: 1, width: 640, height: 360 },
      ]);
    });

    it("re-sends the current MAX for every flightId with reporters", () => {
      const { client, reports, openControl } = fakeClient();
      const reg = createClientDisplaySizes(client);
      reg.report(1, "small", 40, 40);
      reg.report(1, "big", 512, 512);
      reg.report(2, "a", 320, 180);
      reg.report(3, "gone", 99, 99);
      reg.clear(3, "gone");
      reports.length = 0;
      openControl();
      expect(reports).toEqual([
        { flightId: 1, width: 512, height: 512 },
        { flightId: 2, width: 320, height: 180 },
      ]);
    });

    it("does not send duplicates while connected", () => {
      const { client, reports } = fakeClient();
      const reg = createClientDisplaySizes(client);
      reg.report(1, "a", 640, 360);
      reg.report(1, "a", 640, 360);
      reg.report(1, "b", 100, 100);
      expect(reports).toHaveLength(1);
    });

    it("keeps de-duplicating after a reconnect resend", () => {
      const { client, reports, openControl } = fakeClient();
      const reg = createClientDisplaySizes(client);
      reg.report(1, "a", 640, 360);
      openControl();
      reg.report(1, "a", 640, 360);
      expect(reports).toHaveLength(2);
    });

    it("sends nothing on reconnect when no reporters exist", () => {
      const { client, reports, openControl } = fakeClient();
      createClientDisplaySizes(client);
      openControl();
      expect(reports).toEqual([]);
    });
  });
});

describe("createClientSubscriptions reconnect", () => {
  function subsClient() {
    const calls: string[] = [];
    const handlers: Array<() => void> = [];
    const client = {
      on: (_event: string, h: () => void) => {
        handlers.push(h);
        return () => {};
      },
      subscribe: vi.fn(async (id: number) => {
        calls.push(`subscribe:${id}`);
      }),
      unsubscribe: vi.fn(async (id: number) => {
        calls.push(`unsubscribe:${id}`);
      }),
      camera: () => ({ waitingForSlot: false, on: () => () => {} }),
      reportDisplaySize: vi.fn(async (id: number) => {
        calls.push(`size:${id}`);
      }),
    } as unknown as KerbcastClient;
    return { client, calls, openControl: () => handlers.forEach((h) => h()) };
  }

  it("re-subscribes every held flightId when the control channel reopens", () => {
    const { client, calls, openControl } = subsClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.acquire(1);
    subs.acquire(2);
    subs.acquire(3);
    subs.release(3);
    calls.length = 0;
    openControl();
    /* 3 was only waiting out its release grace, so it is dropped, not bound again. */
    expect(calls).toEqual(["unsubscribe:3", "subscribe:1", "subscribe:2"]);
  });

  it("does not throw when a re-subscribe is rejected", async () => {
    const { client, openControl } = subsClient();
    (client.subscribe as ReturnType<typeof vi.fn>).mockRejectedValue(new Error("pool full"));
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    expect(() => openControl()).not.toThrow();
    await Promise.resolve();
  });

  it("re-subscribes before the display-size resend", () => {
    const { client, calls, openControl } = subsClient();
    const subs = createClientSubscriptions(client);
    const sizes = createClientDisplaySizes(client);
    subs.acquire(1);
    sizes.report(1, "a", 640, 360);
    calls.length = 0;
    openControl();
    expect(calls).toEqual(["subscribe:1", "size:1"]);
  });
});

describe("createClientSubscriptions release grace", () => {
  function graceClient() {
    const calls: string[] = [];
    const waiting = new Set<number>();
    const waitingHandlers = new Map<number, (w: boolean) => void>();
    const handlers = new Map<string, Array<(arg?: unknown) => void>>();
    const client = {
      on: (event: string, h: (arg?: unknown) => void) => {
        handlers.set(event, [...(handlers.get(event) ?? []), h]);
        return () => {};
      },
      subscribe: vi.fn(async (id: number) => {
        calls.push(`subscribe:${id}`);
      }),
      unsubscribe: vi.fn(async (id: number) => {
        calls.push(`unsubscribe:${id}`);
      }),
      camera: (id: number) => ({
        get waitingForSlot() {
          return waiting.has(id);
        },
        on: (_event: string, h: (w: boolean) => void) => {
          waitingHandlers.set(id, h);
          return () => waitingHandlers.delete(id);
        },
      }),
    } as unknown as KerbcastClient;
    const setWaiting = (id: number, w: boolean) => {
      if (w) waiting.add(id);
      else waiting.delete(id);
      waitingHandlers.get(id)?.(w);
    };
    const emit = (event: string, arg?: unknown) => handlers.get(event)?.forEach((h) => h(arg));
    return { client, calls, emit, setWaiting };
  }

  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("sends nothing when a camera is reacquired inside the grace", () => {
    const { client, calls } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.release(1);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS - 1);
    subs.acquire(1);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 5);
    expect(calls).toEqual(["subscribe:1"]);
  });

  it("unsubscribes once when the grace runs out", () => {
    const { client, calls } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.release(1);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS - 1);
    expect(calls).toEqual(["subscribe:1"]);
    vi.advanceTimersByTime(1);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 5);
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1"]);
  });

  it("sends nothing for a flip to B and back inside one task", async () => {
    const { client, calls } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.release(1);
    subs.acquire(2);
    subs.release(2);
    subs.acquire(1);
    await Promise.resolve();
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 5);
    expect(calls).toEqual(["subscribe:1"]);
  });

  it("frees the old camera before subscribing the new one on a switch", async () => {
    const { client, calls } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.release(1);
    subs.acquire(2);
    expect(calls).toEqual(["subscribe:1"]);
    await Promise.resolve();
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1", "subscribe:2"]);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 5);
    expect(calls).toHaveLength(3);
  });

  it("keeps a remount's grace when a switch lands in the same commit", async () => {
    const { client, calls } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.acquire(3);
    /* All cleanups, then all effects: 3 remounts, 1 switches to 2. */
    subs.release(1);
    subs.release(3);
    subs.acquire(2);
    subs.acquire(3);
    await Promise.resolve();
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 5);
    expect(calls).toEqual(["subscribe:1", "subscribe:3", "unsubscribe:1", "subscribe:2"]);
  });

  it("sends nothing for a camera released before its queued subscribe went out", async () => {
    const { client, calls } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.release(1);
    subs.acquire(2);
    subs.release(2);
    await Promise.resolve();
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 5);
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1"]);
  });

  it("survives many rapid remounts with one subscribe and no unsubscribe", () => {
    const { client, calls } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    for (let i = 0; i < 50; i++) {
      subs.release(1);
      vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS / 2);
      subs.acquire(1);
    }
    expect(calls).toEqual(["subscribe:1"]);
    subs.release(1);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS);
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1"]);
  });

  it("unsubscribes at once with a zero grace", () => {
    const { client, calls } = graceClient();
    const subs = createClientSubscriptions(client, { releaseGraceMs: 0 });
    subs.acquire(1);
    subs.release(1);
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1"]);
  });

  it("drops a pending release when the connection ends", () => {
    const { client, calls, emit } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.release(1);
    emit("state-change", "disconnected");
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1"]);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 2);
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1"]);
    subs.acquire(1);
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1", "subscribe:1"]);
  });

  it("subscribes afresh after a reconnect dropped a pending release", () => {
    const { client, calls, emit } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.release(1);
    emit("control-open");
    subs.acquire(1);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 2);
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1", "subscribe:1"]);
  });

  it("skips the grace while a held camera waits for a slot", () => {
    const { client, calls, setWaiting } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    subs.acquire(2);
    setWaiting(2, true);
    subs.release(1);
    expect(calls).toEqual(["subscribe:1", "subscribe:2", "unsubscribe:1"]);
  });

  it("ends a grace under way when a held camera starts waiting for a slot", () => {
    const { client, calls, setWaiting } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(2);
    subs.acquire(1);
    subs.release(1);
    expect(calls).toEqual(["subscribe:2", "subscribe:1"]);
    setWaiting(2, true);
    expect(calls).toEqual(["subscribe:2", "subscribe:1", "unsubscribe:1"]);
    vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 2);
    expect(calls).toHaveLength(3);
  });

  it("skips the grace for a camera that was itself waiting for a slot", () => {
    const { client, calls, setWaiting } = graceClient();
    const subs = createClientSubscriptions(client);
    subs.acquire(1);
    setWaiting(1, true);
    subs.release(1);
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1"]);
  });
});

describe("KerbcastProvider defaults", () => {
  function stubClient() {
    const calls: string[] = [];
    const on = vi.fn(() => () => {});
    const client = {
      on,
      subscribe: vi.fn(async (id: number) => {
        calls.push(`subscribe:${id}`);
      }),
      unsubscribe: vi.fn(async (id: number) => {
        calls.push(`unsubscribe:${id}`);
      }),
      camera: () => ({ waitingForSlot: false, on: () => () => {} }),
    } as unknown as KerbcastClient;
    return { client, calls, on };
  }

  it("builds nothing when both seams are supplied", () => {
    const { client, on } = stubClient();
    render(
      <KerbcastProvider
        client={client}
        subscriptions={{ acquire() {}, release() {} }}
        displaySizes={{ report() {}, clear() {} }}
      >
        {null}
      </KerbcastProvider>,
    );
    expect(on).not.toHaveBeenCalled();
  });

  it("takes a release grace for its default subscriptions", () => {
    const { client, calls } = stubClient();
    function Probe(): null {
      const subs = useKerbcastSubscriptions();
      subs.acquire(1);
      subs.release(1);
      return null;
    }
    render(
      <KerbcastProvider client={client} releaseGraceMs={0}>
        <Probe />
      </KerbcastProvider>,
    );
    expect(calls).toEqual(["subscribe:1", "unsubscribe:1"]);
  });

  it("shares one default manager between providers on the same client", () => {
    const { client } = stubClient();
    const seen: unknown[] = [];
    function Probe(): null {
      seen.push(useKerbcastSubscriptions());
      return null;
    }
    render(
      <>
        <KerbcastProvider client={client}>
          <Probe />
        </KerbcastProvider>
        <KerbcastProvider client={client}>
          <Probe />
        </KerbcastProvider>
      </>,
    );
    expect(seen[0]).toBe(seen[1]);
  });

  function Holder({ flightId }: { flightId: number }): null {
    const subs = useKerbcastSubscriptions();
    useEffect(() => {
      subs.acquire(flightId);
      return () => subs.release(flightId);
    }, [subs, flightId]);
    return null;
  }

  it("keeps a camera through a change of the provider's grace", async () => {
    vi.useFakeTimers();
    try {
      const { client, calls } = stubClient();
      const view = render(
        <KerbcastProvider client={client}>
          <Holder flightId={5} />
        </KerbcastProvider>,
      );
      view.rerender(
        <KerbcastProvider client={client} releaseGraceMs={500}>
          <Holder flightId={5} />
        </KerbcastProvider>,
      );
      await act(async () => {
        vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 3);
      });
      expect(calls).toEqual(["subscribe:5"]);
      view.unmount();
      await act(async () => {
        vi.advanceTimersByTime(500);
      });
      expect(calls).toEqual(["subscribe:5", "unsubscribe:5"]);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not unsubscribe a camera another provider with a different grace still shows", async () => {
    vi.useFakeTimers();
    try {
      const { client, calls } = stubClient();
      const both = (withZero: boolean) => (
        <>
          <KerbcastProvider client={client}>
            <Holder flightId={5} />
          </KerbcastProvider>
          {withZero && (
            <KerbcastProvider client={client} releaseGraceMs={0}>
              <Holder flightId={5} />
            </KerbcastProvider>
          )}
        </>
      );
      const view = render(both(true));
      view.rerender(both(false));
      await act(async () => {
        vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS * 3);
      });
      expect(calls).toEqual(["subscribe:5"]);
      view.unmount();
      await act(async () => {
        vi.advanceTimersByTime(KERBCAST_RELEASE_GRACE_MS);
      });
      expect(calls).toEqual(["subscribe:5", "unsubscribe:5"]);
    } finally {
      vi.useRealTimers();
    }
  });
});
