import { HardHatIcon, useKerbcastInFlight } from "@ksp-gonogo/kerbcast-react";
import styled from "styled-components";

/**
 * One dashboard-level standby shown when KSP is not in a flight scene.
 * Passive scrim (the toolbar stays interactive, tiles beneath stay
 * clickable); the per-feed CameraFeed standby icon supersedes it per tile.
 * Hidden while the in-flight signal is unknown so it never flashes on
 * connect.
 */
export function StandbyOverlay() {
  const inFlight = useKerbcastInFlight();
  if (inFlight !== false) return null;
  return (
    <Scrim role="status" aria-live="polite">
      <IconWrap aria-hidden="true">
        <HardHatIcon size={44} />
      </IconWrap>
      <Copy>Camera feeds activate in a flight scene.</Copy>
    </Scrim>
  );
}

const Scrim = styled.div`
  position: absolute;
  inset: 0;
  z-index: 5;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.75rem;
  pointer-events: none;
  background: rgba(0, 0, 0, 0.55);
  color: var(--kc-text-muted);
  text-align: center;
`;

const IconWrap = styled.div`
  opacity: 0.6;
`;

const Copy = styled.p`
  margin: 0;
  font-size: 0.9rem;
  font-weight: 600;
  letter-spacing: 0.04em;
`;
