/*
 * Tracking networks: a header drawer opened from Settings. Lists every
 * network as a card (the definition from the game, its live state from the
 * camera that runs it) and holds the create and edit form.
 */

import { CommandAimKind, CommandVantageKind } from "@ksp-gonogo/kerbcast";
import type {
  CameraState,
  CatalogStation,
  CatalogVessel,
  CommandAim,
  CommandVantage,
  NetworkDefinition,
  NetworkVantageSpec,
  SiteCatalogPayload,
} from "@ksp-gonogo/kerbcast";
import { NetworkIcon, useKerbcastCameras, useKerbcastClient, useNetworks, useSiteCatalog } from "@ksp-gonogo/kerbcast-react";
import { Check, ChevronLeft, LayoutGrid, Lock, Pencil, Plus, RadioTower, Satellite, Trash2, X } from "lucide-react";
import { type MutableRefObject, useCallback, useEffect, useMemo, useRef, useState } from "react";
import styled, { css } from "styled-components";
import { useDialogFocus } from "../useDialogFocus";
import {
  DEFAULT_NETWORK_NAME,
  disabledReasonText,
  filterVessels,
  formatLatLon,
  HIDDEN_VESSEL_TYPES,
  MAX_NAME_LENGTH,
  MAX_VANTAGES,
  memberStates,
  networkCamera,
  networkStatus,
  requestErrorText,
  situationText,
  sortVesselTypes,
  stationKey,
  typeLabel,
  typePlural,
  vantageKey,
  vantageName,
  vesselKey,
  vantageStateText,
  watchDescription,
} from "./model";
import type { Tone } from "./model";

export interface NetworksPanelProps {
  onClose: () => void;
  /** Adds a tile showing this camera. Absent when the grid cannot show network feeds. */
  onShowInGrid?: (camera: CameraState) => void;
  shownFlightIds: ReadonlySet<number>;
}

type View = { kind: "list" } | { kind: "form"; networkId: string | null };

/**
 * Whether the game applies saves and deletes now, from the client's last
 * `network-definitions`; null until it says. It does not in scenes where
 * command cameras do not run, or while a scene loads.
 */
function useNetworksEditable(): boolean | null {
  const client = useKerbcastClient();
  const [editable, setEditable] = useState(() => client.networksEditable);
  useEffect(() => {
    setEditable(client.networksEditable);
    return client.on("networks", () => setEditable(client.networksEditable));
  }, [client]);
  return editable;
}

/* Opened from Settings, which closes as this opens, so its gear is the way back. */
const settingsButton = () => document.querySelector<HTMLElement>('header button[aria-label="Settings"]');

