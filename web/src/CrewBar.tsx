/**
 * CrewBar — the crew face-camera surface (shown when crew are NOT merged into
 * the regular camera list).
 *
 * Camera-driven: renders every OPEN `kind === "kerbal"` camera as a square
 * KerbalFaceFeed (keyed by flightId), with a name label, an IVA/EVA badge from
 * crewLocation, and a SIGNAL LOST treatment for a destroyed kerbal.
 *
 * Open/close mirrors the part cameras' add/remove: each face has a close (x);
 * closed crew move into an "Add crew" menu in the header and can be reopened.
 * The open/closed set is owned + persisted by the App (like the tile grid), so
 * it survives reload; default (nothing stored) is all-open.
 *
 * Placement (row / column / wrap) and minimise are CSS-only reflows of the SAME
 * mounted feeds — switching either never remounts a KerbalFaceFeed.
 */

import { CrewLocation } from "@ksp-gonogo/kerbcast";
import type { CameraState } from "@ksp-gonogo/kerbcast";
import { KerbalFaceFeed, buildCameraLabeler, isCameraDestroyed, useKerbcastCameras } from "@ksp-gonogo/kerbcast-react";
import type { FeedAction } from "@ksp-gonogo/kerbcast-react";
import { PanelBottomClose, PanelBottomOpen, Plus, X } from "lucide-react";
import { useMemo, useRef } from "react";
import styled from "styled-components";
import type { CrewBarPlacement } from "./settings";

interface CrewBarProps {
  placement: CrewBarPlacement;
  /** Collapsed out of the way (feeds stay mounted). Transient. */
  minimised: boolean;
  onToggleMinimise: () => void;
  /** Wire-ids of closed (hidden) crew faces. */
  closed: ReadonlySet<number>;
  onClose: (flightId: number) => void;
  onOpen: (flightId: number) => void;
}

export function CrewBar({
  placement,
  minimised,
  onToggleMinimise,
  closed,
  onClose,
  onOpen,
}: CrewBarProps): React.JSX.Element | null {
  const cameras = useKerbcastCameras();
  const kerbals = useMemo(
    () => cameras.filter((c) => c.kind === "kerbal"),
    [cameras],
  );
  const labelFor = useMemo(() => buildCameraLabeler(cameras), [cameras]);

  // No crew cameras at all -> render nothing (don't reserve empty space). When
  // crew exist but are all closed we still render the header so "Add crew" is
  // reachable to reopen them.
  if (kerbals.length === 0) return null;

  const open = kerbals.filter((k) => !closed.has(k.flightId));
  const closedKerbals = kerbals.filter((k) => closed.has(k.flightId));

  return (
    <Root $placement={placement} data-testid="crew-bar" data-placement={placement}>
      <Bar>
        <BarTitle>Crew</BarTitle>
        <BarControls>
          {closedKerbals.length > 0 && (
            <AddCrew>
              <AddCrewSummary aria-label="Add crew">
                <Plus size={14} strokeWidth={2} aria-hidden="true" />
                Add crew
              </AddCrewSummary>
              <AddCrewMenu>
                {closedKerbals.map((k) => (
                  <AddCrewItem
                    key={k.flightId}
                    type="button"
                    onClick={(e) => {
                      onOpen(k.flightId);
                      // Close the native disclosure after picking.
                      e.currentTarget.closest("details")?.removeAttribute("open");
                    }}
                  >
                    {labelFor(k)}
                  </AddCrewItem>
                ))}
              </AddCrewMenu>
            </AddCrew>
          )}
          <MinButton
            type="button"
            aria-label={minimised ? "Show crew" : "Hide crew"}
            aria-pressed={minimised}
            onClick={onToggleMinimise}
          >
            {minimised ? <PanelBottomOpen size={15} aria-hidden="true" /> : <PanelBottomClose size={15} aria-hidden="true" />}
          </MinButton>
        </BarControls>
      </Bar>
      {/* Faces stay mounted when minimised — collapsed via CSS, never unmounted. */}
      <Faces $placement={placement} $minimised={minimised} aria-hidden={minimised}>
        {open.map((k) => (
          <CrewFace key={k.flightId} cam={k} onClose={() => onClose(k.flightId)} />
        ))}
      </Faces>
    </Root>
  );
}

