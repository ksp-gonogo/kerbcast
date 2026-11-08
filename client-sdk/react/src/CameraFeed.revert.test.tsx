/**
 * A pinned feed whose camera leaves the list and comes back under the same
 * flight id with a new attach, as a scene reload does to every camera. The
 * sidecar starts the returned camera with no size from anyone, so the feed's
 * size must reach it again whatever order the release, the return and the
 * subscribe come in. Runs the real client against the MockSidecar, whose
 * `displaySizeFor` follows the sidecar's rules for what it keeps.
 */

import { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { act, cleanup, render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { CameraFeed, type CameraStreamHook } from "./CameraFeed";
import { KERBCAST_RELEASE_GRACE_MS, KerbcastProvider } from "./context";

const TILE = { width: 368, height: 207 };
const originalResizeObserver = globalThis.ResizeObserver;
const clients: KerbcastClient[] = [];

beforeEach(() => {
  vi.useFakeTimers();
  /* A browser measures an element as soon as it is observed. */
  globalThis.ResizeObserver = class {
    constructor(private readonly cb: ResizeObserverCallback) {}
    observe() {
      this.cb([{ contentRect: TILE }] as ResizeObserverEntry[], this as unknown as ResizeObserver);
    }
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterEach(() => {
  cleanup();
  for (const c of clients) c.disconnect();
  clients.length = 0;
  globalThis.ResizeObserver = originalResizeObserver;
  vi.useRealTimers();
  vi.restoreAllMocks();
});

const camera = (attach: number) => ({ flightId: 42, maxWidth: 1024, maxHeight: 576, attach });

async function connected() {
  const sidecar = new MockSidecar();
  sidecar.addCamera(camera(1));
  const client = new KerbcastClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  clients.push(client);
  await act(async () => {
    await client.connect([], { slots: 4 });
  });
  await act(async () => {
    sidecar.open();
    sidecar.setConnectionState("connected");
  });
  return { client, sidecar };
}

/* In steps, so React renders what each timer set before the next one is due, as it would in a browser. */
async function wait(ms: number) {
  for (let left = ms; left >= 0; left -= 100) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(Math.min(100, left));
    });
  }
}

async function leave(sidecar: MockSidecar) {
  await act(async () => sidecar.setCameras([]));
}

async function comeBack(sidecar: MockSidecar) {
  await act(async () => sidecar.setCameras([camera(2)]));
  await wait(0);
}

/** A host that binds slots itself, so the feed only shows and measures. */
const hostStream: CameraStreamHook = () => null;

describe("CameraFeed: a pinned camera that leaves the list and comes back", () => {
  describe("with the default subscriptions", () => {
    async function mounted() {
      const { client, sidecar } = await connected();
      render(
        <KerbcastProvider client={client}>
          <CameraFeed flightId={42} />
        </KerbcastProvider>,
      );
      await wait(0);
      const asked = sidecar.displaySizeFor(42);
      expect(asked).toBeDefined();
      expect(sidecar.slotMidFor(42)).toBeDefined();
      return { client, sidecar, asked };
    }

    it("reports its size again after the feed released the camera and the client unsubscribed", async () => {
      const { sidecar, asked } = await mounted();
      await leave(sidecar);
      await wait(KERBCAST_RELEASE_GRACE_MS * 2 + 500);
      expect(sidecar.lastCommand("unsubscribe", 42)).toBeDefined();
      expect(sidecar.slotMidFor(42)).toBeUndefined();
      await comeBack(sidecar);
      expect(sidecar.slotMidFor(42)).toBeDefined();
      expect(sidecar.displaySizeFor(42)).toEqual(asked);
    });

    it("reports its size again when the camera is back before the feed lets it go", async () => {
      const { sidecar, asked } = await mounted();
      await leave(sidecar);
      await wait(KERBCAST_RELEASE_GRACE_MS - 500);
      await comeBack(sidecar);
      expect(sidecar.displaySizeFor(42)).toEqual(asked);
    });

    it("reports its size again when the camera is back while its release waits out the grace", async () => {
      const { sidecar, asked } = await mounted();
      await leave(sidecar);
      await wait(KERBCAST_RELEASE_GRACE_MS + 500);
      await comeBack(sidecar);
      await wait(KERBCAST_RELEASE_GRACE_MS * 2);
      expect(sidecar.slotMidFor(42)).toBeDefined();
      expect(sidecar.displaySizeFor(42)).toEqual(asked);
    });
  });

  describe("with a host that binds the camera's slot itself", () => {
    /*
     * The host follows the camera list: it frees the slot of a camera that
     * leaves, or binds a returned one afresh with an unsubscribe and a
     * subscribe. Its listener runs before or after the provider's, depending on
     * which was built first.
     */
    async function mounted(opts: { rebind: "free-on-leave" | "unsubscribe-and-subscribe"; hostFirst: boolean }) {
      const { client, sidecar } = await connected();
      await act(async () => {
        await client.subscribe(42);
      });
      let listed = true;
      const follow = () => {
        const present = client.cameras.some((c) => c.flightId === 42);
        if (present === listed) return;
        listed = present;
        if (!present) {
          if (opts.rebind === "free-on-leave") void client.unsubscribe(42);
          return;
        }
        if (opts.rebind === "free-on-leave") void client.subscribe(42);
        else void client.unsubscribe(42).then(() => client.subscribe(42));
      };
      if (opts.hostFirst) client.on("cameras-change", follow);
      render(
        <KerbcastProvider client={client}>
          <CameraFeed flightId={42} useStream={hostStream} />
        </KerbcastProvider>,
      );
      if (!opts.hostFirst) client.on("cameras-change", follow);
      await wait(0);
      const asked = sidecar.displaySizeFor(42);
      expect(asked).toBeDefined();
      return { client, sidecar, asked };
    }

    for (const hostFirst of [true, false]) {
      const order = hostFirst ? "before" : "after";
      for (const away of [KERBCAST_RELEASE_GRACE_MS - 500, KERBCAST_RELEASE_GRACE_MS * 2 + 500]) {
        it(`keeps its size when the host binds the returned camera afresh (host listener ${order}, away ${away} ms)`, async () => {
          const { sidecar, asked } = await mounted({ rebind: "unsubscribe-and-subscribe", hostFirst });
          await leave(sidecar);
          await wait(away);
          await comeBack(sidecar);
          expect(sidecar.slotMidFor(42)).toBeDefined();
          expect(sidecar.displaySizeFor(42)).toEqual(asked);
        });

        it(`keeps its size when the host frees the camera as it leaves and binds it when it is back (host listener ${order}, away ${away} ms)`, async () => {
          const { sidecar, asked } = await mounted({ rebind: "free-on-leave", hostFirst });
          await leave(sidecar);
          await wait(away);
          await comeBack(sidecar);
          expect(sidecar.slotMidFor(42)).toBeDefined();
          expect(sidecar.displaySizeFor(42)).toEqual(asked);
        });
      }
    }

    it("keeps its size when the host binds the camera again before it is listed", async () => {
      const { client, sidecar, asked } = await mounted({ rebind: "free-on-leave", hostFirst: true });
      await leave(sidecar);
      await wait(KERBCAST_RELEASE_GRACE_MS - 500);
      await act(async () => {
        await client.unsubscribe(42);
        await client.subscribe(42);
      });
      await comeBack(sidecar);
      expect(sidecar.displaySizeFor(42)).toEqual(asked);
    });
  });
});
