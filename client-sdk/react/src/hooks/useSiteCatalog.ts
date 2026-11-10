import type { SiteCatalogPayload } from "@ksp-gonogo/kerbcast";
import { useEffect, useState } from "react";
import { useKerbcastClient } from "../context";

/**
 * The ground stations and craft the game lists, from the client's
 * `site-catalog`. Null until the first arrives, and always from a sidecar
 * that never sends one.
 */
export function useSiteCatalog(): SiteCatalogPayload | null {
  const client = useKerbcastClient();
  const [catalog, setCatalog] = useState(() => client.siteCatalog());

  useEffect(() => {
    setCatalog(client.siteCatalog());
    return client.on("site-catalog", (next) => setCatalog(next));
  }, [client]);

  return catalog;
}
