# Simplify review, June 2026

A whole-codebase code-quality pass (reuse / simplification / efficiency / altitude;
no correctness-bug hunting). Six review agents covered the Rust sidecar, the C#
plugin, the TypeScript SDK + React packages, and the web app. This note records
what was applied and what was deferred, so the deferred items read as decisions
rather than oversights.

The bar for everything applied: zero behaviour change, proven by the existing
test suites passing without being edited, green on each toolchain
(cargo build/test/clippy, dotnet build at zero warnings, web build + vitest).
The OS-gated encoder change is verified by sidecar-ci's ubuntu (libva) and
windows (mediafoundation) lanes because those backends do not compile on the
macOS dev host.

## Applied

Each landed as its own atomic commit on `wip/simplify-cleanups`.

- **sidecar `shared_mem/mmap.rs`** — stamp frame-ring headers via `copy_from_slice`
  closures (the `put_u32`/`put_f32` idiom `control.rs` already uses) and
  `header.fill(0)`, dropping the `write_all().unwrap()` boilerplate and the unused
  `std::io::Write` import. Byte layout unchanged (cross-language fixture test passes).
- **sidecar `encoder/mod.rs`** — `selected_backend_name()` now returns
  `auto_select().name()` instead of maintaining a second candidate list; removed
  the stale doc comment claiming a nonexistent `OnceLock` cache.
- **sidecar `webrtc/peer.rs`** — deleted the dead empty `if msg.is_string {}` block;
  the `RequestKeyframe` arm reuses the `request_keyframe_for` helper; fixed a stale
  comment naming the removed `maybe_sleep_idle_cameras`.
- **sidecar `main.rs`** — extracted one `broadcast()` helper for the peer fan-out
  pattern that was repeated five times. The ping task's fan-out is left alone
  (it filters on `is_alive()`).
- **sidecar `encoder/libva.rs` + `mediafoundation.rs`** — snapshot the few scalar
  fields actually read (`width`/`height`, plus `fps` for MF) instead of cloning the
  whole `EncodeConfig` every frame. OS-gated, verified on CI.
- **plugin `KerbcamSettings.cs`** — dropped the dead `if (w % 2 != 0)` guard around
  an already-no-op subtraction.
- **plugin `AdaptiveQualityController.cs`** — removed the unobservable
  `_headroomSince` reset.
- **plugin `ControlBlock.cs`** — inlined the `OptU32`/`OptF32` ternaries in
  `ReadBody` to drop a per-control-change closure allocation (net48/C# 7.3 has no
  static local functions).
- **plugin `PartCapabilities.cs`** — gated the `ForPart` log behind
  `DebugCameraLogging`.
- **web `App.tsx` / `SettingsPanel.tsx` / `Grid.tsx`** — read localStorage tiles
  once on mount; removed the duplicate settings persistence; memoized Grid's
  `shownIds`/`missingCount`. Committed together because they share the regenerated
  `dist` artifact.

## Deferred

Real findings intentionally not applied in this pass, with the reason. These are
candidates for follow-up, not rejected outright unless noted.

### Structural refactors (behaviour-preserving but invasive — want owner sign-off)

- **peer.rs `apply_*` handlers** — extract the shared get-or-error / flush-or-push
  skeleton from the eight near-identical control handlers. The per-handler middles
  (capability checks, clamps) genuinely differ, so only the head/tail generalize.
- **cameras.rs `update_control<F>` mutator** — collapse the
  lock/mutate/clone/flush idiom repeated across the registry and the peer handlers.
  Touches many call sites.
- **C# FX effect base class** — `CoreSheath`/`Embers`/`Bowshock`/`Trail` duplicate
  the attach/detach/dispose/shader-id scaffolding, and two share a part-renderer
  sweep. High value but this is the area of the May-2026 "FX inside the hull"
  drift regression, so the extraction needs careful design and respect for the
  CI-shared-source constraint.
- **TS quality-preset extraction** — the scale/dim math is duplicated in
  `CameraFeed.tsx`, `testing/index.ts`, and mirrors the Rust source. Extracting
  `qualityPresetScale`/`scaleDimEven` into `@jonpepler/kerbcam` changes the
  published package surface (and must be logged in gonogo's interface-changes
  note), so it is a deliberate API decision.

### Efficiency, deferred for risk or low value

- **C# `MmapFrameRing.AcquirePointer`** — caching the base pointer would remove
  ~4-5 re-acquires per frame, but it is an unsafe-lifetime change (release in
  `Dispose` before the view) that wants explicit review.
- **sidecar `main.rs` startup backend probe** — `select_backend` is built once just
  to derive the default bitrate then dropped. Startup-only; a lightweight
  `backend_class` API was judged not worth it.
- **software.rs RGBA→RGB strip**, **libva.rs double codec lookup in `init()`**,
  **MF output-stream-info flag dedup** — marginal or cold-path; left for a future
  tier-2 pass (and the gated backends can't be compiled locally).
- **web `DevPanel` polling effects** → `usePoll` helper, **`Grid` handler
  `useCallback`** (only matters once `Tile` is memoized) — behaviour-neutral churn,
  low urgency.

### Behaviour-change candidates (need intent confirmation, so not "cleanup")

- **web `App.tsx` reduced-motion** — switching to `useSyncExternalStore` would make
  it track live OS `prefers-reduced-motion` changes. That is arguably the intent,
  but it is a behaviour change, not a simplification.
- **CameraFeed.tsx `controllerRef.current?.dispose()` (redundant call)** — likely
  safe to drop, but carries a StrictMode double-effect risk we could not verify
  without running it; not worth an unverifiable behaviour change in a cleanup pass.

### Explicitly won't-fix

- **Encoder unavailable-stub trait impls** repeated across four stubs — intentional
  per the "every backend classifies itself" rule.
- **protocol `QualityPreset` inverse tables**, **control.rs `write_body` flag
  blocks**, **mmap.rs atomic-accessor generics** — the explicit forms are
  compiler-checked / greppable; collapsing them would trade clarity for brevity.
- **`lifecycle.ts getCameraLifecycle`**, **`Tile.tsx` one-way `displayedFlightId`
  flow** — the apparent redundancy is a deliberate defence against out-of-contract
  values / intentional UI behaviour.
