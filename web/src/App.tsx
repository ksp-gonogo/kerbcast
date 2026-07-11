import type { KerbcastClient } from "@ksp-gonogo/kerbcast";
import { KerbcastProvider, useKerbcastCameras } from "@ksp-gonogo/kerbcast-react";
import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import styled from "styled-components";
import { ConnectionManager } from "./connectionManager";
import { DevPanel } from "./DevPanel";
import { ErrorToast } from "./ErrorToast";
import { Grid } from "./Grid";
import { Header } from "./Header";
import { Settings } from "./SettingsPanel";
import { ShedBanner } from "./ShedBanner";
import {
  applyTheme,
  loadDebug,
  loadShowPerfWarnings,
  loadShowStatic,
  loadTheme,
  saveDebug,
  saveShowPerfWarnings,
  saveShowStatic,
  saveTheme,
} from "./settings";
import type { ThemePreference } from "./settings";
import { loadTiles, reconcileTiles, saveTiles, seedTiles } from "./tiles";
import type { Tile as TileData } from "./tiles";

interface AppProps {
  client: KerbcastClient;
}

/**
 * Root app component. Accepts a KerbcastClient injected from main.tsx (or tests).
 * Creates a connection manager, manages tile state, and renders the full UI.
 */
export function App({ client }: AppProps): React.JSX.Element {
  // Connection manager -- stable per client
  const managerRef = useRef<ConnectionManager | null>(null);
  if (managerRef.current === null) {
    managerRef.current = new ConnectionManager(client);
  }
  const manager = managerRef.current;

  const status = useSyncExternalStore(
    (cb) => manager.subscribe(cb),
    () => manager.getStatus(),
  );

  useEffect(() => {
    manager.start();
    return () => manager.stop();
  }, [manager]);

  // Theme + debug settings
  const [theme, setTheme] = useState<ThemePreference>(() => {
    const t = loadTheme();
    applyTheme(t);
    return t;
  });
  const [debug, setDebug] = useState<boolean>(() => loadDebug());
  /*
   * showStaticExplicit: null = auto (resolve from prefers-reduced-motion),
   * true/false = explicit user override stored in localStorage.
   */
  const [showStaticExplicit, setShowStaticExplicit] = useState<boolean | null>(
    () => loadShowStatic(),
  );
  const showStatic =
    showStaticExplicit !== null
      ? showStaticExplicit
      : !window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  const [showPerfWarnings, setShowPerfWarnings] = useState<boolean>(() => loadShowPerfWarnings());
  const [settingsOpen, setSettingsOpen] = useState(false);

  // Tile state
  const storedTiles = loadTiles();
  const [tiles, setTiles] = useState<TileData[]>(() => storedTiles ?? []);
  const [tilesSeeded, setTilesSeeded] = useState<boolean>(() => storedTiles !== null);

  const tileFlightIds = useMemo(
    () => tiles.map((t) => t.flightId),
    [tiles],
  );

  const handleReconcile = useCallback((reconciled: TileData[]) => {
    setTiles(reconciled);
    saveTiles(reconciled);
  }, []);

  return (
    <KerbcastProvider client={client}>
      <PageShell>
        <Header
          status={status}
          client={client}
          onOpenSettings={() => setSettingsOpen((v) => !v)}
        />
        {settingsOpen && (
          <Settings
            theme={theme}
            debug={debug}
            showStatic={showStatic}
            showPerfWarnings={showPerfWarnings}
            onThemeChange={(t: ThemePreference) => { saveTheme(t); applyTheme(t); setTheme(t); }}
            onDebugChange={(d: boolean) => { saveDebug(d); setDebug(d); }}
            onShowStaticChange={(s: boolean) => { saveShowStatic(s); setShowStaticExplicit(s); }}
            onShowPerfWarningsChange={(v: boolean) => { saveShowPerfWarnings(v); setShowPerfWarnings(v); }}
            onClose={() => setSettingsOpen(false)}
          />
        )}
        {showPerfWarnings && <ShedBanner client={client} />}
        <ErrorToast client={client} />
        <MainArea>
          {/* CameraSeeder and CameraReconciler use useKerbcastCameras inside KerbcastProvider */}
          <CameraSeeder
            tilesSeeded={tilesSeeded}
            onSeed={(seeded) => {
              setTiles(seeded);
              saveTiles(seeded);
              setTilesSeeded(true);
            }}
          />
          <CameraReconciler
            tiles={tiles}
            onReconcile={handleReconcile}
          />
          <Grid
            tiles={tiles}
            onTilesChange={setTiles}
            showDebugInfo={debug}
            showStatic={showStatic}
          />
          {debug && (
            <DevPanel client={client} tileFlightIds={tileFlightIds} />
          )}
        </MainArea>
      </PageShell>
    </KerbcastProvider>
  );
}

// ---------------------------------------------------------------------------
// CameraSeeder: seeds default tiles once on first camera arrival
// ---------------------------------------------------------------------------

interface CameraSeederProps {
  tilesSeeded: boolean;
  onSeed: (tiles: TileData[]) => void;
}

function CameraSeeder({ tilesSeeded, onSeed }: CameraSeederProps): null {
  const cameras = useKerbcastCameras();

  useEffect(() => {
    if (tilesSeeded) return;
    if (cameras.length === 0) return;
    onSeed(seedTiles(cameras));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cameras, tilesSeeded]);

  return null;
}

// ---------------------------------------------------------------------------
// CameraReconciler: rebinds stale tile flightIds after KSP revert/recover
// ---------------------------------------------------------------------------

interface CameraReconcilerProps {
  tiles: TileData[];
  onReconcile: (tiles: TileData[]) => void;
}

/*
 * Watches the live camera list and rebinds any tile whose flightId has gone
 * missing but whose stable key (vesselName|partName|cameraName) matches a
 * live camera under a new flightId. This covers KSP revert/recover, which
 * reassigns part.flightID while the camera itself survives.
 *
 * Tiles without a key (stored before this feature shipped) are left as
 * "reconnecting" until the user manually rebinds them.
 */
function CameraReconciler({ tiles, onReconcile }: CameraReconcilerProps): null {
  const cameras = useKerbcastCameras();

  useEffect(() => {
    if (cameras.length === 0) return;
    const reconciled = reconcileTiles(tiles, cameras);
    if (reconciled !== tiles) onReconcile(reconciled);
  }, [cameras, tiles, onReconcile]);

  return null;
}

// ---------------------------------------------------------------------------
// Styled
// ---------------------------------------------------------------------------

const PageShell = styled.div`
  display: flex;
  flex-direction: column;
  /* A definite height (not just min-height) so flex children further down --
     notably the spotlight Stage -- get a bounded height to size against.
     Without it, the spotlit feed's aspect-ratio height resolves against an
     indefinite parent and grows past the viewport. */
  height: 100dvh;
  background: var(--kc-bg);
  position: relative;
`;

const MainArea = styled.main`
  flex: 1;
  min-height: 0;
  /* The tile area scrolls internally; the header/banner stay put above it. */
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  /* Subtle vignette to ground the tile grid */
  background: radial-gradient(ellipse at 50% 0%, transparent 60%, rgba(0,0,0,0.06) 100%);
`;
