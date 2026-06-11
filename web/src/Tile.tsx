import {
  buildCameraLabeler,
  CameraFeed,
  useKerbcamCameras,
} from "@jonpepler/kerbcam-react";
import type { FeedAction } from "@jonpepler/kerbcam-react";
import { ListPlus, Pin, PinOff, Plus, X } from "lucide-react";
import { useMemo, useState } from "react";
import styled from "styled-components";

/**
 * How the tile is sized by its container:
 *  - "grid": fills its grid column at 16:9 (the default flat grid).
 *  - "cell": fills whatever grid cell it's placed in (spotlight mode). The
 *    feed letterboxes inside via object-fit, so the cell need not be 16:9.
 */
export type TileVariant = "grid" | "cell";

interface TileProps {
  flightId: number | null;
  index: number;
  showDebugInfo: boolean;
  spotlit: boolean;
  variant?: TileVariant;
  /** True when any tile is spotlit (drives spotlight grid placement). */
  spotlightActive?: boolean;
  onSelectCamera: (flightId: number) => void;
  onRemove: () => void;
  onToggleSpotlight: () => void;
}

export function Tile({
  flightId,
  index,
  showDebugInfo,
  spotlit,
  variant = "grid",
  spotlightActive = false,
  onSelectCamera,
  onRemove,
  onToggleSpotlight,
}: TileProps): React.JSX.Element {
  /*
   * Corner label: the name of the camera the feed actually displays — which
   * follows CameraFeed's auto-latch/fallback, not just this tile's requested
   * flightId. Tracking the requested id alone mislabels a slot that is showing
   * a borrowed/auto-picked camera (it would read "Tile N" over a live feed).
   * Falls back to the tile number only when the feed shows nothing. The feed's
   * own title chrome is hover-revealed, so this is the at-a-glance identifier.
   */
  const cameras = useKerbcamCameras();
  const [displayedFlightId, setDisplayedFlightId] = useState<number | null>(
    flightId,
  );
  const label = useMemo(() => {
    const cam = cameras.find((c) => c.flightId === displayedFlightId);
    return cam ? buildCameraLabeler(cameras)(cam) : `Tile ${index + 1}`;
  }, [cameras, displayedFlightId, index]);

  /*
   * Tile-level controls injected into the feed's action bar, so there's a
   * single tidy cluster rather than a separate floating close button that
   * collides with it. Spotlight only makes sense once a camera is bound.
   */
  const actions = useMemo<FeedAction[]>(() => {
    if (flightId === null) return [];
    return [
      {
        id: "spotlight",
        label: spotlit ? "Remove from spotlight" : "Spotlight this feed",
        active: spotlit,
        icon: spotlit ? <PinOff size={14} /> : <Pin size={14} />,
        onClick: onToggleSpotlight,
      },
    ];
  }, [flightId, spotlit, onToggleSpotlight]);

  // Remove sits at the far corner (after the built-in fullscreen/PiP controls).
  const trailingActions = useMemo<FeedAction[]>(
    () => [
      {
        id: "remove",
        label: `Remove tile ${index + 1}`,
        icon: <X size={14} />,
        onClick: onRemove,
      },
    ],
    [index, onRemove],
  );

  return (
    <TileRoot $variant={variant} $spotlit={spotlit} $spotlightActive={spotlightActive}>
      <FeedWrap>
        <CameraFeed
          flightId={flightId}
          showDebugInfo={showDebugInfo}
          onSelectCamera={onSelectCamera}
          onDisplayedCameraChange={setDisplayedFlightId}
          enableFullscreen
          enablePictureInPicture
          enableQualityControl
          actions={actions}
          trailingActions={trailingActions}
        />
      </FeedWrap>
      <TileCornerLabel>{label}</TileCornerLabel>
    </TileRoot>
  );
}

interface AddTileProps {
  onClick: () => void;
  isEmpty?: boolean;
  /** Spotlight mode: render as a small 1x1 grid cell rather than a 16:9 tile. */
  compact?: boolean;
  /** Flat fill mode: a slim full-width bar below the feeds. */
  bar?: boolean;
}

