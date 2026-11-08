import type { Anchor, KerbcastClient } from "@ksp-gonogo/kerbcast";

/**
 * A client holds one anchor set per camera, so feeds showing the same
 * camera each claim their anchors here and the camera gets them all: the
 * first claim of an id wins, and the set stops at the client's cap.
 */
const claims = new WeakMap<KerbcastClient, Map<number, Map<symbol, readonly Anchor[]>>>();

/** Add `anchors` to `flightId`'s set until the returned release is called. */
export function claimAnchors(
  client: KerbcastClient,
  flightId: number,
  anchors: readonly Anchor[],
): () => void {
  let byFlight = claims.get(client);
  if (!byFlight) {
    byFlight = new Map();
    claims.set(client, byFlight);
  }
  let feeds = byFlight.get(flightId);
  if (!feeds) {
    feeds = new Map();
    byFlight.set(flightId, feeds);
  }
  const token = Symbol("anchors");
  feeds.set(token, anchors);
  publish(client, flightId, feeds);
  return () => {
    if (!feeds.delete(token)) return;
    if (feeds.size === 0) byFlight.delete(flightId);
    publish(client, flightId, feeds);
  };
}

function publish(client: KerbcastClient, flightId: number, feeds: Map<symbol, readonly Anchor[]>): void {
  const ids = new Map<string, string>();
  const merged: Anchor[] = [];
  let dropped = 0;
  for (const list of feeds.values()) {
    for (const a of list) {
      const seen = ids.get(a.id);
      if (seen !== undefined) {
        if (seen !== JSON.stringify(a)) {
          console.warn(`[kerbcast] two feeds on camera ${flightId} set different anchors with id ${a.id}; the first is kept`);
        }
        continue;
      }
      if (merged.length >= client.maxAnchorsPerCamera) {
        dropped++;
        continue;
      }
      ids.set(a.id, JSON.stringify(a));
      merged.push(a);
    }
  }
  if (dropped > 0) {
    console.warn(`[kerbcast] ${dropped} anchors over the ${client.maxAnchorsPerCamera} per camera left out`);
  }
  if (merged.length === 0) {
    void client.setAnchors(flightId, null).catch(() => {});
    return;
  }
  client.setAnchors(flightId, merged).catch((err: unknown) => {
    console.warn("[kerbcast] anchors refused", err);
  });
}
