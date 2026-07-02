import { beforeEach, describe, expect, it } from "vitest";
import {
  addAllCameras,
  addTile,
  cameraKey,
  loadPerfNoteDismissed,
  loadTiles,
  reconcileTiles,
  removeAllCameras,
  removeAllLostCameras,
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

/** A Destroyed tombstone camera (lingers in the snapshot as SIGNAL LOST). */
function deadCam(
  flightId: number,
  vesselName = "Kerbal X",
  partName = "hull.cam",
  cameraName = "Camera",
) {
  return { ...cam(flightId, vesselName, partName, cameraName), lifecycle: "destroyed" as const };
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

  it("leaves keyless tiles unrebound by identity when no live camera is free", () => {
    // Keyless dead tile cannot rebind by identity. The one live camera is
    // already shown by another tile, so there is nothing to repurpose it to.
    const tiles = [tile(1, false, "Kerbal X|hull.cam|FwdCam"), tile(99, false, null)];
    const liveCameras = [cam(1, "Kerbal X", "hull.cam", "FwdCam")];
    expect(reconcileTiles(tiles, liveCameras)).toBe(tiles);
    expect(reconcileTiles(tiles, liveCameras)[1].flightId).toBe(99);
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

  it("rebinds off a destroyed id even while that id lingers in the snapshot", () => {
    // After a revert the sidecar keeps the old camera as a `destroyed`
    // tombstone (for the SIGNAL LOST UI) AND republishes the same physical
    // camera under a new active flightId. A tile still on the old id must
    // rebind to the active one, not be treated as live just because the
    // destroyed entry is still present.
    const tiles = [tile(1875100382, false, "vmod test|DC.TurretCam|TurretCam")];
    const liveCameras = [
      { ...cam(1875100382, "vmod test", "DC.TurretCam", "TurretCam"), lifecycle: "destroyed" as const },
      { ...cam(1218154353, "vmod test", "DC.TurretCam", "TurretCam"), lifecycle: "active" as const },
    ];
    const result = reconcileTiles(tiles, liveCameras);
    expect(result[0].flightId).toBe(1218154353);
  });

  it("does not rebind a tile to a destroyed camera", () => {
    // A dead id with no active replacement stays reconnecting; we never bind
    // a tile to a destroyed camera.
    const tiles = [tile(1, false, "Kerbal X|hull.cam|Camera")];
    const liveCameras = [
      { ...cam(1, "Kerbal X", "hull.cam", "Camera"), lifecycle: "destroyed" as const },
    ];
    expect(reconcileTiles(tiles, liveCameras)).toBe(tiles);
  });

  // ---- change #4: repurpose un-rebindable dead tiles -----------------------

  it("repurposes an un-rebindable dead tile to an unshown live camera", () => {
    // Tile points at a dead id whose key has no live match: it can never
    // rebind. A live camera (2) is not shown anywhere, so the dead tile takes
    // it rather than lingering as SIGNAL LOST.
    const tiles = [tile(1, false, "Kerbal X|hull.cam|GoneCam")];
    const liveCameras = [cam(2, "Kerbal X", "hull.cam", "FreshCam")];
    const result = reconcileTiles(tiles, liveCameras);
    expect(result[0].flightId).toBe(2);
    expect(result[0].key).toBe("Kerbal X|hull.cam|FreshCam");
  });

  it("repurposes a keyless dead tile to an unshown live camera", () => {
    const tiles = [tile(99, false, null)];
    const liveCameras = [cam(2, "Kerbal X", "hull.cam", "FreshCam")];
    const result = reconcileTiles(tiles, liveCameras);
    expect(result[0].flightId).toBe(2);
    expect(result[0].key).toBe("Kerbal X|hull.cam|FreshCam");
  });

  it("does not repurpose a dead tile when no live camera is unshown", () => {
    // The only live camera is already displayed by another tile, so the dead
    // tile stays SIGNAL LOST (nothing to repurpose it to).
    const tiles = [
      tile(1, false, "Kerbal X|hull.cam|Live"),
      tile(99, false, "Kerbal X|hull.cam|GoneCam"),
    ];
    const liveCameras = [cam(1, "Kerbal X", "hull.cam", "Live")];
    const result = reconcileTiles(tiles, liveCameras);
    expect(result[0].flightId).toBe(1);
    expect(result[1].flightId).toBe(99);
  });

  it("prefers key rebind over repurpose (revert/recover path intact)", () => {
    // The dead tile CAN rebind by key to the same physical camera (new id 5),
    // so it must take its own camera, not an unrelated unshown live one.
    const tiles = [tile(1, false, "Kerbal X|hull.cam|FwdCam")];
    const liveCameras = [
      cam(5, "Kerbal X", "hull.cam", "FwdCam"),
      cam(6, "Kerbal X", "hull.cam", "OtherCam"),
    ];
    const result = reconcileTiles(tiles, liveCameras);
    expect(result[0].flightId).toBe(5);
  });

  it("leaves surviving live tiles untouched during repurpose", () => {
    const liveTile = tile(1, true, "Kerbal X|hull.cam|Live");
    const deadTile = tile(99, false, "Kerbal X|hull.cam|GoneCam");
    const tiles = [liveTile, deadTile];
    const liveCameras = [
      cam(1, "Kerbal X", "hull.cam", "Live"),
      cam(2, "Kerbal X", "hull.cam", "FreshCam"),
    ];
    const result = reconcileTiles(tiles, liveCameras);
    // Live tile keeps its exact object reference (no feed remount).
    expect(result[0]).toBe(liveTile);
    // Dead tile repurposed to the unshown live camera.
    expect(result[1].flightId).toBe(2);
  });

  it("does not repurpose a dead tile to a destroyed camera", () => {
    const tiles = [tile(99, false, "Kerbal X|hull.cam|GoneCam")];
    const liveCameras = [deadCam(2, "Kerbal X", "hull.cam", "AlsoDead")];
    // No active camera to take, so the tile stays SIGNAL LOST.
    expect(reconcileTiles(tiles, liveCameras)).toBe(tiles);
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

  it("skips Destroyed cameras", () => {
    // Only live cameras (1, 3) get added; the tombstone (2) is skipped.
    const next = addAllCameras([], [cam(1), deadCam(2), cam(3)]);
    expect(next.map((t) => t.flightId)).toEqual([1, 3]);
  });

  it("is idempotent when the only missing camera is Destroyed", () => {
    const tiles = [tile(1)];
    expect(addAllCameras(tiles, [cam(1), deadCam(2)])).toBe(tiles);
  });
});

// ---------------------------------------------------------------------------
// removeAllCameras
// ---------------------------------------------------------------------------

describe("removeAllCameras", () => {
  it("clears the grid", () => {
    expect(removeAllCameras([tile(1), tile(2), tile(null)])).toEqual([]);
  });

  it("returns an empty array for an already-empty grid", () => {
    expect(removeAllCameras([])).toEqual([]);
  });
});

// ---------------------------------------------------------------------------
// removeAllLostCameras
// ---------------------------------------------------------------------------

describe("removeAllLostCameras", () => {
  it("keeps live tiles and empty slots, drops lost tiles", () => {
    const live = tile(1, false, "Ship|part|Live");
    const empty = tile(null);
    const lost = tile(99, false, "Ship|part|Gone");
    const result = removeAllLostCameras([live, empty, lost], [cam(1)]);
    expect(result).toEqual([live, empty]);
  });

  it("treats a Destroyed camera's tile as lost", () => {
    const lost = tile(2, false, "Ship|part|Dead");
    const result = removeAllLostCameras([tile(1), lost], [cam(1), deadCam(2)]);
    expect(result.map((t) => t.flightId)).toEqual([1]);
  });

  it("preserves surviving live tiles by identity (never remounts a feed)", () => {
    const live = tile(1, true, "Ship|part|Live");
    const result = removeAllLostCameras([live, tile(99)], [cam(1)]);
    // Same object reference: React key/props unchanged for the live tile.
    expect(result[0]).toBe(live);
  });

  it("returns all tiles when nothing is lost", () => {
    const tiles = [tile(1), tile(2), tile(null)];
    const result = removeAllLostCameras(tiles, [cam(1), cam(2)]);
    expect(result.map((t) => t.flightId)).toEqual([1, 2, null]);
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
