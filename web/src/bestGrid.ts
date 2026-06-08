/**
 * Choose the grid arrangement (cols × rows) that fits `n` fixed-aspect tiles
 * into a `width`×`height` area while maximizing the size of each tile.
 *
 * Used by the flat grid to grow tiles to fill the viewport: a wide-but-short
 * area packs tiles into more columns; a tall area uses more rows. The caller
 * then sizes each tile to the largest 16:9 box that fits a cell and centers
 * the block, so tiles are never stretched — this only decides the split.
 */
export function bestGrid(
  n: number,
  width: number,
  height: number,
  aspect = 16 / 9,
): { cols: number; rows: number } {
  if (n <= 1) return { cols: 1, rows: 1 };
  // Unmeasured (e.g. first paint before ResizeObserver fires): single row.
  if (width <= 0 || height <= 0) return { cols: n, rows: 1 };

  let best = { cols: 1, rows: n, area: -1 };
  for (let cols = 1; cols <= n; cols++) {
    const rows = Math.ceil(n / cols);
    const cellW = width / cols;
    const cellH = height / rows;
    // Largest box of the target aspect that fits inside the cell.
    let tileW = cellW;
    let tileH = cellW / aspect;
    if (tileH > cellH) {
      tileH = cellH;
      tileW = cellH * aspect;
    }
    const area = tileW * tileH;
    if (area > best.area) best = { cols, rows, area };
  }
  return { cols: best.cols, rows: best.rows };
}
