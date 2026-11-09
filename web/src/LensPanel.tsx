import type { LensLevelsPayload } from "@ksp-gonogo/kerbcast";
import { Check, Lock, X } from "lucide-react";
import { forwardRef } from "react";
import styled from "styled-components";

/** Game modes where lenses are earned, so the page offers the lens panel. */
export function lensPanelApplies(lens: LensLevelsPayload | null): lens is LensLevelsPayload {
  return lens !== null && (lens.gameMode === "career" || lens.gameMode === "science");
}

/** A lens resolution in arcseconds, as `15″` or `0.05″`. */
export function formatArcsec(arcsec: number): string {
  return `${Number(arcsec.toPrecision(3))}″`;
}

/** A zoom floor in degrees, as `1.2°` or `0.016°`. */
export function formatFov(deg: number): string {
  return `${Number(deg.toPrecision(2))}°`;
}

interface LensPanelProps {
  lens: LensLevelsPayload;
  onClose: () => void;
}

/**
 * The lenses command cameras have in this save: the ground lens by Tracking
 * Station level and the space lens steps research unlocks.
 */
export const LensPanel = forwardRef<HTMLDivElement, LensPanelProps>(function LensPanel(
  { lens, onClose },
  ref,
) {
  const bestLevel = lens.trackingLevels.reduce<number | null>(
    (best, l) => (best === null || l.level > best ? l.level : best),
    null,
  );
  const groundLevel = lens.trackingGated ? lens.trackingLevel : bestLevel;

  return (
    <Panel ref={ref} role="dialog" aria-label="Camera lenses">
      <PanelHeader>
        <PanelTitle>Camera lenses</PanelTitle>
        <CloseIconButton type="button" onClick={onClose} aria-label="Close">
          <X size={14} strokeWidth={1.75} aria-hidden="true" />
        </CloseIconButton>
      </PanelHeader>

      <Sections>
        {!lens.opticsOn && (
          <Notice>Deep-space optics are off, so lenses do not blur and the usual zoom limits apply</Notice>
        )}

        <section aria-label="Tracking Station">
          <SectionHead>
            <SectionLabel>Tracking Station</SectionLabel>
            <ColumnHeads aria-hidden="true">
              <span>Lens</span>
              <span>Zooms to</span>
            </ColumnHeads>
          </SectionHead>
          <Hint>
            {lens.trackingGated
              ? `Ground cameras now: ${formatArcsec(lens.trackingLensArcsec)} lens, zoom to ${formatFov(lens.trackingFovMinDeg)}`
              : "Ground cameras have the best ground lens in this game"}
          </Hint>
          <Rows>
            {lens.trackingLevels.map((l) => {
              const current = l.level === groundLevel;
              return (
                <Row key={l.level} $current={current} aria-current={current ? "true" : undefined}>
                  <Name>
                    Level {l.level}
                    {current && <Tag>now</Tag>}
                  </Name>
                  <Value>{formatArcsec(l.lensArcsec)}</Value>
                  <Value>{formatFov(l.fovMinDeg)}</Value>
                </Row>
              );
            })}
          </Rows>
        </section>

        <section aria-label="Space lens">
          <SectionHead>
            <SectionLabel>Space lens</SectionLabel>
            <ColumnHeads aria-hidden="true">
              <span>Lens</span>
              <span>Zooms to</span>
            </ColumnHeads>
          </SectionHead>
          <Hint>Craft cameras outside an atmosphere, unlocked by research</Hint>
          {lens.spaceSteps.length === 0 ? (
            <Hint>This game has no space lens upgrades</Hint>
          ) : (
            <Rows>
              {lens.spaceSteps.map((step) => (
                <Row key={step.techId} $current={false} $locked={!step.unlocked}>
                  <Name>
                    <StatusIcon $unlocked={step.unlocked}>
                      {step.unlocked ? (
                        <Check size={12} strokeWidth={2} aria-hidden="true" />
                      ) : (
                        <Lock size={12} strokeWidth={1.75} aria-hidden="true" />
                      )}
                    </StatusIcon>
                    <TechTitle>{step.techTitle}</TechTitle>
                    <VisuallyHidden>{step.unlocked ? " (unlocked)" : " (locked)"}</VisuallyHidden>
                  </Name>
                  <Value>{formatArcsec(step.lensArcsec)}</Value>
                  <Value>{formatFov(step.fovMinDeg)}</Value>
                </Row>
              ))}
            </Rows>
          )}
          <Hint>A craft camera uses whichever is better, its space lens or the ground lens</Hint>
        </section>
      </Sections>
    </Panel>
  );
});

