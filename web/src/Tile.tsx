import { CameraFeed } from "@jonpepler/kerbcam-react";
import styled from "styled-components";

interface TileProps {
  flightId: number | null;
  index: number;
  showDebugInfo: boolean;
  onSelectCamera: (flightId: number) => void;
  onRemove: () => void;
}

export function Tile({
  flightId,
  index,
  showDebugInfo,
  onSelectCamera,
  onRemove,
}: TileProps): React.JSX.Element {
  return (
    <TileRoot>
      <TileHeader>
        <TileLabel>Tile {index + 1}</TileLabel>
        <RemoveButton
          type="button"
          aria-label={`Remove tile ${index + 1}`}
          onClick={onRemove}
        >
          x
        </RemoveButton>
      </TileHeader>
      <FeedWrap>
        <CameraFeed
          flightId={flightId}
          showDebugInfo={showDebugInfo}
          onSelectCamera={onSelectCamera}
        />
      </FeedWrap>
    </TileRoot>
  );
}

export function AddTile({ onClick }: { onClick: () => void }): React.JSX.Element {
  return (
    <AddRoot
      type="button"
      aria-label="Add tile"
      onClick={onClick}
    >
      +
    </AddRoot>
  );
}

const TileRoot = styled.div`
  display: flex;
  flex-direction: column;
  background: var(--kc-surface);
  border: 1px solid var(--kc-border);
  border-radius: 6px;
  overflow: hidden;
  min-height: 200px;
`;

const TileHeader = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.25rem 0.5rem;
  background: var(--kc-surface-raised);
  border-bottom: 1px solid var(--kc-border);
`;

const TileLabel = styled.span`
  font-size: 0.75rem;
  color: var(--kc-text-muted);
`;

const RemoveButton = styled.button`
  background: none;
  border: none;
  cursor: pointer;
  color: var(--kc-text-muted);
  font-size: 0.85rem;
  padding: 0 0.25rem;
  line-height: 1;

  &:hover {
    color: var(--kc-danger);
  }

  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 2px;
  }
`;

const FeedWrap = styled.div`
  flex: 1;
  min-height: 0;
  position: relative;

  /* Let CameraFeed Stage fill the tile */
  & > div {
    height: 100%;
    min-height: 200px;
  }
`;

const AddRoot = styled.button`
  display: flex;
  align-items: center;
  justify-content: center;
  background: none;
  border: 2px dashed var(--kc-border);
  border-radius: 6px;
  min-height: 200px;
  cursor: pointer;
  color: var(--kc-text-muted);
  font-size: 2rem;
  font-family: inherit;

  &:hover {
    border-color: var(--kc-accent);
    color: var(--kc-accent);
    background: var(--kc-accent-wash);
  }

  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 2px;
  }
`;
