import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { NetworkIcon } from "./NetworkIcon";

describe("NetworkIcon", () => {
  it("sizes by prop and takes the surrounding colour", () => {
    const { container } = render(<NetworkIcon size={20} />);
    const svg = container.querySelector("svg")!;
    expect(svg.getAttribute("width")).toBe("20");
    expect(svg.getAttribute("stroke")).toBe("currentColor");
    expect(svg.getAttribute("aria-hidden")).toBe("true");
    expect(svg.querySelectorAll("circle")).toHaveLength(4);
  });
});
