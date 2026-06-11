/*
 * Camera display labels, shared by every widget that lets the operator pick a
 * camera.
 *
 * Hullcam names cameras non-uniquely: its DockingPortCameraPatch labels EVERY
 * docking-port camera "NavCam", colliding with the dedicated NavCam part, so a
 * raw `cameraName` list shows two indistinguishable "NavCam" rows and the
 * docking-port feed looks missing. When a name is shared by more than one
 * camera in the list, we append the part title (e.g. "NavCam - Clamp-O-Tron
 * Docking Port Jr.") to tell them apart; non-colliding cameras are unchanged.
 *
 * Identical parts defeat that too (nine BoosterCams share both name and part
 * title), so labels that still collide get a "#N" suffix, numbered in
 * ascending flightId order. flightId is stable per part, so the numbers do
 * not shuffle between sessions or when unrelated cameras come and go.
 */

export interface LabelableCamera {
  flightId: number;
  cameraName: string;
  partTitle?: string | null;
}

/**
 * Build a labeller closed over the current camera list. The returned function
 * maps a camera to its display name, disambiguating only the cameras whose
 * `cameraName` collides with another in the same list.
 *
 * The label does NOT include the vessel name. Call sites append that (and any
 * " - signal lost" suffix) themselves.
 */
export function buildCameraLabeler<T extends LabelableCamera>(
  cameras: readonly T[],
): (camera: T) => string {
  const counts = new Map<string, number>();
  for (const c of cameras) {
    counts.set(c.cameraName, (counts.get(c.cameraName) ?? 0) + 1);
  }
  const baseLabel = (camera: T): string =>
    (counts.get(camera.cameraName) ?? 0) > 1 &&
    camera.partTitle &&
    camera.partTitle !== camera.cameraName
      ? `${camera.cameraName} - ${camera.partTitle}`
      : camera.cameraName;

  /* Number the cameras whose base label still collides: per colliding label,
     sort by flightId and hand out #1..#N. */
  const byLabel = new Map<string, T[]>();
  for (const c of cameras) {
    const label = baseLabel(c);
    const group = byLabel.get(label);
    if (group) group.push(c);
    else byLabel.set(label, [c]);
  }
  const ordinals = new Map<number, number>();
  for (const group of byLabel.values()) {
    if (group.length < 2) continue;
    [...group]
      .sort((a, b) => a.flightId - b.flightId)
      .forEach((c, i) => ordinals.set(c.flightId, i + 1));
  }

  return (camera: T): string => {
    const label = baseLabel(camera);
    const ordinal = ordinals.get(camera.flightId);
    return ordinal === undefined ? label : `${label} #${ordinal}`;
  };
}
