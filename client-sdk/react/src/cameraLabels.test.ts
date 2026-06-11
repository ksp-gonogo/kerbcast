import { describe, expect, it } from "vitest";
import { buildCameraLabeler } from "./cameraLabels";
import type { LabelableCamera } from "./cameraLabels";

function cam(
  flightId: number,
  cameraName: string,
  partTitle?: string | null,
): LabelableCamera {
  return { flightId, cameraName, partTitle };
}

describe("buildCameraLabeler", () => {
  it("leaves non-colliding names unchanged", () => {
    const cameras = [cam(1, "NavCam", "NavCam"), cam(2, "Tail Cam", "Some Part")];
    const label = buildCameraLabeler(cameras);
    expect(label(cameras[0]!)).toBe("NavCam");
    expect(label(cameras[1]!)).toBe("Tail Cam");
  });

  it("disambiguates colliding names by part title when titles differ", () => {
    const cameras = [
      cam(1, "NavCam", "NavCam"),
      cam(2, "NavCam", "Clamp-O-Tron Docking Port Jr."),
    ];
    const label = buildCameraLabeler(cameras);
    expect(label(cameras[0]!)).toBe("NavCam");
    expect(label(cameras[1]!)).toBe("NavCam - Clamp-O-Tron Docking Port Jr.");
  });

  it("numbers identical cameras that the part title cannot tell apart", () => {
    const cameras = [
      cam(10, "KazzelBlad - BoosterCam", "KazzelBlad - BoosterCam"),
      cam(20, "KazzelBlad - BoosterCam", "KazzelBlad - BoosterCam"),
      cam(30, "KazzelBlad - BoosterCam", "KazzelBlad - BoosterCam"),
    ];
    const label = buildCameraLabeler(cameras);
    expect(label(cameras[0]!)).toBe("KazzelBlad - BoosterCam #1");
    expect(label(cameras[1]!)).toBe("KazzelBlad - BoosterCam #2");
    expect(label(cameras[2]!)).toBe("KazzelBlad - BoosterCam #3");
  });

  it("numbers by flightId order, not list order", () => {
    const cameras = [
      cam(30, "BoosterCam", "BoosterCam"),
      cam(10, "BoosterCam", "BoosterCam"),
      cam(20, "BoosterCam", "BoosterCam"),
    ];
    const label = buildCameraLabeler(cameras);
    expect(label(cameras[0]!)).toBe("BoosterCam #3");
    expect(label(cameras[1]!)).toBe("BoosterCam #1");
    expect(label(cameras[2]!)).toBe("BoosterCam #2");
  });

  it("keeps numbers stable when unrelated cameras join the list", () => {
    const a = cam(10, "BoosterCam", "BoosterCam");
    const b = cam(20, "BoosterCam", "BoosterCam");
    const before = buildCameraLabeler([a, b]);
    const after = buildCameraLabeler([a, b, cam(5, "NavCam", "NavCam")]);
    expect(after(a)).toBe(before(a));
    expect(after(b)).toBe(before(b));
  });

  it("numbers cameras whose part titles collide too, after title disambiguation", () => {
    const cameras = [
      cam(1, "NavCam", "NavCam"),
      cam(2, "NavCam", "Clamp-O-Tron"),
      cam(3, "NavCam", "Clamp-O-Tron"),
    ];
    const label = buildCameraLabeler(cameras);
    expect(label(cameras[0]!)).toBe("NavCam");
    expect(label(cameras[1]!)).toBe("NavCam - Clamp-O-Tron #1");
    expect(label(cameras[2]!)).toBe("NavCam - Clamp-O-Tron #2");
  });

  it("falls back to numbering when colliding cameras have no part title", () => {
    const cameras = [cam(1, "Camera", null), cam(2, "Camera", undefined)];
    const label = buildCameraLabeler(cameras);
    expect(label(cameras[0]!)).toBe("Camera #1");
    expect(label(cameras[1]!)).toBe("Camera #2");
  });
});
