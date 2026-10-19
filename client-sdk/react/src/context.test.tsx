import { describe, expect, it, vi } from "vitest";
import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { createClientDisplaySizes, createClientSubscriptions } from "./context";

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
    expect(calls).toEqual(["subscribe:1", "subscribe:2"]);
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
