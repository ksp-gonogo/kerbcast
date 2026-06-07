/**
 * Developer panel - shown only when debug mode is enabled.
 *
 * Exposes:
 * - Per-camera: layer checkboxes (NEAR/SCALED/GALAXY), auto-shed marker,
 *   degrade slider, bitrate readout (encoder vs target kbps)
 * - Stagger/profile line polling GET /profile once per second
 * - Per-tile RTC inbound stats (pkts/bytes/framesDecoded/jitter)
 */

import type { CameraState, InboundVideoStats, KerbcamClient } from "@jonpepler/kerbcam";
import { Layer } from "@jonpepler/kerbcam";
import { useKerbcamCameras } from "@jonpepler/kerbcam-react";
import { useEffect, useRef, useState } from "react";
import styled from "styled-components";

const ALL_LAYERS = [Layer.Near, Layer.Scaled, Layer.Galaxy] as const;

interface ProfileData {
  staggerBudget?: number;
  kerbcamFrameMs?: number;
  kspFps?: number;
}

interface DevPanelProps {
  client: KerbcamClient;
  /** flightIds currently shown in tiles (for per-tile stats) */
  tileFlightIds: (number | null)[];
}

export function DevPanel({ client, tileFlightIds }: DevPanelProps): React.JSX.Element {
  const cameras = useKerbcamCameras();
  const [profile, setProfile] = useState<ProfileData | null>(null);
  const [profileError, setProfileError] = useState(false);
  const [rtcStats, setRtcStats] = useState<Map<number, InboundVideoStats>>(new Map());

  // /profile polling
  useEffect(() => {
    let cancelled = false;
    async function poll() {
      try {
        const r = await fetch("/profile");
        if (!r.ok) {
          if (!cancelled) { setProfile(null); setProfileError(true); }
          return;
        }
        const data = (await r.json()) as ProfileData;
        if (!cancelled) { setProfile(data); setProfileError(false); }
      } catch {
        if (!cancelled) { setProfile(null); setProfileError(true); }
      }
    }

    void poll();
    const interval = setInterval(() => { void poll(); }, 1000);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, []);

  // RTC stats polling
  useEffect(() => {
    let cancelled = false;
    async function poll() {
      try {
        const stats = await client.inboundVideoStats();
        if (!cancelled) setRtcStats(stats);
      } catch {
        // ignore
      }
    }

    void poll();
    const interval = setInterval(() => { void poll(); }, 1000);
    return () => {
      cancelled = true;
      clearInterval(interval);
    };
  }, [client]);

  return (
    <Root role="region" aria-label="Developer panel">
      <Section>
        <SectionTitle>Telemetry</SectionTitle>
        {profileError ? (
          <Mono>telemetry off</Mono>
        ) : profile ? (
          <Mono>
            stagger: {profile.staggerBudget ?? "?"} cam/frame | kerbcam{" "}
            {Number(profile.kerbcamFrameMs ?? 0).toFixed(1)}ms | KSP{" "}
            {Number(profile.kspFps ?? 0).toFixed(0)} fps
          </Mono>
        ) : (
          <Mono>loading...</Mono>
        )}
      </Section>

      <Section>
        <SectionTitle>Cameras</SectionTitle>
        {cameras.length === 0 ? (
          <Muted>No cameras connected</Muted>
        ) : (
          cameras.map((cam) => (
            <CameraRow
              key={cam.flightId}
              cam={cam}
              client={client}
              rtcStats={rtcStats.get(cam.flightId)}
            />
          ))
        )}
      </Section>

      {tileFlightIds.some((id) => id !== null) && (
        <Section>
          <SectionTitle>Tile RTC stats</SectionTitle>
          {tileFlightIds.map((flightId, i) => {
            if (flightId === null) return null;
            const stats = rtcStats.get(flightId);
            return (
              <TileStats key={i}>
                <strong>Tile {i + 1}</strong> (flight {flightId}):{" "}
                {stats
                  ? `pkts=${stats.packetsReceived} bytes=${stats.bytesReceived} dec=${stats.framesDecoded ?? "?"} jitter=${stats.jitter !== undefined ? stats.jitter.toFixed(3) : "?"}`
                  : "no stats"}
              </TileStats>
            );
          })}
        </Section>
      )}
    </Root>
  );
}

// ---------------------------------------------------------------------------
// Per-camera row in the dev panel
// ---------------------------------------------------------------------------

interface CameraRowProps {
  cam: CameraState;
  client: KerbcamClient;
  rtcStats?: InboundVideoStats;
}

function CameraRow({ cam, client }: CameraRowProps): React.JSX.Element {
  const handle = client.camera(cam.flightId);
  const degRef = useRef<number>(cam.degradeLevel ?? 0);

  const effectiveLayers = new Set<Layer>(cam.layers);
  const operatorLayers = new Set<Layer>(cam.operatorLayers);

  const handleLayerChange = (layer: Layer, checked: boolean) => {
    const next = new Set<Layer>(operatorLayers);
    if (checked) next.add(layer); else next.delete(layer);
    void handle.setLayers([...next]);
  };

  const handleDegrade = (value: number) => {
    degRef.current = value;
    void handle.setDegrade(value);
  };

  const encKbps = cam.encoderBitrateBps > 0 ? Math.round(cam.encoderBitrateBps / 1000) : null;
  const tgtKbps = cam.targetBitrateBps > 0 ? Math.round(cam.targetBitrateBps / 1000) : null;
  const showTarget =
    tgtKbps !== null &&
    encKbps !== null &&
    Math.abs(tgtKbps - encKbps) / Math.max(encKbps, 1) > 0.05;

  return (
    <CamBlock>
      <CamName>
        {cam.cameraName ?? cam.partTitle} (flight {cam.flightId})
      </CamName>
      <CamControls>
        {ALL_LAYERS.map((layer) => {
          const isOperator = operatorLayers.has(layer);
          const isEffective = effectiveLayers.has(layer);
          const autoShed = isOperator && !isEffective;
          return (
            <LayerField key={layer}>
              <input
                type="checkbox"
                id={`kc-layer-${cam.flightId}-${layer}`}
                aria-label={`${layer} layer`}
                checked={isOperator}
                onChange={(e) => handleLayerChange(layer, e.target.checked)}
              />
              <label htmlFor={`kc-layer-${cam.flightId}-${layer}`}>
                {layer}
              </label>
              {autoShed && <AutoShed aria-label="auto-shed">(auto-shed)</AutoShed>}
            </LayerField>
          );
        })}
      </CamControls>
      <DegradeRow>
        <label htmlFor={`kc-degrade-${cam.flightId}`}>Degrade</label>
        <input
          id={`kc-degrade-${cam.flightId}`}
          aria-label="Degrade"
          type="range"
          min={0}
          max={100}
          step={5}
          defaultValue={Math.round((cam.degradeLevel ?? 0) * 100)}
          onChange={(e) => handleDegrade(Number(e.target.value) / 100)}
        />
        <span>{Math.round((cam.degradeLevel ?? 0) * 100)}%</span>
        {showTarget && (
          <Muted>
            server: {tgtKbps}kbps
          </Muted>
        )}
      </DegradeRow>
      <BitrateRow>
        Bitrate: {encKbps !== null ? `${encKbps} kbps` : "-"}
        {showTarget && encKbps !== null && tgtKbps !== null && (
          <Warn> (target {tgtKbps} kbps)</Warn>
        )}
      </BitrateRow>
    </CamBlock>
  );
}

// ---------------------------------------------------------------------------
// Styled
// ---------------------------------------------------------------------------

const Root = styled.div`
  border-top: 1px solid var(--kc-border);
  background: var(--kc-surface);
  padding: 0.75rem 1rem;
  display: flex;
  flex-direction: column;
  gap: 1rem;
`;

const Section = styled.div`
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
`;

const SectionTitle = styled.h3`
  margin: 0;
  font-size: 0.75rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--kc-text-muted);
`;

const Mono = styled.code`
  font-size: 0.75rem;
  color: var(--kc-text);
`;

const Muted = styled.span`
  font-size: 0.75rem;
  color: var(--kc-text-muted);
`;

const Warn = styled.span`
  color: var(--kc-warn);
  font-size: 0.75rem;
`;

const CamBlock = styled.div`
  border: 1px solid var(--kc-border);
  border-radius: 4px;
  padding: 0.5rem 0.75rem;
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
  background: var(--kc-surface-raised);
`;

const CamName = styled.div`
  font-size: 0.8rem;
  font-weight: 600;
  color: var(--kc-text);
`;

const CamControls = styled.div`
  display: flex;
  gap: 1rem;
  flex-wrap: wrap;
`;

const LayerField = styled.span`
  display: flex;
  align-items: center;
  gap: 0.25rem;
  font-size: 0.8rem;
  color: var(--kc-text);

  input[type="checkbox"] {
    accent-color: var(--kc-accent);
  }
`;

const AutoShed = styled.span`
  font-size: 0.7rem;
  color: var(--kc-warn);
`;

const DegradeRow = styled.div`
  display: flex;
  align-items: center;
  gap: 0.5rem;
  font-size: 0.8rem;
  color: var(--kc-text);

  input[type="range"] {
    accent-color: var(--kc-accent);
    width: 80px;
  }
`;

const BitrateRow = styled.div`
  font-size: 0.75rem;
  color: var(--kc-text-muted);
`;

const TileStats = styled.div`
  font-size: 0.75rem;
  color: var(--kc-text-muted);
  font-family: ui-monospace, monospace;
`;
