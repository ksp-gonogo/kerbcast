/**
 * The header's lens button and the lens panel it opens. Header subscribes
 * through its `client` prop, so a stub client with a settable `lensLevels`
 * stands in for the SDK.
 */

import type { KerbcastClient, LensLevelsPayload } from "@ksp-gonogo/kerbcast";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { Header } from "./Header";
import { formatArcsec, formatFov } from "./LensPanel";
import { MOCK_LENS_LEVELS } from "./mock/driver";

function stubClient(lensLevels: LensLevelsPayload | null) {
  const listeners = new Set<() => void>();
  const client = {
    lensLevels,
    on: (event: string, fn: () => void) => {
      if (event !== "settings-change") return () => {};
      listeners.add(fn);
      return () => listeners.delete(fn);
    },
  };
  const push = (next: LensLevelsPayload | null) => {
    client.lensLevels = next;
    act(() => listeners.forEach((fn) => fn()));
  };
  return { client: client as unknown as KerbcastClient, push };
}

function renderHeader(lensLevels: LensLevelsPayload | null) {
  const stub = stubClient(lensLevels);
  render(
    <Header
      status={{ kind: "connected" }}
      client={stub.client}
      onOpenSettings={() => {}}
      recordingsOpen={false}
      onToggleRecordings={() => {}}
      recSelectMode={false}
      onToggleRecSelectMode={() => {}}
      recordingGroupActive={false}
    />,
  );
  return stub;
}

const lensButton = () => screen.queryByRole("button", { name: "Camera lenses" });

describe("Header lens button", () => {
  it("shows in career and science saves", () => {
    const { push } = renderHeader(MOCK_LENS_LEVELS);
    expect(lensButton()).toBeTruthy();
    push({ ...MOCK_LENS_LEVELS, gameMode: "science" });
    expect(lensButton()).toBeTruthy();
  });

  it("is hidden in sandbox and when the game reports no lenses", () => {
    const { push } = renderHeader(null);
    expect(lensButton()).toBeNull();
    push({ ...MOCK_LENS_LEVELS, gameMode: "sandbox" });
    expect(lensButton()).toBeNull();
    push(MOCK_LENS_LEVELS);
    expect(lensButton()).toBeTruthy();
  });

  it("opens a panel listing the Tracking Station levels and space lens steps", () => {
    renderHeader(MOCK_LENS_LEVELS);
    fireEvent.click(lensButton()!);
    const panel = screen.getByRole("dialog", { name: "Camera lenses" });

    const ground = within(panel).getByRole("region", { name: "Tracking Station" });
    const levels = within(ground).getAllByRole("listitem");
    expect(levels).toHaveLength(3);
    expect(levels[1].getAttribute("aria-current")).toBe("true");
    expect(levels[1].textContent).toContain("4″");
    expect(levels[1].textContent).toContain("0.32°");

    const space = within(panel).getByRole("region", { name: "Space lens" });
    const steps = within(space).getAllByRole("listitem");
    expect(steps.map((s) => s.textContent)).toEqual([
      "Scanning Tech (unlocked)0.5″0.04°",
      "Advanced Science Tech (locked)0.2″0.016°",
      "Experimental Science (locked)0.05″0.004°",
    ]);
    expect(within(panel).queryByText(/optics are off/i)).toBeNull();
  });

  it("says when optics are off and when the ground lens is not gated", () => {
    renderHeader({ ...MOCK_LENS_LEVELS, opticsOn: false, trackingGated: false });
    fireEvent.click(lensButton()!);
    const panel = screen.getByRole("dialog", { name: "Camera lenses" });
    expect(within(panel).getByText(/optics are off/i)).toBeTruthy();
    expect(within(panel).getByText(/best ground lens/i)).toBeTruthy();
  });

  it("closes on Escape and on a click outside, and toggles from its button", () => {
    renderHeader(MOCK_LENS_LEVELS);
    fireEvent.click(lensButton()!);
    expect(screen.queryByRole("dialog")).toBeTruthy();
    fireEvent.keyDown(document, { key: "Escape" });
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(lensButton()!);
    fireEvent.pointerDown(document.body);
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.click(lensButton()!);
    fireEvent.pointerDown(lensButton()!);
    fireEvent.click(lensButton()!);
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("lens formatting", () => {
  it("formats arcseconds and degrees without trailing noise", () => {
    expect(formatArcsec(15)).toBe("15″");
    expect(formatArcsec(0.05)).toBe("0.05″");
    expect(formatFov(1.2)).toBe("1.2°");
    expect(formatFov(0.016)).toBe("0.016°");
    expect(formatFov(0.004)).toBe("0.004°");
  });
});
