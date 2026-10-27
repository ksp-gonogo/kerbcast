import { describe, expect, it } from "vitest";
import { anchorPoint, findAnchor, fitFrame, frameLengthToScreen, frameToScreen } from "./anchors";

describe("fitFrame", () => {
  it("letterboxes under contain", () => {
    /* A 16:9 frame in a square box: full width, bars above and below. */
    expect(fitFrame(400, 400, 1280, 720, "contain")).toEqual({
      x: 0,
      y: 87.5,
      width: 400,
      height: 225,
      scale: 400 / 1280,
    });
  });

  it("crops under cover, overflowing the box", () => {
    const r = fitFrame(400, 400, 1280, 720, "cover");
    expect(r.height).toBe(400);
    expect(r.width).toBeCloseTo(711.11, 2);
    expect(r.x).toBeCloseTo(-155.56, 2);
    expect(r.y).toBe(0);
  });

  it("fills the box while a size is unknown", () => {
    expect(fitFrame(300, 200, 0, 0)).toMatchObject({ x: 0, y: 0, width: 300, height: 200 });
  });
});

describe("frameToScreen", () => {
  it("maps frame pixels into the fitted rect for both fits", () => {
    const contain = fitFrame(400, 400, 1280, 720, "contain");
    expect(frameToScreen({ x: 640, y: 360 }, 1280, 720, contain)).toEqual({ x: 200, y: 200 });
    expect(frameToScreen({ x: 0, y: 0 }, 1280, 720, contain)).toEqual({ x: 0, y: 87.5 });

    const cover = fitFrame(400, 400, 1280, 720, "cover");
    const corner = frameToScreen({ x: 0, y: 0 }, 1280, 720, cover);
    expect(corner.x).toBeCloseTo(-155.56, 2);
    expect(frameToScreen({ x: 640, y: 360 }, 1280, 720, cover)).toEqual({ x: 200, y: 200 });
  });

  it("scales a capture-pixel length by the displayed height", () => {
    const r = fitFrame(640, 360, 1280, 720);
    expect(frameLengthToScreen(20, 720, r)).toBe(10);
  });
});

describe("findAnchor", () => {
  const base = { x: 0, y: 0, inFrame: true, behind: false, apparentRadiusPx: 0, angleOffAxis: 0 };
  const anchors = [
    { ...base, id: "subject", owner: "kerbcast" },
    { ...base, id: "subject" },
  ];
  it("finds this client's own anchor without an owner, and another's with one", () => {
    expect(findAnchor(anchors, "subject")).toBe(anchors[1]);
    expect(findAnchor(anchors, "subject", "kerbcast")).toBe(anchors[0]);
    expect(findAnchor(anchors, "nope")).toBeNull();
    expect(findAnchor(undefined, "subject")).toBeNull();
  });
});

describe("anchorPoint", () => {
  it("is null for an observed result out of view, which carries no coordinates", () => {
    expect(
      anchorPoint({ id: "subject", owner: "kerbcast", observed: true, inFrame: true, occluded: true }),
    ).toBeNull();
    expect(anchorPoint({ id: "a", inFrame: true, x: 3, y: 4 })).toEqual({ x: 3, y: 4 });
  });
});
