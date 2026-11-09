/**
 * Mock tracking networks for ?mock=1: a site catalog of stations and craft,
 * four starting networks, and a stand-in for the game's scoring that moves
 * each running network between its members, so the cards and the feed names
 * change as a real one's would. Each running network is a command camera on
 * the MockSidecar carrying the `commandCentre.network` block the game sends,
 * and the MockSidecar answers saves and deletes as the game does.
 */

import { CameraKind, CameraLifecycle, CommandAimKind, CommandVantageKind, SiteKind } from "@ksp-gonogo/kerbcast";
import type {
  CatalogStation,
  CatalogVessel,
  CommandCentreInfo,
  CommandNetworkVantage,
  CommandVantage,
  NetworkDefinition,
  NetworkVantageSpec,
  SiteCatalogPayload,
} from "@ksp-gonogo/kerbcast";
import type { MockCameraInit, MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { MOCK_COMMAND_FRAME_HEIGHT } from "./commandScenarios";

const ACTIVE_PID = 1001;

const STATIONS: CatalogStation[] = [
  { nodeName: "KSC", displayName: "Kerbal Space Center", body: "Kerbin", latitude: -0.13, longitude: -74.6, altitude: 95, enabled: true },
  { nodeName: "Baikerbanur", displayName: "Baikerbanur", body: "Kerbin", latitude: 20.66, longitude: -146.42, altitude: 427, enabled: true },
  { nodeName: "CraterRim", displayName: "Crater Rim", body: "Kerbin", latitude: -9.01, longitude: -172.1, altitude: 3964, enabled: true },
  { nodeName: "HarvesterMassif", displayName: "Harvester Massif", body: "Kerbin", latitude: 11.95, longitude: 33.74, altitude: 2701, enabled: true },
  { nodeName: "NyeIsland", displayName: "Nye Island", body: "Kerbin", latitude: 5.36, longitude: 108.55, altitude: 95, enabled: true },
  {
    nodeName: "MesaSouth",
    displayName: "Mesa South",
    body: "Kerbin",
    latitude: -59.59,
    longitude: -36.26,
    altitude: 1240,
    enabled: false,
    disabledReason: "extraStationsOff",
  },
  { nodeName: "KK_MunFarside", displayName: "Mun Farside Dish", body: "Mun", latitude: 2.4, longitude: 172.5, altitude: 1810, enabled: true },
  {
    nodeName: "KK_MunArch",
    displayName: "Mun Arch Relay",
    body: "Mun",
    latitude: 2.47,
    longitude: 81.5,
    altitude: 2290,
    enabled: false,
    disabledReason: "modDisabled",
  },
];

const vessel = (
  persistentId: number,
  name: string,
  vesselType: string,
  body: string,
  situation: string,
  extra: Partial<CatalogVessel> = {},
): CatalogVessel => ({ persistentId, name, vesselType, body, situation, loaded: false, active: false, ...extra });

/* The active craft first, as the game lists them. */
const VESSELS: CatalogVessel[] = [
  vessel(ACTIVE_PID, "Kerbal X", "Ship", "Kerbin", "ORBITING", { loaded: true, active: true }),
  vessel(1015, "Chase Plane", "Plane", "Kerbin", "FLYING", { loaded: true }),
  vessel(1002, "Comms Relay", "Relay", "Kerbin", "ORBITING"),
  vessel(1005, "Relay K2", "Relay", "Kerbin", "ORBITING"),
  vessel(1006, "Relay K3", "Relay", "Kerbin", "ORBITING"),
  vessel(1008, "KSS Gateway", "Station", "Kerbin", "ORBITING"),
  vessel(1007, "Mun Orbiter", "Probe", "Mun", "ORBITING"),
  vessel(1012, "Rover Two", "Rover", "Mun", "LANDED"),
  vessel(1010, "Minmus Base", "Base", "Minmus", "LANDED"),
  vessel(1003, "Duna Probe", "Probe", "Sun", "ORBITING"),
  vessel(1009, "Kerbal X Debris", "Debris", "Kerbin", "SUB_ORBITAL", { loaded: true }),
  vessel(1013, "Kerbal X Fairing", "Debris", "Kerbin", "ORBITING"),
  vessel(1004, "Ast. HSJ-227", "SpaceObject", "Sun", "ORBITING"),
  vessel(1014, "First Steps", "Flag", "Mun", "LANDED"),
  vessel(1016, "Valentina Kerman", "EVA", "Kerbin", "ORBITING"),
];

export const MOCK_SITE_CATALOG: SiteCatalogPayload = { stations: STATIONS, vessels: VESSELS };

const st = (station: string, extra: Partial<NetworkVantageSpec> = {}): NetworkVantageSpec => ({
  vantage: { kind: CommandVantageKind.Station, station },
  ...extra,
});
const vs = (persistentId: number, extra: Partial<NetworkVantageSpec> = {}): NetworkVantageSpec => ({
  vantage: { kind: CommandVantageKind.Vessel, persistentId },
  ...extra,
});

/** Network cameras take flight ids from here up, clear of the other mock cameras. */
const FIRST_NETWORK_FLIGHT_ID = 401;

const INITIAL: NetworkDefinition[] = [
  {
    networkId: "n1",
    name: "Kerbin ground net",
    vantages: [st("KSC"), st("Baikerbanur"), st("CraterRim"), st("HarvesterMassif"), st("NyeIsland"), vs(ACTIVE_PID), vs(1002)],
    revision: 4,
    flightId: 401,
  },
  {
    networkId: "n2",
    name: "Mun watch",
    watch: { kind: CommandAimKind.Vessel, persistentId: 1007 },
    vantages: [
      st("KK_MunFarside"),
      st("KSC", { fallback: true }),
      st("Baikerbanur"),
      st("KK_MunArch"),
      vs(1007),
      vs(1099, { label: "Relay K4" }),
      vs(1003),
      vs(1002),
      st("OldTower", { label: "Old Tower" }),
      vs(1005),
      vs(1006),
      vs(1015),
      vs(1008),
    ],
    revision: 2,
    flightId: 402,
  },
  {
    networkId: "h1",
    name: "Polar net",
    watch: { kind: CommandAimKind.Vessel, persistentId: 1008 },
    vantages: [st("CraterRim", { fallback: true }), st("MesaSouth"), st("NyeIsland")],
    revision: 1,
    flightId: 403,
    owner: "mission-control",
  },
  {
    networkId: "n3",
    name: "Old probe watch",
    watch: { kind: CommandAimKind.Vessel, persistentId: 1099 },
    vantages: [st("KSC"), st("Baikerbanur")],
    revision: 1,
  },
];

/* Mun watch shows a member in every state the game sends, and one it may send later; Polar net has no view. */
const FIXED_STATES: Record<string, Record<string, { state: string; occluder?: string }>> = {
  n2: {
    "station:KK_MunFarside": { state: "ok" },
    "station:KSC": { state: "occluded", occluder: "Mun" },
    "station:Baikerbanur": { state: "belowHorizon" },
    "station:KK_MunArch": { state: "disabled" },
    "vessel:1007": { state: "subject" },
    "vessel:1099": { state: "gone" },
    "vessel:1003": { state: "tooFar" },
    "vessel:1002": { state: "unknown" },
    "station:OldTower": { state: "unresolved" },
    "vessel:1005": { state: "noSubject" },
    "vessel:1006": { state: "pending" },
    "vessel:1015": { state: "ok" },
    "vessel:1008": { state: "jammed" },
  },
  h1: {
    "station:CraterRim": { state: "occluded", occluder: "Kerbin" },
    "station:MesaSouth": { state: "disabled" },
    "station:NyeIsland": { state: "belowHorizon" },
  },
};

const CUT_EVERY_S = 8;
const HUES = [205, 30, 140, 280, 350, 60, 175, 95];

function keyOf(v: CommandVantage): string {
  return v.kind === CommandVantageKind.Station ? `station:${v.station}` : `vessel:${v.persistentId}`;
}

function siteKeyOf(v: CommandVantage): string {
  return v.kind === CommandVantageKind.Station ? (v.station ?? "") : `vessel:${v.persistentId}`;
}

function labelOf(spec: NetworkVantageSpec): string {
  if (spec.label) return spec.label;
  const v = spec.vantage;
  if (v.kind === CommandVantageKind.Station) return STATIONS.find((s) => s.nodeName === v.station)?.displayName ?? v.station ?? "?";
  return VESSELS.find((c) => c.persistentId === v.persistentId)?.name ?? `Craft ${v.persistentId}`;
}

function watchedPid(def: NetworkDefinition): number {
  return def.watch?.kind === CommandAimKind.Vessel ? (def.watch.persistentId ?? 0) : ACTIVE_PID;
}

/* The gates the game applies, then a rotating window of clear members so cuts come round. */
function stateOf(def: NetworkDefinition, spec: NetworkVantageSpec, index: number, t: number): { state: string; occluder?: string } {
  const v = spec.vantage;
  const fixed = FIXED_STATES[def.networkId]?.[keyOf(v)];
  if (fixed) return fixed;
  if (v.kind === CommandVantageKind.Station) {
    const s = STATIONS.find((x) => x.nodeName === v.station);
    if (!s) return { state: "unresolved" };
    if (!s.enabled) return { state: "disabled" };
  } else {
    const c = VESSELS.find((x) => x.persistentId === v.persistentId);
    if (!c) return { state: "gone" };
    if (c.persistentId === watchedPid(def)) return { state: "subject" };
  }
  const phase = Math.floor(t / CUT_EVERY_S);
  if ((index + phase) % 3 !== 0) return { state: "ok" };
  return index % 2 === 0 ? { state: "belowHorizon" } : { state: "occluded", occluder: "Kerbin" };
}

export interface MockNetworks {
  /** Every network's camera, for the starting camera list. */
  cameras: MockCameraInit[];
  /** The feed picture for a network camera, or undefined for any other flight id. */
  trackFor: (flightId: number) => MediaStreamTrack | undefined;
  /** Moves each running network on by one second of mock time. */
  tick: (ut: number) => void;
}

/**
 * Turns on networks in `sidecar` (call before connecting) and returns their
 * cameras and the hooks the driver's clock and track delivery need.
 */
export function installMockNetworks(sidecar: MockSidecar): MockNetworks {
  const live = new Map<string, { activeKey: string | null; cutAt: number }>();
  let nextFlightId = FIRST_NETWORK_FLIGHT_ID + INITIAL.length;
  let t = 0;
  let ut = 0;

  const defs = (): ReadonlyArray<NetworkDefinition> => sidecar.networks;

  const status = (def: NetworkDefinition) => {
    const states = def.vantages.map((spec, i) => ({ spec, ...stateOf(def, spec, i, t) }));
    const l = live.get(def.networkId) ?? { activeKey: null, cutAt: -999 };
    const current = states.find((s) => keyOf(s.spec.vantage) === l.activeKey);
    const firstOk = states.find((s) => s.state === "ok");
    const due = t - l.cutAt >= CUT_EVERY_S && def.networkId !== "n2";
    if (!current || (current.state !== "ok" && firstOk) || (firstOk && due && current.state === "ok")) {
      const start = current ? states.indexOf(current) + 1 : 0;
      const order = [...states.slice(start), ...states.slice(0, start)];
      const next = order.find((s) => s.state === "ok") ?? states.find((s) => s.spec.fallback) ?? current ?? states[0];
      if (next && keyOf(next.spec.vantage) !== l.activeKey) {
        l.activeKey = keyOf(next.spec.vantage);
        l.cutAt = t;
      }
    }
    live.set(def.networkId, l);
    const active = states.find((s) => keyOf(s.spec.vantage) === l.activeKey) ?? states[0];
    return { states, active, noView: !states.some((s) => s.state === "ok") };
  };

  const commandCentreFor = (def: NetworkDefinition): CommandCentreInfo => {
    const { states, active, noView } = status(def);
    const av = active?.spec.vantage;
    const station = av?.kind === CommandVantageKind.Station ? STATIONS.find((s) => s.nodeName === av.station) : undefined;
    const vantages: CommandNetworkVantage[] = states.map((s) => ({
      siteKey: siteKeyOf(s.spec.vantage),
      label: labelOf(s.spec),
      state: s.state,
      ...(s.occluder ? { occluder: s.occluder } : {}),
      active: s === active,
      fallback: s.spec.fallback === true,
    }));
    const siteKey = av ? siteKeyOf(av) : "";
    return {
      stationId: `ground:${siteKey}`,
      nodeName: siteKey,
      siteKey,
      siteKind: av?.kind === CommandVantageKind.Vessel ? SiteKind.Vessel : SiteKind.Station,
      vantagePersistentId: av?.kind === CommandVantageKind.Vessel ? (av.persistentId ?? 0) : 0,
      displayName: active ? labelOf(active.spec) : def.name,
      body: station?.body ?? "Kerbin",
      latitude: station?.latitude ?? 0,
      longitude: station?.longitude ?? 0,
      altitude: station?.altitude ?? 0,
      subjectPersistentId: watchedPid(def),
      rangeM: 1_240_000,
      trackingLevel: 3,
      targets: [],
      created: true,
      network: { name: def.name, followsActiveVessel: def.watch === undefined, state: noView ? "noView" : "ok", vantages },
    };
  };

  const trackStateFor = (cc: CommandCentreInfo) => {
    const occluded = cc.network?.state === "noView";
    const onAir = cc.network?.vantages.find((v) => v.active);
    return {
      ut,
      hasFix: true,
      rangeM: 1_240_000,
      occluded,
      occluder: occluded ? (onAir?.occluder ?? "Kerbin") : "",
      sunlit: true,
      bracketHalfPx: 8,
      frameHeightPx: MOCK_COMMAND_FRAME_HEIGHT,
      framingFits: true,
      events: [],
    };
  };

  /* A network camera is named by its network, as the game names it. */
  const initFor = (def: NetworkDefinition, flightId: number): MockCameraInit => {
    const commandCentre = commandCentreFor(def);
    return {
      flightId,
      kind: CameraKind.Command,
      cameraName: def.name,
      vesselName: def.name,
      partName: "kerbcast.commandCamera",
      partTitle: "Command camera",
      lifecycle: CameraLifecycle.Active,
      supportsZoom: true,
      fov: 30,
      fovMin: 0.02,
      fovMax: 30,
      supportsPan: false,
      renderWidth: 1024,
      renderHeight: MOCK_COMMAND_FRAME_HEIGHT,
      operatorWidth: 1024,
      operatorHeight: MOCK_COMMAND_FRAME_HEIGHT,
      encoderBitrateBps: 1_000_000,
      commandCentre,
      commandTrack: trackStateFor(commandCentre),
    };
  };

  const push = (def: NetworkDefinition) => {
    if (def.flightId === undefined) return;
    const commandCentre = commandCentreFor(def);
    sidecar.updateCamera(def.flightId, {
      cameraName: def.name,
      vesselName: def.name,
      commandCentre,
      commandTrack: trackStateFor(commandCentre),
    });
  };

  sidecar.withNetworks(INITIAL, {
    /* ?networksLocked=1 shows the drawer as it is in a scene that cannot apply changes. */
    editable: new URLSearchParams(globalThis.location?.search ?? "").get("networksLocked") !== "1",
    answerDelayMs: 300,
    onSave: (def, created) => {
      const flightId = created ? nextFlightId++ : def.flightId;
      if (flightId === undefined) return undefined;
      if (created) sidecar.addCamera(initFor(def, flightId));
      /* addCamera only registers a camera; the update announces it, or a changed one, to the page. */
      queueMicrotask(() => push({ ...def, flightId }));
      return flightId;
    },
    onDelete: (def) => {
      live.delete(def.networkId);
      if (def.flightId !== undefined) sidecar.removeCamera(def.flightId);
    },
  });
  sidecar.fireSiteCatalog(MOCK_SITE_CATALOG);

  const tracks = new Map<number, MediaStreamTrack>();
  const trackFor = (flightId: number): MediaStreamTrack | undefined => {
    const existing = tracks.get(flightId);
    if (existing) return existing;
    if (!defs().some((d) => d.flightId === flightId)) return undefined;
    const track = buildNetworkCanvas(() => {
      const def = defs().find((d) => d.flightId === flightId);
      if (!def) return null;
      const l = live.get(def.networkId);
      const idx = def.vantages.findIndex((v) => keyOf(v.vantage) === l?.activeKey);
      const spec = def.vantages[idx];
      return { name: def.name, via: spec ? labelOf(spec) : "", hue: HUES[Math.max(0, idx) % HUES.length]! };
    });
    tracks.set(flightId, track);
    return track;
  };

  return {
    cameras: INITIAL.filter((d) => d.flightId !== undefined).map((d) => initFor(d, d.flightId!)),
    trackFor,
    tick: (nowUt) => {
      t += 1;
      ut = nowUt;
      for (const def of defs()) push(def);
    },
  };
}

/* The feed picture: the network's name, the member it shows, and a hue per member so a cut is visible. */
function buildNetworkCanvas(read: () => { name: string; via: string; hue: number } | null): MediaStreamTrack {
  const width = 640;
  const height = 360;
  const canvas = document.createElement("canvas");
  canvas.width = width;
  canvas.height = height;
  const ctx = canvas.getContext("2d");
  let frame = 0;
  setInterval(() => {
    const s = read();
    if (!ctx || !s) return;
    frame = (frame + 1) % 360;
    const t = frame / 360;
    ctx.fillStyle = `hsl(${s.hue}, 45%, ${12 + t * 8}%)`;
    ctx.fillRect(0, 0, width, height);
    ctx.fillStyle = `hsl(${s.hue}, 70%, 65%)`;
    ctx.fillRect(0, Math.round(t * height), width, 6);
    ctx.fillStyle = "#ffffff";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = "bold 22px sans-serif";
    ctx.fillText(s.name, width / 2, height * 0.74);
    ctx.font = "16px sans-serif";
    ctx.fillText(`seen from ${s.via}`, width / 2, height * 0.84);
    ctx.fillRect(width / 2 - 2, height / 2 - 2, 4, 4);
  }, 1000 / 24);
  type HasCaptureStream = HTMLCanvasElement & { captureStream(fps?: number): MediaStream };
  const track = (canvas as HasCaptureStream).captureStream(24).getVideoTracks()[0];
  if (!track) throw new Error("captureStream returned no video tracks");
  return track;
}
