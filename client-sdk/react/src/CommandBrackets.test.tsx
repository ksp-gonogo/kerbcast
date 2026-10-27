import { effectiveCommandTrack, type CommandTrackState, type FrameAnchor } from "@ksp-gonogo/kerbcast";
import { cleanup, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import {
  CommandBlank,
  CommandBrackets,
  bracketGeometry,
  containedContentRect,
  type CommandBracketContext,
  type CommandBracketOverlay,
} from "./CommandBrackets";
import {
  MAX_UT_EXTRAPOLATION_MS,
  extrapolateCaptureUt,
  msUntilNextEvent,
} from "./hooks/useCommandDisplayUt";

afterEach(cleanup);

function track(overrides: Partial<CommandTrackState> = {}): CommandTrackState {
  return {
    ut: 1000,
    hasFix: true,
    rangeM: 4200,
    occluded: false,
    occluder: "",
    sunlit: true,
    bracketHalfPx: 20,
    frameHeightPx: 576,
    framingFits: true,
    events: [],
    ...overrides,
  };
}

const AUTHORITY = { owner: "mission-control" };

describe("containedContentRect", () => {
  it("letterboxes wide media in a tall box", () => {
    expect(containedContentRect(400, 400, 1600, 900)).toEqual({
      x: 0,
      y: 87.5,
      width: 400,
      height: 225,
    });
  });

  it("pillarboxes tall media in a wide box", () => {
    expect(containedContentRect(800, 300, 400, 400)).toEqual({
      x: 250,
      y: 0,
      width: 300,
      height: 300,
    });
  });

  it("uses the whole box while the media size is unknown", () => {
    expect(containedContentRect(640, 360, 0, 0)).toEqual({ x: 0, y: 0, width: 640, height: 360 });
  });
});

describe("bracketGeometry", () => {
  const rect = { x: 10, y: 20, width: 1024, height: 288 };

  it("scales capture px by the displayed height and centres on the frame", () => {
    const g = bracketGeometry(rect, 40, 576);
    expect(g.cx).toBe(522);
    expect(g.cy).toBe(164);
    expect(g.half).toBe(20);
    expect(g.arm).toBeCloseTo(12);
  });

  it("never draws a box smaller than 14 display px half-size", () => {
    const g = bracketGeometry(rect, 2, 576);
    expect(g.half).toBe(14);
    expect(g.arm).toBeCloseTo(8.4);
    expect(bracketGeometry(rect, null, 576).half).toBe(14);
  });

  it("reports both corners around the centre", () => {
    const g = bracketGeometry(rect, 40, 576);
    expect(g.topLeft).toEqual({ x: 502, y: 144 });
    expect(g.bottomRight).toEqual({ x: 542, y: 184 });
  });
});

describe("extrapolateCaptureUt", () => {
  it("advances at the warp rate and stops at the cap", () => {
    expect(extrapolateCaptureUt(100, 4, 1000, 1500)).toBe(102);
    expect(extrapolateCaptureUt(100, 1, 0, MAX_UT_EXTRAPOLATION_MS * 10)).toBe(
      100 + MAX_UT_EXTRAPOLATION_MS / 1000,
    );
    expect(extrapolateCaptureUt(100, 1, 1000, 500)).toBe(100);
  });
});

describe("msUntilNextEvent", () => {
  const ev = (ut: number) => ({ ut, occluded: false, occluder: "", sunlit: true });
  it("times the first event after the displayed UT at the warp rate", () => {
    expect(msUntilNextEvent([ev(99), ev(103), ev(101)], 100, 2)).toBe(500);
    expect(msUntilNextEvent([ev(99)], 100, 1)).toBeNull();
    expect(msUntilNextEvent([], 100, 1)).toBeNull();
    expect(msUntilNextEvent([ev(101)], 100, 0)).toBeNull();
  });
});

function renderBrackets(
  t: CommandTrackState,
  displayUt = 1000,
  overlay?: CommandBracketOverlay,
) {
  return render(
    <CommandBrackets
      state={effectiveCommandTrack(t, displayUt)}
      displayUt={displayUt}
      utSource="override"
      video={null}
      fallbackSize={{ width: 1024, height: 576 }}
      overlay={overlay}
    />,
  );
}

function corner(container: HTMLElement, which: "topLeft" | "bottomRight") {
  return container.querySelector(`path[data-corner="${which}"]`);
}

describe("CommandBrackets", () => {
  it("draws only the two corners, solid, when in view", () => {
    const { container } = renderBrackets(track());
    expect(container.querySelectorAll("path")).toHaveLength(2);
    expect(corner(container, "topLeft")!.getAttribute("stroke-dasharray")).toBeNull();
    expect(corner(container, "bottomRight")!.getAttribute("stroke-dasharray")).toBeNull();
    expect(screen.getByText("IN VIEW · 4.2 km")).toBeTruthy();
  });

  it("dashes both corners when occluded", () => {
    const { container } = renderBrackets(track({ occluded: true, occluder: "Mun" }));
    expect(corner(container, "topLeft")!.getAttribute("stroke-dasharray")).toBe("3 2");
    expect(corner(container, "bottomRight")!.getAttribute("stroke-dasharray")).toBe("3 2");
    expect(screen.getByText("OCCLUDED MUN · 4.2 km")).toBeTruthy();
  });

  it("mirrors the camera corner under an authority too", () => {
    const { container } = renderBrackets(
      track({ occluded: true, occluder: "Mun", authority: AUTHORITY }),
    );
    expect(corner(container, "bottomRight")!.getAttribute("stroke-dasharray")).toBe("3 2");
    expect(screen.getByText("OCCLUDED MUN · 4.2 km")).toBeTruthy();
  });

  it("draws no box without a fix, only the readout", () => {
    const { container } = renderBrackets(track({ hasFix: false, rangeM: undefined }));
    expect(container.querySelector("svg")).toBeNull();
    expect(screen.getByText("NO FIX")).toBeTruthy();
  });
});

describe("CommandBrackets overlay", () => {
  it("still runs the overlay and readout without a fix, with no default corners", () => {
    const { container } = renderBrackets(track({ hasFix: false, rangeM: undefined }), 1000, {
      render: (ctx) => (ctx.state.hasFix ? null : <circle data-testid="held" r={3} />),
      readout: (text) => `${text} · HELD`,
    });
    expect(container.querySelectorAll("path")).toHaveLength(0);
    expect(screen.getByTestId("held").closest("svg")).toBe(container.querySelector("svg"));
    expect(screen.getByText("NO FIX · HELD")).toBeTruthy();
  });

  it("runs over a blanked feed with an empty default readout", () => {
    let readoutDefault: string | null = null;
    const { container } = renderBrackets(track({ blank: "Lost" }), 1000, {
      render: (ctx) => (ctx.state.blank ? <circle data-testid="held" r={3} /> : null),
      readout: (text) => {
        readoutDefault = text;
        return "HELD 2m";
      },
    });
    expect(readoutDefault).toBe("");
    expect(container.querySelectorAll("path")).toHaveLength(0);
    expect(screen.getByTestId("held")).toBeTruthy();
    expect(screen.getByText("HELD 2m")).toBeTruthy();
  });

  it("draws nothing over a blanked feed without an overlay", () => {
    const { container } = renderBrackets(track({ blank: "Lost" }));
    expect(container.querySelector("svg")).toBeNull();
    expect(container.textContent).toBe("");
  });

  it("renders inside the bracket SVG with the geometry, UT and state", () => {
    let seen: CommandBracketContext | null = null;
    const { container } = renderBrackets(track({ authority: AUTHORITY }), 1234, {
      render: (ctx) => {
        seen = ctx;
        const { x, y } = ctx.geometry.bottomRight;
        return <rect data-testid="mark" x={x - 6} y={y - 6} width={4} height={4} />;
      },
    });
    const ctx = seen as CommandBracketContext | null;
    expect(ctx).not.toBeNull();
    expect(ctx!.displayUt).toBe(1234);
    expect(ctx!.utSource).toBe("override");
    expect(ctx!.state.authority).toEqual(AUTHORITY);
    expect(ctx!.geometry.stroke).toBe(2);
    expect(ctx!.geometry.bottomRight.x).toBe(ctx!.geometry.cx + ctx!.geometry.half);
    const path = corner(container, "bottomRight")!.getAttribute("d")!;
    const { x, y } = ctx!.geometry.bottomRight;
    expect(path).toContain(`L${x} ${y}`);
    const mark = screen.getByTestId("mark");
    expect(mark.closest("svg")).toBe(container.querySelector("svg"));
    expect(mark.getAttribute("x")).toBe(String(x - 6));
  });

  it("applies static corner overrides", () => {
    const { container } = renderBrackets(track(), 1000, {
      corners: { bottomRight: { colour: "rgb(1, 2, 3)", dashed: true } },
    });
    const br = corner(container, "bottomRight") as SVGPathElement;
    expect(br.getAttribute("stroke-dasharray")).toBe("3 2");
    expect(br.style.stroke).toBe("rgb(1, 2, 3)");
    expect(corner(container, "topLeft")!.getAttribute("stroke-dasharray")).toBeNull();
  });

  it("hides a corner and takes overrides from a function of the context", () => {
    const { container } = renderBrackets(track({ occluded: true, occluder: "Mun" }), 1000, {
      corners: (ctx) => ({
        bottomRight: { hidden: true },
        topLeft: { dashed: !ctx.state.occluded },
      }),
    });
    expect(corner(container, "bottomRight")).toBeNull();
    expect(corner(container, "topLeft")!.getAttribute("stroke-dasharray")).toBeNull();
  });

  it("extends the readout with the displayed UT", () => {
    renderBrackets(track(), 1060, {
      readout: (text, ctx) => `${text} · AGE ${ctx.displayUt - 1000}s`,
    });
    expect(screen.getByText("IN VIEW · 4.2 km · AGE 60s")).toBeTruthy();
  });

  it("replaces the readout, or hides it with null", () => {
    renderBrackets(track(), 1000, { readout: () => "CUSTOM" });
    expect(screen.getByText("CUSTOM")).toBeTruthy();
    cleanup();
    renderBrackets(track(), 1000, { readout: () => null });
    expect(screen.queryByText(/IN VIEW/)).toBeNull();
  });
});

/** A video element with a laid-out box and a decoded size, as a browser reports them. */
function sizedVideo(boxW: number, boxH: number, mediaW = boxW, mediaH = boxH): HTMLVideoElement {
  const v = document.createElement("video");
  Object.defineProperty(v, "clientWidth", { value: boxW });
  Object.defineProperty(v, "clientHeight", { value: boxH });
  Object.defineProperty(v, "videoWidth", { value: mediaW });
  Object.defineProperty(v, "videoHeight", { value: mediaH });
  return v;
}

function subjectAnchor(overrides: Partial<FrameAnchor> = {}): FrameAnchor {
  return {
    id: "subject",
    owner: "kerbcast",
    x: 256,
    y: 144,
    inFrame: true,
    behind: false,
    distanceM: 4200,
    apparentRadiusPx: 40,
    angleOffAxis: 12,
    occluded: false,
    ...overrides,
  };
}

describe("CommandBrackets on the subject anchor", () => {
  function renderOnSubject(
    subject: FrameAnchor | null,
    t: CommandTrackState = track(),
    overlay?: CommandBracketOverlay,
    frame = { width: 1024, height: 576 },
  ) {
    return render(
      <CommandBrackets
        state={effectiveCommandTrack(t, 1000)}
        displayUt={1000}
        utSource="exact"
        video={sizedVideo(1024, 576)}
        overlay={overlay}
        subject={subject}
        subjectFrameSize={frame}
      />,
    );
  }

  function seenWith(subject: FrameAnchor | null, t: CommandTrackState = track()) {
    let seen: CommandBracketContext | null = null;
    const view = renderOnSubject(subject, t, { render: (ctx) => ((seen = ctx), null) });
    return { ctx: seen as unknown as CommandBracketContext, view };
  }

  /* An observed result out of frame or occluded: no coordinates at all. */
  const unseen = (o: Partial<FrameAnchor>): FrameAnchor => ({
    id: "subject",
    owner: "kerbcast",
    observed: true,
    inFrame: true,
    ...o,
  });

  it("brackets the subject where it really is, not the frame centre", () => {
    const { ctx, view } = seenWith(subjectAnchor());
    expect([ctx.geometry.cx, ctx.geometry.cy, ctx.geometry.half]).toEqual([256, 144, 40]);
    expect(ctx.subject?.id).toBe("subject");
    expect(view.container.querySelector("svg")!.getAttribute("data-subject")).toBe("placed");
    expect(corner(view.container, "topLeft")!.getAttribute("stroke-dasharray")).toBeNull();
  });

  it("maps a capture frame larger than the decoded video by the frame's own size", () => {
    let seen: CommandBracketContext | null = null;
    renderOnSubject(
      subjectAnchor({ x: 512, y: 288, apparentRadiusPx: 80 }),
      track(),
      { render: (ctx) => ((seen = ctx), null) },
      { width: 2048, height: 1152 },
    );
    const g = (seen as CommandBracketContext | null)!.geometry;
    expect([g.cx, g.cy, g.half]).toEqual([256, 144, 40]);
  });

  it("falls back to the 1.11 box when the subject is occluded: centred, aimed range, aim-line colour", () => {
    const t = track({ occluded: true, occluder: "Mun" });
    const { ctx, view } = seenWith(unseen({ occluded: true }), t);
    expect([ctx.geometry.cx, ctx.geometry.cy]).toEqual([512, 288]);
    expect(ctx.geometry).toEqual(bracketGeometry({ x: 0, y: 0, width: 1024, height: 576 }, 20, 576));
    expect(corner(view.container, "topLeft")!.getAttribute("stroke-dasharray")).toBe("3 2");
    expect(view.container.querySelector("svg")!.getAttribute("data-subject")).toBe("centred");
    expect(ctx.subject?.occluded).toBe(true);
  });

  it("falls back to the 1.11 box, corners drawn, when the subject is out of frame", () => {
    const { ctx, view } = seenWith(unseen({ inFrame: false, occluded: false }));
    expect([ctx.geometry.cx, ctx.geometry.cy]).toEqual([512, 288]);
    expect(view.container.querySelectorAll("path")).toHaveLength(2);
    expect(screen.getByText("IN VIEW · 4.2 km")).toBeTruthy();
  });

  it("keeps the 1.11 box under the command lock even with the subject in view", () => {
    /* The authority aims somewhere else; the box is its aim, the anchor a second mark for the consumer. */
    const locked = track({ authority: AUTHORITY, occluded: true, occluder: "Mun", bracketHalfPx: 30 });
    const { ctx, view } = seenWith(subjectAnchor(), locked);
    expect(ctx.geometry).toEqual(bracketGeometry({ x: 0, y: 0, width: 1024, height: 576 }, 30, 576));
    expect(corner(view.container, "topLeft")!.getAttribute("stroke-dasharray")).toBe("3 2");
    expect(ctx.subject).toMatchObject({ id: "subject", x: 256, y: 144 });
    expect(screen.getByText("OCCLUDED MUN · 4.2 km")).toBeTruthy();
  });

  it("centres on the frame as before without a subject anchor", () => {
    const { ctx } = seenWith(null);
    expect([ctx.geometry.cx, ctx.geometry.cy]).toEqual([512, 288]);
    expect(ctx.subject).toBeNull();
  });
});

describe("CommandBlank", () => {
  it("labels the blank with its reason", () => {
    render(<CommandBlank reason="Waiting for mission control" />);
    expect(screen.getByRole("status", { name: "Feed blanked" }).textContent).toBe(
      "Waiting for mission control",
    );
  });
});
