/**
 * The tracking networks drawer, driven by the page's own mock networks
 * through a real KerbcastClient and MockSidecar.
 */

import { CommandVantageKind, KerbcastClient } from "@ksp-gonogo/kerbcast";
import type { CatalogStation, NetworkDefinition } from "@ksp-gonogo/kerbcast";
import { MockSidecar } from "@ksp-gonogo/kerbcast/testing";
import { KerbcastProvider } from "@ksp-gonogo/kerbcast-react";
import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { App } from "../App";
import { installMockNetworks } from "../mock/networks";
import { NetworksPanel } from "./NetworksPanel";

const clients: KerbcastClient[] = [];

async function connect(sidecar: MockSidecar): Promise<KerbcastClient> {
  sidecar.withSlots(["0", "1", "2", "3"]);
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
  return client;
}

async function mockGame(): Promise<{ client: KerbcastClient; sidecar: MockSidecar }> {
  const sidecar = new MockSidecar();
  const networks = installMockNetworks(sidecar);
  for (const cam of networks.cameras) sidecar.addCamera(cam);
  return { client: await connect(sidecar), sidecar };
}

function renderPanel(client: KerbcastClient, onShowInGrid = vi.fn(), onClose: () => void = () => {}) {
  return render(
    <KerbcastProvider client={client}>
      <NetworksPanel onClose={onClose} onShowInGrid={onShowInGrid} shownFlightIds={new Set()} />
    </KerbcastProvider>,
  );
}

const card = (name: string) => screen.getByRole("region", { name: `Network ${name}` });

beforeEach(() => {
  localStorage.clear();
  vi.stubGlobal("fetch", async () => new Response("not found", { status: 404 }));
});

afterEach(() => {
  cleanup();
  for (const c of clients) void c.disconnect();
  clients.length = 0;
  vi.unstubAllGlobals();
});

describe("NetworksPanel cards", () => {
  it("shows every card read only, with a note, while the game cannot apply changes", async () => {
    const { client, sidecar } = await mockGame();
    renderPanel(client);
    expect(screen.getByRole("button", { name: "New network" })).toBeTruthy();
    await act(async () => sidecar.setNetworksEditable(false));
    expect(screen.queryByRole("button", { name: "New network" })).toBeNull();
    expect(screen.getByText(/can be changed in flight or at the Space Center/)).toBeTruthy();
    expect(within(card("Kerbin ground net")).queryByRole("button", { name: "Edit" })).toBeNull();
    expect(within(card("Kerbin ground net")).queryByRole("button", { name: /Delete/ })).toBeNull();
    await act(async () => sidecar.setNetworksEditable(true));
    expect(within(card("Kerbin ground net")).getByRole("button", { name: "Edit" })).toBeTruthy();
  });

  it("shows each network's status and every member state in words", async () => {
    const { client } = await mockGame();
    renderPanel(client);

    expect(within(card("Kerbin ground net")).getByText("Live")).toBeTruthy();
    const mun = within(card("Mun watch"));
    for (const words of [
      "Clear",
      "Blocked by Mun",
      "Below horizon",
      "Turned off",
      "Is the target",
      "Gone",
      "Too far",
      "No fix",
      "Not found",
      "No target",
      "Placing",
      "Unavailable",
    ]) {
      expect(mun.getAllByText(words).length).toBeGreaterThan(0);
    }
    expect(mun.queryByText("Too close")).toBeNull();

    const polar = within(card("Polar net"));
    expect(polar.getByText("No view")).toBeTruthy();
    expect(polar.getByText("Made by mission-control, which alone can change it")).toBeTruthy();
    expect(polar.queryByRole("button", { name: /edit/i })).toBeNull();
    expect(polar.queryByRole("button", { name: /delete/i })).toBeNull();

    const old = within(card("Old probe watch"));
    expect(old.getByText("Not running")).toBeTruthy();
    expect(old.getByText(/No target/)).toBeTruthy();
    expect(old.queryByRole("button", { name: /show/i })).toBeNull();
  });

  it("deletes after an inline confirm and the network's camera ends", async () => {
    const { client } = await mockGame();
    const confirmSpy = vi.spyOn(window, "confirm");
    renderPanel(client);

    fireEvent.click(within(card("Kerbin ground net")).getByRole("button", { name: "Delete Kerbin ground net" }));
    expect(screen.getByText(/Its feed ends for every viewer/)).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Delete" }));

    await waitFor(() => expect(screen.queryByRole("region", { name: "Network Kerbin ground net" })).toBeNull());
    expect(client.cameras.some((c) => c.flightId === 401)).toBe(false);
    expect(confirmSpy).not.toHaveBeenCalled();
  });

  it("offers Show for a running network", async () => {
    const { client } = await mockGame();
    const onShow = vi.fn();
    renderPanel(client, onShow);
    fireEvent.click(within(card("Mun watch")).getByRole("button", { name: /show/i }));
    expect(onShow).toHaveBeenCalledWith(expect.objectContaining({ flightId: 402 }));
  });
});

