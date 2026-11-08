/**
 * Plain helpers for the tracking networks drawer: the words each wire code
 * reads as, how a saved member is keyed, and how a definition joins the live
 * camera that runs it.
 */

import { CameraLifecycle, CommandAimKind, CommandVantageKind } from "@ksp-gonogo/kerbcast";
import type {
  CameraState,
  CatalogVessel,
  CommandNetworkVantage,
  CommandVantage,
  NetworkDefinition,
  SiteCatalogPayload,
} from "@ksp-gonogo/kerbcast";

/** The most members a network takes. */
export const MAX_VANTAGES = 64;

/** The longest name a network takes. */
export const MAX_NAME_LENGTH = 48;

/** What a blank name saves as. */
export const DEFAULT_NETWORK_NAME = "Tracking network";

/** Craft types the vessel list hides unless the viewer asks for them. */
export const HIDDEN_VESSEL_TYPES: ReadonlySet<string> = new Set([
  "Debris",
  "SpaceObject",
  "Flag",
  "EVA",
  "DroppedPart",
  "DeployedScienceController",
  "DeployedSciencePart",
]);

/** The order type chips appear in; any type not named here follows, alphabetically. */
const TYPE_ORDER = ["Relay", "Probe", "Station", "Ship", "Lander", "Base", "Rover", "Plane"];

const TYPE_LABELS: Record<string, { one: string; many: string }> = {
  SpaceObject: { one: "Asteroid", many: "asteroids and comets" },
  EVA: { one: "Kerbal", many: "kerbals" },
  DroppedPart: { one: "Dropped part", many: "dropped parts" },
  DeployedScienceController: { one: "Science", many: "science stations" },
  DeployedSciencePart: { one: "Science part", many: "science parts" },
  Debris: { one: "Debris", many: "debris" },
};

/** A craft type as a chip or a row shows it. */
export function typeLabel(type: string): string {
  return TYPE_LABELS[type]?.one ?? type;
}

export function sortVesselTypes(types: Iterable<string>): string[] {
  const rank = (t: string) => {
    const i = TYPE_ORDER.indexOf(t);
    return i < 0 ? TYPE_ORDER.length : i;
  };
  return [...new Set(types)].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b));
}

export type Tone = "ok" | "muted" | "warn" | "bad" | "accent";

export interface StateText {
  short: string;
  long: string;
  tone: Tone;
}

/** Plain words for a member's state code, as a card shows them. A code this page does not know reads as unable to see. */
export function vantageStateText(state: string, occluder?: string): StateText {
  switch (state) {
    case "ok":
      return { short: "Clear", long: "Can see the craft", tone: "ok" };
    case "occluded":
      return {
        short: occluder ? `Blocked by ${occluder}` : "Blocked",
        long: occluder ? `${occluder} is between this member and the craft` : "Something is between this member and the craft",
        tone: "muted",
      };
    case "belowHorizon":
      return { short: "Below horizon", long: "The craft is below this member's horizon", tone: "muted" };
    case "tooFar":
      return { short: "Too far", long: "The craft is out of this member's range", tone: "muted" };
    case "unknown":
      return { short: "No fix", long: "This member does not know where the craft is", tone: "muted" };
    case "subject":
      return { short: "Is the target", long: "A craft never watches itself. It is used again when the target changes", tone: "muted" };
    case "gone":
      return { short: "Gone", long: "This craft no longer exists", tone: "bad" };
    case "disabled":
      return { short: "Turned off", long: "The game has this ground station turned off. It joins when the game turns it on", tone: "warn" };
    case "unresolved":
      return { short: "Not found", long: "This game has no such place. It may come from a mod that is not installed", tone: "bad" };
    case "noSubject":
      return { short: "No target", long: "There is no craft to watch", tone: "muted" };
    case "pending":
    case "unplaced":
      return { short: "Placing", long: "The game is still placing this member", tone: "muted" };
    default:
      return { short: "Unavailable", long: `Cannot see the craft (${state})`, tone: "muted" };
  }
}

/** Why the game has a station off, in words. */
export function disabledReasonText(reason: string | undefined): string {
  switch (reason) {
    case "extraStationsOff":
      return "Extra ground stations are off in this game's settings";
    case "modDisabled":
      return "Turned off by the mod that adds it";
    default:
      return "The game has this station turned off";
  }
}

/** A refused save or delete, in words. */
export function requestErrorText(code: string): string {
  switch (code) {
    case "notOwner":
      return "Only the mod that made this network can change it";
    case "notFound":
      return "This network no longer exists";
    case "noVantages":
      return "Pick at least one ground station or craft";
    case "tooManyVantages":
      return `A network takes at most ${MAX_VANTAGES} members`;
    case "badVantage":
      return "The game refused one of the members";
    case "badAim":
      return "The game refused the craft to watch";
    case "badName":
      return `The name must be at most ${MAX_NAME_LENGTH} characters`;
    case "badNumber":
      return "The game refused one of the settings";
    case "writeFailed":
      return "The game could not write its networks file";
    case "timeout":
      return "The game did not answer. Try again";
    case "unsupported":
      return "This game cannot save networks";
    case "notConnected":
      return "Not connected to the game";
    default:
      return `The game refused it (${code})`;
  }
}

const SITUATIONS: Record<string, string> = {
  LANDED: "Landed",
  SPLASHED: "Splashed",
  PRELAUNCH: "On the pad",
  FLYING: "Flying",
  SUB_ORBITAL: "Sub-orbital",
  ORBITING: "Orbiting",
  ESCAPING: "Escaping",
  DOCKED: "Docked",
};

