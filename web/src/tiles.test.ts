import { beforeEach, describe, expect, it } from "vitest";
import {
  addAllCameras,
  addTile,
  cameraKey,
  loadPerfNoteDismissed,
  loadTiles,
  reconcileTiles,
  savePerfNoteDismissed,
  saveTiles,
  seedTiles,
  updateTile,
} from "./tiles";
import type { Tile } from "./tiles";

function tile(
  flightId: number | null,
  spotlit = false,
  key: string | null = null,
): Tile {
  return { flightId, spotlit, key };
}

function cam(
  flightId: number,
  vesselName = "Kerbal X",
  partName = "hull.cam",
  cameraName = "Camera",
) {
  return { flightId, vesselName, partName, cameraName };
}

// ---------------------------------------------------------------------------
// cameraKey
// ---------------------------------------------------------------------------

describe("cameraKey", () => {
  it("produces a stable pipe-separated string", () => {
    expect(cameraKey(cam(1, "Kerbal X", "hull.cam", "FwdCam"))).toBe(
      "Kerbal X|hull.cam|FwdCam",
    );
  });

  it("handles undefined fields by treating them as empty strings", () => {
    const k = cameraKey({ vesselName: undefined as unknown as string, partName: "p", cameraName: "c" });
    expect(k).toBe("|p|c");
  });

  it("two cameras with the same identity produce the same key", () => {
    expect(cameraKey(cam(1))).toBe(cameraKey(cam(99)));
  });

  it("two cameras with different cameraName produce different keys", () => {
    expect(cameraKey(cam(1, "Kerbal X", "hull.cam", "FwdCam"))).not.toBe(
      cameraKey(cam(2, "Kerbal X", "hull.cam", "AftCam")),
    );
  });
});

// ---------------------------------------------------------------------------
// seedTiles
// ---------------------------------------------------------------------------

describe("seedTiles", () => {
  it("creates one tile per camera, capped at 4", () => {
    const cameras = Array.from({ length: 6 }, (_, i) =>
      cam(i + 1, "Ship", `part${i}`, `Cam${i}`),
    );
    const tiles = seedTiles(cameras);
    expect(tiles).toHaveLength(4);
    expect(tiles.map((t) => t.flightId)).toEqual([1, 2, 3, 4]);
  });

  it("stores the stable key on each seeded tile", () => {
    const cameras = [cam(42, "Kerbal X", "hull.cam", "FwdCam")];
    const tiles = seedTiles(cameras);
    expect(tiles[0].key).toBe("Kerbal X|hull.cam|FwdCam");
    expect(tiles[0].flightId).toBe(42);
  });
});

// ---------------------------------------------------------------------------
// reconcileTiles
// ---------------------------------------------------------------------------

describe("reconcileTiles", () => {
  it("returns the same array reference when nothing needs rebinding", () => {
    const tiles = [tile(1, false, "Ship|part|Cam"), tile(2, false, "Ship|part|Cam2")];
    const liveCameras = [cam(1), cam(2)];
    expect(reconcileTiles(tiles, liveCameras)).toBe(tiles);
  });

  it("rebinds a stale flightId to the new flightId of the same camera", () => {
    const tiles = [tile(1, false, "Kerbal X|hull.cam|FwdCam")];
    // After revert: same camera but new flightId (5).
    const liveCameras = [cam(5, "Kerbal X", "hull.cam", "FwdCam")];
    const result = reconcileTiles(tiles, liveCameras);
    expect(result[0].flightId).toBe(5);
    expect(result[0].key).toBe("Kerbal X|hull.cam|FwdCam");
    expect(result[0].spotlit).toBe(false);
  });

  it("preserves spotlight state when rebinding", () => {
    const tiles = [tile(1, true, "Kerbal X|hull.cam|FwdCam")];
    const liveCameras = [cam(7, "Kerbal X", "hull.cam", "FwdCam")];
    const result = reconcileTiles(tiles, liveCameras);
    expect(result[0].flightId).toBe(7);
    expect(result[0].spotlit).toBe(true);
  });

  it("leaves null-flightId (empty) tiles untouched", () => {
    const tiles = [tile(null, false, null)];
    const liveCameras = [cam(1)];
    expect(reconcileTiles(tiles, liveCameras)).toBe(tiles);
  });

  it("leaves tiles with no key as reconnecting (cannot rebind)", () => {
    const tiles = [tile(99, false, null)];
    const liveCameras = [cam(1, "Kerbal X", "hull.cam", "FwdCam")];
    expect(reconcileTiles(tiles, liveCameras)).toBe(tiles);
    expect(reconcileTiles(tiles, liveCameras)[0].flightId).toBe(99);
  });

  it("leaves tiles whose key has no live match as reconnecting", () => {
    const tiles = [tile(1, false, "Kerbal X|hull.cam|GoneCam")];
    const liveCameras = [cam(1, "Kerbal X", "hull.cam", "DifferentCam")];
    // flightId 1 is still live so tile is not stale; no rebind needed.
    expect(reconcileTiles(tiles, liveCameras)).toBe(tiles);
  });

  it("does not double-bind two stale tiles to the same live camera", () => {
    // Both tiles have the same key (e.g. duplicate camera names).
    const tiles = [
      tile(1, false, "Kerbal X|hull.cam|FwdCam"),
      tile(2, false, "Kerbal X|hull.cam|FwdCam"),
    ];
    const liveCameras = [cam(10, "Kerbal X", "hull.cam", "FwdCam")];
    const result = reconcileTiles(tiles, liveCameras);
    // First tile gets rebound; second stays stale.
    expect(result[0].flightId).toBe(10);
    expect(result[1].flightId).toBe(2);
  });

  it("rebinds multiple tiles independently in a mixed scenario", () => {
    const tiles = [
      tile(1, false, "Kerbal X|hull.cam|FwdCam"),  // stale, has match
      tile(2, false, "Kerbal X|hull.cam|AftCam"),   // stale, has match
      tile(3, true, "Kerbal X|hull.cam|TopCam"),    // still live
    ];
    const liveCameras = [
      cam(10, "Kerbal X", "hull.cam", "FwdCam"),
      cam(11, "Kerbal X", "hull.cam", "AftCam"),
      cam(3, "Kerbal X", "hull.cam", "TopCam"),
    ];
    const result = reconcileTiles(tiles, liveCameras);
    expect(result[0].flightId).toBe(10);
    expect(result[1].flightId).toBe(11);
    expect(result[2].flightId).toBe(3);
    expect(result[2].spotlit).toBe(true);
  });
});

