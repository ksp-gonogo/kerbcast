import { describe, expect, it } from "vitest";
import { bestGrid } from "./bestGrid";

describe("bestGrid", () => {
  it("returns a single cell for one tile", () => {
    expect(bestGrid(1, 1000, 500)).toEqual({ cols: 1, rows: 1 });
  });

  it("packs two tiles side-by-side in a wide, short area", () => {
    expect(bestGrid(2, 1000, 300)).toEqual({ cols: 2, rows: 1 });
  });

  it("stacks two tiles in a tall, narrow area", () => {
    expect(bestGrid(2, 300, 1000)).toEqual({ cols: 1, rows: 2 });
  });

  it("uses a 2x2 grid for four tiles in a square area", () => {
    expect(bestGrid(4, 1000, 1000)).toEqual({ cols: 2, rows: 2 });
  });

  it("keeps four tiles in one row when the area is very wide", () => {
    expect(bestGrid(4, 4000, 300)).toEqual({ cols: 4, rows: 1 });
  });

  it("falls back to a single row when the area is unmeasured", () => {
    expect(bestGrid(3, 0, 0)).toEqual({ cols: 3, rows: 1 });
  });

  it("never chooses more columns than tiles", () => {
    const { cols, rows } = bestGrid(3, 5000, 100);
    expect(cols).toBeLessThanOrEqual(3);
    expect(cols * rows).toBeGreaterThanOrEqual(3);
  });
});
