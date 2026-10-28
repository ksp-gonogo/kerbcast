import { describe, expect, it } from "vitest";
import type { CommandCentreInfo, CommandTrackState, FrameCommandState } from "./__generated__/types";
import {
  COMMAND_CENTRE_MAX_ENTRIES,
  COMMAND_CENTRE_MAX_LAG_UT,
  COMMAND_TRACK_LABELS,
  CommandCentreHistory,
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

const AUTHORITY = { owner: "mission-control" };

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
    events: [
      { ut: 990, occluded: true, occluder: "Mun", sunlit: true },
      { ut: 999, occluded: false, occluder: "", sunlit: true, blank: "Waiting for mission control" },
    ],
  });

  it("uses the event in force at the displayed UT for discrete fields", () => {
    const s = effectiveCommandTrack(withEvents, 995);
    expect(s.camera).toBe("occluded");
    expect(s.occluder).toBe("Mun");
    expect(s.blank).toBeNull();
    expect(s.rangeM).toBe(11_507_905.1);
    expect(s.authority).toBeNull();
  });

  it("carries the event's blank", () => {
    expect(effectiveCommandTrack(withEvents, 999.5).blank).toBe("Waiting for mission control");
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

  it("reads the same under an authority once the displayed frame reaches it", () => {
    const locked = track({ occluded: true, occluder: "Mun", authority: AUTHORITY });
    const s = effectiveCommandTrack(locked, locked.ut);
    expect(commandReadout(s)).toBe(`OCCLUDED MUN${COMMAND_TRACK_LABELS.separator}11,508 km`);
  });

  it("holds a locked snapshot's fix until the displayed frame reaches it", () => {
    const locked = track({ authority: AUTHORITY });
    expect(effectiveCommandTrack(locked, locked.ut - 1)).toMatchObject({
      hasFix: false,
      rangeM: null,
      bracketHalfPx: null,
      camera: "noFix",
    });
    expect(effectiveCommandTrack(track(), 0).hasFix).toBe(true);
  });

  it("uses a locked snapshot as sent when no displayed UT is known", () => {
    const locked = track({ authority: AUTHORITY });
    for (const ut of [undefined, null]) {
      expect(effectiveCommandTrack(locked, ut)).toMatchObject({
        hasFix: true,
        rangeM: 11_507_905.1,
        bracketHalfPx: 4.2,
        camera: "inView",
      });
    }
  });

  it("shows NO FIX without a range when there is no fix", () => {
    const s = effectiveCommandTrack(track({ hasFix: false, rangeM: undefined }));
    expect(commandReadout(s)).toBe("NO FIX");
  });
});

describe("per-frame command state", () => {
  const frame: FrameCommandState = {
    hasFix: true,
    rangeM: 1500,
    bracketHalfPx: 9,
    frameHeightPx: 720,
    framingFits: true,
    occluded: true,
    occluder: "Mun",
    sunlit: true,
    subjectPersistentId: 42,
    authority: AUTHORITY,
    refusal: undefined,
  };

  it("wins over the status snapshot and its events", () => {
    const locked = track({ hasFix: false, rangeM: undefined, bracketHalfPx: undefined, authority: AUTHORITY });
    const s = effectiveCommandTrack(locked, 1000, frame);
    expect(s).toMatchObject({
      hasFix: true,
      rangeM: 1500,
      bracketHalfPx: 9,
      frameHeightPx: 720,
      occluded: true,
      occluder: "Mun",
      camera: "occluded",
      authority: AUTHORITY,
      fromFrame: true,
      refusal: null,
    });
  });

  it("carries a refusal and a blank", () => {
    const s = effectiveCommandTrack(track(), 1000, {
      ...frame,
      hasFix: false,
      rangeM: undefined,
      blank: "Authority not responding",
      refusal: "refused",
      refusalCode: "noSignal",
      refusalText: "No signal",
    });
    expect(s).toMatchObject({
      camera: "noFix",
      blank: "Authority not responding",
      refusal: "refused",
      refusalCode: "noSignal",
      refusalText: "No signal",
    });
  });

  it("says when the camera is waiting for a target, and not otherwise", () => {
    expect(effectiveCommandTrack(track(), 1000, frame).waitingForTarget).toBe(false);
    const s = effectiveCommandTrack(track(), 1000, {
      ...frame,
      hasFix: false,
      rangeM: undefined,
      subjectPersistentId: 0,
      waitingForTarget: true,
    });
    expect(s).toMatchObject({ waitingForTarget: true, camera: "noFix" });
  });

  it("falls back to the snapshot without one", () => {
    expect(effectiveCommandTrack(track(), 1000, null).fromFrame).toBeUndefined();
  });
});

