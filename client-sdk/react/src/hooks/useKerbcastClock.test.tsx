/**
 * useKerbcastClock capture-clock subscription.
 *
 * Renders a probe through the real hook + real KerbcastClient with only the
 * WebRTC transport faked by the SDK's MockSidecar. Asserts the hook seeds from
 * the client and re-renders on each `settings-state` push.
 */

import { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { act, cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { KerbcastProvider } from "../context";
import { useKerbcastClock } from "./useKerbcastClock";

let lastClock: ReturnType<typeof useKerbcastClock> | undefined;

function ClockProbe(): null {
  lastClock = useKerbcastClock();
  return null;
}

async function connected(): Promise<{
  client: KerbcastClient;
  sidecar: MockSidecar;
}> {
  const sidecar = new MockSidecar();
  const client = new KerbcastClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  await act(async () => {
    await client.connect([], { slots: 4 });
    sidecar.open();
    sidecar.setConnectionState("connected");
  });
  return { client, sidecar };
}

afterEach(() => {
  cleanup();
  lastClock = undefined;
});

describe("useKerbcastClock", () => {
  it("starts with no clock and updates on a settings-state push", async () => {
    const { client, sidecar } = await connected();

    render(
      <KerbcastProvider client={client}>
        <ClockProbe />
      </KerbcastProvider>,
    );

    /* Open sent a bare settings-state (no capture fields) => no clock. */
    expect(lastClock).toEqual({ captureUt: null, epoch: 0, warpRate: 1 });

    act(() => {
      sidecar.fireSettingsState({
        throttleMainScreen: false,
        captureUt: 500.25,
        captureEpoch: 2,
        timeWarpRate: 10,
      });
    });

    expect(lastClock).toEqual({ captureUt: 500.25, epoch: 2, warpRate: 10 });

    await act(async () => {
      await client.disconnect();
    });
  });
});