export function situationText(s: string): string {
  return SITUATIONS[s] ?? s;
}

export function formatLatLon(lat: number, lon: number): string {
  const ns = lat >= 0 ? "N" : "S";
  const ew = lon >= 0 ? "E" : "W";
  return `${Math.abs(lat).toFixed(1)}°${ns} ${Math.abs(lon).toFixed(1)}°${ew}`;
}

export const stationKey = (nodeName: string): string => `station:${nodeName}`;
export const vesselKey = (persistentId: number): string => `vessel:${persistentId}`;

/** A member's identity in the form: one tick box per station or craft. */
export function vantageKey(v: CommandVantage): string {
  switch (v.kind) {
    case CommandVantageKind.Station:
      return stationKey(v.station ?? "");
    case CommandVantageKind.Vessel:
      return vesselKey(v.persistentId ?? 0);
    default:
      return `${v.kind}:${JSON.stringify(v)}`;
  }
}

/** The site key the camera reports for a member: a station's node name, or `vessel:` and the craft's persistentId. */
export function vantageSiteKey(v: CommandVantage): string | null {
  switch (v.kind) {
    case CommandVantageKind.Station:
      return v.station ?? "";
    case CommandVantageKind.Vessel:
      return `vessel:${v.persistentId ?? 0}`;
    default:
      return null;
  }
}

/** A member's name from the catalog, or its saved label, or a plain fallback. */
export function vantageName(v: CommandVantage, label: string | undefined, catalog: SiteCatalogPayload | null): string {
  if (label) return label;
  if (v.kind === CommandVantageKind.Station) {
    return catalog?.stations?.find((s) => s.nodeName === v.station)?.displayName ?? v.station ?? "Ground station";
  }
  if (v.kind === CommandVantageKind.Vessel) {
    return catalog?.vessels?.find((c) => c.persistentId === v.persistentId)?.name ?? `Craft ${v.persistentId ?? 0}`;
  }
  if (v.kind === CommandVantageKind.Surface) return v.body ? `A point on ${v.body}` : "A surface point";
  return v.site || "Launch site";
}

/** The live camera a network runs as in this scene, if it is running. */
export function networkCamera(def: NetworkDefinition, cameras: ReadonlyArray<CameraState>): CameraState | undefined {
  if (def.flightId === undefined) return undefined;
  const cam = cameras.find((c) => c.flightId === def.flightId);
  if (!cam || cam.lifecycle === CameraLifecycle.Destroyed) return undefined;
  return cam;
}

/**
 * Each saved member's live state, in the saved order. The camera lists its
 * vantages in the same order, so a member takes the entry at its own
 * position when the site keys agree there, and otherwise the entry with its
 * site key, which covers a list the game has not caught up with yet.
 */
export function memberStates(
  def: NetworkDefinition,
  camera: CameraState | undefined,
): (CommandNetworkVantage | undefined)[] {
  const live = camera?.commandCentre?.network?.vantages ?? [];
  return def.vantages.map((spec, i) => {
    const key = vantageSiteKey(spec.vantage);
    const atPosition = live[i];
    if (atPosition && (key === null || atPosition.siteKey === key)) return atPosition;
    if (key !== null) return live.find((v) => v.siteKey === key);
    return undefined;
  });
}

export type NetworkStatus = "live" | "noView" | "notRunning";

export function networkStatus(camera: CameraState | undefined): NetworkStatus {
  const network = camera?.commandCentre?.network;
  if (!network) return "notRunning";
  return network.state === "ok" ? "live" : "noView";
}

/**
 * What a network watches, in words. `missing` is set when it watches a
 * chosen craft the catalog no longer lists, so the card can say it has no
 * target. A catalog that left craft out to stay small cannot say that.
 */
export function watchDescription(
  def: Pick<NetworkDefinition, "watch">,
  catalog: SiteCatalogPayload | null,
): { text: string; missing: boolean } {
  const vessels = catalog?.vessels ?? [];
  const watch = def.watch;
  if (!watch) {
    const active = vessels.find((v) => v.active);
    return { text: active ? `the active vessel (now ${active.name})` : "the active vessel", missing: false };
  }
  if (watch.kind !== CommandAimKind.Vessel) return { text: "a point its maker chose", missing: false };
  const craft = vessels.find((v) => v.persistentId === watch.persistentId);
  if (craft) return { text: craft.name, missing: false };
  const missing = catalog !== null && !catalog.vesselsOmitted;
  return { text: missing ? "a craft that no longer exists" : `craft ${watch.persistentId ?? 0}`, missing };
}

/** The craft the vessel list shows under the current filter. */
export function filterVessels(
  vessels: ReadonlyArray<CatalogVessel>,
  opts: { showHidden: boolean; type: string },
): CatalogVessel[] {
  return vessels
    .filter((v) => opts.showHidden || !HIDDEN_VESSEL_TYPES.has(v.vesselType))
    .filter((v) => opts.type === "all" || v.vesselType === opts.type);
}

/** Plural for a type chip's "Add all" link: "relays", "probes". */
export function typePlural(type: string): string {
  const known = TYPE_LABELS[type];
  if (known) return known.many;
  const lower = type.toLowerCase();
  return lower.endsWith("s") ? lower : `${lower}s`;
}
