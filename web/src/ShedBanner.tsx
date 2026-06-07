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
      <BannerText>
        Quality reduced: {shed.reason} (KSP {shed.kspFps.toFixed(1)} fps)
      </BannerText>
      <DismissButton
        type="button"
        aria-label="Dismiss quality warning"
        onClick={() => setDismissed(true)}
      >
        x
      </DismissButton>
    </Banner>
  );
}

const Banner = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  padding: 0.5rem 1rem;
  background: var(--kc-accent-wash);
  border-bottom: 1px solid var(--kc-warn);
  color: var(--kc-warn);
`;

const BannerText = styled.span`
  font-size: 0.85rem;
`;

const DismissButton = styled.button`
  background: none;
  border: none;
  cursor: pointer;
  color: var(--kc-warn);
  font-size: 1rem;
  padding: 0 0.25rem;
  line-height: 1;
  flex-shrink: 0;

  &:focus-visible {
    outline: 2px solid var(--kc-warn);
    outline-offset: 2px;
  }
`;
