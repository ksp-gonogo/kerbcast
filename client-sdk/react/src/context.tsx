import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { createContext, useContext, useMemo, type ReactNode } from "react";

// ---------------------------------------------------------------------------
// Subscriptions seam
// ---------------------------------------------------------------------------

/**
 * Lifecycle controller for per-camera slot subscriptions. The default
 * implementation refcounts so multiple widgets sharing the same flightId
 * share one slot.
 */
export interface KerbcastSubscriptions {
  acquire(flightId: number): void;
  release(flightId: number): void;
}

/**
 * How long a camera's slot is kept after its last holder releases it, in
 * milliseconds. A feed that remounts inside this window (a layout change, a
 * scene switch re-rendering the tree) takes the slot back with its stream
 * still flowing, instead of freeing it and waiting for a fresh keyframe.
 */
export const KERBCAST_RELEASE_GRACE_MS = 2000;

export interface ClientSubscriptionsOptions {
  /**
   * Delay before a released camera is unsubscribed. Defaults to
   * {@link KERBCAST_RELEASE_GRACE_MS}; 0 unsubscribes at once.
   */
  releaseGraceMs?: number;
}

/**
 * Build the default refcounted subscriptions implementation for a client.
 * When the last holder of a camera releases it the unsubscribe waits
 * `releaseGraceMs`, and an acquire inside that window keeps the slot with no
 * message sent either way.
 */
export function createClientSubscriptions(
  client: KerbcastClient,
  options: ClientSubscriptionsOptions = {},
): KerbcastSubscriptions {
  const graceMs = options.releaseGraceMs ?? KERBCAST_RELEASE_GRACE_MS;
  const refcounts = new Map<number, number>();
  const pendingRelease = new Map<number, ReturnType<typeof setTimeout>>();
  const stopWatchingSlot = new Map<number, () => void>();

  function unsubscribeNow(flightId: number): void {
    stopWatchingSlot.get(flightId)?.();
    stopWatchingSlot.delete(flightId);
    client.unsubscribe(flightId).catch(() => {});
  }

  /*
   * A slot kept for a feed that might come back is worth less than one a
   * mounted feed is waiting for. While any held camera waits for a slot,
   * releases skip the grace, and a camera that starts waiting ends every
   * grace under way so the slots it frees can carry it.
   */
  function slotWanted(): boolean {
    for (const flightId of refcounts.keys()) {
      if (client.camera(flightId).waitingForSlot) return true;
    }
    return false;
  }

  function watchSlot(flightId: number): void {
    if (stopWatchingSlot.has(flightId)) return;
    stopWatchingSlot.set(
      flightId,
      client.camera(flightId).on("waiting", (waiting) => {
        if (waiting) flushPendingReleases();
      }),
    );
  }

  /*
   * A connection that ends takes this client's slots with it, so a camera
   * waiting out its grace has nothing left to keep. Unsubscribe it now, which
   * also clears the client's own record that it wants the camera.
   */
  function flushPendingReleases(): void {
    for (const [flightId, timer] of pendingRelease) {
      clearTimeout(timer);
      unsubscribeNow(flightId);
    }
    pendingRelease.clear();
  }

  /*
   * A reconnect clears every slot binding on the client and the sidecar starts
   * the new peer empty, so re-subscribe each camera still held. Listeners fire
   * in registration order and the provider builds this before the display-size
   * registry, so subscriptions go out first. The sidecar treats a repeat
   * subscribe for a bound camera as a re-announce, and a rejection (for
   * example a full slot pool) is swallowed like every other subscribe here.
   * A camera only waiting out its grace is not held, so it is dropped rather
   * than bound again on the new peer.
   */
  client.on("control-open", () => {
    flushPendingReleases();
    for (const flightId of refcounts.keys()) {
      client.subscribe(flightId).catch(() => {});
    }
  });
  client.on("state-change", (state) => {
    if (state === "disconnected" || state === "failed") flushPendingReleases();
  });

  return {
    acquire(flightId: number): void {
      const count = (refcounts.get(flightId) ?? 0) + 1;
      refcounts.set(flightId, count);
      const pending = pendingRelease.get(flightId);
      if (pending !== undefined) {
        clearTimeout(pending);
        pendingRelease.delete(flightId);
        return;
      }
      if (count === 1) {
        watchSlot(flightId);
        client.subscribe(flightId).catch(() => {});
      }
    },
    release(flightId: number): void {
      const count = (refcounts.get(flightId) ?? 1) - 1;
      if (count > 0) {
        refcounts.set(flightId, count);
        return;
      }
      refcounts.delete(flightId);
      if (pendingRelease.has(flightId)) return;
      if (graceMs <= 0 || slotWanted()) {
        unsubscribeNow(flightId);
        return;
      }
      pendingRelease.set(
        flightId,
        setTimeout(() => {
          pendingRelease.delete(flightId);
          unsubscribeNow(flightId);
        }, graceMs),
      );
    },
  };
}

