import { describe, expect, it, vi } from "vitest";
import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { createClientDisplaySizes } from "./context";

/**
 * A client stub that only records reportDisplaySize calls. The registry is the
 * per-consumer MAX collapse across every mounted reporter for one flightId, so
 * a spotlight and a tiny avatar of the same camera resolve to one report = max,
 * and a departing big reporter relaxes the stream back down (the v1.6.3
 * "don't pin large" discipline at the client edge).
 */
function fakeClient() {
  const reports: Array<{ flightId: number; width: number; height: number }> = [];
  const client = {
    reportDisplaySize: vi.fn(async (flightId: number, width: number, height: number) => {
      reports.push({ flightId, width, height });
    }),
  } as unknown as KerbcastClient;
  return { client, reports };
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
});
