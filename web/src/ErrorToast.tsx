import type { ErrorPayload, KerbcastClient } from "@jonpepler/kerbcast";
import { useEffect, useRef, useState } from "react";
import { X } from "lucide-react";
import styled from "styled-components";

interface ErrorToastProps {
  client: KerbcastClient;
}

interface Notice {
  id: number;
  message: string;
}

/* How long a notice lingers before it auto-dismisses. */
const AUTO_DISMISS_MS = 6000;

/**
 * Surfaces sidecar error replies (e.g. a failed rebind, "no free slot") as a
 * stack of transient, dismissible notices in the corner of the screen. Each
 * notice auto-clears after a few seconds; the operator can also dismiss it.
 */
export function ErrorToast({ client }: ErrorToastProps): React.JSX.Element | null {
  const [notices, setNotices] = useState<Notice[]>([]);
  const nextId = useRef(0);

  useEffect(() => {
    return client.on("error", (payload: ErrorPayload) => {
      const id = nextId.current++;
      setNotices((prev) => [...prev, { id, message: payload.message }]);
      setTimeout(() => {
        setNotices((prev) => prev.filter((n) => n.id !== id));
      }, AUTO_DISMISS_MS);
    });
  }, [client]);

  if (notices.length === 0) return null;

  const dismiss = (id: number) =>
    setNotices((prev) => prev.filter((n) => n.id !== id));

  return (
    <ToastStack>
      {notices.map((n) => (
        <Toast key={n.id} role="status">
          <WarnDot aria-hidden="true" />
          <ToastText>{n.message}</ToastText>
          <DismissButton
            type="button"
            aria-label="Dismiss error"
            onClick={() => dismiss(n.id)}
          >
            <X size={12} strokeWidth={1.75} aria-hidden="true" />
          </DismissButton>
        </Toast>
      ))}
    </ToastStack>
  );
}

const ToastStack = styled.div`
  position: fixed;
  bottom: 1rem;
  right: 1rem;
  z-index: 100;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  max-width: min(360px, calc(100vw - 2rem));
`;

const Toast = styled.div`
  display: flex;
  align-items: center;
  gap: 0.6rem;
  padding: 0.55rem 0.85rem;
  background: var(--kc-warn-bg);
  border: 1px solid var(--kc-warn);
  border-radius: 6px;
  color: var(--kc-warn);
  box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
`;

const WarnDot = styled.span`
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--kc-warn);
  flex-shrink: 0;
`;

const ToastText = styled.span`
  font-size: 0.78rem;
  flex: 1;
  letter-spacing: 0.01em;
  word-break: break-word;
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
