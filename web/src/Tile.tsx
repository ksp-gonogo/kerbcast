import {
  buildCameraLabeler,
  CameraFeed,
  useKerbcamCameras,
} from "@jonpepler/kerbcam-react";
import { Plus, X } from "lucide-react";
import { useMemo } from "react";
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
  /*
   * Corner label: the camera's name once one is bound, the tile number
   * until then. The feed's own title chrome is hover-revealed, so this
   * is the at-a-glance identifier.
   */
  const cameras = useKerbcamCameras();
  const label = useMemo(() => {
    const cam = cameras.find((c) => c.flightId === flightId);
    return cam ? buildCameraLabeler(cameras)(cam) : `Tile ${index + 1}`;
  }, [cameras, flightId, index]);
  return (
    <TileRoot>
      <FeedWrap>
        <CameraFeed
          flightId={flightId}
          showDebugInfo={showDebugInfo}
          onSelectCamera={onSelectCamera}
        />
      </FeedWrap>
      <TileCornerLabel>{label}</TileCornerLabel>
      <RemoveButton
        type="button"
        aria-label={`Remove tile ${index + 1}`}
        onClick={onRemove}
        title={`Remove tile ${index + 1}`}
      >
        <X size={11} strokeWidth={1.75} aria-hidden="true" />
      </RemoveButton>
    </TileRoot>
  );
}

interface AddTileProps {
  onClick: () => void;
  isEmpty?: boolean;
}

export function AddTile({ onClick, isEmpty }: AddTileProps): React.JSX.Element {
  return (
    <AddRoot
      type="button"
      aria-label="Add tile"
      onClick={onClick}
      $isEmpty={isEmpty ?? false}
    >
      <AddIcon aria-hidden="true">
        <Plus size={20} strokeWidth={1.5} />
      </AddIcon>
      {isEmpty && <AddLabel>Add camera</AddLabel>}
    </AddRoot>
  );
}

// ---------------------------------------------------------------------------
// Styled
// ---------------------------------------------------------------------------

const TileRoot = styled.div`
  position: relative;
  display: flex;
  flex-direction: column;
  background: var(--kc-surface);
  border: 1px solid var(--kc-border);
  border-radius: var(--kc-tile-radius);
  overflow: hidden;
  aspect-ratio: 16 / 9;

  /* Subtle inner shadow to give depth without distraction */
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.18), 0 0 0 0.5px rgba(0, 0, 0, 0.08);
`;

/* Label: bottom-left corner, barely visible; fades out on hover because the
   feed's own title chrome appears and would duplicate it */
const TileCornerLabel = styled.span`
  position: absolute;
  bottom: 0.4rem;
  left: 0.5rem;
  font-size: 0.6rem;
  letter-spacing: 0.06em;
  color: rgba(255, 255, 255, 0.35);
  pointer-events: none;
  user-select: none;
  text-shadow: 0 1px 3px rgba(0,0,0,0.6);
  z-index: 2;
  transition: opacity 0.15s ease;

  ${TileRoot}:hover & {
    opacity: 0;
  }

  @media (prefers-reduced-motion: reduce) {
    transition: none;
  }
`;

const RemoveButton = styled.button`
  position: absolute;
  top: 0.4rem;
  right: 0.4rem;
  z-index: 3;
  width: 22px;
  height: 22px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: rgba(0, 0, 0, 0.45);
  border: 1px solid rgba(255, 255, 255, 0.15);
  border-radius: 4px;
  cursor: pointer;
  color: rgba(255, 255, 255, 0.6);
  padding: 0;
  opacity: 0;
  transition: opacity 0.15s ease, color 0.15s ease, background 0.15s ease;
  backdrop-filter: blur(4px);

  ${TileRoot}:hover & {
    opacity: 1;
  }

  &:hover {
    color: #fff;
    background: var(--kc-danger-bg);
    border-color: rgba(255, 255, 255, 0.25);
  }

  &:focus-visible {
    opacity: 1;
    outline: 2px solid var(--kc-accent);
    outline-offset: 2px;
  }

  @media (prefers-reduced-motion: reduce) {
    opacity: 1;
  }
`;

const FeedWrap = styled.div`
  flex: 1;
  min-height: 0;
  position: relative;

  /* Let CameraFeed Stage fill the tile */
  & > div {
    height: 100%;
    min-height: 0;
  }

  /* The feed's Next/Previous step buttons land exactly where the tile's
     remove button sits, and the title menu already covers camera selection
     on this page, so suppress them here. */
  & button[aria-label="Next camera"],
  & button[aria-label="Previous camera"] {
    display: none;
  }
`;

interface AddRootProps {
  $isEmpty: boolean;
}

const AddRoot = styled.button<AddRootProps>`
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.5rem;
  background: none;
  border: 1.5px dashed var(--kc-border);
  border-radius: var(--kc-tile-radius);
  aspect-ratio: 16 / 9;
  cursor: pointer;
  color: var(--kc-text-muted);
  font-family: inherit;
  transition: border-color 0.15s ease, color 0.15s ease, background 0.15s ease;

  ${(p) => p.$isEmpty && `
    /* When it's the only element, don't force 16:9; be more of a CTA bar */
    aspect-ratio: unset;
    min-height: 80px;
    max-height: 100px;
  `}

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

const AddIcon = styled.span`
  opacity: 0.6;
  line-height: 0;

  ${AddRoot}:hover & {
    opacity: 1;
  }
`;

const AddLabel = styled.span`
  font-size: 0.72rem;
  letter-spacing: 0.05em;
  opacity: 0.7;

  ${AddRoot}:hover & {
    opacity: 1;
  }
`;
