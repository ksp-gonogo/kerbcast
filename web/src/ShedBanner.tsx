import type { AdaptiveShedPayload } from "@ksp-gonogo/kerbcast";
import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { useEffect, useState } from "react";
import styled from "styled-components";

interface ShedBannerProps {
  client: KerbcastClient;
}

export function ShedBanner({ client }: ShedBannerProps): React.JSX.Element | null {
  const [shed, setShed] = useState<AdaptiveShedPayload | null>(null);

  useEffect(() => {
    return client.on("adaptive-shed", (payload) => {
      if (payload.level > 0) {
        setShed(payload);
      } else {
        setShed(null);
      }
    });
  }, [client]);

  if (!shed) return null;

  return (
    <Overlay role="alert">
      <WarnDot aria-hidden="true" />
      <OverlayText>
        Quality reduced: {shed.reason} (KSP {shed.kspFps.toFixed(1)} fps)
      </OverlayText>
    </Overlay>
  );
}

const Overlay = styled.div`
  position: fixed;
  bottom: 1rem;
  left: 1rem;
  z-index: 100;
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.55rem 0.85rem;
  background: var(--kc-warn-bg);
  border: 1px solid var(--kc-warn);
  border-radius: 6px;
  color: var(--kc-warn);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
  max-width: min(360px, calc(100vw - 2rem));
`;

const WarnDot = styled.span`
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--kc-warn);
  flex-shrink: 0;
`;

const OverlayText = styled.span`
  font-size: 0.78rem;
  flex: 1;
  letter-spacing: 0.01em;
  word-break: break-word;
`;
