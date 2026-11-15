import { describe, expect, it } from "vitest";
import { nearShapeFit } from "./videoBox";

describe("nearShapeFit", () => {
  it("covers a 16:9 box with a frame rounded to whole macroblocks", () => {
    expect(nearShapeFit(830, 466, 848, 480)).toBe("cover");
    expect(nearShapeFit(1280, 720, 1920, 1072)).toBe("cover");
  });

  it("letterboxes a frame of a different shape", () => {
    expect(nearShapeFit(830, 466, 480, 480)).toBe("contain");
    expect(nearShapeFit(830, 466, 640, 480)).toBe("contain");
  });

  it("letterboxes while either size is unknown", () => {
    expect(nearShapeFit(830, 466, 0, 0)).toBe("contain");
    expect(nearShapeFit(0, 0, 848, 480)).toBe("contain");
  });
});