export function AddTile({ onClick, isEmpty, compact, bar }: AddTileProps): React.JSX.Element {
  return (
    <AddRoot
      type="button"
      aria-label="Add tile"
      onClick={onClick}
      $isEmpty={isEmpty ?? false}
      $compact={compact ?? false}
      $bar={bar ?? false}
    >
      <AddIcon aria-hidden="true">
        <Plus size={20} strokeWidth={1.5} />
      </AddIcon>
      {(isEmpty || bar) && <AddLabel>Add camera</AddLabel>}
    </AddRoot>
  );
}

/**
 * "Add all cameras" sibling of AddTile: one click points a tile at every
 * camera the grid is not already showing. Same look and sizing variants as
 * AddTile so the two read as one control cluster.
 */
export function AddAllTile({ onClick, isEmpty, compact, bar }: AddTileProps): React.JSX.Element {
  return (
    <AddRoot
      type="button"
      aria-label="Add all cameras"
      title="Add all cameras"
      onClick={onClick}
      $isEmpty={isEmpty ?? false}
      $compact={compact ?? false}
      $bar={bar ?? false}
    >
      <AddIcon aria-hidden="true">
        <ListPlus size={20} strokeWidth={1.5} />
      </AddIcon>
      {(isEmpty || bar) && <AddLabel>Add all cameras</AddLabel>}
    </AddRoot>
  );
}

// ---------------------------------------------------------------------------
// Styled
// ---------------------------------------------------------------------------

interface TileRootProps {
  $variant: TileVariant;
  $spotlit: boolean;
  $spotlightActive: boolean;
}

const TileRoot = styled.div<TileRootProps>`
  position: relative;
  display: flex;
  flex-direction: column;
  background: var(--kc-surface);
  border: 1px solid var(--kc-border);
  border-radius: var(--kc-tile-radius);
  overflow: hidden;

  /* Subtle inner shadow to give depth without distraction */
  box-shadow: 0 1px 3px rgba(0, 0, 0, 0.18), 0 0 0 0.5px rgba(0, 0, 0, 0.08);

  ${(p) =>
    p.$variant === "grid"
      ? `
    /* --tile-w is set by Grid in fill mode (the grid is sized to the
       viewport); without it the tile just fills its column. aspect-ratio
       derives the height either way, and the max-* guards keep a stale
       measurement from overflowing. */
    width: var(--tile-w, 100%);
    max-width: 100%;
    max-height: 100%;
    aspect-ratio: 16 / 9;
  `
      : `
    /* Fill the grid cell; the feed letterboxes inside (object-fit: contain),
       so a non-16:9 cell just adds bars rather than stretching the video. */
    width: 100%;
    height: 100%;
    min-height: 0;
  `}

  ${(p) =>
    p.$spotlightActive &&
    (p.$spotlit
      ? `
    /* Spotlit feeds enlarge and sort ahead of the rest. */
    grid-column: span 2;
    grid-row: span 2;
    order: 0;
  `
      : `order: 1;`)}

  ${(p) =>
    p.$spotlit &&
    `
    border-color: var(--kc-accent);
    box-shadow: 0 0 0 1px var(--kc-accent), 0 2px 8px rgba(0, 0, 0, 0.3);
  `}
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
     action cluster sits, and the title menu already covers camera selection
     on this page, so suppress them here. */
  & button[aria-label="Next camera"],
  & button[aria-label="Previous camera"] {
    display: none;
  }
`;

interface AddRootProps {
  $isEmpty: boolean;
  $compact: boolean;
  $bar: boolean;
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

  ${(p) => p.$compact && `
    /* Spotlight mode: fill a 1x1 grid cell, sorted after the feeds. */
    aspect-ratio: unset;
    width: 100%;
    height: 100%;
    min-height: 0;
    order: 1;
  `}

  ${(p) => p.$bar && `
    /* Flat fill mode: a slim bar below the feeds. grid-column applies when
       the button is a grid item; flex applies when it shares the add-controls
       row with a sibling button. */
    grid-column: 1 / -1;
    flex: 1;
    flex-direction: row;
    aspect-ratio: unset;
    min-height: 0;
    height: 52px;
    gap: 0.4rem;
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
