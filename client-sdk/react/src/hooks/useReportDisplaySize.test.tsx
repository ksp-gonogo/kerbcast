/**
 * Tests for useReportDisplaySize: the self-measure -> report path. Drives a
 * controllable ResizeObserver and a recording displaySizes registry override
 * so the debounce / bucket / prompt-first / square-vs-wh / clear-on-unmount
 * behaviour is asserted directly, without a real layout engine.
 */

import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { CameraLifecycle } from "@ksp-gonogo/kerbcast";
import { act, cleanup, render } from "@testing-library/react";
import { useRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { KerbcastProvider, type KerbcastDisplaySizes } from "../context";
import { displayReportSize, MIN_DISPLAY_REPORT_PX, useReportDisplaySize } from "./useReportDisplaySize";

// A controllable ResizeObserver: captures the callback so a test can fire a
// resize with a chosen contentRect.
let resizeCallback:
  | ((entries: ResizeObserverEntry[], observer: ResizeObserver) => void)
  | undefined;
const originalResizeObserver = globalThis.ResizeObserver;

// A recording registry override, injected via the provider.
function recordingRegistry() {
  const reports: Array<{ flightId: number; id: string; w: number; h: number }> = [];
  const clears: Array<{ flightId: number; id: string }> = [];
  const displaySizes: KerbcastDisplaySizes = {
    report: (flightId, id, w, h) => reports.push({ flightId, id, w, h }),
    clear: (flightId, id) => clears.push({ flightId, id }),
  };
  return { displaySizes, reports, clears };
}

const fakeClient = { on: () => () => {} } as unknown as KerbcastClient;

function fireResize(width: number, height: number) {
  act(() => {
    resizeCallback?.(
      [{ contentRect: { width, height } }] as ResizeObserverEntry[],
      {} as ResizeObserver,
    );
  });
}

function Harness({
  flightId,
  square,
  enabled,
}: {
  flightId: number | null;
  square?: boolean;
  enabled?: boolean;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useReportDisplaySize(flightId, ref, { square, enabled });
  return <div ref={ref} data-testid="feed" />;
}

function LateHarness({ show }: { show: boolean }) {
  const ref = useRef<HTMLDivElement>(null);
  useReportDisplaySize(7, ref);
  return show ? <div ref={ref} data-testid="late" /> : null;
}

beforeEach(() => {
  vi.useFakeTimers();
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
  vi.useRealTimers();
});

describe("useReportDisplaySize", () => {
  it("reports the first measurement promptly (no debounce wait)", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    // 440x330 -> width ceiled to a 16-multiple (448), height at 16:9 rounded to even (252), reported without advancing timers.
    fireResize(440, 330);
    expect(reports).toEqual([{ flightId: 7, id: expect.any(String), w: 448, h: 252 }]);
  });

  it("asks for the 16:9 picture that fits the box, never the box's shape", () => {
    expect(displayReportSize(1024, 576)).toEqual({ width: 1024, height: 576 });
    expect(displayReportSize(441, 246)).toEqual({ width: 448, height: 252 });
    // A narrow, tall tile is limited by its width, then raised so the height meets the floor.
    expect(displayReportSize(170, 400)).toEqual({ width: 240, height: 136 });
    // A wide, short one by its height: the letterboxed picture is ~356x200.
    expect(displayReportSize(800, 200)).toEqual({ width: 368, height: 208 });
  });

  it("reports a square (min edge) when square is set", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} square />
      </KerbcastProvider>,
    );
    // min(250,200)=200 -> 208x208 square.
    fireResize(250, 200);
    expect(reports.at(-1)).toMatchObject({ w: 208, h: 208 });
  });

  it("debounces churn after the first report", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    fireResize(300, 300); // prompt first -> 304x172
    fireResize(600, 600); // debounced, not yet applied
    expect(reports).toHaveLength(1);
    act(() => vi.advanceTimersByTime(200));
    // 600 -> 608 wide at 16:9; the debounced resize now lands.
    expect(reports.at(-1)).toMatchObject({ w: 608, h: 342 });
  });

  it("does not re-report a sub-bucket change", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    fireResize(290, 290); // -> 304x172
    fireResize(300, 300); // still ceils to 304x172 -> no new report
    act(() => vi.advanceTimersByTime(200));
    expect(reports).toHaveLength(1);
  });

  it("clears its registration on unmount", () => {
    const { displaySizes, reports, clears } = recordingRegistry();
    const { unmount } = render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    fireResize(400, 400);
    const id = reports[0].id;
    act(() => unmount());
    expect(clears).toEqual([{ flightId: 7, id }]);
  });

  it("does not observe or report when disabled", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} enabled={false} />
      </KerbcastProvider>,
    );
    // The observer is never constructed when disabled, so no callback is armed.
    expect(resizeCallback).toBeUndefined();
    expect(reports).toEqual([]);
  });

  it("does nothing for a null flightId", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={null} />
      </KerbcastProvider>,
    );
    expect(resizeCallback).toBeUndefined();
    expect(reports).toEqual([]);
  });

  it("observes an element that appears after mount", () => {
    const { displaySizes, reports } = recordingRegistry();
    const view = (show: boolean) => (
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <LateHarness show={show} />
      </KerbcastProvider>
    );
    const { rerender } = render(view(false));
    expect(resizeCallback).toBeUndefined();
    rerender(view(true));
    fireResize(440, 330);
    expect(reports).toEqual([{ flightId: 7, id: expect.any(String), w: 448, h: 252 }]);
  });

  it("never asks for less than the floor on either axis", () => {
    for (const [w, h] of [[16, 10], [1, 1], [140, 130], [130, 2000], [4000, 130]]) {
      const size = displayReportSize(w, h);
      expect(size.width).toBeGreaterThanOrEqual(MIN_DISPLAY_REPORT_PX);
      expect(size.height).toBeGreaterThanOrEqual(MIN_DISPLAY_REPORT_PX);
    }
    // A tall camera is held to the floor by its width.
    expect(displayReportSize(16, 10, 9 / 16)).toEqual({ width: 128, height: 228 });
  });

  it("reports nothing for an empty box, then reports the first laid out box promptly", () => {
    const { displaySizes, reports, clears } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    fireResize(0, 0);
    fireResize(400, 1);
    act(() => vi.advanceTimersByTime(1000));
    expect(reports).toEqual([]);
    expect(clears).toEqual([]);
    fireResize(440, 330);
    expect(reports).toEqual([{ flightId: 7, id: expect.any(String), w: 448, h: 252 }]);
  });

  it("withdraws its size when the box empties, and reports again when it is laid out again", () => {
    const { displaySizes, reports, clears } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    fireResize(440, 330);
    const id = reports[0].id;
    fireResize(0, 0);
    act(() => vi.advanceTimersByTime(200));
    expect(clears).toEqual([{ flightId: 7, id }]);
    expect(reports).toHaveLength(1);
    fireResize(440, 330);
    act(() => vi.advanceTimersByTime(200));
    expect(reports).toHaveLength(2);
    expect(reports.at(-1)).toMatchObject({ w: 448, h: 252 });
  });

  it("raises a small box's request to the floor", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    fireResize(18, 10);
    expect(reports).toEqual([{ flightId: 7, id: expect.any(String), w: 240, h: 136 }]);
  });

  it("raises a small square box's request to the floor", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} square />
      </KerbcastProvider>,
    );
    fireResize(50, 40);
    expect(reports.at(-1)).toMatchObject({ w: 128, h: 128 });
  });

  describe("with the default registry, across a camera registered again", () => {
    /* A client that records what reaches reportDisplaySize, with cameras-change driven by the test. */
    function sizingClient() {
      const sent: Array<{ flightId: number; w: number; h: number }> = [];
      const handlers = new Map<string, Array<(arg?: unknown) => void>>();
      const client = {
        on: (event: string, h: (arg?: unknown) => void) => {
          handlers.set(event, [...(handlers.get(event) ?? []), h]);
          return () => {};
        },
        reportDisplaySize: vi.fn(async (flightId: number, w: number, h: number) => {
          sent.push({ flightId, w, h });
        }),
        cameras: [{ flightId: 7, lifecycle: CameraLifecycle.Active, attach: 3 }],
      } as unknown as KerbcastClient;
      const list = (lifecycle: CameraLifecycle, attach: number) =>
        act(() => handlers.get("cameras-change")?.forEach((h) => h([{ flightId: 7, lifecycle, attach }])));
      return { client, sent, list };
    }

    it("re-sends the unchanged size of a reporter that stays mounted while its camera is destroyed and back", () => {
      const { client, sent, list } = sizingClient();
      render(
        <KerbcastProvider client={client}>
          <Harness flightId={7} />
        </KerbcastProvider>,
      );
      fireResize(440, 330);
      expect(sent).toEqual([{ flightId: 7, w: 448, h: 252 }]);
      list(CameraLifecycle.Destroyed, 3);
      list(CameraLifecycle.Active, 3);
      expect(sent).toEqual([
        { flightId: 7, w: 448, h: 252 },
        { flightId: 7, w: 448, h: 252 },
      ]);
    });

    it("re-sends the unchanged size of a reporter that stays mounted while its camera's ring is attached afresh", () => {
      const { client, sent, list } = sizingClient();
      render(
        <KerbcastProvider client={client}>
          <Harness flightId={7} />
        </KerbcastProvider>,
      );
      fireResize(440, 330);
      list(CameraLifecycle.Active, 4);
      expect(sent).toHaveLength(2);
      expect(sent.at(-1)).toEqual({ flightId: 7, w: 448, h: 252 });
    });

    it("withdraws when the feed moves to another camera, and reports afresh when it moves back", () => {
      const { client, sent, list } = sizingClient();
      const { rerender } = render(
        <KerbcastProvider client={client}>
          <Harness flightId={7} />
        </KerbcastProvider>,
      );
      fireResize(440, 330);
      rerender(
        <KerbcastProvider client={client}>
          <Harness flightId={8} />
        </KerbcastProvider>,
      );
      expect(sent).toContainEqual({ flightId: 7, w: 0, h: 0 });
      list(CameraLifecycle.Destroyed, 3);
      list(CameraLifecycle.Active, 3);
      rerender(
        <KerbcastProvider client={client}>
          <Harness flightId={7} />
        </KerbcastProvider>,
      );
      fireResize(440, 330);
      expect(sent.filter((r) => r.flightId === 7)).toEqual([
        { flightId: 7, w: 448, h: 252 },
        { flightId: 7, w: 0, h: 0 },
        { flightId: 7, w: 448, h: 252 },
      ]);
    });
  });
});
