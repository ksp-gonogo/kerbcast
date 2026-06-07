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
  const isPulse = status.kind === "connecting" || status.kind === "reconnecting";

  return (
    <Root>
      <Wordmark>kerbcam</Wordmark>
      <Divider />
      <StatusArea>
        <StatusDot $color={statusColor} $pulse={isPulse} />
        <StatusLabel>{statusText}</StatusLabel>
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
          <GearIcon aria-hidden="true" />
        </GearButton>
      </Actions>
    </Root>
  );
}

function GearIcon() {
  return (
    <svg
      width="16"
      height="16"
      viewBox="0 0 16 16"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
    >
      <path
        d="M8 10a2 2 0 100-4 2 2 0 000 4z"
        fill="currentColor"
      />
      <path
        fillRule="evenodd"
        clipRule="evenodd"
        d="M6.68 1.28a.75.75 0 01.74-.28l.02.005L8 1.07l.56-.005.02-.005a.75.75 0 01.74.28l.57.83a5.06 5.06 0 011.05.61l.98-.18a.75.75 0 01.74.35l.56.97a.75.75 0 01-.1.87l-.7.69c.03.17.05.35.05.53s-.02.36-.05.53l.7.69a.75.75 0 01.1.87l-.56.97a.75.75 0 01-.74.35l-.98-.18a5.06 5.06 0 01-1.05.61l-.57.83a.75.75 0 01-.74.28L8 10.93l-.56.005-.02.005a.75.75 0 01-.74-.28l-.57-.83a5.06 5.06 0 01-1.05-.61l-.98.18a.75.75 0 01-.74-.35l-.56-.97a.75.75 0 01.1-.87l.7-.69A4.1 4.1 0 012.5 8c0-.18.02-.36.05-.53l-.7-.69a.75.75 0 01-.1-.87l.56-.97a.75.75 0 01.74-.35l.98.18a5.06 5.06 0 011.05-.61l.57-.83zM8 2.57l-.4.58a.75.75 0 01-.5.3 3.56 3.56 0 00-1.35.79.75.75 0 01-.57.18l-.7-.13-.19.33.5.49a.75.75 0 01.2.65A2.6 2.6 0 005.75 8c0 .1 0 .2.02.3a.75.75 0 01-.2.64l-.5.49.19.33.7-.13a.75.75 0 01.57.18c.38.35.83.63 1.35.79a.75.75 0 01.5.3l.39.57h.38l.4-.58a.75.75 0 01.5-.3c.52-.16.97-.44 1.35-.79a.75.75 0 01.57-.18l.7.13.19-.33-.5-.49a.75.75 0 01-.2-.65C10.5 8.2 10.5 8.1 10.5 8c0-.1 0-.2-.02-.3a.75.75 0 01.2-.64l.5-.49-.19-.33-.7.13a.75.75 0 01-.57-.18A3.56 3.56 0 008.9 5.4a.75.75 0 01-.5-.3L8 4.53l-.38-.01z"
        fill="currentColor"
        opacity="0.8"
      />
    </svg>
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
  padding: 0 1rem;
  height: var(--kc-header-h);
  background: var(--kc-surface);
  border-bottom: 1px solid var(--kc-border);
  flex-shrink: 0;
`;

const Wordmark = styled.h1`
  margin: 0;
  font-size: 0.85rem;
  font-weight: 600;
  letter-spacing: 0.12em;
  text-transform: lowercase;
  color: var(--kc-text);
  font-family: inherit;
`;

const Divider = styled.span`
  width: 1px;
  height: 18px;
  background: var(--kc-border);
  flex-shrink: 0;
`;

const StatusArea = styled.div`
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex: 1;
  min-width: 0;
`;

interface DotProps {
  $color: string;
  $pulse: boolean;
}

const StatusDot = styled.span<DotProps>`
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: ${(p) => p.$color};
  flex-shrink: 0;
  transition: background 0.2s ease;

  ${(p) =>
    p.$pulse &&
    `
    animation: kc-pulse 1.4s ease-in-out infinite;
    @media (prefers-reduced-motion: reduce) {
      animation: none;
    }
  `}

  @keyframes kc-pulse {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.35; }
  }
`;

const StatusLabel = styled.span`
  font-size: 0.75rem;
  letter-spacing: 0.02em;
  color: var(--kc-text-muted);
`;

const Meta = styled.span`
  font-size: 0.7rem;
  color: var(--kc-text-muted);
  opacity: 0.7;
  font-family: inherit;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
`;

const Actions = styled.div`
  display: flex;
  align-items: center;
  gap: 0.5rem;
  margin-left: auto;
`;

const GearButton = styled.button`
  background: none;
  border: 1px solid transparent;
  border-radius: 4px;
  padding: 0.3rem;
  cursor: pointer;
  color: var(--kc-text-muted);
  line-height: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: color 0.15s ease, border-color 0.15s ease, background 0.15s ease;

  &:hover {
    color: var(--kc-text);
    border-color: var(--kc-border);
    background: var(--kc-surface-raised);
  }

  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 2px;
  }
`;
