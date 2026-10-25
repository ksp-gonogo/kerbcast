import { effectiveCommandTrack, type CommandTrackState } from "@ksp-gonogo/kerbcast";
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

const AUTHORITY = { owner: "gonogo" };

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

describe("CommandBlank", () => {
  it("labels the blank with its reason", () => {
    render(<CommandBlank reason="Waiting for gonogo" />);
    expect(screen.getByRole("status", { name: "Feed blanked" }).textContent).toBe(
      "Waiting for gonogo",
    );
  });
});
