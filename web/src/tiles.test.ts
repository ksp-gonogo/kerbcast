import { beforeEach, describe, expect, it } from "vitest";
import {
  addAllCameras,
  loadPerfNoteDismissed,
  savePerfNoteDismissed,
} from "./tiles";
import type { Tile } from "./tiles";

function tile(flightId: number | null, spotlit = false): Tile {
  return { flightId, spotlit };
}

describe("addAllCameras", () => {
  it("appends a tile for every camera not already shown", () => {
    const next = addAllCameras([tile(1)], [1, 2, 3]);
    expect(next.map((t) => t.flightId)).toEqual([1, 2, 3]);
  });

  it("fills unpointed slots before appending", () => {
    const next = addAllCameras([tile(1), tile(null), tile(null)], [1, 2, 3, 4]);
    expect(next.map((t) => t.flightId)).toEqual([1, 2, 3, 4]);
  });

  it("is idempotent when every camera is already shown", () => {
    const tiles = [tile(1), tile(2)];
    expect(addAllCameras(tiles, [1, 2])).toBe(tiles);
    expect(addAllCameras(addAllCameras(tiles, [1, 2, 3]), [1, 2, 3])).toHaveLength(3);
  });

  it("does not duplicate cameras and preserves existing tiles", () => {
    const tiles = [tile(2, true)];
    const next = addAllCameras(tiles, [1, 2, 3]);
    expect(next.map((t) => t.flightId)).toEqual([2, 1, 3]);
    expect(next[0]).toBe(tiles[0]); // existing tile untouched (spotlit kept)
  });

  it("handles an empty grid", () => {
    const next = addAllCameras([], [5, 6]);
    expect(next).toEqual([tile(5), tile(6)]);
  });
});

describe("performance note persistence", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("defaults to not dismissed", () => {
    expect(loadPerfNoteDismissed()).toBe(false);
  });

  it("remembers dismissal", () => {
    savePerfNoteDismissed();
    expect(loadPerfNoteDismissed()).toBe(true);
  });
});
