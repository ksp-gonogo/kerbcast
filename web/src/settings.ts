/**
 * Persistent settings for the kerbcam web page.
 * Backed by localStorage. Each getter reads the stored value on every call
 * so callers always see the latest.
 */

export type ThemePreference = "auto" | "light" | "dark";

const KEY_THEME = "kerbcam:theme";
const KEY_DEBUG = "kerbcam:debug";
const KEY_STATIC_ON_STALE = "kerbcam:staticOnStale";

export function loadTheme(): ThemePreference {
  const raw = localStorage.getItem(KEY_THEME);
  if (raw === "light" || raw === "dark" || raw === "auto") return raw;
  return "auto";
}

export function saveTheme(theme: ThemePreference): void {
  localStorage.setItem(KEY_THEME, theme);
}

/** Apply data-theme to <html>. Call whenever theme changes. */
export function applyTheme(theme: ThemePreference): void {
  if (theme === "auto") {
    document.documentElement.removeAttribute("data-theme");
  } else {
    document.documentElement.setAttribute("data-theme", theme);
  }
}

export function loadDebug(): boolean {
  return localStorage.getItem(KEY_DEBUG) === "true";
}

export function saveDebug(enabled: boolean): void {
  localStorage.setItem(KEY_DEBUG, String(enabled));
}

/** Static-on-stale-feeds: defaults ON; only an explicit "false" disables. */
export function loadStaticOnStale(): boolean {
  return localStorage.getItem(KEY_STATIC_ON_STALE) !== "false";
}

export function saveStaticOnStale(enabled: boolean): void {
  localStorage.setItem(KEY_STATIC_ON_STALE, String(enabled));
}
