import { describe, expect, it } from "vitest";
import type { CommandTrackState } from "./__generated__/types";
import {
  COMMAND_TRACK_LABELS,
  effectiveCommandTrack,
  formatCommandRange,
  commandEventAt,
  commandReadout,
} from "./commandTrack";

function track(overrides: Partial<CommandTrackState> = {}): CommandTrackState {
  return {
    ut: 1000,
    hasFix: true,
    rangeM: 11_507_905.1,
    occluded: false,
    occluder: "",
    sunlit: true,
    bracketHalfPx: 4.2,
    frameHeightPx: 576,
    framingFits: true,
    events: [],
    ...overrides,
  };
}

const AUTHORITY = { owner: "gonogo" };

describe("formatCommandRange", () => {
  it.each([
    [850, "850 m"],
    [999.4, "999 m"],
    [4200, "4.2 km"],
    [9_949, "9.9 km"],
    [11_507_905.1, "11,508 km"],
    [12_000, "12 km"],
    [1_234_567_000, "1,234,567 km"],
  ])("%s m -> %s", (m, expected) => {
    expect(formatCommandRange(m)).toBe(expected);
  });
});

describe("commandEventAt", () => {
  const events = [
    { ut: 10, occluded: false, occluder: "", sunlit: true },
    { ut: 20, occluded: true, occluder: "Mun", sunlit: true },
    { ut: 30, occluded: false, occluder: "", sunlit: false },
  ];

  it("picks the latest event at or before the displayed UT", () => {
    expect(commandEventAt(events, 25)?.ut).toBe(20);
    expect(commandEventAt(events, 20)?.ut).toBe(20);
    expect(commandEventAt(events, 99)?.ut).toBe(30);
  });

  it("returns null when every event is newer", () => {
    expect(commandEventAt(events, 5)).toBeNull();
  });
});

describe("effectiveCommandTrack", () => {
  const withEvents = track({
    occluded: false,
    sunlit: true,
    authority: AUTHORITY,
    events: [
      { ut: 990, occluded: true, occluder: "Mun", sunlit: true },
      { ut: 999, occluded: false, occluder: "", sunlit: true, blank: "Waiting for gonogo" },
    ],
  });

  it("uses the event in force at the displayed UT for discrete fields", () => {
    const s = effectiveCommandTrack(withEvents, 995);
    expect(s.camera).toBe("occluded");
    expect(s.occluder).toBe("Mun");
    expect(s.blank).toBeNull();
    expect(s.rangeM).toBe(11_507_905.1);
    expect(s.authority).toEqual(AUTHORITY);
  });

  it("carries the event's blank", () => {
    expect(effectiveCommandTrack(withEvents, 999.5).blank).toBe("Waiting for gonogo");
  });

  it("falls back to the snapshot before the first event or without a displayed UT", () => {
    for (const ut of [900, null, undefined]) {
      expect(effectiveCommandTrack(withEvents, ut).camera).toBe("inView");
    }
  });

  it("reports no fix ahead of any visibility state", () => {
    expect(effectiveCommandTrack(track({ hasFix: false, occluded: true })).camera).toBe("noFix");
  });

  it("has no authority when standalone", () => {
    expect(effectiveCommandTrack(track()).authority).toBeNull();
  });
});

describe("commandReadout", () => {
  it("shows the camera part and the range", () => {
    expect(commandReadout(effectiveCommandTrack(track()))).toBe("IN VIEW · 11,508 km");
  });

  it("reads the same under an authority", () => {
    const s = effectiveCommandTrack(
      track({ occluded: true, occluder: "Mun", authority: AUTHORITY }),
    );
    expect(commandReadout(s)).toBe(`OCCLUDED MUN${COMMAND_TRACK_LABELS.separator}11,508 km`);
  });

  it("shows NO FIX without a range when there is no fix", () => {
    const s = effectiveCommandTrack(track({ hasFix: false, rangeM: undefined }));
    expect(commandReadout(s)).toBe("NO FIX");
  });
});