describe("CommandCentreHistory", () => {
  const centre = (ut: number | undefined, rangeM: number): CommandCentreInfo => ({
    stationId: "ground:KSC",
    nodeName: "KSC",
    siteKey: "KSC",
    siteKind: "station" as CommandCentreInfo["siteKind"],
    vantagePersistentId: 0,
    displayName: "KSC",
    body: "Kerbin",
    latitude: 0,
    longitude: 0,
    altitude: 0,
    subjectPersistentId: 42,
    rangeM,
    trackingLevel: 3,
    targets: [],
    created: false,
    ut,
  });

  it("shows nothing newer than the displayed UT", () => {
    const h = new CommandCentreHistory();
    h.record(1, centre(100, 1000));
    h.record(1, centre(200, 2000));
    expect(h.at(1, 99)).toBeNull();
    expect(h.at(1, 150)?.rangeM).toBe(1000);
    expect(h.at(1, 200)?.rangeM).toBe(2000);
    expect(h.at(1)?.rangeM).toBe(2000);
    expect(h.at(2, 500)).toBeNull();
  });

  it("keeps the UT unchanged content first had", () => {
    const h = new CommandCentreHistory();
    h.record(1, centre(100, 1000));
    h.record(1, centre(300, 1000));
    expect(h.at(1, 150)?.ut).toBe(100);
  });

  it("applies a list from an older game at once", () => {
    const h = new CommandCentreHistory();
    h.record(1, centre(undefined, 1000));
    expect(h.at(1, 0)?.rangeM).toBe(1000);
  });

  it("drops what a load or revert undid", () => {
    const h = new CommandCentreHistory();
    h.record(1, centre(100, 1000));
    h.record(1, centre(200, 2000));
    h.record(1, centre(150, 3000));
    expect(h.at(1, 250)?.rangeM).toBe(3000);
    expect(h.at(1, 120)?.rangeM).toBe(1000);
  });

  it("shows unchanged content again from the UT a revert went back to", () => {
    const h = new CommandCentreHistory();
    h.record(1, centre(1000, 500));
    h.record(1, centre(400, 500));
    expect(h.at(1, 450)?.rangeM).toBe(500);
    expect(h.at(1, 450)?.ut).toBe(400);
  });

  it("keeps lists back to the display lag, with one at or before it", () => {
    const h = new CommandCentreHistory(100);
    for (let ut = 0; ut <= 1000; ut += 10) h.record(1, centre(ut, ut));
    expect(h.at(1, 900)?.rangeM).toBe(900);
    expect(h.at(1, 905)?.rangeM).toBe(900);
    expect(h.at(1, 899)).toBeNull();
    expect(h.size(1)).toBe(11);
    expect(COMMAND_CENTRE_MAX_LAG_UT).toBe(7200);
  });

  it("keeps no more than its entry cap inside the window", () => {
    const h = new CommandCentreHistory(COMMAND_CENTRE_MAX_LAG_UT, 5);
    for (let ut = 1; ut <= 10; ut++) h.record(1, centre(ut, ut));
    expect(h.size(1)).toBe(5);
    expect(h.at(1, 6)?.rangeM).toBe(6);
    expect(h.at(1, 5)).toBeNull();
    expect(COMMAND_CENTRE_MAX_ENTRIES).toBe(600);
  });

  it("shares unchanged targets between lists", () => {
    const h = new CommandCentreHistory();
    const target = (id: number, rangeM: number) =>
      ({ persistentId: id, vesselName: `c${id}`, vesselType: "Ship", rangeM, eligibility: "ok", eligibilityText: "" }) as CommandCentreInfo["targets"][number];
    h.record(1, { ...centre(100, 1), targets: [target(1, 10), target(2, 20)] });
    h.record(1, { ...centre(200, 1), targets: [target(1, 10), target(2, 30)] });
    const a = h.at(1, 100)!.targets;
    const b = h.at(1, 200)!.targets;
    expect(b[0]).toBe(a[0]);
    expect(b[1]).not.toBe(a[1]);
  });

  it("forgets cameras", () => {
    const h = new CommandCentreHistory();
    h.record(1, centre(100, 1));
    h.record(2, centre(100, 1));
    h.retain([2]);
    expect(h.at(1)).toBeNull();
    expect(h.at(2)).not.toBeNull();
    h.clear();
    expect(h.at(2)).toBeNull();
  });
});
