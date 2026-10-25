import { useCallback, useMemo, useState } from "react";

const STORAGE_PREFIX = "kerbcast:target-brackets:";

function readPreference(key: string): boolean {
  try {
    return globalThis.localStorage?.getItem(STORAGE_PREFIX + key) !== "0";
  } catch {
    return true;
  }
}

function writePreference(key: string, on: boolean): void {
  try {
    if (on) globalThis.localStorage?.removeItem(STORAGE_PREFIX + key);
    else globalThis.localStorage?.setItem(STORAGE_PREFIX + key, "0");
  } catch {
    /* Storage blocked or full: the toggle still works for this session. */
  }
}

/**
 * Whether target brackets are shown for one command camera, remembered per
 * camera in this browser. Defaults on. `key` is null for a feed with no
 * command camera, which reads as on and ignores toggles.
 */
export function useTargetBracketsPreference(key: string | null): [boolean, () => void] {
  const [overrides, setOverrides] = useState<Record<string, boolean>>({});
  const stored = useMemo(() => (key === null ? true : readPreference(key)), [key]);
  const on = key !== null && key in overrides ? overrides[key]! : stored;
  const toggle = useCallback(() => {
    if (key === null) return;
    const next = !on;
    writePreference(key, next);
    setOverrides((o) => ({ ...o, [key]: next }));
  }, [key, on]);
  return [on, toggle];
}
