import { describe, beforeEach, it, expect } from "vitest";
import { installDomStubs } from "./testing/index";

// Tests confirm the stubs install cleanly and expose the minimum surface
// that kerbcam component tests rely on.

describe("installDomStubs", () => {
  beforeEach(() => {
    installDomStubs();
  });

  it("installs idempotently (second call does not throw)", () => {
    expect(() => installDomStubs()).not.toThrow();
  });

  it("ResizeObserver is constructible after install", () => {
    const ro = new ResizeObserver(() => {});
    expect(ro).toBeDefined();
    expect(typeof ro.observe).toBe("function");
    expect(typeof ro.unobserve).toBe("function");
    expect(typeof ro.disconnect).toBe("function");
  });

  it("MediaStream is constructible", () => {
    const ms = new MediaStream();
    expect(ms).toBeDefined();
    expect(typeof ms.getTracks).toBe("function");
    expect(Array.isArray(ms.getTracks())).toBe(true);
  });

  it("captureStream returns a MediaStream-shaped object", () => {
    const canvas = document.createElement("canvas");
    const stream = (canvas as HTMLCanvasElement & { captureStream(fps?: number): MediaStream }).captureStream(30);
    expect(stream).toBeDefined();
    expect(typeof stream.getTracks).toBe("function");
  });

  it("HTMLMediaElement.play resolves without throwing", async () => {
    const video = document.createElement("video");
    await expect(video.play()).resolves.toBeUndefined();
  });

  it("window.matchMedia returns a non-matching MediaQueryList", () => {
    const mql = window.matchMedia("(prefers-color-scheme: dark)");
    expect(mql).toBeDefined();
    expect(mql.matches).toBe(false);
    expect(typeof mql.addEventListener).toBe("function");
  });
});
