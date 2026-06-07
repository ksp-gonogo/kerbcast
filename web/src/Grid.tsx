import { useKerbcamCameras } from "@jonpepler/kerbcam-react";
import { Video } from "lucide-react";
import styled from "styled-components";
import { AddTile, Tile } from "./Tile";
import { addTile, MAX_TILES, removeTile, saveTiles, updateTile } from "./tiles";
import type { Tile as TileData } from "./tiles";

interface GridProps {
  tiles: TileData[];
  onTilesChange: (tiles: TileData[]) => void;
  showDebugInfo: boolean;
}

export function Grid({ tiles, onTilesChange, showDebugInfo }: GridProps): React.JSX.Element {
  // cameras available for use by Tile select dropdowns
  useKerbcamCameras();

  const handleSelectCamera = (index: number, flightId: number) => {
    const next = updateTile(tiles, index, flightId);
    saveTiles(next);
    onTilesChange(next);
  };

  const handleRemove = (index: number) => {
    const next = removeTile(tiles, index);
    saveTiles(next);
    onTilesChange(next);
  };

  const handleAdd = () => {
    const next = addTile(tiles);
    if (next !== tiles) {
      saveTiles(next);
      onTilesChange(next);
    }
  };

  const isEmpty = tiles.length === 0;

  return (
    <Root>
      {tiles.map((tile, i) => (
        <Tile
          key={i}
          index={i}
          flightId={tile.flightId}
          showDebugInfo={showDebugInfo}
          onSelectCamera={(fid) => handleSelectCamera(i, fid)}
          onRemove={() => handleRemove(i)}
        />
      ))}
      {tiles.length < MAX_TILES && <AddTile onClick={handleAdd} isEmpty={isEmpty} />}
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

const Root = styled.div`
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(360px, 1fr));
  gap: 1rem;
  padding: 1rem;
  flex: 1;
  align-content: start;
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
