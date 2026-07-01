/**
 * Tile grid persistence via localStorage.
 *
 * A tile is a slot in the camera grid. Each tile holds the flightId of the
 * camera it shows (or null for an unpointed slot) and whether it is
 * "spotlit" -- pinned into the enlarged spotlight stage (see Grid).
 *
 * The `key` field is the stable identity of the camera bound to this tile:
 * a pipe-separated string of vesselName, partName, and cameraName that
 * survives KSP revert/recover (which reassigns part.flightID). Old stored
 * tiles without a key load with key: null and get reconciled on first camera
 * arrival.
 *
 * The localStorage key is intentionally minimal -- one per page origin.
 * The key distinguishes "absent" (never set) from "empty array" (user removed
 * all tiles). Seeding from cameras only happens on the first visit (key absent).
 */

import type { CameraState } from "@jonpepler/kerbcast";

export interface Tile {
  flightId: number | null;
  spotlit: boolean;
  /**
   * Stable identity of the bound camera (survives KSP revert/recover).
   * Absent on tiles created before this field was added; treated as null.
   */
  key?: string | null;
}

const STORAGE_KEY = "kerbcast:tiles";

/** Read tiles from localStorage. Returns null when the key is absent. */
export function loadTiles(): Tile[] | null {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw === null) return null;
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) return null;
    return (parsed as unknown[]).map((item) => ({
      flightId:
        typeof (item as { flightId?: unknown }).flightId === "number"
          ? (item as { flightId: number }).flightId
          : null,
      // Older stored tiles predate spotlight; default to not spotlit.
      spotlit: (item as { spotlit?: unknown }).spotlit === true,
      // Older stored tiles predate stable key; default to null (reconciled later).
      key:
        typeof (item as { key?: unknown }).key === "string"
          ? (item as { key: string }).key
          : null,
    }));
  } catch {
    return null;
  }
}

/** Persist tiles to localStorage. */
export function saveTiles(tiles: Tile[]): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(tiles));
  } catch {
    // ignore (private browsing / storage full)
  }
}

/**
 * Derive a stable camera identity string that survives KSP revert/recover.
 * KSP reassigns part.flightID on revert, but vesselName + partName + cameraName
 * stay constant for the same physical camera. Returns an empty string for
 * cameras where all three fields are absent (degenerate case; won't match
 * anything useful).
 */
export function cameraKey(cam: Pick<CameraState, "vesselName" | "partName" | "cameraName">): string {
  return `${cam.vesselName ?? ""}|${cam.partName ?? ""}|${cam.cameraName ?? ""}`;
}

/**
 * Seed default tiles from the discovered camera list.
 * Only called when no tiles are stored yet (key absent).
 * Caps at 4 cameras.
 */
export function seedTiles(cameras: Pick<CameraState, "flightId" | "vesselName" | "partName" | "cameraName">[]): Tile[] {
  return cameras.slice(0, 4).map((cam) => ({
    flightId: cam.flightId,
    spotlit: false,
    key: cameraKey(cam),
  }));
}

/**
 * Reconcile tiles after a KSP revert/recover that reassigns flightIds.
 *
 * After a revert, the sidecar publishes a fresh camera-snapshot where the same
 * physical cameras appear under new flightIds. Tiles still point at the old
 * (now-dead) flightIds and would show the "camera reconnecting" placeholder.
 *
 * This function rebinds each such tile by matching its stored `key` (stable
 * identity: vesselName|partName|cameraName) against the new live camera list.
 * A tile is only rebound if:
 *   - its flightId is no longer in the live set, AND
 *   - its key matches exactly one live camera.
 *
 * If two tiles share the same key (e.g. duplicate camera names on the same
 * vessel), only the first is rebound; the second is left as "reconnecting" to
 * avoid pointing two tiles at the same camera silently.
 *
 * Returns the same array reference if nothing changed (safe for React state
 * comparison).
 */