function newCreateToken(): string {
  return `create-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

const READ_ONLY_NOTE =
  "Networks can be changed in flight or at the Space Center. Until the game is in one of those, they show read only.";

export function NetworksPanel({ onClose, onShowInGrid, shownFlightIds }: NetworksPanelProps): React.JSX.Element {
  const panelRef = useRef<HTMLDivElement>(null);
  const client = useKerbcastClient();
  const networks = useNetworks();
  const locked = useNetworksEditable() === false;
  const catalog = useSiteCatalog();
  const cameras = useKerbcastCameras();
  const [view, setView] = useState<View>({ kind: "list" });
  /* Set by the form: Escape or a tap outside would otherwise lose what has been entered. */
  const formDirty = useRef(false);
  const [confirmingDiscard, setConfirmingDiscard] = useState(false);

  useDialogFocus(panelRef, true, settingsButton);

  const keepEditing = useCallback(() => {
    setConfirmingDiscard(false);
    panelRef.current?.focus({ preventScroll: true });
  }, []);
  const toList = () => {
    setConfirmingDiscard(false);
    setView({ kind: "list" });
  };

  useEffect(() => {
    const askToClose = () => {
      if (formDirty.current) setConfirmingDiscard(true);
      else onClose();
    };
    const handleKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      if (confirmingDiscard) keepEditing();
      else askToClose();
    };
    const handlePointer = (e: PointerEvent) => {
      if (panelRef.current && !panelRef.current.contains(e.target as Node)) askToClose();
    };
    document.addEventListener("keydown", handleKey);
    document.addEventListener("pointerdown", handlePointer);
    return () => {
      document.removeEventListener("keydown", handleKey);
      document.removeEventListener("pointerdown", handlePointer);
    };
  }, [onClose, confirmingDiscard, keepEditing]);

  const editing = view.kind === "form" && view.networkId !== null ? networks.find((n) => n.networkId === view.networkId) : undefined;
  const title = view.kind === "list" ? "Tracking networks" : view.networkId !== null ? "Edit network" : "New network";

  return (
    <Panel ref={panelRef} role="dialog" aria-label={title} tabIndex={-1}>
      <PanelHeader>
        <HeaderLeft>
          {view.kind === "form" && (
            <IconButton type="button" aria-label="Back to networks" onClick={toList}>
              <ChevronLeft size={14} strokeWidth={1.75} aria-hidden="true" />
            </IconButton>
          )}
          <PanelTitle>{title}</PanelTitle>
        </HeaderLeft>
        <IconButton type="button" onClick={onClose} aria-label="Close">
          <X size={14} strokeWidth={1.75} aria-hidden="true" />
        </IconButton>
      </PanelHeader>
      {confirmingDiscard && view.kind === "form" && (
        <DiscardRow role="group" aria-label="Discard changes">
          <span>Discard what you have entered?</span>
          <Actions>
            <DangerButton type="button" onClick={onClose}>
              Discard
            </DangerButton>
            <GhostButton type="button" autoFocus onClick={keepEditing}>
              Keep editing
            </GhostButton>
          </Actions>
        </DiscardRow>
      )}
      {view.kind === "list" ? (
        <Body>
          <ListTop>
            <Hint>
              A network is one feed that follows a craft and cuts to whichever member has the clearest view. Every
              viewer can pick it from the camera list.
            </Hint>
            {!locked && (
              <PrimaryButton type="button" onClick={() => setView({ kind: "form", networkId: null })}>
                <Plus size={13} strokeWidth={2} aria-hidden="true" />
                New network
              </PrimaryButton>
            )}
          </ListTop>
          {locked && <NoteText role="status">{READ_ONLY_NOTE}</NoteText>}
          {networks.length === 0 && <Empty>No networks in this save yet.</Empty>}
          {networks.map((def) => {
            const camera = networkCamera(def, cameras);
            return (
              <NetworkCard
                key={def.networkId}
                def={def}
                catalog={catalog}
                camera={camera}
                locked={locked}
                onEdit={() => setView({ kind: "form", networkId: def.networkId })}
                onShowInGrid={camera && onShowInGrid ? () => onShowInGrid(camera) : undefined}
                inGrid={camera !== undefined && shownFlightIds.has(camera.flightId)}
              />
            );
          })}
          {client.networksOmitted > 0 && (
            <Hint>
              {client.networksOmitted} more network{client.networksOmitted === 1 ? " is" : "s are"} too large to list here.
            </Hint>
          )}
        </Body>
      ) : (
        <NetworkForm
          key={view.networkId ?? "new"}
          locked={locked}
          catalog={catalog}
          editing={view.networkId !== null ? (editing ?? null) : null}
          gone={view.networkId !== null && editing === undefined}
          dirtyRef={formDirty}
          onDone={toList}
        />
      )}
    </Panel>
  );
}

interface NetworkCardProps {
  def: NetworkDefinition;
  catalog: SiteCatalogPayload | null;
  camera: CameraState | undefined;
  onEdit: () => void;
  onShowInGrid?: () => void;
  inGrid: boolean;
  /** The game does not apply changes now: no Edit or Delete. */
  locked: boolean;
}

function NetworkCard({ def, catalog, camera, onEdit, onShowInGrid, inGrid, locked }: NetworkCardProps): React.JSX.Element {
  const client = useKerbcastClient();
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const status = networkStatus(camera);
  const live = memberStates(def, camera);
  const onAir = camera?.commandCentre?.network?.vantages.find((v) => v.active);
  const watch = watchDescription(def, catalog);
  const readOnly = def.owner !== undefined;

  const remove = async () => {
    setBusy(true);
    setError(null);
    try {
      await client.deleteNetwork(def.networkId);
    } catch (err) {
      setError(requestErrorText((err as { code?: string }).code ?? "unknown"));
      setBusy(false);
    }
  };

  return (
    <Card aria-label={`Network ${def.name}`}>
      <CardHead>
        <CardTitle>
          <NetworkIcon size={14} />
          <span>{def.name}</span>
        </CardTitle>
        {status === "notRunning" ? (
          <Pill $tone="muted">Not running</Pill>
        ) : status === "noView" ? (
          <Pill $tone="warn">No view</Pill>
        ) : (
          <Pill $tone="ok">Live</Pill>
        )}
      </CardHead>
      <CardMeta>
        Watches {watch.text}
        {watch.missing && <StateChip $tone="muted"> · No target</StateChip>}
        {onAir && (
          <>
            <br />
            {status === "noView" ? (
              <>
                No member can see it. Holding at <strong>{onAir.label}</strong>
              </>
            ) : (
              <>
                Showing it via <strong>{onAir.label}</strong>
              </>
            )}
          </>
        )}
      </CardMeta>
      <Members>
        {def.vantages.map((spec, i) => {
          const member = live[i];
          const text =
            status === "notRunning" ? null : vantageStateText(member?.state ?? "pending", member?.occluder);
          const name = member?.label || vantageName(spec.vantage, spec.label, catalog);
          return (
            <Member key={`${i}:${vantageKey(spec.vantage)}`} $active={member?.active === true} title={text?.long}>
              <MemberIcon aria-hidden="true">
                {spec.vantage.kind === CommandVantageKind.Vessel ? (
                  <Satellite size={12} strokeWidth={1.75} />
                ) : (
                  <RadioTower size={12} strokeWidth={1.75} />
                )}
              </MemberIcon>
              <MemberName>
                {name}
                {spec.fallback && <Tag>fallback</Tag>}
              </MemberName>
              {member?.active && <ActiveMark>{status === "noView" ? "HOLDING" : "ON AIR"}</ActiveMark>}
              {text && <StateChip $tone={text.tone}>{text.short}</StateChip>}
            </Member>
          );
        })}
      </Members>
      {confirming ? (
        <ConfirmRow role="group" aria-label="Confirm delete">
          <span>Delete {def.name}? Its feed ends for every viewer.</span>
          <Actions>
            <DangerButton type="button" disabled={busy} onClick={() => void remove()}>
              Delete
            </DangerButton>
            <GhostButton type="button" onClick={() => { setConfirming(false); setError(null); }}>
              Keep
            </GhostButton>
          </Actions>
          {error && <ErrorText role="alert">{error}</ErrorText>}
        </ConfirmRow>
      ) : (
        <CardFoot>
          {readOnly ? (
            <Owner>
              <Lock size={11} strokeWidth={1.75} aria-hidden="true" />
              Made by {def.owner}, which alone can change it
            </Owner>
          ) : (
            <span />
          )}
          <Actions>
            {onShowInGrid && (
              <GhostButton type="button" onClick={onShowInGrid} disabled={inGrid} title={inGrid ? "Already in the grid" : undefined}>
                <LayoutGrid size={12} strokeWidth={1.75} aria-hidden="true" />
                {inGrid ? "In grid" : "Show"}
              </GhostButton>
            )}
            {!readOnly && !locked && (
              <>
                <GhostButton type="button" onClick={onEdit}>
                  <Pencil size={12} strokeWidth={1.75} aria-hidden="true" />
                  Edit
                </GhostButton>
                <GhostButton type="button" onClick={() => setConfirming(true)} aria-label={`Delete ${def.name}`}>
                  <Trash2 size={12} strokeWidth={1.75} aria-hidden="true" />
                </GhostButton>
              </>
            )}
          </Actions>
        </CardFoot>
      )}
    </Card>
  );
}

/** Everything the form edits, read from a definition (or blank for a new one). */
interface FormState {
  name: string;
  /** `active`, a craft's persistentId, or `keep` for an aim the form cannot show. */
  watch: string;
  /** Ticked members by key, in saved order with new ticks at the end. */
  selected: string[];
  fallback: string;
  /** The saved spec behind each key, so an edit keeps labels and placement it does not show. */
  priors: Map<string, NetworkVantageSpec>;
  base: NetworkDefinition | null;
}

function formStateFrom(def: NetworkDefinition | null): FormState {
  const priors = new Map<string, NetworkVantageSpec>();
  for (const spec of def?.vantages ?? []) {
    const k = vantageKey(spec.vantage);
    if (!priors.has(k)) priors.set(k, spec);
  }
  const fallbackSpec = def?.vantages.find((v) => v.fallback);
  const watch = def?.watch;
  return {
    name: def?.name ?? "",
    watch: !watch ? "active" : watch.kind === CommandAimKind.Vessel ? String(watch.persistentId ?? 0) : "keep",
    selected: [...priors.keys()],
    fallback: fallbackSpec ? vantageKey(fallbackSpec.vantage) : "",
    priors,
    base: def,
  };
}

/* The form's own fields, so a change and its undoing count as no change. */
function sameEntries(a: FormState, b: FormState): boolean {
  return (
    a.name === b.name &&
    a.watch === b.watch &&
    a.fallback === b.fallback &&
    [...a.selected].sort().join("\n") === [...b.selected].sort().join("\n")
  );
}

function vantageFromKey(key: string): CommandVantage {
  const colon = key.indexOf(":");
  const kind = key.slice(0, colon);
  const id = key.slice(colon + 1);
  return kind === "station"
    ? { kind: CommandVantageKind.Station, station: id }
    : { kind: CommandVantageKind.Vessel, persistentId: Number(id) };
}

interface NetworkFormProps {
  catalog: SiteCatalogPayload | null;
  editing: NetworkDefinition | null;
  /** The network being edited has been deleted meanwhile. */
  gone: boolean;
  /** Kept true while the form holds entries a close would lose. */
  dirtyRef: MutableRefObject<boolean>;
  onDone: () => void;
  /** The game stopped applying changes while the form was open (a scene change). */
  locked: boolean;
}

/* The request may have reached the game, so a retry has to reuse its token. */
const UNSETTLED_CODES = new Set(["timeout", "notConnected", "unknown"]);

function NetworkForm({ catalog, editing, gone, dirtyRef, onDone, locked }: NetworkFormProps): React.JSX.Element {
  const client = useKerbcastClient();
  /* One token per new network, so a retry after a timeout finds the network the first try made instead of making another. */
  const createToken = useRef(newCreateToken());
  const stations = catalog?.stations ?? [];
  const vessels = catalog?.vessels ?? [];
  const [form, setForm] = useState<FormState>(() => formStateFrom(editing));
  const baseline = useRef(form);
  const dirty = !sameEntries(form, baseline.current);
  useEffect(() => {
    dirtyRef.current = dirty;
  }, [dirty, dirtyRef]);
  useEffect(
    () => () => {
      dirtyRef.current = false;
    },
    [dirtyRef],
  );
  const [typeFilter, setTypeFilter] = useState<string>("all");
  const [showHidden, setShowHidden] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const isEdit = form.base !== null;
  const selected = useMemo(() => new Set(form.selected), [form.selected]);

  /* A member the catalog does not list (a gone craft, a station from a missing mod) stays listed, so an edit keeps it until it is unticked. */
  const orphans = useMemo(
    () =>
      [...form.priors.entries()].filter(([, spec]) => {
        const v = spec.vantage;
        if (v.kind === CommandVantageKind.Station) return !stations.some((s) => s.nodeName === v.station);
        if (v.kind === CommandVantageKind.Vessel) return !vessels.some((c) => c.persistentId === v.persistentId);
        return true;
      }),
    [form.priors, stations, vessels],
  );

  const activeVessel = vessels.find((v) => v.active);
  const watchedPid = form.watch === "active" ? activeVessel?.persistentId : form.watch === "keep" ? undefined : Number(form.watch);
  const shownVessels = useMemo(() => filterVessels(vessels, { showHidden, type: "all" }), [vessels, showHidden]);
  const types = useMemo(() => sortVesselTypes(shownVessels.map((v) => v.vesselType)), [shownVessels]);
  /* A chip whose type the filter now hides falls back to all. */
  const activeType = types.includes(typeFilter) ? typeFilter : "all";
  const listedVessels = useMemo(
    () => filterVessels(vessels, { showHidden, type: activeType }),
    [vessels, showHidden, activeType],
  );

  const toggle = (k: string) =>
    setForm((f) => {
      const on = f.selected.includes(k);
      const next = on ? f.selected.filter((x) => x !== k) : [...f.selected, k];
      return { ...f, selected: next, fallback: next.includes(f.fallback) ? f.fallback : "" };
    });
  const setMany = (keys: string[], on: boolean) =>
    setForm((f) => {
      const next = on ? [...f.selected, ...keys.filter((k) => !f.selected.includes(k))] : f.selected.filter((k) => !keys.includes(k));
      return { ...f, selected: next, fallback: next.includes(f.fallback) ? f.fallback : "" };
    });

  const enabledStationKeys = stations.filter((s) => s.enabled).map((s) => stationKey(s.nodeName));
  const allStationsOn = enabledStationKeys.length > 0 && enabledStationKeys.every((k) => selected.has(k));
  const listedVesselKeys = listedVessels.map((v) => vesselKey(v.persistentId));
  const allVesselsOn = listedVesselKeys.length > 0 && listedVesselKeys.every((k) => selected.has(k));
  const stationCount = stations.filter((s) => selected.has(stationKey(s.nodeName))).length;
  const vesselCount = vessels.filter((v) => selected.has(vesselKey(v.persistentId))).length;
  const total = form.selected.length;

  const memberLabel = (k: string): string => {
    const spec = form.priors.get(k);
    return vantageName(spec?.vantage ?? vantageFromKey(k), spec?.label, catalog);
  };

  const submit = async () => {
    setError(null);
    setNote(null);
    const vantages: NetworkVantageSpec[] = form.selected.map((k) => {
      const prior = form.priors.get(k);
      return {
        vantage: prior?.vantage ?? vantageFromKey(k),
        ...(prior?.label ? { label: prior.label } : {}),
        ...(k === form.fallback ? { fallback: true } : {}),
      };
    });
    const watch: CommandAim | undefined =
      form.watch === "active"
        ? undefined
        : form.watch === "keep"
          ? form.base?.watch
          : { kind: CommandAimKind.Vessel, persistentId: Number(form.watch) };
    const name = form.name.trim();
    /* A create that timed out may have reached the game, and a repeat of its token changes nothing there, so the form's entries go as an edit of what it made. */
    const base = form.base ?? client.networks().find((n) => n.createToken === createToken.current);
    setBusy(true);
    try {
      await client.saveNetwork({
        ...(base ? { networkId: base.networkId } : {}),
        ...(base?.revision !== undefined ? { baseRevision: base.revision } : {}),
        ...(name ? { name } : {}),
        ...(watch ? { watch } : {}),
        vantages,
        ...(base?.options ? { options: base.options } : {}),
      }, base ? {} : { token: createToken.current });
      onDone();
    } catch (err) {
      setBusy(false);
      const code = (err as { code?: string }).code ?? "unknown";
      /* A refused try's token is spent: the game answers a repeat of it with nothing new. */
      if (!base && !UNSETTLED_CODES.has(code)) createToken.current = newCreateToken();
      /* Read now: the definitions that made this stale arrived after the click. */
      const now = base ? client.networks().find((n) => n.networkId === base.networkId) : undefined;
      if (code === "stale" && now) {
        const fresh = formStateFrom(now);
        baseline.current = fresh;
        setForm(fresh);
        setNote("Someone else changed this network while you were editing. It now shows their version; make your changes again and save");
        return;
      }
      setError(requestErrorText(code));
    }
  };

  const watchOptions = vessels.filter(
    (v) => !HIDDEN_VESSEL_TYPES.has(v.vesselType) || v.persistentId === watchedPid,
  );
  const watchedMissing = form.watch !== "active" && form.watch !== "keep" && !vessels.some((v) => String(v.persistentId) === form.watch);
  const missingLabel = catalog && !catalog.vesselsOmitted ? "A craft that no longer exists" : `Craft ${form.watch}`;

  return (
    <>
      <Body>
        {gone && <ErrorText role="alert">Another viewer deleted this network</ErrorText>}
        {locked && <NoteText role="status">{READ_ONLY_NOTE}</NoteText>}
        {note && <NoteText role="status">{note}</NoteText>}
        {!catalog && <Hint>The game has not listed its ground stations and craft yet.</Hint>}
        <Field>
          <FieldLabel htmlFor="kc-net-name">Name</FieldLabel>
          <TextInput
            id="kc-net-name"
            value={form.name}
            maxLength={MAX_NAME_LENGTH}
            placeholder={DEFAULT_NETWORK_NAME}
            onChange={(e) => {
              const value = e.target.value.slice(0, MAX_NAME_LENGTH);
              setForm((f) => ({ ...f, name: value }));
            }}
          />
        </Field>
        <Field>
          <FieldLabel htmlFor="kc-net-watch">Watches</FieldLabel>
          <Select id="kc-net-watch" value={form.watch} onChange={(e) => { const watch = e.target.value; setForm((f) => ({ ...f, watch })); }}>
            <option value="active">Active vessel{activeVessel ? ` (now ${activeVessel.name})` : ""}</option>
            {form.watch === "keep" && <option value="keep">As its maker set it</option>}
            {watchedMissing && <option value={form.watch}>{missingLabel}</option>}
            {watchOptions.length > 0 && (
              <optgroup label="A chosen craft">
                {watchOptions.map((v) => (
                  <option key={v.persistentId} value={String(v.persistentId)}>
                    {v.name}
                  </option>
                ))}
              </optgroup>
            )}
          </Select>
          <FieldHint>Any viewer can still retarget the feed from its own controls</FieldHint>
        </Field>

        <Section aria-label="Ground stations">
          <SectionHead>
            <SectionLabel>Ground stations</SectionLabel>
            <Count>
              {stationCount} of {stations.length}
            </Count>
            {enabledStationKeys.length > 0 && (
              <LinkButton type="button" onClick={() => setMany(enabledStationKeys, !allStationsOn)}>
                {allStationsOn ? "Remove all" : "Add all"}
              </LinkButton>
            )}
          </SectionHead>
          <Rows>
            {stations.map((s) => (
              <StationRow key={s.nodeName} s={s} checked={selected.has(stationKey(s.nodeName))} onToggle={() => toggle(stationKey(s.nodeName))} />
            ))}
            {orphans
              .filter(([, spec]) => spec.vantage.kind === CommandVantageKind.Station)
              .map(([k, spec]) => (
                <OrphanRow key={k} name={vantageName(spec.vantage, spec.label, catalog)} sub="Not in this game" badge="Not found" checked={selected.has(k)} onToggle={() => toggle(k)} />
              ))}
            {stations.length === 0 && orphans.length === 0 && <EmptyRow>No ground stations listed</EmptyRow>}
          </Rows>
          <FieldHint>
            Add all takes the stations the game has on. One that is off can still be added; it joins when the game turns
            it on
          </FieldHint>
        </Section>

        <Section aria-label="Vessels">
          <SectionHead>
            <SectionLabel>Vessels</SectionLabel>
            <Count>
              {vesselCount} of {shownVessels.length}
            </Count>
            {listedVesselKeys.length > 0 && (
              <LinkButton type="button" onClick={() => setMany(listedVesselKeys, !allVesselsOn)}>
                {allVesselsOn ? "Remove all" : activeType === "all" ? "Add all" : `Add all ${typePlural(activeType)}`}
              </LinkButton>
            )}
          </SectionHead>
          {types.length > 1 && (
            <Chips role="group" aria-label="Vessel type">
              {["all", ...types].map((t) => (
                <Chip
                  key={t}
                  type="button"
                  aria-pressed={activeType === t}
                  $on={activeType === t}
                  onClick={() => setTypeFilter(t)}
                >
                  {t === "all" ? "All" : typeLabel(t)}
                </Chip>
              ))}
            </Chips>
          )}
          <Rows>
            {listedVessels.map((v) => (
              <VesselRow
                key={v.persistentId}
                v={v}
                checked={selected.has(vesselKey(v.persistentId))}
                onToggle={() => toggle(vesselKey(v.persistentId))}
                isTarget={v.persistentId === watchedPid}
              />
            ))}
            {orphans
              .filter(([, spec]) => spec.vantage.kind !== CommandVantageKind.Station)
              .map(([k, spec]) => {
                const craft = spec.vantage.kind === CommandVantageKind.Vessel;
                /* A shortened craft list leaves out craft that still exist, so a missing one is not called gone. */
                const cut = (catalog?.vesselsOmitted ?? 0) > 0;
                return (
                  <OrphanRow
                    key={k}
                    name={vantageName(spec.vantage, spec.label, catalog)}
                    sub={!craft ? "Placed by its maker" : cut ? "Not in the shortened craft list" : "No longer exists"}
                    badge={craft && !cut ? "Gone" : null}
                    checked={selected.has(k)}
                    onToggle={() => toggle(k)}
                  />
                );
              })}
            {shownVessels.length === 0 && orphans.length === 0 && <EmptyRow>No craft listed</EmptyRow>}
          </Rows>
          <InlineToggle>
            <Checkbox type="checkbox" checked={showHidden} onChange={(e) => setShowHidden(e.target.checked)} />
            Show debris, flags and asteroids
          </InlineToggle>
          {catalog?.vesselsOmitted ? (
            <FieldHint>
              {catalog.vesselsOmitted} more craft are not listed, to keep the list short (debris and asteroids first)
            </FieldHint>
          ) : null}
          <FieldHint>Add all takes the craft listed now. Craft launched later are not added</FieldHint>
        </Section>

        <Field>
          <FieldLabel htmlFor="kc-net-fallback">When no member can see the craft</FieldLabel>
          <Select id="kc-net-fallback" value={form.fallback} onChange={(e) => { const fallback = e.target.value; setForm((f) => ({ ...f, fallback })); }}>
            <option value="">Stay where it is</option>
            {form.selected.map((k) => (
              <option key={k} value={k}>
                Go to {memberLabel(k)}
              </option>
            ))}
          </Select>
        </Field>
      </Body>
      <FormFoot>
        <Count $over={total > MAX_VANTAGES}>
          {total} of {MAX_VANTAGES} members
        </Count>
        {error && <ErrorText role="alert">{error}</ErrorText>}
        <Actions>
          <GhostButton type="button" onClick={onDone}>
            Cancel
          </GhostButton>
          <PrimaryButton type="button" disabled={busy || gone || locked || total === 0 || total > MAX_VANTAGES} onClick={() => void submit()}>
            <Check size={13} strokeWidth={2} aria-hidden="true" />
            {isEdit ? "Save" : "Create network"}
          </PrimaryButton>
        </Actions>
      </FormFoot>
    </>
  );
}

function StationRow({ s, checked, onToggle }: { s: CatalogStation; checked: boolean; onToggle: () => void }): React.JSX.Element {
  return (
    <Row $off={!s.enabled}>
      <Checkbox type="checkbox" checked={checked} onChange={onToggle} aria-label={s.displayName} />
      <RowText>
        <RowName>{s.displayName}</RowName>
        <RowSub>{s.enabled ? `${s.body} · ${formatLatLon(s.latitude, s.longitude)}` : disabledReasonText(s.disabledReason)}</RowSub>
      </RowText>
      {!s.enabled && <Badge $tone="warn">Off</Badge>}
    </Row>
  );
}

function VesselRow({ v, checked, onToggle, isTarget }: { v: CatalogVessel; checked: boolean; onToggle: () => void; isTarget: boolean }): React.JSX.Element {
  return (
    <Row>
      <Checkbox type="checkbox" checked={checked} onChange={onToggle} aria-label={v.name} />
      <RowText>
        <RowName>{v.name}</RowName>
        <RowSub>
          {typeLabel(v.vesselType)} · {v.body} · {situationText(v.situation)}
        </RowSub>
        {isTarget && <RowNote>Skipped while it is the target</RowNote>}
      </RowText>
      {v.active && <Badge $tone="accent">Active</Badge>}
      {isTarget && <Badge $tone="muted">Target</Badge>}
    </Row>
  );
}

function OrphanRow({
  name,
  sub,
  badge,
  checked,
  onToggle,
}: {
  name: string;
  sub: string;
  badge: string | null;
  checked: boolean;
  onToggle: () => void;
}): React.JSX.Element {
  return (
    <Row $off>
      <Checkbox type="checkbox" checked={checked} onChange={onToggle} aria-label={name} />
      <RowText>
        <RowName>{name}</RowName>
        <RowSub>{sub}</RowSub>
      </RowText>
      {badge && <Badge $tone="bad">{badge}</Badge>}
    </Row>
  );
}

const toneColour = (t: Tone) =>
  t === "ok"
    ? "var(--kc-ok)"
    : t === "warn"
      ? "var(--kc-warn)"
      : t === "bad"
        ? "var(--kc-danger)"
        : t === "accent"
          ? "var(--kc-accent)"
          : "var(--kc-text-muted)";

const Panel = styled.div`
  position: absolute;
  top: calc(var(--kc-header-h) + 0.5rem);
  right: 1rem;
  z-index: 100;
  width: min(440px, calc(100vw - 1rem));
  max-height: calc(100dvh - var(--kc-header-h) - 1rem);
  display: flex;
  flex-direction: column;
  background: var(--kc-surface);
  border: 1px solid var(--kc-border);
  border-radius: 8px;
  overflow: hidden;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.18), 0 2px 6px rgba(0, 0, 0, 0.12);

  &:focus {
    outline: none;
  }

  @media (max-width: 520px) {
    right: 0.5rem;
    left: 0.5rem;
    width: auto;
  }
