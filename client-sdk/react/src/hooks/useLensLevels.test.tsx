/**
 * useLensLevels through a real KerbcastClient, with only the WebRTC
 * transport faked by the SDK's MockSidecar.
 */

import { KerbcastClient, type LensLevelsPayload } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { act, cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { KerbcastProvider } from "../context";
import { useLensLevels } from "./useLensLevels";

let last: LensLevelsPayload | null | undefined;

function Probe(): null {
  last = useLensLevels();
  return null;
}

const LENS: LensLevelsPayload = {
  gameMode: "science",
  opticsOn: true,
  trackingGated: false,
  trackingLevel: 1,
  trackingLensArcsec: 1,
  trackingFovMinDeg: 0.08,
  trackingLevels: [{ level: 1, lensArcsec: 15, fovMinDeg: 1.2 }],
  spaceSteps: [],
};

afterEach(() => {
  cleanup();
  last = undefined;
});

describe("useLensLevels", () => {
  it("is null until the sidecar reports lenses, then follows settings-state", async () => {
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

    render(
      <KerbcastProvider client={client}>
        <Probe />
      </KerbcastProvider>,
    );
    expect(last).toBeNull();

    act(() => sidecar.setLensLevels(LENS));
    expect(last).toEqual(LENS);

    await act(async () => {
      await client.disconnect();
    });
  });
});
