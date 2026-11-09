import type { CameraState, NetworkDefinition } from "@ksp-gonogo/kerbcast";
import { describe, expect, it } from "vitest";
import { isNetworkCamera } from "./networkVantage";

type Camera = Pick<CameraState, "flightId" | "commandCentre">;

const station = { stationId: "ground:KSC", nodeName: "KSC", displayName: "KSC" } as NonNullable<Camera["commandCentre"]>;
const definition = (flightId?: number): NetworkDefinition => ({ networkId: "n1", name: "Net", vantages: [], flightId });

describe("isNetworkCamera", () => {
  it("is true while the camera list describes a network", () => {
    const network = { name: "Net", state: "ok", vantages: [] } as unknown as typeof station.network;
    expect(isNetworkCamera({ flightId: 7, commandCentre: { ...station, network } })).toBe(true);
  });

  it("is true across the whole saved network id range without a network object", () => {
    expect(isNetworkCamera({ flightId: 0xffff0000, commandCentre: station })).toBe(true);
    expect(isNetworkCamera({ flightId: 0xfffffffe })).toBe(true);
  });

  it("is false just outside the saved network id range", () => {
    expect(isNetworkCamera({ flightId: 0xfffeffff, commandCentre: station })).toBe(false);
    expect(isNetworkCamera({ flightId: 0xffffffff, commandCentre: station })).toBe(false);
  });

  it("recognises a mod's network by its definition while its rig is down", () => {
    expect(isNetworkCamera({ flightId: 7, commandCentre: station }, [definition(7)])).toBe(true);
    expect(isNetworkCamera({ flightId: 8, commandCentre: station }, [definition(7), definition()])).toBe(false);
  });

  it("is false for a part camera", () => {
    expect(isNetworkCamera({ flightId: 42 })).toBe(false);
  });
});