const Panel = styled.div`
  position: absolute;
  top: calc(var(--kc-header-h) + 0.5rem);
  right: 1rem;
  z-index: 100;
  width: min(360px, calc(100vw - 2rem));
  max-height: calc(100vh - var(--kc-header-h) - 1rem);
  overflow-y: auto;
  background: var(--kc-surface);
  border: 1px solid var(--kc-border);
  border-radius: 8px;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.18), 0 2px 6px rgba(0, 0, 0, 0.12);
`;

const PanelHeader = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.65rem 0.85rem 0.6rem;
  border-bottom: 1px solid var(--kc-border);
  background: var(--kc-surface-raised);
`;

const PanelTitle = styled.h2`
  margin: 0;
  font-size: 0.75rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--kc-text-muted);
`;

const CloseIconButton = styled.button`
  background: none;
  border: none;
  cursor: pointer;
  color: var(--kc-text-muted);
  padding: 0.1rem;
  line-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 3px;
  transition: color 0.12s ease;

  &:hover {
    color: var(--kc-text);
  }

  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 2px;
  }
`;

const Sections = styled.div`
  padding: 0.85rem;
  display: flex;
  flex-direction: column;
  gap: 1rem;
`;

const Notice = styled.p`
  margin: 0;
  padding: 0.45rem 0.6rem;
  font-size: 0.7rem;
  line-height: 1.4;
  color: var(--kc-warn);
  background: var(--kc-warn-bg);
  border-radius: 4px;
`;

const SectionHead = styled.div`
  display: grid;
  grid-template-columns: 1fr 3.4rem 3.4rem;
  align-items: baseline;
  gap: 0.5rem;
  padding: 0 0.5rem;
`;

const SectionLabel = styled.h3`
  margin: 0;
  font-size: 0.65rem;
  font-weight: 600;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--kc-text-muted);
  opacity: 0.7;
`;

const ColumnHeads = styled.div`
  display: contents;
  font-size: 0.6rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--kc-text-muted);
  opacity: 0.7;

  & > span {
    text-align: right;
  }
`;

const Hint = styled.p`
  margin: 0.3rem 0 0.4rem;
  padding: 0 0.5rem;
  font-size: 0.68rem;
  line-height: 1.4;
  color: var(--kc-text-muted);
`;

const Rows = styled.ul`
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
`;

const Row = styled.li<{ $current: boolean; $locked?: boolean }>`
  display: grid;
  grid-template-columns: 1fr 3.4rem 3.4rem;
  align-items: center;
  gap: 0.5rem;
  padding: 0.3rem 0.5rem;
  border-radius: 4px;
  border: 1px solid ${(p) => (p.$current ? "var(--kc-accent)" : "transparent")};
  background: ${(p) => (p.$current ? "var(--kc-accent-wash)" : "var(--kc-surface-raised)")};
  color: ${(p) => (p.$locked ? "var(--kc-text-muted)" : "var(--kc-text)")};
  font-size: 0.75rem;
`;

const Name = styled.span`
  display: flex;
  align-items: center;
  gap: 0.4rem;
  min-width: 0;
`;

const TechTitle = styled.span`
  min-width: 0;
  overflow-wrap: anywhere;
`;

const Tag = styled.span`
  font-size: 0.6rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--kc-accent);
`;

const StatusIcon = styled.span<{ $unlocked: boolean }>`
  display: inline-flex;
  flex-shrink: 0;
  color: ${(p) => (p.$unlocked ? "var(--kc-ok)" : "var(--kc-text-muted)")};
`;

const Value = styled.span`
  text-align: right;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
`;

const VisuallyHidden = styled.span`
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
`;