// ---------------------------------------------------------------------------
// updateTile (key parameter)
// ---------------------------------------------------------------------------

describe("updateTile with key", () => {
  it("stores the key when provided", () => {
    const tiles = [tile(1, false, null)];
    const result = updateTile(tiles, 0, 2, "Ship|part|Cam");
    expect(result[0].flightId).toBe(2);
    expect(result[0].key).toBe("Ship|part|Cam");
  });

  it("preserves existing key when key argument is omitted", () => {
    const tiles = [tile(1, false, "Ship|part|Cam")];
    const result = updateTile(tiles, 0, 2);
    expect(result[0].key).toBe("Ship|part|Cam");
  });

  it("clears key when null is passed explicitly", () => {
    const tiles = [tile(1, false, "Ship|part|Cam")];
    const result = updateTile(tiles, 0, null, null);
    expect(result[0].key).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// addTile
// ---------------------------------------------------------------------------

describe("addTile", () => {
  it("creates a tile with null key and null flightId", () => {
    const result = addTile([]);
    expect(result[0]).toEqual({ flightId: null, spotlit: false, key: null });
  });
});

// ---------------------------------------------------------------------------
// addAllCameras (now takes camera objects)
// ---------------------------------------------------------------------------

describe("addAllCameras", () => {
  it("appends a tile for every camera not already shown", () => {
    const next = addAllCameras([tile(1)], [cam(1), cam(2), cam(3)]);
    expect(next.map((t) => t.flightId)).toEqual([1, 2, 3]);
  });

  it("fills unpointed slots before appending", () => {
    const next = addAllCameras(
      [tile(1), tile(null), tile(null)],
      [cam(1), cam(2), cam(3), cam(4)],
    );
    expect(next.map((t) => t.flightId)).toEqual([1, 2, 3, 4]);
  });

  it("is idempotent when every camera is already shown", () => {
    const tiles = [tile(1), tile(2)];
    expect(addAllCameras(tiles, [cam(1), cam(2)])).toBe(tiles);
  });

  it("stores the stable key on newly added tiles", () => {
    const next = addAllCameras([], [cam(42, "Kerbal X", "hull.cam", "FwdCam")]);
    expect(next[0].key).toBe("Kerbal X|hull.cam|FwdCam");
  });

  it("does not duplicate cameras and preserves existing tiles", () => {
    const tiles = [tile(2, true)];
    const next = addAllCameras(tiles, [cam(1), cam(2), cam(3)]);
    expect(next.map((t) => t.flightId)).toEqual([2, 1, 3]);
    expect(next[0]).toBe(tiles[0]); // existing tile untouched (spotlit kept)
  });

  it("handles an empty grid", () => {
    const next = addAllCameras([], [cam(5), cam(6)]);
    expect(next.map((t) => t.flightId)).toEqual([5, 6]);
  });
});

// ---------------------------------------------------------------------------
// localStorage round-trip (key field)
// ---------------------------------------------------------------------------

describe("tile key persistence", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("round-trips the key field through localStorage", () => {
    const tiles: Tile[] = [
      { flightId: 1, spotlit: false, key: "Kerbal X|hull.cam|FwdCam" },
    ];
    saveTiles(tiles);
    const loaded = loadTiles();
    expect(loaded).not.toBeNull();
    expect(loaded![0].key).toBe("Kerbal X|hull.cam|FwdCam");
  });

  it("loads old tiles without key as key: null", () => {
    localStorage.setItem(
      "kerbcast:tiles",
      JSON.stringify([{ flightId: 1, spotlit: false }]),
    );
    const loaded = loadTiles();
    expect(loaded).not.toBeNull();
    expect(loaded![0].key).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// performance note persistence
// ---------------------------------------------------------------------------

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