`;

const PanelHeader = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0.65rem 0.85rem 0.6rem;
  border-bottom: 1px solid var(--kc-border);
  background: var(--kc-surface-raised);
  flex: none;
`;

const HeaderLeft = styled.div`
  display: flex;
  align-items: center;
  gap: 0.4rem;
`;

const PanelTitle = styled.h2`
  margin: 0;
  font-size: 0.75rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--kc-text-muted);
`;

const IconButton = styled.button`
  background: none;
  border: none;
  cursor: pointer;
  color: var(--kc-text-muted);
  padding: 0.1rem;
  line-height: 0;
  display: flex;
  border-radius: 3px;

  &:hover {
    color: var(--kc-text);
  }
  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 2px;
  }
`;

const Body = styled.div`
  padding: 0.85rem;
  display: flex;
  flex-direction: column;
  gap: 0.75rem;
  overflow-y: auto;
  overflow-x: hidden;
  min-height: 0;
`;

const ListTop = styled.div`
  display: flex;
  align-items: flex-start;
  flex-wrap: wrap;
  gap: 0.75rem;
`;

const Hint = styled.p`
  margin: 0;
  font-size: 0.7rem;
  line-height: 1.45;
  color: var(--kc-text-muted);
  flex: 1 1 12rem;
`;

