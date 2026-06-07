import { useKerbcamCameras } from "@jonpepler/kerbcam-react";
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
      {tiles.length < MAX_TILES && <AddTile onClick={handleAdd} />}
    </Root>
  );
}

const Root = styled.div`
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(360px, 1fr));
  gap: 0.75rem;
  padding: 0.75rem;
`;
