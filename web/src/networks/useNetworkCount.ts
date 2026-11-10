import { useKerbcastClient, useNetworks } from "@ksp-gonogo/kerbcast-react";
import { useCallback, useSyncExternalStore } from "react";

/**
 * How many camera networks the game has, or null when the connected
 * sidecar does not take network saves (an older game), so Settings can hide
 * its entry. Support is learnt from `hello`, which the camera list follows.
 */
export function useNetworkCount(): number | null {
  const client = useKerbcastClient();
  const networks = useNetworks();
  const subscribe = useCallback(
    (onChange: () => void) => {
      const offs = [client.on("cameras-change", onChange), client.on("networks", onChange), client.on("state-change", onChange)];
      return () => offs.forEach((off) => off());
    },
    [client],
  );
  const supported = useSyncExternalStore(subscribe, () => client.supportsNetworks);
  return supported ? networks.length : null;
}