const Empty = styled.p`
  margin: 0.5rem 0;
  font-size: 0.75rem;
  color: var(--kc-text-muted);
  text-align: center;
`;

const buttonBase = css`
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  font-size: 0.72rem;
  font-family: inherit;
  border-radius: 4px;
  padding: 0.3rem 0.55rem;
  cursor: pointer;
  white-space: nowrap;

  &:disabled {
    opacity: 0.45;
    cursor: default;
  }
  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 1px;
  }
`;

const PrimaryButton = styled.button`
  ${buttonBase}
  background: var(--kc-accent);
  color: #fff;
  border: 1px solid var(--kc-accent);
`;

const GhostButton = styled.button`
  ${buttonBase}
  background: none;
  color: var(--kc-text);
  border: 1px solid var(--kc-border);

  &:hover:not(:disabled) {
    background: var(--kc-surface-raised);
  }
`;

const DangerButton = styled.button`
  ${buttonBase}
  background: var(--kc-danger);
  color: #fff;
  border: 1px solid var(--kc-danger);
`;

const LinkButton = styled.button`
  background: none;
  border: none;
  padding: 0;
  font-size: 0.7rem;
  font-family: inherit;
  color: var(--kc-accent);
  cursor: pointer;
  white-space: nowrap;

  &:hover {
    text-decoration: underline;
  }
  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 1px;
  }
`;