// ---------------------------------------------------------------------------
// A single crew face
// ---------------------------------------------------------------------------

function CrewFace({ cam, onClose }: { cam: CameraState; onClose: () => void }): React.JSX.Element {
  const wrapRef = useRef<HTMLDivElement>(null);
  const destroyed = isCameraDestroyed(cam);
  const eva = cam.crewLocation === CrewLocation.Eva;
  const name = cam.cameraName || "Kerbal";

  // Fullscreen the FACE CONTAINER (the video goes fullscreen with it) — the
  // KerbalFaceFeed primitive doesn't expose its <video>, so element fullscreen
  // on the wrapper is the composable path. Close hides this face from the bar
  // (persisted; reopen via the Add crew menu).
  const actions = useMemo<FeedAction[]>(
    () => [
      {
        id: "close",
        label: "Close",
        icon: <X size={13} strokeWidth={2} aria-hidden="true" />,
        onClick: onClose,
      },
    ],
    [onClose],
  );

  return (
    <FaceWrap ref={wrapRef} data-testid="crew-face" data-flight-id={cam.flightId} data-destroyed={destroyed} onDoubleClick={() => toggleFullscreen(wrapRef.current)}>
      <KerbalFaceFeed flightId={cam.flightId} actions={actions} showStandby={!destroyed}>
        <Overlay>
          <Badge $eva={eva}>{eva ? "EVA" : "IVA"}</Badge>
          {destroyed && <Lost>SIGNAL LOST</Lost>}
          <Name title={name}>{name}</Name>
        </Overlay>
      </KerbalFaceFeed>
    </FaceWrap>
  );
}

function toggleFullscreen(el: HTMLElement | null): void {
  if (!el) return;
  const d = document as Document & {
    webkitFullscreenElement?: Element | null;
    webkitExitFullscreen?: () => void;
  };
  const e = el as HTMLElement & { webkitRequestFullscreen?: () => void };
  const current = document.fullscreenElement ?? d.webkitFullscreenElement ?? null;
  if (current) {
    (document.exitFullscreen ?? d.webkitExitFullscreen)?.call(document);
  } else {
    (e.requestFullscreen ?? e.webkitRequestFullscreen)?.call(e);
  }
}

// ---------------------------------------------------------------------------
// Styled
// ---------------------------------------------------------------------------

/** Fixed square edge for a crew face (px). */
const FACE = 132;

const Root = styled.div<{ $placement: CrewBarPlacement }>`
  display: flex;
  flex-direction: column;
  min-height: 0;
  ${(p) =>
    p.$placement === "column"
      ? `
    /* Side column: fixed-width vertical dock. */
    width: ${FACE + 32}px;
    flex: 0 0 auto;
    border-left: 1px solid var(--kc-border, rgba(255,255,255,0.1));
    background: var(--kc-surface, rgba(0,0,0,0.15));
  `
      : `
    /* Bottom dock (row / wrap): full-width horizontal strip. */
    flex: 0 0 auto;
    border-top: 1px solid var(--kc-border, rgba(255,255,255,0.1));
    background: var(--kc-surface, rgba(0,0,0,0.15));
  `}
`;

const Bar = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.3rem 0.6rem;
  flex: 0 0 auto;
`;

const BarTitle = styled.span`
  font-size: 0.68rem;
  font-weight: 600;
  letter-spacing: 0.12em;
  text-transform: uppercase;
  color: var(--kc-text-muted, rgba(255,255,255,0.6));
`;

const BarControls = styled.div`
  display: flex;
  align-items: center;
  gap: 0.3rem;
`;

const AddCrew = styled.details`
  position: relative;
