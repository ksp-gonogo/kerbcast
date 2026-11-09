import type { CameraState, CommandCentreInfo, FrameCommandState, NetworkDefinition } from "@ksp-gonogo/kerbcast";

/** What a tracking network camera shows the craft from, on the displayed frame. */
export interface NetworkVantageReading {
  /** The vantage's label, as the host set it or the site's own name. */
  siteLabel: string;
  /** `ok`, or `noView` when no vantage can see the craft. An unrecognised value reads as `noView`. */
  networkState: string;
  /** The network's own name, which stays the same through every cut. */
  networkName: string;
}

/** Every word a network feed's name uses, kept together so a change of wording is one edit. */
export const NETWORK_FEED_TEXT = {
  name: (siteLabel: string, networkName: string) => `${siteLabel} via ${networkName}`,
} as const;

/** A network feed's name: "Crater Rim via Kerbin ground net". */
export function networkFeedName(siteLabel: string, networkName: string): string {
  return NETWORK_FEED_TEXT.name(siteLabel, networkName);
}

const SAVED_NETWORK_FIRST_ID = 0xffff0000;
const SAVED_NETWORK_LAST_ID = 0xfffffffe;

/**
 * Whether a camera is a tracking network, whether or not it has a picture.
 * `commandCentre.network` is only sent while the camera's rig is up, so a
 * network at rest is recognised by its reserved flight id (a saved network)
 * or its definition (a mod's, from {@link useNetworks}).
 */
export function isNetworkCamera(
  camera: Pick<CameraState, "flightId" | "commandCentre">,
  networks: readonly NetworkDefinition[] = [],
): boolean {
  return (
    !!camera.commandCentre?.network ||
    (camera.flightId >= SAVED_NETWORK_FIRST_ID && camera.flightId <= SAVED_NETWORK_LAST_ID) ||
    networks.some((n) => n.flightId === camera.flightId)
  );
}

/** Whether a network state means the craft is seen. Anything but `ok` is no view. */
export function networkSees(networkState: string): boolean {
  return networkState === "ok";
}

/**
 * The vantage a network feed shows, from the displayed frame when it says,
 * else from the camera list (which may trail the picture by up to a poll).
 * Null for a feed that is no network camera, or before any label is known.
 */
export function readNetworkVantage(
  frame: FrameCommandState | null | undefined,
  centre: CommandCentreInfo | null | undefined,
  cameraName?: string,
): NetworkVantageReading | null {
  const network = centre?.network;
  if (!network && frame?.networkState == null) return null;
  const active = network?.vantages.find((v) => v.active);
  const siteLabel = frame?.siteLabel || active?.label || centre?.displayName || "";
  if (!siteLabel) return null;
  return {
    siteLabel,
    networkState: frame?.networkState ?? network?.state ?? "ok",
    networkName: network?.name || cameraName || "",
  };
}