const Card = styled.section`
  border: 1px solid var(--kc-border);
  border-radius: 6px;
  background: var(--kc-surface);
  display: flex;
  flex-direction: column;
  min-width: 0;
`;

const CardHead = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 0.5rem;
  padding: 0.6rem 0.7rem 0.2rem;
`;

const CardTitle = styled.h3`
  margin: 0;
  display: flex;
  align-items: center;
  gap: 0.4rem;
  min-width: 0;
  font-size: 0.82rem;
  font-weight: 600;
  color: var(--kc-text);

  span {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
`;

const Pill = styled.span<{ $tone: Tone }>`
  flex: none;
  font-size: 0.6rem;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  padding: 0.12rem 0.4rem;
  border-radius: 999px;
  color: ${(p) => toneColour(p.$tone)};
  border: 1px solid currentColor;
`;

const CardMeta = styled.p`
  margin: 0;
  padding: 0 0.7rem 0.5rem;
  font-size: 0.7rem;
  line-height: 1.5;
  color: var(--kc-text-muted);
  overflow-wrap: anywhere;

  strong {
    color: var(--kc-text);
    font-weight: 600;
  }
`;

const Members = styled.ul`
  list-style: none;
  margin: 0;
  padding: 0;
  border-top: 1px solid var(--kc-border-subtle);
`;

const Member = styled.li<{ $active: boolean }>`
  display: flex;
  align-items: center;
  gap: 0.45rem;
  padding: 0.3rem 0.7rem;
  font-size: 0.72rem;
  border-left: 2px solid ${(p) => (p.$active ? "var(--kc-accent)" : "transparent")};
  background: ${(p) => (p.$active ? "var(--kc-accent-wash)" : "none")};

  & + & {
    border-top: 1px solid var(--kc-border-subtle);
  }
`;

const MemberIcon = styled.span`
  color: var(--kc-text-muted);
  line-height: 0;
`;

const MemberName = styled.span`
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--kc-text);
`;

const Tag = styled.span`
  margin-left: 0.4rem;
  font-size: 0.58rem;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: var(--kc-text-muted);
  border: 1px dashed var(--kc-border);
  border-radius: 3px;
  padding: 0 0.25rem;
