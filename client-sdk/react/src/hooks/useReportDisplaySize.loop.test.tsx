/**
 * The render-size request must never feed the stream back into itself. A host
 * that sizes the measured box from the stream (a frame at the video's aspect,
 * a `<video>` at its own size) closes a loop: stream -> box -> request ->
 * stream. These tests run that loop against a stand-in sidecar that delivers
 * a stream of whatever size it is asked for (or smaller, or late) and assert
 * the request settles instead of spiralling.
 *
 * Layout is faked: a ResizeObserver stub reports the px size in the measured
 * element's inline style (or the tile's when it has none), once per change,
 * on each animation frame the test steps through.
 */

import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { act, cleanup, render } from "@testing-library/react";
import { useEffect, useRef, useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { KerbcastProvider, type KerbcastDisplaySizes } from "../context";
import { useReportDisplaySize, type Size } from "./useReportDisplaySize";

const FRAME_MS = 16;
const fakeClient = { on: () => () => {} } as unknown as KerbcastClient;

let tile: Size = { width: 170, height: 400 };
/* Re-renders the host after the tile changes, as a real layout change would. */
let relayout = () => {};

function resizeTile(size: Size) {
  tile = size;
  act(() => relayout());
}

interface Watched {
  el: Element;
  cb: ResizeObserverCallback;
  last: string | null;
}
let watched: Watched[] = [];

function boxOf(el: Element): Size {
  const style = (el as HTMLElement).style;
  if (style?.width.endsWith("px") && style.height.endsWith("px")) {
    return { width: Number.parseFloat(style.width), height: Number.parseFloat(style.height) };
  }
  return tile;
}

function layout() {
  for (const w of watched) {
    const box = boxOf(w.el);
    const key = `${box.width}x${box.height}`;
    if (key === w.last) continue;
    w.last = key;
    w.cb(
      [{ target: w.el, contentRect: box as DOMRectReadOnly } as ResizeObserverEntry],
      {} as ResizeObserver,
    );
  }
}

/** Step `ms` of time a frame at a time, laying out after each. */
function run(ms: number) {
  for (let t = 0; t < ms; t += FRAME_MS) {
    act(() => {
      vi.advanceTimersByTime(FRAME_MS);
    });
    act(() => layout());
  }
}

const FIRST_RENDER: Size = { width: 1280, height: 720 };

type Mode = "fit" | "intrinsic" | "late-intrinsic" | "tile";

/**
 * The stand-in sidecar: after `latencyMs` the stream becomes `deliver(request)`,
 * seen both as the video's own size and as the camera's render size.
 */
interface Sidecar {
  reports: Size[];
  deliver: (req: Size) => Size;
  latencyMs: number;
  streamBecomes: (size: Size) => void;
}

function makeSidecar(): Sidecar {
  return {
    reports: [],
    deliver: (req) => req,
    latencyMs: 300,
    streamBecomes: () => {},
  };
}

function registry(sidecar: Sidecar): KerbcastDisplaySizes {
  return {
    report: (_flightId, _id, width, height) => {
      sidecar.reports.push({ width, height });
      setTimeout(() => sidecar.streamBecomes(sidecar.deliver({ width, height })), sidecar.latencyMs);
    },
    clear: () => {},
  };
}

/**
 * A host whose measured box comes from the stream in one of a few natural
 * ways, or ("tile") from layout alone.
 */
function Host({ mode, sidecar }: { mode: Mode; sidecar: Sidecar }) {
  const frameRef = useRef<HTMLDivElement>(null);
  const videoRef = useRef<HTMLVideoElement>(null);
  const [render, setRender] = useState<Size>(FIRST_RENDER);
  const [shown, setShown] = useState<Size | null>(null);
  const [, bump] = useState(0);
  relayout = () => bump((n) => n + 1);

  sidecar.streamBecomes = (size) => {
    const v = videoRef.current;
    if (!v) return;
    act(() => {
      Object.defineProperty(v, "videoWidth", { value: size.width, configurable: true });
      Object.defineProperty(v, "videoHeight", { value: size.height, configurable: true });
      setRender(size);
      v.dispatchEvent(new Event("resize"));
    });
  };

  useEffect(() => {
    const v = videoRef.current;
    if (!v) return;
    const onResize = () => {
      bump((n) => n + 1);
      const size = { width: v.videoWidth, height: v.videoHeight };
      setTimeout(() => setShown(size), 1500);
    };
    v.addEventListener("resize", onResize);
    return () => v.removeEventListener("resize", onResize);
  }, []);

  useReportDisplaySize(7, frameRef, { videoRef, streamSize: render });

  const v = videoRef.current;
  const stream = v && v.videoWidth > 0 ? { width: v.videoWidth, height: v.videoHeight } : render;
  let style: React.CSSProperties | undefined;
  if (mode === "fit") {
    const aspect = stream.width / stream.height;
    const w = Math.min(tile.width, tile.height * aspect);
    style = { width: `${Math.floor(w)}px`, height: `${Math.floor(w / aspect)}px` };
  } else if (mode === "intrinsic") {
    style = { width: `${stream.width}px`, height: `${stream.height}px` };
  } else if (mode === "late-intrinsic") {
    const s = shown ?? FIRST_RENDER;
    style = { width: `${s.width}px`, height: `${s.height}px` };
  }
  return (
    <div ref={frameRef} style={style}>
      <video ref={videoRef} />
    </div>
  );
}

function mount(mode: Mode, sidecar = makeSidecar()) {
  render(
    <KerbcastProvider client={fakeClient} displaySizes={registry(sidecar)}>
      <Host mode={mode} sidecar={sidecar} />
    </KerbcastProvider>,
  );
  run(FRAME_MS * 2);
  return sidecar;
}

beforeEach(() => {
  vi.useFakeTimers();
  tile = { width: 170, height: 400 };
  watched = [];
  globalThis.ResizeObserver = class {
    private readonly cb: ResizeObserverCallback;
    constructor(cb: ResizeObserverCallback) {
      this.cb = cb;
    }
    observe(el: Element) {
      watched.push({ el, cb: this.cb, last: null });
    }
    unobserve() {}
    disconnect() {
      watched = watched.filter((w) => w.cb !== this.cb);
    }
  } as unknown as typeof ResizeObserver;
});

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe("useReportDisplaySize: a box sized from the stream", () => {
  it("asks once for a tile sized by layout, at 16:9 whatever the tile's shape", () => {
    const sidecar = mount("tile");
    run(5000);
    expect(sidecar.reports).toEqual([{ width: 176, height: 100 }]);
  });

  it("settles after the first request when the frame keeps the stream's aspect", () => {
    const sidecar = mount("fit");
    run(10_000);
    expect(sidecar.reports).toEqual([{ width: 176, height: 100 }]);
  });

  it("does not ratchet down when a video at its own size arrives smaller than asked", () => {
    const sidecar = makeSidecar();
    sidecar.deliver = (req) => ({ width: req.width / 2, height: req.height / 2 });
    mount("intrinsic", sidecar);
    run(20_000);
    expect(sidecar.reports).toEqual([{ width: 1280, height: 720 }]);
  });

  it("sends nothing when the stream changes size or shape on its own", () => {
    const sidecar = mount("fit");
    const before = sidecar.reports.length;
    for (const size of [
      { width: 96, height: 54 },
      { width: 1920, height: 1080 },
      { width: 160, height: 120 },
      { width: 176, height: 400 },
    ]) {
      sidecar.streamBecomes(size);
      run(3000);
    }
    expect(sidecar.reports.length - before).toBe(0);
  });

  it("sends nothing while the stream flips between two sizes", () => {
    const sidecar = mount("fit");
    const before = sidecar.reports.length;
    for (let i = 0; i < 6; i++) {
      sidecar.streamBecomes(i % 2 ? { width: 176, height: 128 } : { width: 176, height: 96 });
      run(300);
    }
    run(5000);
    expect(sidecar.reports.length - before).toBe(0);
  });

  it("lets at most one more request through when the box follows too late to be recognised", () => {
    const sidecar = makeSidecar();
    sidecar.deliver = (req) => ({ width: (req.width * 3) / 4, height: (req.height * 3) / 4 });
    mount("late-intrinsic", sidecar);
    run(30_000);
    expect(sidecar.reports.length).toBeLessThanOrEqual(2);
    for (const r of sidecar.reports) {
      expect(r.width).toBeGreaterThanOrEqual(960);
      expect(r.height).toBeGreaterThanOrEqual(540);
    }
    const settled = sidecar.reports.length;
    run(30_000);
    expect(sidecar.reports.length).toBe(settled);
  });
});

describe("useReportDisplaySize: a genuine layout change", () => {
  it("sends exactly one request, sized from the tile, for a resize dragged across several sizes", () => {
    const sidecar = mount("tile");
    const before = sidecar.reports.length;
    for (const width of [240, 280, 320]) {
      resizeTile({ width, height: 400 });
      act(() => layout());
    }
    run(1000);
    expect(sidecar.reports.slice(before)).toEqual([{ width: 320, height: 180 }]);
  });

  it("still reports once a stream-sized frame has settled", () => {
    const sidecar = mount("fit");
    run(10_000);
    const before = sidecar.reports.length;
    resizeTile({ width: 320, height: 400 });
    run(1000);
    expect(sidecar.reports.slice(before)).toEqual([{ width: 320, height: 180 }]);
  });

  it("still reports a resize right after a stream change that the stream can not explain", () => {
    const sidecar = mount("fit");
    run(10_000);
    const before = sidecar.reports.length;
    sidecar.streamBecomes({ width: 160, height: 90 });
    resizeTile({ width: 640, height: 400 });
    run(1000);
    expect(sidecar.reports.slice(before)).toEqual([{ width: 640, height: 360 }]);
  });
});