describe("NetworksPanel form", () => {
  it("lists stations the game has off with the reason and adds only enabled ones with Add all", async () => {
    const { client } = await mockGame();
    renderPanel(client);
    fireEvent.click(screen.getByRole("button", { name: /new network/i }));

    expect(screen.getByText("Extra ground stations are off in this game's settings")).toBeTruthy();
    expect(screen.getByText("Turned off by the mod that adds it")).toBeTruthy();
    expect((screen.getByLabelText("Mesa South") as HTMLInputElement).disabled).toBe(false);

    const stations = within(screen.getByRole("region", { name: "Ground stations" }));
    fireEvent.click(stations.getByRole("button", { name: "Add all" }));
    expect(stations.getByText("6 of 8")).toBeTruthy();
    expect((screen.getByLabelText("Mesa South") as HTMLInputElement).checked).toBe(false);
  });

  it("hides debris, flags and asteroids until asked and adds a type's craft as a snapshot", async () => {
    const { client } = await mockGame();
    renderPanel(client);
    fireEvent.click(screen.getByRole("button", { name: /new network/i }));

    const vessels = within(screen.getByRole("region", { name: "Vessels" }));
    expect(vessels.queryByLabelText("Kerbal X Debris")).toBeNull();
    expect(vessels.queryByRole("button", { name: "Debris" })).toBeNull();
    expect(vessels.getByText("Skipped while it is the target")).toBeTruthy();
    expect(vessels.queryByText(/too close/i)).toBeNull();
    expect(vessels.getByText("Craft launched later are not added", { exact: false })).toBeTruthy();

    fireEvent.click(vessels.getByRole("button", { name: "Relay" }));
    fireEvent.click(vessels.getByRole("button", { name: "Add all relays" }));
    expect(screen.getByText("3 of 64 members")).toBeTruthy();

    fireEvent.click(vessels.getByLabelText("Show debris, flags and asteroids"));
    fireEvent.click(vessels.getByRole("button", { name: "All" }));
    expect(vessels.getByLabelText("Kerbal X Debris")).toBeTruthy();
    expect(vessels.getByRole("button", { name: "Asteroid" })).toBeTruthy();
  });

  it("creates a network that every viewer then lists", async () => {
    const { client } = await mockGame();
    renderPanel(client);
    fireEvent.click(screen.getByRole("button", { name: /new network/i }));

    const name = screen.getByLabelText("Name") as HTMLInputElement;
    expect(name.maxLength).toBe(48);
    fireEvent.change(name, { target: { value: "Relay ring" } });
    fireEvent.click(screen.getByLabelText("Relay K2"));
    fireEvent.click(screen.getByLabelText("Kerbal Space Center"));
    fireEvent.click(screen.getByRole("button", { name: /create network/i }));

    await waitFor(() => expect(card("Relay ring")).toBeTruthy());
    const saved = client.networks().find((n) => n.name === "Relay ring");
    expect(saved?.vantages.map((v) => v.vantage)).toEqual([
      { kind: CommandVantageKind.Vessel, persistentId: 1005 },
      { kind: CommandVantageKind.Station, station: "KSC" },
    ]);
    expect(saved?.watch).toBeUndefined();
    await waitFor(() => expect(within(card("Relay ring")).getByText("Live")).toBeTruthy());
  });

  it("reloads the latest version when someone else saved first", async () => {
    const { client, sidecar } = await mockGame();
    renderPanel(client);
    fireEvent.click(within(card("Kerbin ground net")).getByRole("button", { name: "Edit" }));
    fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Mine" } });

    await act(async () => {
      sidecar.fireNetworkDefinitions(
        sidecar.networks.map((n) => (n.networkId === "n1" ? { ...n, name: "Theirs", revision: (n.revision ?? 0) + 1 } : n)),
      );
    });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(screen.getByRole("status").textContent).toMatch(/Someone else changed this network/));
    expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("Theirs");
  });

  it("reloads the definitions that arrived after Save was clicked", async () => {
    const def: NetworkDefinition = {
      networkId: "x",
      name: "Mine",
      vantages: [{ vantage: { kind: CommandVantageKind.Station, station: "KSC" } }],
      revision: 1,
    };
    const sidecar = new MockSidecar().withNetworks([def], { manual: true });
    const client = await connect(sidecar);
    renderPanel(client);
    fireEvent.click(within(card("Mine")).getByRole("button", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await act(async () => {
      sidecar.fireNetworkDefinitions([{ ...def, name: "Theirs", revision: 2 }]);
      sidecar.fireNetworkResult({ token: sidecar.lastCommand("save-network")!.content.token, ok: false, code: "stale" });
    });

    await waitFor(() => expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("Theirs"));
  });

  describe("retrying a create", () => {
    const ksc: CatalogStation = { nodeName: "KSC", displayName: "KSC", body: "Kerbin", latitude: 0, longitude: 0, altitude: 0, enabled: true };

    async function newForm() {
      const sidecar = new MockSidecar().withNetworks([], { manual: true });
      sidecar.fireSiteCatalog({ stations: [ksc], vessels: [] });
      const client = await connect(sidecar);
      renderPanel(client);
      fireEvent.click(screen.getByRole("button", { name: /new network/i }));
      fireEvent.click(within(screen.getByRole("region", { name: "Ground stations" })).getByRole("button", { name: "Add all" }));
      return { client, sidecar };
    }
    const create = () => fireEvent.click(screen.getByRole("button", { name: /create network/i }));

    it("uses a new token after a definite refusal", async () => {
      const { sidecar } = await newForm();
      create();
      const first = sidecar.lastCommand("save-network")!.content.token;
      await act(async () => {
        sidecar.fireNetworkResult({ token: first, ok: false, code: "full" });
      });
      await waitFor(() => expect(screen.getByRole("alert")).toBeTruthy());

      await waitFor(() => expect((screen.getByRole("button", { name: /create network/i }) as HTMLButtonElement).disabled).toBe(false));
      create();
      await waitFor(() => expect(sidecar.lastCommand("save-network")!.content.token).not.toBe(first));
    });

    it("keeps its token after a timeout, which may have reached the game", async () => {
      const { client } = await newForm();
      const save = vi.spyOn(client, "saveNetwork").mockRejectedValueOnce(Object.assign(new Error("late"), { code: "timeout" }));
      await act(async () => {
        create();
      });
      save.mockResolvedValueOnce({ networkId: "n" });
      await waitFor(() => expect((screen.getByRole("button", { name: /create network/i }) as HTMLButtonElement).disabled).toBe(false));
      await act(async () => {
        create();
      });
      expect(save.mock.calls[1]![1]).toEqual(save.mock.calls[0]![1]);
    });

    it("sends what changed after a timeout as an edit of the network the game made", async () => {
      const { client, sidecar } = await newForm();
      const save = vi.spyOn(client, "saveNetwork").mockRejectedValueOnce(Object.assign(new Error("late"), { code: "timeout" }));
      await act(async () => {
        create();
      });
      const token = save.mock.calls[0]![1]!.token!;
      await act(async () => {
        sidecar.fireNetworkDefinitions([{ networkId: "made", name: "", vantages: [], revision: 4, createToken: token }]);
      });
      await waitFor(() => expect((screen.getByRole("button", { name: /create network/i }) as HTMLButtonElement).disabled).toBe(false));
      fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Changed" } });
      save.mockResolvedValueOnce({ networkId: "made" });
      await act(async () => {
        create();
      });
      expect(save.mock.calls[1]![0]).toMatchObject({ networkId: "made", baseRevision: 4, name: "Changed" });
      expect(save.mock.calls[1]![1]).toEqual({});
    });
  });

  describe("closing with entries in the form", () => {
    async function formOpen() {
      const onClose = vi.fn();
      const { client } = await mockGame();
      renderPanel(client, vi.fn(), onClose);
      fireEvent.click(screen.getByRole("button", { name: /new network/i }));
      return onClose;
    }
    const typeName = () => fireEvent.change(screen.getByLabelText("Name"), { target: { value: "Part filled" } });
    const confirmRow = () => screen.queryByRole("group", { name: "Discard changes" });

    it("closes at once when nothing has been entered", async () => {
      const onClose = await formOpen();
      fireEvent.keyDown(document, { key: "Escape" });
      expect(onClose).toHaveBeenCalledTimes(1);
      expect(confirmRow()).toBeNull();
    });

    it("asks before Escape discards, and Keep editing leaves the form as it was", async () => {
      const onClose = await formOpen();
      typeName();
      fireEvent.keyDown(document, { key: "Escape" });
      expect(onClose).not.toHaveBeenCalled();
      fireEvent.click(within(confirmRow()!).getByRole("button", { name: "Keep editing" }));
      expect(confirmRow()).toBeNull();
      expect((screen.getByLabelText("Name") as HTMLInputElement).value).toBe("Part filled");
    });

    it("asks before an outside tap discards, and Discard closes", async () => {
      const onClose = await formOpen();
      typeName();
      fireEvent.pointerDown(document.body);
      expect(onClose).not.toHaveBeenCalled();
      fireEvent.click(within(confirmRow()!).getByRole("button", { name: "Discard" }));
      expect(onClose).toHaveBeenCalledTimes(1);
    });

    it("treats a second Escape as keep editing", async () => {
      const onClose = await formOpen();
      typeName();
      fireEvent.keyDown(document, { key: "Escape" });
      fireEvent.keyDown(document, { key: "Escape" });
      expect(confirmRow()).toBeNull();
      expect(onClose).not.toHaveBeenCalled();
    });

    it("does not count a change undone as an entry", async () => {
      const onClose = await formOpen();
      typeName();
      fireEvent.change(screen.getByLabelText("Name"), { target: { value: "" } });
      fireEvent.keyDown(document, { key: "Escape" });
      expect(onClose).toHaveBeenCalledTimes(1);
    });
  });

  it("takes focus when it opens and gives it back when it closes", async () => {
    const { client } = await mockGame();
    const opener = document.createElement("button");
    document.body.append(opener);
    opener.focus();

    const { unmount } = renderPanel(client);
    expect(document.activeElement).toBe(screen.getByRole("dialog"));

    unmount();
    expect(document.activeElement).toBe(opener);
    opener.remove();
  });

  it("refuses more than 64 members before sending", async () => {
    const sidecar = new MockSidecar().withNetworks([]);
    const stations: CatalogStation[] = Array.from({ length: 70 }, (_, i) => ({
      nodeName: `S${i}`,
      displayName: `Station ${i}`,
      body: "Kerbin",
      latitude: 0,
      longitude: i,
      altitude: 0,
      enabled: true,
    }));
    sidecar.fireSiteCatalog({ stations, vessels: [] });
    const client = await connect(sidecar);
    renderPanel(client);
    fireEvent.click(screen.getByRole("button", { name: /new network/i }));
    fireEvent.click(within(screen.getByRole("region", { name: "Ground stations" })).getByRole("button", { name: "Add all" }));

    expect(screen.getByText("70 of 64 members")).toBeTruthy();
    expect((screen.getByRole("button", { name: /create network/i }) as HTMLButtonElement).disabled).toBe(true);
  });

  it("shows a short message for any other refusal", async () => {
    const owned: NetworkDefinition = {
      networkId: "x",
      name: "Edited elsewhere",
      vantages: [{ vantage: { kind: CommandVantageKind.Station, station: "KSC" } }],
      revision: 1,
    };
    const sidecar = new MockSidecar().withNetworks([owned], { manual: true });
    const client = await connect(sidecar);
    renderPanel(client);
    fireEvent.click(within(card("Edited elsewhere")).getByRole("button", { name: "Edit" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    const token = sidecar.lastCommand("save-network")!.content.token;
    await act(async () => {
      sidecar.fireNetworkResult({ token, ok: false, code: "writeFailed" });
    });
    expect(screen.getByRole("alert").textContent).toBe("The game could not write its networks file, so nothing changed");
  });
});

describe("Settings entry", () => {
  async function openSettings(sidecar: MockSidecar) {
    const client = new KerbcastClient(
      { host: "h", port: 1, negotiate: (o) => sidecar.negotiate(o) },
      sidecar.createTransport(),
    );
    clients.push(client);
    await act(async () => {
      render(<App client={client} />);
    });
    await act(async () => {
      sidecar.open();
      sidecar.setConnectionState("connected");
    });
    fireEvent.click(screen.getByRole("button", { name: /settings/i }));
  }

  it("is hidden when the game does not take networks", async () => {
    await openSettings(new MockSidecar());
    expect(screen.queryByText("Tracking networks")).toBeNull();
  });

  it("opens the drawer with the count", async () => {
    const sidecar = new MockSidecar();
    installMockNetworks(sidecar);
    await openSettings(sidecar);
    fireEvent.click(screen.getByRole("button", { name: "Manage (4)" }));
    expect(screen.getByRole("dialog", { name: "Tracking networks" })).toBeTruthy();
  });
});