`;

const ActiveMark = styled.span`
  font-size: 0.58rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  color: var(--kc-accent);
`;

const StateChip = styled.span<{ $tone: Tone }>`
  font-size: 0.65rem;
  color: ${(p) => toneColour(p.$tone)};
  white-space: nowrap;
`;

const CardFoot = styled.div`
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 0.5rem;
  padding: 0.5rem 0.7rem;
  border-top: 1px solid var(--kc-border-subtle);
`;

const ConfirmRow = styled(CardFoot)`
  font-size: 0.7rem;
  color: var(--kc-text);
`;

const DiscardRow = styled(ConfirmRow)`
  padding: 0.5rem 0.85rem;
  border-bottom: 1px solid var(--kc-border);
  background: var(--kc-surface-raised);
`;

const Owner = styled.span`
  display: inline-flex;
  align-items: center;
  gap: 0.3rem;
  font-size: 0.65rem;
  color: var(--kc-text-muted);
`;

const Actions = styled.div`
  display: flex;
  gap: 0.35rem;
  margin-left: auto;
`;

const Field = styled.div`
  display: flex;
  flex-direction: column;
  gap: 0.3rem;
`;

const FieldLabel = styled.label`
  font-size: 0.65rem;
  font-weight: 600;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--kc-text-muted);
