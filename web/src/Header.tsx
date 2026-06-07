import type { KerbcamClient } from "@jonpepler/kerbcam";
import { useEffect, useState } from "react";
import styled from "styled-components";
import type { ManagerStatus } from "./connectionManager";

interface HeaderProps {
  status: ManagerStatus;
  client: KerbcamClient;
  onOpenSettings: () => void;
}

export function Header({ status, client, onOpenSettings }: HeaderProps): React.JSX.Element {
  const [sidecarVersion, setSidecarVersion] = useState<string | null>(null);
  const [encoderBackend, setEncoderBackend] = useState<string | null>(null);

  useEffect(() => {
    // cameras-change fires after hello + snapshot, so sidecarVersion and
    // encoderBackend are already populated on the client by this point.
    const offCamerasChange = client.on("cameras-change", () => {
      setSidecarVersion(client.sidecarVersion);
      setEncoderBackend(client.encoderBackend);
    });
    // Clear on disconnect so stale info doesn't linger across reconnects.
    const offStateChange = client.on("state-change", (s) => {
      if (s !== "connected") {
        setSidecarVersion(null);
        setEncoderBackend(null);
      }
    });
    return () => {
      offCamerasChange();
      offStateChange();
    };
  }, [client]);

  const statusText = formatStatus(status);
  const statusColor = getStatusColor(status);

  return (
    <Root>
      <Title>kerbcam</Title>
      <StatusArea>
        <StatusDot $color={statusColor} />
        <StatusLabel style={{ color: statusColor }}>{statusText}</StatusLabel>
        {sidecarVersion && (
          <Meta>
            {sidecarVersion}
            {encoderBackend ? ` / ${encoderBackend}` : ""}
          </Meta>
        )}
      </StatusArea>
      <Actions>
        <GearButton
          type="button"
          aria-label="Settings"
          onClick={onOpenSettings}
        >
          {"⚙"}
        </GearButton>
      </Actions>
    </Root>
  );
}

function formatStatus(status: ManagerStatus): string {
  switch (status.kind) {
    case "idle":
      return "idle";
    case "connecting":
      return "connecting";
    case "connected":
      return "connected";
    case "reconnecting":
      return `reconnecting in ${Math.round(status.delayMs / 1000)}s`;
    case "disconnected":
      return "disconnected";
  }
}

function getStatusColor(status: ManagerStatus): string {
  switch (status.kind) {
    case "connected":
      return "var(--kc-ok)";
    case "reconnecting":
    case "connecting":
      return "var(--kc-warn)";
    case "disconnected":
    case "idle":
      return "var(--kc-text-muted)";
  }
}

// ---------------------------------------------------------------------------
// Styled components
// ---------------------------------------------------------------------------

const Root = styled.header`
  display: flex;
  align-items: center;
  gap: 0.75rem;
  padding: 0.5rem 1rem;
  background: var(--kc-surface);
  border-bottom: 1px solid var(--kc-border);
`;

const Title = styled.h1`
  margin: 0;
  font-size: 1rem;
  font-weight: 700;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--kc-text);
`;

const StatusArea = styled.div`
  display: flex;
  align-items: center;
  gap: 0.4rem;
  flex: 1;
`;

const StatusDot = styled.span<{ $color: string }>`
  width: 8px;
  height: 8px;
  border-radius: 50%;
  background: ${(p) => p.$color};
  flex-shrink: 0;
`;

const StatusLabel = styled.span`
  font-size: 0.8rem;
`;

const Meta = styled.span`
  font-size: 0.75rem;
  color: var(--kc-text-muted);
  font-family: ui-monospace, monospace;
`;

const Actions = styled.div`
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-left: auto;
`;

const GearButton = styled.button`
  background: none;
  border: 1px solid var(--kc-border);
  border-radius: 4px;
  padding: 0.25rem 0.5rem;
  cursor: pointer;
  color: var(--kc-text);
  font-size: 1rem;
  line-height: 1;

  &:hover {
    background: var(--kc-surface-raised);
  }

  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 2px;
  }
`;
