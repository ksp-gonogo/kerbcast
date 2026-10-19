import { effectiveCommandTrack, commandReadout } from "@ksp-gonogo/kerbcast";
import { describe, expect, it } from "vitest";
import { MOCK_COMMAND_SCENARIO_COUNT, mockCommandTrack } from "./commandScenarios";

describe("mockCommandTrack", () => {
  const ut = 1_000_000;
  const readouts = Array.from({ length: MOCK_COMMAND_SCENARIO_COUNT }, (_, i) => {
    const state = effectiveCommandTrack(mockCommandTrack(i, ut, ut), ut);
    return state.blank ?? commandReadout(state);
  });

  it("covers in view, occluded, in shadow and blank", () => {
    expect(readouts).toEqual([
      "IN VIEW · 11,508 km",
      "OCCLUDED MUN · 11,508 km",
      "IN SHADOW · 11,508 km",
      "OCCLUDED KERBIN · 11,508 km",
      "Waiting for gonogo",
    ]);
  });

  it("wraps past the last scenario", () => {
    expect(mockCommandTrack(MOCK_COMMAND_SCENARIO_COUNT, ut, ut)).toEqual(mockCommandTrack(0, ut, ut));
  });
});
