/**
 * Tests for useReportDisplaySize: the self-measure -> report path. Drives a
 * controllable ResizeObserver and a recording displaySizes registry override
 * so the debounce / bucket / prompt-first / square-vs-wh / clear-on-unmount
 * behaviour is asserted directly, without a real layout engine.
 */

import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { act, cleanup, render } from "@testing-library/react";
import { useRef } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { KerbcastProvider, type KerbcastDisplaySizes } from "../context";
import { useReportDisplaySize } from "./useReportDisplaySize";

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

const fakeClient = {} as unknown as KerbcastClient;

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
    // 40x30 -> ceil to a 16-multiple = 48x32, reported without advancing timers.
    fireResize(40, 30);
    expect(reports).toEqual([{ flightId: 7, id: expect.any(String), w: 48, h: 32 }]);
  });

  it("reports a square (min edge) when square is set", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} square />
      </KerbcastProvider>,
    );
    // min(50,40)=40 -> 48x48 square.
    fireResize(50, 40);
    expect(reports.at(-1)).toMatchObject({ w: 48, h: 48 });
  });

  it("debounces churn after the first report", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    fireResize(48, 48); // prompt first -> 48x48
    fireResize(200, 200); // debounced, not yet applied
    expect(reports).toHaveLength(1);
    act(() => vi.advanceTimersByTime(200));
    // 200 -> 208; the debounced resize now lands.
    expect(reports.at(-1)).toMatchObject({ w: 208, h: 208 });
  });

  it("does not re-report a sub-bucket change", () => {
    const { displaySizes, reports } = recordingRegistry();
    render(
      <KerbcastProvider client={fakeClient} displaySizes={displaySizes}>
        <Harness flightId={7} />
      </KerbcastProvider>,
    );
    fireResize(40, 40); // -> 48x48
    fireResize(44, 44); // still ceils to 48x48 -> no new report
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
    fireResize(40, 40);
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
});