`;

const FieldHint = styled.p`
  margin: 0;
  font-size: 0.65rem;
  color: var(--kc-text-muted);
  opacity: 0.8;
`;

const inputBase = css`
  color: var(--kc-text);
  background: var(--kc-surface-raised);
  border: 1px solid var(--kc-border);
  border-radius: 4px;
  padding: 0.35rem 0.5rem;
  font-size: 0.8rem;
  font-family: inherit;
  min-width: 0;
  max-width: 100%;

  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 1px;
  }
`;

const TextInput = styled.input`
  ${inputBase}
`;

const Select = styled.select`
  ${inputBase}
  cursor: pointer;
`;

const Section = styled.section`
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
`;

const SectionHead = styled.div`
  display: flex;
  align-items: baseline;
  gap: 0.5rem;
`;

const SectionLabel = styled.h3`
  margin: 0;
  font-size: 0.65rem;
  font-weight: 600;
  letter-spacing: 0.1em;
  text-transform: uppercase;
  color: var(--kc-text-muted);
`;

const Count = styled.span<{ $over?: boolean }>`
  font-size: 0.65rem;
  color: ${(p) => (p.$over ? "var(--kc-danger)" : "var(--kc-text-muted)")};
  margin-right: auto;
`;

const Rows = styled.div`
  border: 1px solid var(--kc-border);
  border-radius: 6px;
  overflow: hidden;
