import type { LensLevelsPayload } from "@ksp-gonogo/kerbcast";
import { useEffect, useState } from "react";
import { useKerbcastClient } from "../context";

/**
 * The command camera lenses in the loaded save, from the client's
 * `lensLevels`: the ground lens per Tracking Station level, the space lens
 * steps research unlocks, and each lens's zoom floor. Null until a sidecar
 * reports them, and always from a game too old to send them.
 */
export function useLensLevels(): LensLevelsPayload | null {
  const client = useKerbcastClient();
  const [lensLevels, setLensLevels] = useState(() => client.lensLevels);

  useEffect(() => {
    setLensLevels(client.lensLevels);
    return client.on("settings-change", () => setLensLevels(client.lensLevels));
  }, [client]);

  return lensLevels;
}
