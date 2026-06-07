/**
 * Tile grid persistence via localStorage.
 *
 * A tile is a slot in the camera grid. Each tile holds the flightId of the
 * camera it shows (or null for an unpointed slot).
 *
 * The localStorage key is intentionally minimal -- one per page origin.
 * The key distinguishes "absent" (never set) from "empty array" (user removed
 * all tiles). Seeding from cameras only happens on the first visit (key absent).
 */

export interface Tile {
  flightId: number | null;
}

const STORAGE_KEY = "kerbcam:tiles";
const MAX_TILES = 8;

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
  return flightIds.slice(0, 4).map((flightId) => ({ flightId }));
}

/** Add a new empty tile (returns tiles unchanged if already at max). */
export function addTile(tiles: Tile[]): Tile[] {
  if (tiles.length >= MAX_TILES) return tiles;
  return [...tiles, { flightId: null }];
}

/** Remove a tile by index. */
export function removeTile(tiles: Tile[], index: number): Tile[] {
  return tiles.filter((_, i) => i !== index);
}

/** Update the flightId of a tile at an index. */
export function updateTile(
  tiles: Tile[],
  index: number,
  flightId: number | null,
): Tile[] {
  return tiles.map((t, i) => (i === index ? { flightId } : t));
}

export { MAX_TILES };