`;

const Row = styled.label<{ $off?: boolean }>`
  display: flex;
  align-items: center;
  gap: 0.55rem;
  padding: 0.35rem 0.6rem;
  cursor: pointer;
  opacity: ${(p) => (p.$off ? 0.55 : 1)};

  & + & {
    border-top: 1px solid var(--kc-border-subtle);
  }
  &:hover {
    background: var(--kc-surface-raised);
  }
`;

const EmptyRow = styled.p`
  margin: 0;
  padding: 0.5rem 0.6rem;
  font-size: 0.7rem;
  color: var(--kc-text-muted);
`;

const Checkbox = styled.input`
  accent-color: var(--kc-accent);
  width: 0.95rem;
  height: 0.95rem;
  flex: none;
  margin: 0;
`;

const RowText = styled.span`
  display: flex;
  flex-direction: column;
  flex: 1;
  min-width: 0;
`;

const RowName = styled.span`
  font-size: 0.76rem;
  color: var(--kc-text);
  overflow-wrap: anywhere;
`;

const RowSub = styled.span`
  font-size: 0.64rem;
  color: var(--kc-text-muted);
`;

const RowNote = styled.span`
  font-size: 0.62rem;
  color: var(--kc-text-muted);
  font-style: italic;
`;

const Badge = styled.span<{ $tone: Tone }>`
  font-size: 0.58rem;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
  color: ${(p) => toneColour(p.$tone)};
  border: 1px solid currentColor;
  border-radius: 3px;
  padding: 0.05rem 0.3rem;
  flex: none;
`;

const Chips = styled.div`
  display: flex;
  flex-wrap: wrap;
  gap: 0.3rem;
`;

const Chip = styled.button<{ $on: boolean }>`
  font-size: 0.65rem;
  font-family: inherit;
  padding: 0.15rem 0.5rem;
  border-radius: 999px;
  cursor: pointer;
  border: 1px solid ${(p) => (p.$on ? "var(--kc-accent)" : "var(--kc-border)")};
  background: ${(p) => (p.$on ? "var(--kc-accent-wash)" : "none")};
  color: ${(p) => (p.$on ? "var(--kc-accent)" : "var(--kc-text-muted)")};

  &:focus-visible {
    outline: 2px solid var(--kc-accent);
    outline-offset: 1px;
  }
`;

const InlineToggle = styled.label`
  display: flex;
  align-items: center;
  gap: 0.45rem;
  font-size: 0.7rem;
  color: var(--kc-text-muted);
  cursor: pointer;
`;

const FormFoot = styled.div`
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 0.5rem;
  padding: 0.6rem 0.85rem;
  border-top: 1px solid var(--kc-border);
  background: var(--kc-surface-raised);
  flex: none;
`;

const ErrorText = styled.span`
  font-size: 0.68rem;
  color: var(--kc-danger);
  flex-basis: 100%;
  order: 3;
`;

const NoteText = styled.p`
  margin: 0;
  font-size: 0.7rem;
  line-height: 1.45;
  color: var(--kc-warn);
  background: var(--kc-warn-bg);
  border-radius: 4px;
  padding: 0.4rem 0.55rem;
`;