`;

const AddCrewSummary = styled.summary`
  display: inline-flex;
  align-items: center;
  gap: 0.25rem;
  padding: 0.15rem 0.4rem;
  font-size: 0.68rem;
  border-radius: 3px;
  cursor: pointer;
  list-style: none;
  color: var(--kc-text-muted, rgba(255,255,255,0.6));

  &::-webkit-details-marker {
    display: none;
  }
  &:hover {
    color: var(--kc-text, #fff);
    background: rgba(255, 255, 255, 0.08);
  }
  &:focus-visible {
    outline: 2px solid var(--kc-accent, #6ab0ff);
    outline-offset: 2px;
  }
`;

const AddCrewMenu = styled.div`
  position: absolute;
  top: 100%;
  right: 0;
  z-index: 20;
  margin-top: 4px;
  min-width: 160px;
  display: flex;
  flex-direction: column;
  padding: 4px;
  border-radius: 6px;
  background: var(--kc-surface-raised, #222);
  border: 1px solid var(--kc-border, rgba(255,255,255,0.12));
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.24);
`;

const AddCrewItem = styled.button`
  text-align: left;
  padding: 0.3rem 0.5rem;
  font-size: 0.75rem;
  border: none;
  border-radius: 3px;
  background: transparent;
  color: var(--kc-text, #fff);
  cursor: pointer;

  &:hover {
    background: rgba(255, 255, 255, 0.1);
  }
  &:focus-visible {
    outline: 2px solid var(--kc-accent, #6ab0ff);
    outline-offset: -2px;
  }
`;

const MinButton = styled.button`
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  padding: 0;
  border: none;
  border-radius: 3px;
  cursor: pointer;
  background: transparent;
  color: var(--kc-text-muted, rgba(255,255,255,0.6));

  &:hover {
    color: var(--kc-text, #fff);
    background: rgba(255, 255, 255, 0.08);
  }
  &:focus-visible {
    outline: 2px solid var(--kc-accent, #6ab0ff);
    outline-offset: 2px;
  }
`;

const Faces = styled.div<{ $placement: CrewBarPlacement; $minimised: boolean }>`
  display: flex;
  gap: 0.6rem;
  padding: 0.6rem;
  ${(p) =>
    p.$placement === "column"
      ? `flex-direction: column; overflow-y: auto; align-items: center;`
      : p.$placement === "wrap"
        ? `flex-direction: row; flex-wrap: wrap; overflow-y: auto; align-content: flex-start;`
        : `flex-direction: row; flex-wrap: nowrap; overflow-x: auto;`}

  /* Minimise collapses the strip without unmounting the feeds. */
  ${(p) =>
    p.$minimised
      ? `max-height: 0; padding-top: 0; padding-bottom: 0; overflow: hidden; opacity: 0; pointer-events: none;`
      : ``}
  transition: max-height 0.18s ease, opacity 0.18s ease;

  @media (prefers-reduced-motion: reduce) {
    transition: none;
  }
`;

const FaceWrap = styled.div`
  position: relative;
  width: ${FACE}px;
  height: ${FACE}px;
  flex: 0 0 auto;

  /* Actions (top-right, from the primitive) hover-reveal: hidden until the face
     is hovered or a control is focused (keyboard reachable). */
  & button {
    opacity: 0;
    transition: opacity 0.12s ease;
  }
  &:hover button,
  &:focus-within button {
    opacity: 1;
  }
  @media (prefers-reduced-motion: reduce) {
    & button {
      transition: none;
    }
  }
`;

const Overlay = styled.div`
  position: absolute;
  inset: 0;
  pointer-events: none;
`;

const Badge = styled.span<{ $eva: boolean }>`
  position: absolute;
  top: 4px;
  left: 4px;
  padding: 1px 5px;
  font-size: 0.6rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  border-radius: 3px;
  color: #fff;
  background: ${(p) => (p.$eva ? "rgba(230,140,30,0.85)" : "rgba(60,120,200,0.85)")};
`;

const Name = styled.span`
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  padding: 3px 6px;
  font-size: 0.68rem;
  font-weight: 600;
  color: #fff;
  background: linear-gradient(to top, rgba(0, 0, 0, 0.7), transparent);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
`;

const Lost = styled.span`
  position: absolute;
  top: 50%;
  left: 0;
  right: 0;
  transform: translateY(-50%);
  text-align: center;
  font-size: 0.62rem;
  font-weight: 700;
  letter-spacing: 0.14em;
  color: #ff6b6b;
`;
