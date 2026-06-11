/**
 * Tile grid persistence via localStorage.
 *
 * A tile is a slot in the camera grid. Each tile holds the flightId of the
 * camera it shows (or null for an unpointed slot) and whether it is
 * "spotlit" -- pinned into the enlarged spotlight stage (see Grid).
 *
 * The localStorage key is intentionally minimal -- one per page origin.
 * The key distinguishes "absent" (never set) from "empty array" (user removed
 * all tiles). Seeding from cameras only happens on the first visit (key absent).
 */

export interface Tile {
  flightId: number | null;
  spotlit: boolean;
}

const STORAGE_KEY = "kerbcam:tiles";

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
 * Seed default tiles from the discovered camera list.
 * Only called when no tiles are stored yet (key absent).
 * Caps at 4 cameras.
 */
export function seedTiles(flightIds: number[]): Tile[] {
  return flightIds.slice(0, 4).map((flightId) => ({ flightId, spotlit: false }));
}

/**
 * Add a new empty tile. There is no upper bound on tile count: the plugin and
 * sidecar adapt to load (capture staggering, frame budget), and the grid
 * layout flows into more rows as tiles are added.
 */
export function addTile(tiles: Tile[]): Tile[] {
  return [...tiles, { flightId: null, spotlit: false }];
}

/** Remove a tile by index. */
export function removeTile(tiles: Tile[], index: number): Tile[] {
  return tiles.filter((_, i) => i !== index);
}

/** Update the flightId of a tile at an index (preserves spotlight state). */
export function updateTile(
  tiles: Tile[],
  index: number,
  flightId: number | null,
): Tile[] {
  return tiles.map((t, i) => (i === index ? { ...t, flightId } : t));
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
export function addAllCameras(tiles: Tile[], flightIds: number[]): Tile[] {
  const present = new Set<number>();
  for (const t of tiles) {
    if (t.flightId !== null) present.add(t.flightId);
  }
  const missing = flightIds.filter((id) => !present.has(id));
  if (missing.length === 0) return tiles;

  const queue = [...missing];
  const next = tiles.map((t) => {
    if (t.flightId !== null) return t;
    const id = queue.shift();
    return id === undefined ? t : { ...t, flightId: id };
  });
  for (const id of queue) next.push({ flightId: id, spotlit: false });
  return next;
}

/**
 * One-time performance note shown when "add all cameras" lands the grid above
 * PERF_NOTE_TILE_THRESHOLD tiles. Dismissal is remembered per origin.
 */
export const PERF_NOTE_TILE_THRESHOLD = 8;

const PERF_NOTE_KEY = "kerbcam:perfNoteDismissed";

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
