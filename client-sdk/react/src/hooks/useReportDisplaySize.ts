import { useEffect, useId } from "react";
import { useKerbcastDisplaySizes } from "../context";

/*
 * Debounce + bucket for self-measured display-size reporting. The first
 * measurement after mount reports promptly (so a feed never sits at a stale
 * resolution until it is resized); later churn is debounced. Sizes are ceiled
 * to a BUCKET_PX multiple: that meets-the-minimum-need (never under-serve),
 * yields an even value (H.264-friendly), and gives a material-change threshold
 * so a feed re-reports only when a resize crosses a bucket boundary.
 */
const DEBOUNCE_MS = 200;
const BUCKET_PX = 16;

function bucket(px: number): number {
  const b = Math.ceil(px / BUCKET_PX) * BUCKET_PX;
  return b < BUCKET_PX ? BUCKET_PX : b;
}

export interface ReportDisplaySizeOptions {
  /**
   * Report while mounted. Default true. Set false for a fixed-resolution feed
   * that should not drive auto-resolution. This is a RESOLUTION concern only,
   * independent of whether the feed shows its action UI.
   */
  enabled?: boolean;
  /**
   * Report a square (side x side, the min edge) rather than the raw w x h.
   * Face cams are square; part cams are not.
   */
  square?: boolean;
}

/**
 * Self-measure a feed element and drive the client's per-consumer
 * `reportDisplaySize` (via the context display-size registry, which collapses
 * multiple feeds of the same camera to one MAX report). Attach `ref` to the
 * element whose rendered pixel box is the display size. Reporting follows the
 * mount lifecycle: on unmount / `flightId` change / `enabled` flip it
 * deregisters, and the sidecar's clear-on-departure (unsubscribe) does the
 * server-side clear.
 */
export function useReportDisplaySize(
  flightId: number | null,
  ref: React.RefObject<Element | null>,
  { enabled = true, square = false }: ReportDisplaySizeOptions = {},
): void {
  const displaySizes = useKerbcastDisplaySizes();
  const instanceId = useId();

  useEffect(() => {
    if (flightId === null || !enabled) return;
    const el = ref.current;
    if (!el) return;

    let timer: ReturnType<typeof setTimeout> | null = null;
    let lastW = -1;
    let lastH = -1;
    let firstReported = false;

    const report = (width: number, height: number) => {
      const side = square ? Math.min(width, height) : 0;
      const bw = bucket(square ? side : width);
      const bh = bucket(square ? side : height);
      if (bw === lastW && bh === lastH) return;
      lastW = bw;
      lastH = bh;
      displaySizes.report(flightId, instanceId, bw, bh);
    };

    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (!entry) return;
      const { width, height } = entry.contentRect;
      if (!firstReported) {
        firstReported = true;
        report(width, height);
        return;
      }
      if (timer !== null) clearTimeout(timer);
      timer = setTimeout(() => report(width, height), DEBOUNCE_MS);
    });
    observer.observe(el);

    return () => {
      observer.disconnect();
      if (timer !== null) clearTimeout(timer);
      displaySizes.clear(flightId, instanceId);
    };
  }, [displaySizes, flightId, ref, enabled, square, instanceId]);
}