export function reconcileTiles(
  tiles: Tile[],
  liveCameras: Pick<CameraState, "flightId" | "vesselName" | "partName" | "cameraName">[],
): Tile[] {
  const liveIds = new Set(liveCameras.map((c) => c.flightId));

  // Build a key -> live camera map (first match wins for duplicate keys).
  const keyToLive = new Map<string, Pick<CameraState, "flightId" | "vesselName" | "partName" | "cameraName">>();
  for (const cam of liveCameras) {
    const k = cameraKey(cam);
    if (!keyToLive.has(k)) keyToLive.set(k, cam);
  }

  // Track which live flightIds have already been claimed by a tile rebind in
  // this pass, so two stale tiles with the same key don't both rebind to the
  // same new camera.
  const claimedIds = new Set<number>();

  let changed = false;
  const next = tiles.map((tile) => {
    // Already live, or an empty slot: leave untouched.
    if (tile.flightId === null || liveIds.has(tile.flightId)) return tile;
    // No stable key stored: can't rebind; leave as "reconnecting".
    if (tile.key == null) return tile;

    const match = keyToLive.get(tile.key);
    if (match === undefined) return tile;
    if (claimedIds.has(match.flightId)) return tile;

    claimedIds.add(match.flightId);
    changed = true;
    return { ...tile, flightId: match.flightId };
  });

  return changed ? next : tiles;
}

/**
 * Add a new empty tile. There is no upper bound on tile count: the plugin and
 * sidecar adapt to load (capture staggering, frame budget), and the grid
 * layout flows into more rows as tiles are added.
 */
export function addTile(tiles: Tile[]): Tile[] {
  return [...tiles, { flightId: null, spotlit: false, key: null }];
}

/** Remove a tile by index. */
export function removeTile(tiles: Tile[], index: number): Tile[] {
  return tiles.filter((_, i) => i !== index);
}

/** Update the flightId of a tile at an index (preserves spotlight state and key). */
export function updateTile(
  tiles: Tile[],
  index: number,
  flightId: number | null,
  key?: string | null,
): Tile[] {
  return tiles.map((t, i) =>
    i === index
      ? { ...t, flightId, key: key !== undefined ? key : t.key }
      : t,
  );
}

/** Toggle whether the tile at an index is spotlit. */
export function toggleSpotlight(tiles: Tile[], index: number): Tile[] {
  return tiles.map((t, i) => (i === index ? { ...t, spotlit: !t.spotlit } : t));
}

/**
 * Point every camera not already shown at a tile: unpointed slots are filled
 * first, then new tiles are appended. Cameras already in the grid are left
 * alone, so the action is idempotent (returns the same array when nothing is
 * missing).
 */
export function addAllCameras(
  tiles: Tile[],
  cameras: Pick<CameraState, "flightId" | "vesselName" | "partName" | "cameraName">[],
): Tile[] {
  const present = new Set<number>();
  for (const t of tiles) {
    if (t.flightId !== null) present.add(t.flightId);
  }
  const missing = cameras.filter((c) => !present.has(c.flightId));
  if (missing.length === 0) return tiles;

  const queue = [...missing];
  const next = tiles.map((t) => {
    if (t.flightId !== null) return t;
    const cam = queue.shift();
    return cam === undefined ? t : { ...t, flightId: cam.flightId, key: cameraKey(cam) };
  });
  for (const cam of queue) next.push({ flightId: cam.flightId, spotlit: false, key: cameraKey(cam) });
  return next;
}

/**
 * One-time performance note shown when "add all cameras" lands the grid above
 * PERF_NOTE_TILE_THRESHOLD tiles. Dismissal is remembered per origin.
 */
export const PERF_NOTE_TILE_THRESHOLD = 8;

const PERF_NOTE_KEY = "kerbcast:perfNoteDismissed";

export function loadPerfNoteDismissed(): boolean {
  try {
    return localStorage.getItem(PERF_NOTE_KEY) === "true";
  } catch {
    return false;
  }
}

export function savePerfNoteDismissed(): void {
  try {
    localStorage.setItem(PERF_NOTE_KEY, "true");
  } catch {
    // ignore (private browsing / storage full)
  }
}
