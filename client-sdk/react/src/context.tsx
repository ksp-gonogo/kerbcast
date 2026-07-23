import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import {
  createContext,
  useContext,
  useMemo,
  useRef,
  type ReactNode,
} from "react";

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

/** Build the default refcounted subscriptions implementation for a client. */
export function createClientSubscriptions(
  client: KerbcastClient,
): KerbcastSubscriptions {
  const refcounts = new Map<number, number>();
  return {
    acquire(flightId: number): void {
      const count = (refcounts.get(flightId) ?? 0) + 1;
      refcounts.set(flightId, count);
      if (count === 1) {
        client.subscribe(flightId).catch(() => {});
      }
    },
    release(flightId: number): void {
      const count = (refcounts.get(flightId) ?? 1) - 1;
      if (count <= 0) {
        refcounts.delete(flightId);
        client.unsubscribe(flightId).catch(() => {});
      } else {
        refcounts.set(flightId, count);
      }
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
  // Create default subscriptions once per client. A ref so the object is
  // stable across renders even if the parent re-renders.
  const defaultSubsRef = useRef<KerbcastSubscriptions | null>(null);
  if (defaultSubsRef.current === null) {
    defaultSubsRef.current = createClientSubscriptions(client);
  }

  // Same for the display-size registry: one per client, stable across renders.
  const defaultSizesRef = useRef<KerbcastDisplaySizes | null>(null);
  if (defaultSizesRef.current === null) {
    defaultSizesRef.current = createClientDisplaySizes(client);
  }

  const value = useMemo(
    () => ({
      client,
      subscriptions: subscriptionsProp ?? defaultSubsRef.current!,
      displaySizes: displaySizesProp ?? defaultSizesRef.current!,
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [client, subscriptionsProp, displaySizesProp],
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
