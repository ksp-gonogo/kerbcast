import type { KerbcastClient } from "@jonpepler/kerbcast";
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
// Context shape
// ---------------------------------------------------------------------------

interface KerbcastContextValue {
  client: KerbcastClient;
  subscriptions: KerbcastSubscriptions;
}

const KerbcastContext = createContext<KerbcastContextValue | null>(null);

// ---------------------------------------------------------------------------
// Provider
// ---------------------------------------------------------------------------

export interface KerbcastProviderProps {
  client: KerbcastClient;
  /** Override the subscription manager. Defaults to a refcounted per-client impl. */
  subscriptions?: KerbcastSubscriptions;
  children: ReactNode;
}

/**
 * Provide a `KerbcastClient` (and optional subscriptions override) to the
 * component subtree. Hooks and `CameraFeed` read both from this context.
 */
export function KerbcastProvider({
  client,
  subscriptions: subscriptionsProp,
  children,
}: KerbcastProviderProps): React.JSX.Element {
  // Create default subscriptions once per client. A ref so the object is
  // stable across renders even if the parent re-renders.
  const defaultSubsRef = useRef<KerbcastSubscriptions | null>(null);
  if (defaultSubsRef.current === null) {
    defaultSubsRef.current = createClientSubscriptions(client);
  }

  const value = useMemo(
    () => ({
      client,
      subscriptions: subscriptionsProp ?? defaultSubsRef.current!,
    }),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [client, subscriptionsProp],
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
