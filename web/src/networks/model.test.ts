import { CameraKind, CameraLifecycle, CommandAimKind, CommandVantageKind } from "@ksp-gonogo/kerbcast";
import type { CameraState, NetworkDefinition } from "@ksp-gonogo/kerbcast";
import { describe, expect, it } from "vitest";
import {
  disabledReasonText,
  filterVessels,
  memberStates,
  networkCamera,
  networkStatus,
  sortVesselTypes,
  vantageStateText,
  watchDescription,
} from "./model";

const def: NetworkDefinition = {
  networkId: "a",
  name: "Ground",
  vantages: [
    { vantage: { kind: CommandVantageKind.Station, station: "KSC" } },
    { vantage: { kind: CommandVantageKind.Vessel, persistentId: 7 } },
  ],
  revision: 1,
  flightId: 50,
};

function camera(vantages: { siteKey: string; state: string; active?: boolean }[], state = "ok"): CameraState {
  return {
    flightId: 50,
    kind: CameraKind.Command,
    lifecycle: CameraLifecycle.Active,
    commandCentre: {
      stationId: "",
      nodeName: "",
      displayName: "",
      body: "Kerbin",
      latitude: 0,
      longitude: 0,
      altitude: 0,
      subjectPersistentId: 0,
      trackingLevel: 3,
      targets: [],
      network: {
        name: "Ground",
        state,
        vantages: vantages.map((v) => ({ label: v.siteKey, active: false, ...v })),
      },
    },
  } as unknown as CameraState;
}

describe("vantageStateText", () => {
  it("reads every code the game sends, and an unknown one as unavailable", () => {
    expect(vantageStateText("ok").short).toBe("Clear");
    expect(vantageStateText("occluded", "Mun").short).toBe("Blocked by Mun");
    expect(vantageStateText("occluded").short).toBe("Blocked");
    expect(vantageStateText("belowHorizon").short).toBe("Below horizon");
    expect(vantageStateText("tooFar").short).toBe("Too far");
    expect(vantageStateText("unknown").short).toBe("No fix");
    expect(vantageStateText("subject").short).toBe("Is the target");
    expect(vantageStateText("gone").short).toBe("Gone");
    expect(vantageStateText("disabled")).toMatchObject({ short: "Turned off", tone: "warn" });
    expect(vantageStateText("unresolved").short).toBe("Not found");
    expect(vantageStateText("noSubject").short).toBe("No target");
    expect(vantageStateText("pending").short).toBe("Placing");
    expect(vantageStateText("unplaced").short).toBe("Placing");
    expect(vantageStateText("loaded").short).toBe("Unavailable");
    expect(vantageStateText("somethingNew").short).toBe("Unavailable");
  });
});

describe("disabledReasonText", () => {
  it("names both known reasons and falls back for a new one", () => {
    expect(disabledReasonText("extraStationsOff")).toBe("Extra ground stations are off in this game's settings");
    expect(disabledReasonText("modDisabled")).toBe("Turned off by the mod that adds it");
    expect(disabledReasonText("later")).toBe("The game has this station turned off");
  });
});

describe("memberStates", () => {
  it("takes each member's entry by position when the site keys agree", () => {
    const live = memberStates(def, camera([{ siteKey: "KSC", state: "ok" }, { siteKey: "vessel:7", state: "gone" }]));
    expect(live.map((v) => v?.state)).toEqual(["ok", "gone"]);
  });

  it("finds a member by site key when the order differs, and leaves one the camera lacks empty", () => {
    const live = memberStates(def, camera([{ siteKey: "vessel:7", state: "tooFar" }]));
    expect(live.map((v) => v?.state)).toEqual([undefined, "tooFar"]);
  });
});

describe("networkCamera and networkStatus", () => {
  it("joins by flight id and reads live, no view and not running", () => {
    const cam = camera([], "noView");
    expect(networkCamera(def, [cam])).toBe(cam);
    expect(networkStatus(cam)).toBe("noView");
    expect(networkStatus(camera([]))).toBe("live");
    expect(networkCamera({ ...def, flightId: undefined }, [cam])).toBeUndefined();
    expect(networkCamera(def, [{ ...cam, lifecycle: CameraLifecycle.Destroyed }])).toBeUndefined();
    expect(networkStatus(undefined)).toBe("notRunning");
  });
});

describe("watchDescription", () => {
  const catalog = {
    stations: [],
    vessels: [
      { persistentId: 1, name: "Kerbal X", vesselType: "Ship", body: "Kerbin", situation: "ORBITING", active: true },
      { persistentId: 2, name: "Relay", vesselType: "Relay", body: "Kerbin", situation: "ORBITING" },
    ],
  };

  it("names the active vessel, a chosen craft, or a craft that is gone", () => {
    expect(watchDescription({}, catalog).text).toBe("the active vessel (now Kerbal X)");
    expect(watchDescription({ watch: { kind: CommandAimKind.Vessel, persistentId: 2 } }, catalog)).toEqual({
      text: "Relay",
      missing: false,
    });
    expect(watchDescription({ watch: { kind: CommandAimKind.Vessel, persistentId: 9 } }, catalog).missing).toBe(true);
    expect(
      watchDescription({ watch: { kind: CommandAimKind.Vessel, persistentId: 9 } }, { ...catalog, vesselsOmitted: 3 })
        .missing,
    ).toBe(false);
  });
});

describe("filterVessels and sortVesselTypes", () => {
  const vessels = [
    { persistentId: 1, name: "A", vesselType: "Probe", body: "Kerbin", situation: "ORBITING" },
    { persistentId: 2, name: "B", vesselType: "Debris", body: "Kerbin", situation: "ORBITING" },
    { persistentId: 3, name: "C", vesselType: "Relay", body: "Kerbin", situation: "ORBITING" },
    { persistentId: 4, name: "D", vesselType: "SpaceObject", body: "Sun", situation: "ORBITING" },
  ];

  it("hides debris and space objects unless asked, and filters by type", () => {
    expect(filterVessels(vessels, { showHidden: false, type: "all" }).map((v) => v.name)).toEqual(["A", "C"]);
    expect(filterVessels(vessels, { showHidden: true, type: "all" })).toHaveLength(4);
    expect(filterVessels(vessels, { showHidden: false, type: "Relay" }).map((v) => v.name)).toEqual(["C"]);
  });

  it("orders known types first", () => {
    expect(sortVesselTypes(["Probe", "Zeppelin", "Relay", "Probe"])).toEqual(["Relay", "Probe", "Zeppelin"]);
  });
});
