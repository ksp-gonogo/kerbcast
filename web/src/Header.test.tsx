/**
 * Tests for the REC+ and Recordings buttons added to the Header toolbar.
 * Header is a plain prop-driven component for these controls (it doesn't
 * call `useRecordings` itself -- App/RecGroupBar own the recording state),
 * so it renders standalone here without a KerbcastProvider.
 */

import type { KerbcastClient, LensLevelsPayload } from "@ksp-gonogo/kerbcast";
import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Header } from "./Header";
import type { ManagerStatus } from "./connectionManager";

function renderHeader(overrides: Partial<Parameters<typeof Header>[0]> = {}) {
  const status: ManagerStatus = { kind: "idle" };
  const client = { on: () => () => {} } as unknown as KerbcastClient;

  return render(
    <Header
      status={status}
      client={client}
      onOpenSettings={() => {}}
      recordingsOpen={false}
      onToggleRecordings={() => {}}
      recSelectMode={false}
      onToggleRecSelectMode={() => {}}
      recordingGroupActive={false}
      {...overrides}
    />,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("Header - camera lenses panel", () => {
  const career = { gameMode: "career", opticsOn: true, trackingGated: false, trackingLevels: [], spaceSteps: [] } as unknown as LensLevelsPayload;

  function lensClient() {
    let notify = () => {};
    const client = {
      lensLevels: career as LensLevelsPayload | null,
      on: (event: string, cb: () => void) => {
        if (event === "settings-change") notify = cb;
        return () => {};
      },
    };
    return { client: client as unknown as KerbcastClient, setLens: (lens: LensLevelsPayload | null) => { client.lensLevels = lens; act(() => notify()); } };
  }
  const openPanel = () => fireEvent.click(screen.getByRole("button", { name: "Camera lenses" }));

  it("does not reopen by itself when the lenses come back", () => {
    const { client, setLens } = lensClient();
    renderHeader({ client });
    openPanel();
    expect(screen.getByRole("dialog", { name: "Camera lenses" })).toBeTruthy();

    setLens(null);
    expect(screen.queryByRole("dialog")).toBeNull();
    setLens(career);
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("takes focus when it opens and returns it to the toggle on Escape", () => {
    const { client } = lensClient();
    renderHeader({ client });
    const toggle = screen.getByRole("button", { name: "Camera lenses" });
    toggle.focus();
    fireEvent.click(toggle);
    expect(document.activeElement).toBe(screen.getByRole("dialog", { name: "Camera lenses" }));

    fireEvent.keyDown(document, { key: "Escape" });
    expect(document.activeElement).toBe(toggle);
  });
});

describe("Header - REC+ and Recordings buttons", () => {
  it("renders a REC+ toggle and a Recordings toggle", () => {
    renderHeader();
    expect(screen.getByRole("button", { name: /rec\+/i })).toBeTruthy();
    expect(screen.getByRole("button", { name: /recordings/i })).toBeTruthy();
  });

  it("clicking REC+ calls onToggleRecSelectMode", () => {
    const onToggleRecSelectMode = vi.fn();
    renderHeader({ onToggleRecSelectMode });
    fireEvent.click(screen.getByRole("button", { name: /rec\+/i }));
    expect(onToggleRecSelectMode).toHaveBeenCalledTimes(1);
  });

  it("REC+ is disabled while a grouped recording is in progress", () => {
    renderHeader({ recordingGroupActive: true });
    const btn = screen.getByRole("button", { name: /rec\+/i }) as HTMLButtonElement;
    expect(btn.disabled).toBe(true);
  });

  it("REC+ reflects recSelectMode as pressed", () => {
    renderHeader({ recSelectMode: true });
    const btn = screen.getByRole("button", { name: /rec\+/i });
    expect(btn.getAttribute("aria-pressed")).toBe("true");
  });

  it("clicking Recordings calls onToggleRecordings", () => {
    const onToggleRecordings = vi.fn();
    renderHeader({ onToggleRecordings });
    fireEvent.click(screen.getByRole("button", { name: /recordings/i }));
    expect(onToggleRecordings).toHaveBeenCalledTimes(1);
  });
});
