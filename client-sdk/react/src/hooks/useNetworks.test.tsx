/**
 * useNetworks and useSiteCatalog, through the real KerbcastClient with only
 * the WebRTC transport faked by the SDK's MockSidecar.
 */

import { CommandVantageKind, KerbcastClient } from "@ksp-gonogo/kerbcast";
import type { NetworkDefinition, SiteCatalogPayload } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { act, cleanup, render } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";
import { KerbcastProvider } from "../context";
import { useNetworks } from "./useNetworks";
import { useSiteCatalog } from "./useSiteCatalog";

let networks: NetworkDefinition[] = [];
let catalog: SiteCatalogPayload | null = null;

function Probe(): null {
  networks = useNetworks();
  catalog = useSiteCatalog();
  return null;
}

async function connected(sidecar: MockSidecar): Promise<KerbcastClient> {
  const client = new KerbcastClient(
    { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
    sidecar.createTransport(),
  );
  await act(async () => {
    await client.connect([], { slots: 4 });
    sidecar.open();
    sidecar.setConnectionState("connected");
  });
  return client;
}

const ground: NetworkDefinition = {
  networkId: "a",
  name: "Ground",
  vantages: [{ vantage: { kind: CommandVantageKind.Station, station: "KSC" } }],
  revision: 1,
};

afterEach(() => {
  cleanup();
  networks = [];
  catalog = null;
});

describe("useNetworks", () => {
  it("starts with what the client holds and follows every change", async () => {
    const sidecar = new MockSidecar().withNetworks([ground]);
    const client = await connected(sidecar);
    render(
      <KerbcastProvider client={client}>
        <Probe />
      </KerbcastProvider>,
    );
    expect(networks.map((n) => n.name)).toEqual(["Ground"]);

    await act(async () => {
      await client.saveNetwork({ name: "Polar", vantages: ground.vantages });
    });
    expect(networks.map((n) => n.name)).toEqual(["Ground", "Polar"]);

    await act(async () => {
      await client.deleteNetwork("a");
    });
    expect(networks.map((n) => n.name)).toEqual(["Polar"]);

    await act(async () => {
      await client.disconnect();
    });
  });
});

describe("useSiteCatalog", () => {
  it("is null until the first catalog and then follows it", async () => {
    const sidecar = new MockSidecar();
    const client = await connected(sidecar);
    render(
      <KerbcastProvider client={client}>
        <Probe />
      </KerbcastProvider>,
    );
    expect(catalog).toBeNull();

    await act(async () => {
      sidecar.fireSiteCatalog({
        stations: [],
        vessels: [
          { persistentId: 1, name: "Relay", vesselType: "Relay", body: "Kerbin", situation: "ORBITING" },
        ],
      });
    });
    expect(catalog?.vessels?.[0].name).toBe("Relay");

    await act(async () => {
      await client.disconnect();
    });
  });
});
