import { describe, expect, it, vi } from "vitest";
import { ConnectionManager } from "./connectionManager";
import type { KerbcastClient } from "@ksp-gonogo/kerbcast";

/**
 * Minimal client double: just what ConnectionManager touches. The full
 * MockSidecar path is covered by App.test.tsx; this file pins the manager's
 * own lifecycle, which App tests miss because they render without
 * StrictMode's double-mount.
 */
function fakeClient() {
  const handlers = new Map<string, (data: unknown) => void>();
  const client = {
    connect: vi.fn().mockResolvedValue(undefined),
    disconnect: vi.fn(),
    on: vi.fn((event: string, h: (data: unknown) => void) => {
      handlers.set(event, h);
      return () => handlers.delete(event);
    }),
  };
  return { client: client as unknown as KerbcastClient, raw: client, handlers };
}

describe("ConnectionManager lifecycle", () => {
  it("start after stop connects again (StrictMode double-mount)", async () => {
    const { client, raw } = fakeClient();
    const mgr = new ConnectionManager(client);

    mgr.start();
    mgr.stop();
    mgr.start();
    await Promise.resolve();

    expect(raw.connect).toHaveBeenCalledTimes(2);
    expect(mgr.getStatus().kind).toBe("connecting");
  });

  it("stop while started cancels reconnect scheduling", async () => {
    vi.useFakeTimers();
    try {
      const { client, raw, handlers } = fakeClient();
      const mgr = new ConnectionManager(client);
      mgr.start();
      await Promise.resolve();

      handlers.get("state-change")?.("failed");
      expect(mgr.getStatus().kind).toBe("reconnecting");
      mgr.stop();

      vi.advanceTimersByTime(60_000);
      expect(raw.connect).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });
});
