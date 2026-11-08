import { useCallback, useMemo, useState } from "react";

const STORAGE_PREFIX = "kerbcast:target-brackets:";

/* A preference saved under `legacyKey` moves to `key` the first time it is read. */
function readPreference(key: string, legacyKey: string | null): boolean {
  try {
    const storage = globalThis.localStorage;
    if (!storage) return true;
    const value = storage.getItem(STORAGE_PREFIX + key);
    if (value !== null || legacyKey === null || legacyKey === key) return value !== "0";
    const legacy = storage.getItem(STORAGE_PREFIX + legacyKey);
    if (legacy === null) return true;
    storage.setItem(STORAGE_PREFIX + key, legacy);
    storage.removeItem(STORAGE_PREFIX + legacyKey);
    return legacy !== "0";
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
 * command camera, which reads as on and ignores toggles. A preference saved
 * under `legacyKey` (the key an older version used) is moved to `key`.
 */
export function useTargetBracketsPreference(key: string | null, legacyKey: string | null = null): [boolean, () => void] {
  const [overrides, setOverrides] = useState<Record<string, boolean>>({});
  const stored = useMemo(() => (key === null ? true : readPreference(key, legacyKey)), [key, legacyKey]);
  const on = key !== null && key in overrides ? overrides[key]! : stored;
  const toggle = useCallback(() => {
    if (key === null) return;
    const next = !on;
    writePreference(key, next);
    setOverrides((o) => ({ ...o, [key]: next }));
  }, [key, on]);
  return [on, toggle];
}
