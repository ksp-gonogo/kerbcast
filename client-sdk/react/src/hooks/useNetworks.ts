import type { NetworkDefinition } from "@ksp-gonogo/kerbcast";
import { useEffect, useState } from "react";
import { useKerbcastClient } from "../context";

/**
 * Every tracking network the game has, saved or made by a mod, from the
 * client's `networks` event. Empty until the first definitions arrive.
 * Pair with `client.supportsNetworks` to tell "none yet" from "not offered".
 */
export function useNetworks(): NetworkDefinition[] {
  const client = useKerbcastClient();
  const [networks, setNetworks] = useState(() => client.networks());

  useEffect(() => {
    setNetworks(client.networks());
    return client.on("networks", (next) => setNetworks(next));
  }, [client]);

  return networks;
}
