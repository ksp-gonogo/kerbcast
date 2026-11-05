import { beforeEach, describe, expect, it } from "vitest";
import {
  loadRecordFullResolution,
  loadShowLaunchpad,
  saveRecordFullResolution,
  saveShowLaunchpad,
} from "./settings";

describe("record-full-resolution setting", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("defaults to false when nothing is stored", () => {
    expect(loadRecordFullResolution()).toBe(false);
  });

  it("persists true", () => {
    saveRecordFullResolution(true);
    expect(loadRecordFullResolution()).toBe(true);
  });

  it("persists false explicitly (after having been true)", () => {
    saveRecordFullResolution(true);
    saveRecordFullResolution(false);
    expect(loadRecordFullResolution()).toBe(false);
  });
});

describe("show Launchpad camera", () => {
  it("defaults to shown when the key is absent", () => {
    localStorage.clear();
    expect(loadShowLaunchpad()).toBe(true);
  });

  it("round-trips an explicit choice", () => {
    saveShowLaunchpad(false);
    expect(loadShowLaunchpad()).toBe(false);
    saveShowLaunchpad(true);
    expect(loadShowLaunchpad()).toBe(true);
  });
});