// ---------------------------------------------------------------------------
// Display-size reporting seam
// ---------------------------------------------------------------------------

/**
 * Collects each mounted feed's self-measured display size and drives the
 * client's per-consumer `reportDisplaySize`. Multiple feeds can show the SAME
 * camera at once (e.g. a tiny avatar and a spotlight of the same kerbal); the
 * sidecar keys the auto-resolution max per CONSUMER, so those would clobber
 * each other last-writer-wins. This collapses them at the client edge: it
 * keeps the MAX display size across every mounted reporter for a flightId and
 * sends one report per max-change. Co-located with the subscription refcount
 * (same per-flightId lifetime) so one object owns everything this client is
 * doing with camera N.
 */
export interface KerbcastDisplaySizes {
  /** Register/update a reporter's measured size for a camera. */
  report(flightId: number, instanceId: string, width: number, height: number): void;
  /** Deregister a reporter (on unmount / flightId change). */
  clear(flightId: number, instanceId: string): void;
}

/** Build the default per-flightId MAX display-size registry for a client. */
export function createClientDisplaySizes(
  client: KerbcastClient,
): KerbcastDisplaySizes {
  // flightId -> (instanceId -> measured size)
  const perCamera = new Map<number, Map<string, { w: number; h: number }>>();
  // Last size reported to the sidecar per flightId, so we send only on change.
  const lastSent = new Map<number, { w: number; h: number }>();

  function recompute(flightId: number): void {
    const instances = perCamera.get(flightId);
    if (!instances || instances.size === 0) {
      // No reporters left. Drop bookkeeping and send nothing: the subscription
      // release (unsubscribe -> sidecar remove_track -> forget_display_size)
      // clears the sidecar side. Sending a spurious size here would fight it.
      perCamera.delete(flightId);
      lastSent.delete(flightId);
      return;
    }
    let maxW = 0;
    let maxH = 0;
    for (const { w, h } of instances.values()) {
      if (w > maxW) maxW = w;
      if (h > maxH) maxH = h;
    }
    const prev = lastSent.get(flightId);
    if (prev && prev.w === maxW && prev.h === maxH) return;
    lastSent.set(flightId, { w: maxW, h: maxH });
    client.reportDisplaySize(flightId, maxW, maxH).catch(() => {});
  }

  /*
   * The sidecar forgets a peer's display sizes when the peer leaves, so after
   * a reconnect nothing it holds matches lastSent. Re-send the current MAX for
   * every camera that still has reporters, once the new control channel is
   * open and can take messages.
   */
  client.on("control-open", () => {
    lastSent.clear();
    for (const flightId of [...perCamera.keys()]) recompute(flightId);
  });

  return {
    report(flightId: number, instanceId: string, width: number, height: number): void {
      let instances = perCamera.get(flightId);
      if (!instances) {
        instances = new Map();
        perCamera.set(flightId, instances);
      }
      instances.set(instanceId, { w: width, h: height });
      recompute(flightId);
    },
    clear(flightId: number, instanceId: string): void {
      const instances = perCamera.get(flightId);
      if (!instances) return;
      instances.delete(instanceId);
      recompute(flightId);
    },
  };
}

