import type { CommandTrackState } from "@ksp-gonogo/kerbcast";

/** Capture height the mock command camera reports its bracket size against. */
export const MOCK_COMMAND_FRAME_HEIGHT = 576;

const RANGE_M = 11_507_905;

type Scenario = Omit<CommandTrackState, "ut" | "events" | "frameHeightPx">;

/*
 * One per bracket state worth eyeballing: standalone in view and occluded,
 * then governed in shadow, occluded with a wide framing margin, and blanked.
 */
const SCENARIOS: ReadonlyArray<Scenario> = [
  {
    hasFix: true,
    rangeM: RANGE_M,
    occluded: false,
    occluder: "",
    sunlit: true,
    bracketHalfPx: 6,
    framingFits: true,
  },
  {
    hasFix: true,
    rangeM: RANGE_M,
    occluded: true,
    occluder: "Mun",
    sunlit: true,
    bracketHalfPx: 6,
    framingFits: true,
  },
  {
    hasFix: true,
    rangeM: RANGE_M,
    occluded: false,
    occluder: "",
    sunlit: false,
    bracketHalfPx: 6,
    framingFits: true,
    authority: { owner: "mission-control" },
  },
  {
    hasFix: true,
    rangeM: RANGE_M,
    occluded: true,
    occluder: "Kerbin",
    sunlit: true,
    bracketHalfPx: 40,
    framingFits: true,
    authority: { owner: "mission-control" },
  },
  {
    hasFix: true,
    rangeM: RANGE_M,
    occluded: false,
    occluder: "",
    sunlit: true,
    bracketHalfPx: 300,
    framingFits: false,
    authority: { owner: "mission-control" },
    blank: "Waiting for mission control",
  },
];

export const MOCK_COMMAND_SCENARIO_COUNT = SCENARIOS.length;

/**
 * The mock command track for scenario `index` (wrapping), evaluated at `ut`,
 * with one transition event recorded at `switchedAtUt`.
 */
export function mockCommandTrack(index: number, ut: number, switchedAtUt: number): CommandTrackState {
  const track = SCENARIOS[index % SCENARIOS.length]!;
  return {
    ...track,
    ut,
    frameHeightPx: MOCK_COMMAND_FRAME_HEIGHT,
    events: [
      {
        ut: switchedAtUt,
        occluded: track.occluded,
        occluder: track.occluder,
        sunlit: track.sunlit,
        blank: track.blank,
      },
    ],
  };
}
