import { type RefObject, useEffect, useRef } from "react";

/**
 * Moves focus into a panel while `open` and, when it closes, hands focus back
 * to whatever held it before, or to `fallback` when that has left the page. A
 * panel closed by clicking something else leaves focus on that something.
 */
export function useDialogFocus(
  panel: RefObject<HTMLElement | null>,
  open: boolean,
  fallback?: () => HTMLElement | null,
): void {
  const opener = useRef<HTMLElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const active = document.activeElement;
    if (active instanceof HTMLElement && active !== document.body && !panel.current?.contains(active)) {
      opener.current = active;
    }
    panel.current?.focus({ preventScroll: true });
    return () => {
      /* Focus inside the panel drops to the body as the panel unmounts. */
      if (document.activeElement && document.activeElement !== document.body) return;
      const target = opener.current?.isConnected ? opener.current : (fallback?.() ?? null);
      target?.focus({ preventScroll: true });
    };
  }, [open, panel, fallback]);
}
