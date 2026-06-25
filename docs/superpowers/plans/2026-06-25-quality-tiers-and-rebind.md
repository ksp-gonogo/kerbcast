# Quality Tiers + Rebind Robustness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the separate `Width`/`Height`/`BitrateBps` knobs with one `MaxQuality` tier preset (resolution + bitrate bundled), allow HD/Full-HD opt-in with the adaptive ladder protecting framerate, and make the web robust when a camera's flightId disappears.

**Architecture:** Tier→values resolution is a pure Unity-free helper consumed by `KerbcamSettings.Load()`; everything downstream already reads `settings.Width/Height/BitrateBps`, so ring allocation and sidecar launch args stay in lockstep automatically. Web changes are confined to surfacing the existing client `error` event and a reconnecting tile state. No protocol/wire/sidecar-encode changes.

**Tech Stack:** C# / .NET Framework 4.8 (plugin) with net10 console test harnesses; TypeScript + React + vitest (web/client-sdk).

## Global Constraints

- Plugin builds zero-warnings: `dotnet build Plugin/Kerbcam.csproj -c Release /p:KspManaged=$KspManaged /p:KspGameData=$KspGameData` (paths in the plugin-build memory).
- Unity-free test harnesses live in `Plugin/<Name>.Tests/` (net10, no KSP refs) and MUST be excluded from the plugin glob via `<Compile Remove="<Name>.Tests/**/*.cs" />` in `Kerbcam.csproj`.
- H.264 even-dimension rule: every tier resolution must be even.
- Default install behavior unchanged: `MaxQuality` default = `sd` = 1024×576; `sd` bitrate = 0 (inherit the sidecar's per-backend default) so the Deck stays at its current 4 Mbps and software stays at 1.5 Mbps.
- Precedence: an explicit `Width`/`Height`/`BitrateBps` key in either settings.cfg overrides the tier-derived value.
- No em-dashes in any output; comment style = `/* */` blocks for multi-line.
- Conventional Commits; no `Co-Authored-By` trailer.

---

## Phase A — Plugin quality tiers

### Task 1: Pure QualityTier resolver (Phase A — plugin)

**Files:**
- Create: `Plugin/Kerbcam/QualityTier.cs`
- Create: `Plugin/QualityTier.Tests/QualityTier.Tests.csproj` (net10 console, mirror `Plugin/MmapFrameRing.Tests/*.csproj`)
- Create: `Plugin/QualityTier.Tests/Program.cs`
- Modify: `Plugin/Kerbcam.csproj` (add `<Compile Remove="QualityTier.Tests/**/*.cs" />`)

**Interfaces:**
- Produces:
  - `enum QualityTier { Low, Sd, Hd, FullHd }`
  - `static class QualityTiers`
    - `static bool TryParse(string s, out QualityTier tier)` — case-insensitive; accepts `low|sd|hd|fullhd` (and `fullhd`/`full-hd`/`fhd` aliases). Returns false on unknown.
    - `static (int width, int height, int bitrateBps) Values(QualityTier t)` — Low=(640,360,2_000_000), Sd=(1024,576,0), Hd=(1280,720,6_000_000), FullHd=(1920,1080,10_000_000).
    - `static (int width, int height, int bitrateBps) Resolve(QualityTier tier, int? explicitWidth, int? explicitHeight, int? explicitBitrateBps)` — start from `Values(tier)`, then override each field that is non-null. (Bitrate override applies even if 0.)

- [ ] **Step 1: Write failing tests** (`Plugin/QualityTier.Tests/Program.cs`)

```csharp
using System;
using Kerbcam;

int failures = 0;
void Check(bool cond, string msg) { Console.WriteLine((cond ? "  ok   " : "  FAIL ") + msg); if (!cond) failures++; }

// Parse
Check(QualityTiers.TryParse("hd", out var t1) && t1 == QualityTier.Hd, "parse 'hd'");
Check(QualityTiers.TryParse("SD", out var t2) && t2 == QualityTier.Sd, "parse 'SD' case-insensitive");
Check(QualityTiers.TryParse("fullhd", out var t3) && t3 == QualityTier.FullHd, "parse 'fullhd'");
Check(!QualityTiers.TryParse("ultra", out _), "unknown tier rejected");

// Values: every tier even dims
foreach (QualityTier t in Enum.GetValues(typeof(QualityTier))) {
    var (w, h, _) = QualityTiers.Values(t);
    Check(w % 2 == 0 && h % 2 == 0, $"{t} dims even ({w}x{h})");
}
// SD == today's default, bitrate 0 (inherit)
Check(QualityTiers.Values(QualityTier.Sd) == (1024, 576, 0), "SD = 1024x576, bitrate inherit");
Check(QualityTiers.Values(QualityTier.Hd) == (1280, 720, 6_000_000), "HD = 1280x720 @ 6Mbps");

// Resolve precedence: explicit overrides tier
Check(QualityTiers.Resolve(QualityTier.Hd, null, null, null) == (1280, 720, 6_000_000), "HD resolves to tier values");
Check(QualityTiers.Resolve(QualityTier.Hd, 800, 600, null) == (800, 600, 6_000_000), "explicit W/H override tier");
Check(QualityTiers.Resolve(QualityTier.Hd, null, null, 3_000_000) == (1280, 720, 3_000_000), "explicit bitrate overrides tier");

Console.WriteLine(failures == 0 ? "ALL PASS" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
```

- [ ] **Step 2: Run, verify it fails to build** (`Kerbcam` type missing).
Run: `dotnet run --project Plugin/QualityTier.Tests`
Expected: build error (QualityTiers undefined).

- [ ] **Step 3: Implement `Plugin/Kerbcam/QualityTier.cs`**

```csharp
namespace Kerbcam
{
    public enum QualityTier { Low, Sd, Hd, FullHd }

    /* Single source of the resolution+bitrate bundle for each named tier.
       Pure and Unity-free so it is unit-testable; KerbcamSettings consumes it. */
    public static class QualityTiers
    {
        public static bool TryParse(string s, out QualityTier tier)
        {
            tier = QualityTier.Sd;
            if (string.IsNullOrWhiteSpace(s)) return false;
            switch (s.Trim().ToLowerInvariant())
            {
                case "low": tier = QualityTier.Low; return true;
                case "sd": tier = QualityTier.Sd; return true;
                case "hd": tier = QualityTier.Hd; return true;
                case "fullhd": case "full-hd": case "fhd": tier = QualityTier.FullHd; return true;
                default: return false;
            }
        }

        public static (int width, int height, int bitrateBps) Values(QualityTier t)
        {
            switch (t)
            {
                case QualityTier.Low: return (640, 360, 2_000_000);
                case QualityTier.Hd: return (1280, 720, 6_000_000);
                case QualityTier.FullHd: return (1920, 1080, 10_000_000);
                case QualityTier.Sd:
                default: return (1024, 576, 0);
            }
        }

        public static (int width, int height, int bitrateBps) Resolve(
            QualityTier tier, int? explicitWidth, int? explicitHeight, int? explicitBitrateBps)
        {
            var (w, h, b) = Values(tier);
            return (explicitWidth ?? w, explicitHeight ?? h, explicitBitrateBps ?? b);
        }
    }
}
```

- [ ] **Step 4: Add test-glob exclusion** to `Plugin/Kerbcam.csproj` next to the existing `*.Tests` removes:
```xml
<Compile Remove="QualityTier.Tests/**/*.cs" />
```

- [ ] **Step 5: Run tests, verify pass.** Run: `dotnet run --project Plugin/QualityTier.Tests` → `ALL PASS`.

- [ ] **Step 6: Build plugin (zero warnings).** Run the Global-Constraints build command → `0 Warning(s) 0 Error(s)`.

- [ ] **Step 7: Commit.**
```bash
git add Plugin/Kerbcam/QualityTier.cs Plugin/QualityTier.Tests Plugin/Kerbcam.csproj
git commit -m "feat(plugin): add QualityTier resolution+bitrate resolver"
```

### Task 2: Wire MaxQuality into KerbcamSettings (Phase A — plugin)

**Files:**
- Modify: `Plugin/Kerbcam/KerbcamSettings.cs` (defaults ~62-83; `ApplySettings` ~355-358; `Load` ~287-330)

**Interfaces:**
- Consumes: `QualityTiers.TryParse`, `QualityTiers.Resolve` (Task A1).
- Produces: `public QualityTier MaxQuality { get; private set; } = QualityTier.Sd;` and a `Load()` that resolves dims/bitrate from the tier unless explicit keys are present.

- [ ] **Step 1: Implementation — parse + resolve.** In `KerbcamSettings`:
  - Add property `public QualityTier MaxQuality { get; private set; } = QualityTier.Sd;`
  - In the scalar-apply section (where `ApplyInt(node, "Width", ...)` lives), capture whether explicit keys exist by reading them as nullables BEFORE applying, then resolve:

```csharp
/* MaxQuality is the primary knob: it sets resolution + bitrate. Explicit
   Width/Height/BitrateBps keys still win (advanced override). Resolve from
   the merged view so a user file's MaxQuality or explicit dims both work. */
string tierRaw = GetString(node, "MaxQuality");           // existing string getter, or ApplyString
if (tierRaw != null && QualityTiers.TryParse(tierRaw, out var tier)) settings.MaxQuality = tier;
int? explicitW = TryParseIntField(node, "Width");
int? explicitH = TryParseIntField(node, "Height");
int? explicitB = TryParseIntField(node, "BitrateBps");
var (rw, rh, rb) = QualityTiers.Resolve(settings.MaxQuality, explicitW, explicitH, explicitB);
settings.Width = rw; settings.Height = rh; settings.BitrateBps = rb;
```
  - Note: `ApplySettings` runs once per cfg layer (defaults then user). Resolve must run after both layers merge, OR run per-layer with the nullable-explicit capture so the user layer's explicit keys still win. Keep the existing per-layer `ApplyInt` calls but follow them with the resolve block so the final values reflect tier + overrides. Update the load log line (~328) to include `tier={settings.MaxQuality}`.

- [ ] **Step 2: Backward-compat reasoning (no test infra for ConfigNode).** Document in a comment: with no `MaxQuality` key and no explicit dims, `MaxQuality` stays `Sd` → (1024,576,0) = today's exact default. An existing file with `Width=1024 Height=576` resolves identically (explicit == tier). An `hd` file with no explicit dims → (1280,720,6_000_000).

- [ ] **Step 3: Build plugin (zero warnings).** Run the build command → `0/0`.

- [ ] **Step 4: Verify default unchanged via a focused harness** — extend `Plugin/QualityTier.Tests/Program.cs` with the precedence matrix already covering this (Resolve tests in A1 are the proof; ConfigNode parsing is exercised at runtime/live). Re-run → `ALL PASS`.

- [ ] **Step 5: Commit.**
```bash
git add Plugin/Kerbcam/KerbcamSettings.cs
git commit -m "feat(plugin): MaxQuality tier preset drives dims+bitrate, raw keys override"
```

### Task 3: Adaptive ladder on by default (Phase A — plugin)

**Files:**
- Modify: `Plugin/Kerbcam/KerbcamSettings.cs:83` (`AdaptiveQuality` default)

- [ ] **Step 1:** Change `public bool AdaptiveQuality { get; private set; } = false;` → `= true;` and update the adjacent comment to note it is on by default so a higher `MaxQuality` ceiling auto-degrades under load (never promoting above the ceiling).
- [ ] **Step 2: Build (zero warnings).**
- [ ] **Step 3: Commit.**
```bash
git commit -am "feat(plugin): enable adaptive quality degrade by default"
```

### Task 4: Even-dims lockstep guard (test) + settings docs (Phase A — plugin)

**Files:**
- Modify: `Plugin/QualityTier.Tests/Program.cs` (add the invariant test below — may already pass from A1)
- Modify: `GameData/Kerbcam/settings.cfg` (the shipped defaults file: add a documented `MaxQuality = sd` key + comment block listing `low|sd|hd|fullhd` and the override precedence)
- Modify: `Plugin/Kerbcam/KerbcamSettings.cs` header comment (the `Width/Height` doc block ~21) to describe `MaxQuality`

- [ ] **Step 1:** Add/confirm the test asserting **every** tier yields even, ≥2 dims (the H.264 + ring requirement that protects the lockstep): already present in A1 Step 1; keep it.
- [ ] **Step 2:** Add the `MaxQuality` stanza + comment to `GameData/Kerbcam/settings.cfg`, documenting that explicit `Width`/`Height`/`BitrateBps` override the tier, and that `hd`/`fullhd` are higher ceilings the adaptive ladder degrades from. Note Full HD is experimental + memory-heavy.
- [ ] **Step 3: Commit.**
```bash
git add GameData/Kerbcam/settings.cfg Plugin/Kerbcam/KerbcamSettings.cs Plugin/QualityTier.Tests/Program.cs
git commit -m "docs(plugin): document MaxQuality tiers in settings.cfg"
```

---

## Phase B — Web rebind robustness

### Task 5: Surface the sidecar error reply (Phase B — web)

**Files:**
- Modify: `web/src/App.tsx` (subscribe to the client `error` event, show a transient notice)
- Possibly create: `web/src/ErrorToast.tsx` (small transient notice component)
- Test: `web/src/__tests__/errorNotice.test.tsx` (or co-located, matching the repo's vitest layout)

**Interfaces:**
- Consumes: `client.on("error", (e: ErrorPayload) => ...)` — the event already exists (`client-sdk/typescript/src/client.ts:220`). Confirm the client emits it on a `ServerMessage` of type `error`; if it parses but does not emit, wire the emit in `handleServerMessage`.

- [ ] **Step 1: Write failing test** — render the app/hook with a mocked client, emit an `error` event, assert a notice with the error text appears and auto-dismisses (or is dismissible). (Use the existing vitest + testing-library setup; mirror an existing web test for client mocking.)
- [ ] **Step 2: Run, verify fail.** `pnpm --filter kerbcam-web test` → the new test fails.
- [ ] **Step 3: Implement** — in `App.tsx`, subscribe to `client.on("error", ...)` (cleanup on unmount), push into a small state list, render a transient `ErrorToast`. If `handleServerMessage` doesn't emit `error`, add the emit there in `client.ts`.
- [ ] **Step 4: Run tests, verify pass + `tsc` clean.** `pnpm --filter kerbcam-web test` and the package typecheck.
- [ ] **Step 5: Rebuild committed dist** (`pnpm --filter kerbcam-web build`).
- [ ] **Step 6: Commit.**
```bash
git add web/src web/dist/index.html client-sdk/typescript/src/client.ts
git commit -m "feat(web): surface sidecar error replies as a transient notice"
```

### Task 6: Reconnecting/gone tile state (Phase B — web)

**Files:**
- Modify: `web/src/Tile.tsx` (when the tile's `flightId` is not in the current cameras list, render a "camera reconnecting / gone" state instead of a dead feed)
- Test: `web/src/__tests__/tileMissing.test.tsx`

**Interfaces:**
- Consumes: the cameras list already available to `Tile` (from `useKerbcamCameras`/context). A tile is "missing" when `tile.flightId != null && !cameras.some(c => c.flightId === tile.flightId)`.

- [ ] **Step 1: Write failing test** — render `Tile` with a `flightId` absent from the cameras list; assert it shows the reconnecting/gone affordance (text + a way to repoint/remove) and does NOT mount a live `CameraFeed`.
- [ ] **Step 2: Run, verify fail.**
- [ ] **Step 3: Implement** the missing-camera branch in `Tile.tsx` (preserve the "never remount a live feed" rule: only render the placeholder when genuinely missing).
- [ ] **Step 4: Run tests + typecheck, verify pass.**
- [ ] **Step 5: Rebuild dist + commit.**
```bash
git add web/src web/dist/index.html
git commit -m "feat(web): show reconnecting state for a tile whose camera is gone"
```

---

## Phase C — Verification (the goal's gates)

- [ ] **C1 Plugin green:** build zero-warnings + `dotnet run --project Plugin/QualityTier.Tests` ALL PASS + existing harnesses (MmapFrameRing.Tests, ControlBlock.Tests) still pass.
- [ ] **C2 Web green:** `pnpm --filter kerbcam-web build` + `pnpm --filter kerbcam-web test` (and `pnpm -r test` for the SDK) all pass; no test edited to pass.
- [ ] **C3 Push branch `wip/quality-tiers`; CI green** (plugin-ci; sidecar-ci unaffected but will run if touched; web-ci on PR if needed) — confirm conclusions by direct query.
- [ ] **C4 Live Deck verification:** stage the branch bundle (sidecar artifact + DLL), set `MaxQuality = hd` in the Deck settings.cfg, restart KSP; via Chrome confirm: header shows libva, feeds stream at 1280×720, adaptive degrade engages under load (large vessel / many cameras), and a scene change / docking preserves tiles + selections (flightId stable). Capture evidence.
- [ ] **C5:** Record any deferred items; open PR.

## Self-review notes

- Spec coverage: tiers (A1/A2/A4), settings model + precedence (A2), SD default (A1/A2 + Global Constraints), adaptive-on (A3), lockstep (A2 by construction + A4 even-dims guard), rebind error surfacing (B1), dead-tile state (B2), backward-compat (A2 Step 2), memory note (A4 settings doc), live HD (C4). All covered.
- Type consistency: `QualityTier`/`QualityTiers.{TryParse,Values,Resolve}` used consistently A1→A2→A4.
- Known runtime-only gaps: ConfigNode parsing in `KerbcamSettings.Load` isn't unit-tested (KSP-typed); the pure `Resolve`/`TryParse` carry the precedence proof, and C4 live-verifies the wired path.