// ---------------------------------------------------------------------------
// Context shape
// ---------------------------------------------------------------------------

interface KerbcastContextValue {
  client: KerbcastClient;
  subscriptions: KerbcastSubscriptions;
  displaySizes: KerbcastDisplaySizes;
}

const KerbcastContext = createContext<KerbcastContextValue | null>(null);

// ---------------------------------------------------------------------------
// Provider
// ---------------------------------------------------------------------------

export interface KerbcastProviderProps {
  client: KerbcastClient;
  /** Override the subscription manager. Defaults to a refcounted per-client impl. */
  subscriptions?: KerbcastSubscriptions;
  /** Override the display-size registry. Defaults to a per-flightId MAX impl. */
  displaySizes?: KerbcastDisplaySizes;
  children: ReactNode;
}

/*
 * One default subscriptions manager and display-size registry per client, kept
 * for the client's lifetime rather than a provider's. A provider that remounts
 * with the same client then finds the camera releases its feeds just made
 * still in their grace, and two providers sharing a client share one refcount
 * instead of each unsubscribing the other's cameras. A provider handed a new
 * client gets that client's own pair; the old client's pending releases run
 * out on it as they would have.
 */
const clientDefaults = new WeakMap<
  KerbcastClient,
  { subscriptions: KerbcastSubscriptions; displaySizes: KerbcastDisplaySizes }
>();

function defaultsFor(client: KerbcastClient): {
  subscriptions: KerbcastSubscriptions;
  displaySizes: KerbcastDisplaySizes;
} {
  let defaults = clientDefaults.get(client);
  if (!defaults) {
    defaults = {
      subscriptions: createClientSubscriptions(client),
      displaySizes: createClientDisplaySizes(client),
    };
    clientDefaults.set(client, defaults);
  }
  return defaults;
}

/**
 * Provide a `KerbcastClient` (and optional subscriptions override) to the
 * component subtree. Hooks and `CameraFeed` read both from this context.
 */
export function KerbcastProvider({
  client,
  subscriptions: subscriptionsProp,
  displaySizes: displaySizesProp,
  children,
}: KerbcastProviderProps): React.JSX.Element {
  const defaults = defaultsFor(client);
  const value = useMemo(
    () => ({
      client,
      subscriptions: subscriptionsProp ?? defaults.subscriptions,
      displaySizes: displaySizesProp ?? defaults.displaySizes,
    }),
    [client, defaults, subscriptionsProp, displaySizesProp],
  );

  return (
    <KerbcastContext.Provider value={value}>{children}</KerbcastContext.Provider>
  );
}

// ---------------------------------------------------------------------------
// Hooks
// ---------------------------------------------------------------------------

/**
 * Return the `KerbcastClient` from the nearest `KerbcastProvider`.
 * Throws if called outside a provider.
 */
export function useKerbcastClient(): KerbcastClient {
  const ctx = useContext(KerbcastContext);
  if (!ctx) {
    throw new Error("useKerbcastClient must be used inside a KerbcastProvider");
  }
  return ctx.client;
}

/**
 * Return the `KerbcastSubscriptions` from the nearest `KerbcastProvider`.
 * Throws if called outside a provider.
 */
export function useKerbcastSubscriptions(): KerbcastSubscriptions {
  const ctx = useContext(KerbcastContext);
  if (!ctx) {
    throw new Error(
      "useKerbcastSubscriptions must be used inside a KerbcastProvider",
    );
  }
  return ctx.subscriptions;
}

/**
 * Return the `KerbcastDisplaySizes` registry from the nearest
 * `KerbcastProvider`. Throws if called outside a provider.
 */
export function useKerbcastDisplaySizes(): KerbcastDisplaySizes {
  const ctx = useContext(KerbcastContext);
  if (!ctx) {
    throw new Error(
      "useKerbcastDisplaySizes must be used inside a KerbcastProvider",
    );
  }
  return ctx.displaySizes;
}
