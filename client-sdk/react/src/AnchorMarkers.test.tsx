import type { DisplayedFrameUt, FrameAnchor } from "@ksp-gonogo/kerbcast";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import {
  ANCHOR_MARKER_COLOURS,
  AnchorMarkers,
  anchorMarkerGeometry,
  type AnchorContext,
  type AnchorOverlay,
} from "./AnchorMarkers";

afterEach(cleanup);

function sizedVideo(boxW: number, boxH: number, fit?: "cover"): HTMLVideoElement {
  const v = document.createElement("video");
  Object.defineProperty(v, "clientWidth", { value: boxW });
  Object.defineProperty(v, "clientHeight", { value: boxH });
  Object.defineProperty(v, "videoWidth", { value: 1280 });
  Object.defineProperty(v, "videoHeight", { value: 720 });
  if (fit) v.style.objectFit = fit;
  return v;
}

function anchor(overrides: Partial<FrameAnchor> = {}): FrameAnchor {
  return {
    id: "port",
    x: 640,
    y: 360,
    inFrame: true,
    behind: false,
    distanceM: 30,
    apparentRadiusPx: 40,
    angleOffAxis: 0.5,
    ...overrides,
  };
}

function frame(anchors: FrameAnchor[]): DisplayedFrameUt {
  return {
    ut: 5000,
    epoch: 1,
    source: "exact",
    flightId: 7,
    rtpTimestamp: 90_000,
    presentedAt: 0,
    anchors,
    fov: 30,
    frameWidth: 1280,
    frameHeight: 720,
  };
}

function draw(anchors: FrameAnchor[], overlay?: AnchorOverlay, video = sizedVideo(640, 360)) {
  return render(<AnchorMarkers frame={frame(anchors)} video={video} overlay={overlay} />);
}

const marks = (c: HTMLElement) => [...c.querySelectorAll("path[data-corner]")] as SVGPathElement[];

describe("anchorMarkerGeometry", () => {
  it("sizes from the apparent radius on the displayed frame, never below the minimum", () => {
    const content = { x: 0, y: 0, width: 640, height: 360, scale: 0.5 };
    expect(anchorMarkerGeometry({ x: 1, y: 2 }, 40, 720, content)).toMatchObject({ cx: 1, cy: 2, half: 20 });
    expect(anchorMarkerGeometry({ x: 0, y: 0 }, 2, 720, content).half).toBe(14);
  });
});

describe("AnchorMarkers", () => {
  it("draws four solid corner marks around this client's anchor", () => {
    const { container } = draw([anchor()]);
    const paths = marks(container);
    expect(paths.map((p) => p.getAttribute("data-corner"))).toEqual(["top", "right", "bottom", "left"]);
    expect(paths.every((p) => p.getAttribute("data-shape") === "corners")).toBe(true);
    expect(paths.every((p) => p.getAttribute("stroke-dasharray") === null)).toBe(true);
    expect(paths[0].style.stroke).toBe(ANCHOR_MARKER_COLOURS.clear);
    /* Centre (320, 180) on a half-size display, half 20: the top-left corner starts at (300, 160). */
    expect(paths[0].getAttribute("d")).toContain("L300 160");
  });

  it("dashes an occluded anchor in the occluded colour", () => {
    const { container } = draw([anchor({ occluded: true })]);
    expect(marks(container).every((p) => p.getAttribute("stroke-dasharray") === "3 2")).toBe(true);
    expect(marks(container)[0].style.stroke).toBe(ANCHOR_MARKER_COLOURS.occluded);
  });

  it("draws a direction reticle: triangles coloured per side by the consumer", () => {
    const seen: AnchorContext[] = [];
    const { container } = draw([anchor({ forward: [0, 0.1, -1], up: [0, 1, 0] })], {
      shape: "triangles",
      corners: (a, ctx) => {
        seen.push(ctx);
        const tilted = (a.forward?.[1] ?? 0) > 0.05;
        return { top: { colour: tilted ? "red" : "lime" }, bottom: { colour: tilted ? "red" : "lime" } };
      },
    });
    const paths = marks(container);
    expect(paths.every((p) => p.getAttribute("data-shape") === "triangles")).toBe(true);
    expect(paths[0].style.stroke).toBe("red");
    expect(paths[0].style.fill).toBe("red");
    expect(paths[1].style.stroke).toBe(ANCHOR_MARKER_COLOURS.clear);
    expect(seen[0].geometry.cx).toBe(320);
    expect(seen[0].displayUt).toBe(5000);
    expect(seen[0].frame.fov).toBe(30);
  });

  it("hides a mark and switches the shape of one", () => {
    const { container } = draw([anchor()], {
      corners: { right: { hidden: true }, left: { shape: "triangles" } },
    });
    const paths = marks(container);
    expect(paths.map((p) => p.getAttribute("data-corner"))).toEqual(["top", "bottom", "left"]);
    expect(paths[2].getAttribute("data-shape")).toBe("triangles");
  });

  it("runs render for an anchor out of frame but leaves its marks out", () => {
    const { container } = draw([anchor({ inFrame: false, behind: true, x: -500 })], {
      render: (a, ctx) => <circle data-testid="edge" cx={Math.max(0, ctx.geometry.cx)} r={2} data-id={a.id} />,
    });
    expect(marks(container)).toHaveLength(0);
    expect(screen.getByTestId("edge").getAttribute("data-id")).toBe("port");
  });

  it("draws only this client's anchors unless an owner is listed", () => {
    const game = anchor({ id: "reticle", owner: "mission-control" });
    const subject = anchor({ id: "subject", owner: "kerbcast" });
    const mine = draw([anchor(), game, subject]);
    expect(mine.container.querySelectorAll("g[data-anchor]")).toHaveLength(1);
    cleanup();
    const withGame = draw([anchor(), game, subject], { owners: ["mission-control"] });
    const ids = [...withGame.container.querySelectorAll("g[data-anchor]")].map((g) => g.getAttribute("data-anchor"));
    expect(ids).toEqual(["port", "reticle"]);
  });

  it("puts the consumer's readout under the marker", () => {
    draw([anchor()], { readout: (a) => `${a.distanceM} m` });
    const el = screen.getByText("30 m");
    expect(el.style.top).toBe(`${180 + 20 + 11}px`);
  });

  it("maps positions under object-fit cover", () => {
    /* 1280x720 cropped into 400x400: scale 400/720, 311 px cut off each side. */
    const { container } = draw([anchor({ x: 0, y: 360, apparentRadiusPx: 0 })], {}, sizedVideo(400, 400, "cover"));
    const top = marks(container)[0].getAttribute("d")!;
    const cx = -(1280 * (400 / 720) - 400) / 2;
    expect(top.startsWith(`M${cx - 14} `)).toBe(true);
  });

  it("draws no marker for an observed anchor out of view, which carries no position", () => {
    const hidden: FrameAnchor = { id: "port", observed: true, inFrame: true, occluded: true };
    let rendered = 0;
    const { container } = draw([hidden, anchor({ id: "seen", observed: true, occluded: false })], {
      render: () => (rendered++, null),
    });
    const ids = [...container.querySelectorAll("g[data-anchor]")].map((g) => g.getAttribute("data-anchor"));
    expect(ids).toEqual(["seen"]);
    expect(rendered).toBe(1);
  });

  it("draws nothing for a frame without anchors", () => {
    const { container } = render(<AnchorMarkers frame={null} video={sizedVideo(640, 360)} />);
    expect(container.innerHTML).toBe("");
  });
});
