import type { KerbcamClient } from "@jonpepler/kerbcam";
import { KerbcamProvider, useKerbcamCameras } from "@jonpepler/kerbcam-react";
import { useEffect, useMemo, useRef, useState, useSyncExternalStore } from "react";
import styled from "styled-components";
import { ConnectionManager } from "./connectionManager";
import { DevPanel } from "./DevPanel";
import { Grid } from "./Grid";
import { Header } from "./Header";
import { Settings } from "./SettingsPanel";
import { ShedBanner } from "./ShedBanner";
import {
  applyTheme,
  loadDebug,
  loadShowStatic,
  loadTheme,
  saveDebug,
  saveShowStatic,
  saveTheme,
} from "./settings";
import type { ThemePreference } from "./settings";
import { loadTiles, saveTiles, seedTiles } from "./tiles";
import type { Tile as TileData } from "./tiles";

interface AppProps {
  client: KerbcamClient;
}

/**
 * Root app component. Accepts a KerbcamClient injected from main.tsx (or tests).
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
  const [settingsOpen, setSettingsOpen] = useState(false);

  // Tile state
  const storedTiles = loadTiles();
  const [tiles, setTiles] = useState<TileData[]>(() => storedTiles ?? []);
  const [tilesSeeded, setTilesSeeded] = useState<boolean>(() => storedTiles !== null);

  const tileFlightIds = useMemo(
    () => tiles.map((t) => t.flightId),
    [tiles],
  );

  return (
    <KerbcamProvider client={client}>
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
            onThemeChange={(t: ThemePreference) => { saveTheme(t); applyTheme(t); setTheme(t); }}
            onDebugChange={(d: boolean) => { saveDebug(d); setDebug(d); }}
            onShowStaticChange={(s: boolean) => { saveShowStatic(s); setShowStaticExplicit(s); }}
            onClose={() => setSettingsOpen(false)}
          />
        )}
        <ShedBanner client={client} />
        <MainArea>
          {/* CameraSeeder uses useKerbcamCameras inside KerbcamProvider */}
          <CameraSeeder
            tilesSeeded={tilesSeeded}
            onSeed={(seeded) => {
              setTiles(seeded);
              saveTiles(seeded);
              setTilesSeeded(true);
            }}
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
    </KerbcamProvider>
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
  const cameras = useKerbcamCameras();

  useEffect(() => {
    if (tilesSeeded) return;
    if (cameras.length === 0) return;
    onSeed(seedTiles(cameras.map((c) => c.flightId)));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cameras, tilesSeeded]);

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
