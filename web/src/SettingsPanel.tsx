import { useEffect, useRef } from "react";
import styled from "styled-components";
import { applyTheme, saveDebug, saveTheme } from "./settings";
import type { ThemePreference } from "./settings";

interface SettingsProps {
  theme: ThemePreference;
  debug: boolean;
  onThemeChange: (t: ThemePreference) => void;
  onDebugChange: (enabled: boolean) => void;
  onClose: () => void;
}

export function Settings({
  theme,
  debug,
  onThemeChange,
  onDebugChange,
  onClose,
}: SettingsProps): React.JSX.Element {
  const panelRef = useRef<HTMLDivElement>(null);

  // Close on outside click or Escape
  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    const handlePointer = (e: PointerEvent) => {
      if (panelRef.current && !panelRef.current.contains(e.target as Node)) {
        onClose();
      }
    };
    document.addEventListener("keydown", handleKey);
    document.addEventListener("pointerdown", handlePointer);
    return () => {
      document.removeEventListener("keydown", handleKey);
      document.removeEventListener("pointerdown", handlePointer);
    };
  }, [onClose]);

  const handleTheme = (t: ThemePreference) => {
    saveTheme(t);
    applyTheme(t);
    onThemeChange(t);
  };

  const handleDebug = (enabled: boolean) => {
    saveDebug(enabled);
    onDebugChange(enabled);
  };

  return (
    <Panel ref={panelRef} role="dialog" aria-label="Settings">
      <PanelTitle>Settings</PanelTitle>

      <FieldRow>
        <label htmlFor="kc-theme-select">Theme</label>
        <select
          id="kc-theme-select"
          value={theme}
          onChange={(e) => handleTheme(e.target.value as ThemePreference)}
        >
          <option value="auto">Auto</option>
          <option value="light">Light</option>
          <option value="dark">Dark</option>
        </select>
      </FieldRow>

      <FieldRow>
        <label htmlFor="kc-debug-toggle">Show debug info</label>
        <input
          id="kc-debug-toggle"
          type="checkbox"
          checked={debug}
          onChange={(e) => handleDebug(e.target.checked)}
        />
      </FieldRow>

      <CloseButton type="button" onClick={onClose}>
        Close
      </CloseButton>
    </Panel>
  );
}

const Panel = styled.div`
  position: absolute;
  top: 3rem;
  right: 1rem;
  z-index: 100;
  background: var(--kc-surface);
  border: 1px solid var(--kc-border);
  border-radius: 6px;
  padding: 1rem;
  box-shadow: 0 4px 16px rgba(0, 0, 0, 0.15);
  min-width: 220px;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
`;

const PanelTitle = styled.h2`
  margin: 0;
  font-size: 0.9rem;
  font-weight: 600;
  color: var(--kc-text);
`;

const FieldRow = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.75rem;
  font-size: 0.85rem;
  color: var(--kc-text);

  select,
  input[type="checkbox"] {
    color: var(--kc-text);
    background: var(--kc-surface-raised);
    border: 1px solid var(--kc-border);
    border-radius: 4px;
    padding: 0.2rem 0.4rem;
    font-size: 0.85rem;
    font-family: inherit;
  }

  input[type="checkbox"] {
    padding: 0;
    width: 1rem;
    height: 1rem;
    accent-color: var(--kc-accent);
  }
`;

const CloseButton = styled.button`
  margin-top: 0.25rem;
  padding: 0.35rem 0.75rem;
  background: var(--kc-accent);
  color: #fff;
  border: none;
  border-radius: 4px;
  cursor: pointer;
  font-size: 0.85rem;
  font-family: inherit;
  align-self: flex-end;

  &:hover {
    opacity: 0.9;
  }

  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 2px;
  }
`;
