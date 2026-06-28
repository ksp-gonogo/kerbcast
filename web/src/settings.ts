/**
 * Persistent settings for the kerbcast web page.
 * Backed by localStorage. Each getter reads the stored value on every call
 * so callers always see the latest.
 */

export type ThemePreference = "auto" | "light" | "dark";

const KEY_THEME = "kerbcast:theme";
const KEY_DEBUG = "kerbcast:debug";
const KEY_SHOW_STATIC = "kerbcast:showStatic";

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

/**
 * Show-static preference. Returns `null` when no explicit value has been
 * stored (auto mode: resolved from `prefers-reduced-motion` at the call
 * site), `true`/`false` for an explicit override.
 */
export function loadShowStatic(): boolean | null {
  const raw = localStorage.getItem(KEY_SHOW_STATIC);
  if (raw === null) return null;
  return raw !== "false";
}

export function saveShowStatic(enabled: boolean): void {
  localStorage.setItem(KEY_SHOW_STATIC, String(enabled));
}
