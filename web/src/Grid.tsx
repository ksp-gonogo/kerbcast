import { useKerbcamCameras } from "@jonpepler/kerbcam-react";
import { Video } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import styled from "styled-components";
import { bestGrid } from "./bestGrid";
import { AddTile, Tile } from "./Tile";
import { addTile, MAX_TILES, removeTile, saveTiles, toggleSpotlight, updateTile } from "./tiles";
import type { Tile as TileData } from "./tiles";

interface GridProps {
  tiles: TileData[];
  onTilesChange: (tiles: TileData[]) => void;
  showDebugInfo: boolean;
}

const GAP = 16; // 1rem; keep in sync with Root `gap`

export function Grid({ tiles, onTilesChange, showDebugInfo }: GridProps): React.JSX.Element {
  // cameras available for use by Tile select dropdowns
  useKerbcamCameras();

  const commit = (next: TileData[]) => {
    saveTiles(next);
    onTilesChange(next);
  };

  const handleSelectCamera = (index: number, flightId: number) =>
    commit(updateTile(tiles, index, flightId));
  const handleRemove = (index: number) => commit(removeTile(tiles, index));
  const handleToggleSpotlight = (index: number) =>
    commit(toggleSpotlight(tiles, index));
  const handleAdd = () => {
    const next = addTile(tiles);
    if (next !== tiles) commit(next);
  };

  const isEmpty = tiles.length === 0;
  const spotlightActive = tiles.some((t) => t.spotlit);
  const canAdd = tiles.length < MAX_TILES;

  // Measure the container so the flat grid can grow tiles to fill it.
  const rootRef = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  useEffect(() => {
    const el = rootRef.current;
    if (!el) return;
    const obs = new ResizeObserver((entries) => {
      const r = entries[0]?.contentRect;
      if (r) setSize({ w: r.width, h: r.height });
    });
    obs.observe(el);
    return () => obs.disconnect();
  }, []);

  /*
   * Flat grid: size the FEEDS to fill the viewport. Pick the column/row split
   * that makes the feeds the largest 16:9 they can be for the measured area
   * (minus a slim strip reserved for the add bar), hand them an explicit width
   * via --tile-w, and center the block. The add control is a slim full-width
   * bar rather than a feed-sized cell, so it doesn't shrink the feeds or leave
   * a big empty cell. When spotlit, layout switches to the span grid instead.
   */
  const ADD_BAR_H = 52;
  const flatFill = !spotlightActive && !isEmpty && size.w > 0 && size.h > 0;
  let rootStyle: React.CSSProperties | undefined;
  if (flatFill) {
    // contentRect already excludes Root's padding, so size is the usable area.
    const availW = size.w;
    const availH = size.h - (canAdd ? ADD_BAR_H + GAP : 0);
    const { cols, rows } = bestGrid(tiles.length, availW, availH);
    const cellW = (availW - GAP * (cols - 1)) / cols;
    const cellH = (availH - GAP * (rows - 1)) / rows;
    const tileW = Math.max(0, Math.min(cellW, (cellH * 16) / 9));
    rootStyle = {
      gridTemplateColumns: `repeat(${cols}, auto)`,
      justifyContent: "center",
      alignContent: "center",
      ["--tile-w" as string]: `${Math.floor(tileW)}px`,
    };
  }

  return (
    <Root ref={rootRef} $spotlight={spotlightActive} $fill={flatFill} style={rootStyle}>
      {tiles.map((tile, i) => (
        <Tile
          key={i}
          index={i}
          flightId={tile.flightId}
          spotlit={tile.spotlit}
          variant={spotlightActive ? "cell" : "grid"}
          spotlightActive={spotlightActive}
          showDebugInfo={showDebugInfo}
          onSelectCamera={(fid) => handleSelectCamera(i, fid)}
          onRemove={() => handleRemove(i)}
          onToggleSpotlight={() => handleToggleSpotlight(i)}
        />
      ))}
      {canAdd && (
        <AddTile
          onClick={handleAdd}
          isEmpty={isEmpty}
          compact={spotlightActive}
          bar={flatFill}
        />
      )}
      {isEmpty && (
        <EmptyHint>
          <EmptyIcon aria-hidden="true">
            <Video size={32} strokeWidth={1.5} />
          </EmptyIcon>
          <EmptyTitle>No cameras active</EmptyTitle>
          <EmptyBody>
            Connect kerbcam to a running KSP session, then add a tile to watch a camera feed.
          </EmptyBody>
        </EmptyHint>
      )}
    </Root>
  );
}

// ---------------------------------------------------------------------------
// Styled
// ---------------------------------------------------------------------------

const Root = styled.div<{ $spotlight: boolean; $fill: boolean }>`
  display: grid;
  gap: 1rem;
  padding: 1rem;
  flex: 1;
  min-height: 0;

  ${(p) =>
    p.$spotlight
      ? `
    /* Spotlight: a fixed 4-column grid that fills the height. Spotlit tiles
       span 2x2 (sorted first); everything else stays 1x1 and dense-packs into
       the remaining cells. Row tracks share the height equally. */
    grid-template-columns: repeat(4, 1fr);
    grid-auto-rows: 1fr;
    grid-auto-flow: dense;
  `
      : p.$fill
        ? `
    /* Flat grid, grown to fill: columns/centering come from inline style;
       tiles take their width from --tile-w. */
    `
        : `
    grid-template-columns: repeat(auto-fit, minmax(360px, 1fr));
    align-content: start;
  `}
`;

const EmptyHint = styled.div`
  grid-column: 1 / -1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.6rem;
  padding: 3rem 1rem;
  color: var(--kc-text-muted);
  text-align: center;
`;

const EmptyIcon = styled.div`
  opacity: 0.4;
  margin-bottom: 0.25rem;
`;

const EmptyTitle = styled.p`
  margin: 0;
  font-size: 0.85rem;
  font-weight: 600;
  letter-spacing: 0.04em;
  color: var(--kc-text-muted);
`;

const EmptyBody = styled.p`
  margin: 0;
  font-size: 0.75rem;
  opacity: 0.7;
  max-width: 28ch;
  line-height: 1.5;
`;
