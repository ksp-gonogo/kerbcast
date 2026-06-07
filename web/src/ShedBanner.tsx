import type { AdaptiveShedPayload } from "@jonpepler/kerbcam";
import type { KerbcamClient } from "@jonpepler/kerbcam";
import { useEffect, useState } from "react";
import styled from "styled-components";

interface ShedBannerProps {
  client: KerbcamClient;
}

export function ShedBanner({ client }: ShedBannerProps): React.JSX.Element | null {
  const [shed, setShed] = useState<AdaptiveShedPayload | null>(null);
  const [dismissed, setDismissed] = useState(false);

  useEffect(() => {
    return client.on("adaptive-shed", (payload) => {
      if (payload.level > 0) {
        setShed(payload);
        setDismissed(false);
      } else {
        setShed(null);
        setDismissed(false);
      }
    });
  }, [client]);

  if (!shed || dismissed) return null;

  return (
    <Banner role="alert">
      <WarnDot aria-hidden="true" />
      <BannerText>
        Quality reduced: {shed.reason} (KSP {shed.kspFps.toFixed(1)} fps)
      </BannerText>
      <DismissButton
        type="button"
        aria-label="Dismiss quality warning"
        onClick={() => setDismissed(true)}
      >
        <CloseX aria-hidden="true" />
      </DismissButton>
    </Banner>
  );
}

function CloseX() {
  return (
    <svg width="10" height="10" viewBox="0 0 10 10" fill="none" xmlns="http://www.w3.org/2000/svg">
      <path d="M1 1l8 8M9 1L1 9" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round"/>
    </svg>
  );
}

const Banner = styled.div`
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.45rem 1rem;
  background: var(--kc-warn-bg);
  border-bottom: 1px solid var(--kc-warn);
  color: var(--kc-warn);
  flex-shrink: 0;
`;

const WarnDot = styled.span`
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--kc-warn);
  flex-shrink: 0;
`;

const BannerText = styled.span`
  font-size: 0.78rem;
  flex: 1;
  letter-spacing: 0.01em;
`;

const DismissButton = styled.button`
  background: none;
  border: none;
  cursor: pointer;
  color: var(--kc-warn);
  padding: 0.15rem;
  line-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 3px;
  opacity: 0.7;
  flex-shrink: 0;
  transition: opacity 0.12s ease;

  &:hover {
    opacity: 1;
  }

  &:focus-visible {
    outline: 2px solid var(--kc-warn);
    outline-offset: 2px;
    opacity: 1;
  }
`;
