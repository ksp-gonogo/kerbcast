# Testing backlog

Captured 2026-05-19. Rust-side unit + integration tests are in good shape (26 passing, fmt + clippy clean with `-D warnings`, GitHub Actions CI wired). The items below close the gap against the testing strategy in the rebuild design doc (gonogo repo's `local_docs/ocisly_state_and_rebuild.md` §10).

## Done

- ✅ Software encoder unit tests (5)
- ✅ Shared-mem ring unit tests, in-process model (7)
- ✅ Shared-mem ring unit tests, file-backed mmap (7)
- ✅ Protocol message serde roundtrip tests (3)
- ✅ Integration: synthetic frames → in-process ring → encoder (2)
- ✅ Integration: synthetic frames → mmap ring → OpenH264 encoder (2)
- ✅ GitHub Actions CI workflow (fmt + clippy + test on push to main / on PRs touching `sidecar/**`)

## Open

### Quick

- [x] `live_tests/kerbcam.md`: Claude-runnable instructions for verifying a running sidecar (HTTP endpoints once they exist, control-channel message shapes, common failure modes). Sister doc to `gonogo_claude_tools.sh tele …`. *(Added 2026-06-10; validated live against fake_camera + software encoder.)*

### Medium

- [ ] `cargo bench` encoder benchmark via `criterion`. Each available backend × resolutions {240p, 480p, 720p, 1080p}. Output a CSV; commit a baseline; CI alerts on > 20% regression.
- [ ] PerfBudget equivalent of gonogo's `PerfBudget` for the sidecar: declarative budgets that fail tests when exceeded. Important for catching encoder regressions before they hit users.
- [ ] WebRTC peer integration test (no real browser): spawn the sidecar binary, drive it via a Rust-side WebRTC peer (also `webrtc-rs`), assert frames flow.
- [x] **Cross-language `MmapFrameRing` roundtrip.** Small C# fixture tool that writes a known frame (specific dimensions, specific RGBA pattern, specific timestamp) into a ring file; Rust test opens that file and asserts the exact bytes round-trip. Locks the cross-process binary layout contract; without this we're trusting that the field offsets in C#'s `MmapFrameRing.cs` match Rust's `mmap.rs` purely by inspection. *(Added 2026-06-10: `sidecar/testdata/frame_ring_v1.ring`, written by `MmapFrameRing.Tests --write-fixture`; the C# harness asserts regen == committed, the Rust test `csharp_written_fixture_reads_back_exactly` asserts exact read-back.)*

### Bigger

- [ ] Subprocess synthetic-frame harness: spawn `kerbcam-sidecar --shm-path /tmp/...`, the test process writes frames into the ring, asserts the daemon's stats output (and eventually the WebRTC track's RTP packet count).
- [x] KSP smoke-test checklist (`tests/manual/ksp_smoke.md`): canonical scene load, vessel-switch behaviour, undock, stage event, time-warp transitions, game window kill. Run before each release. *(Added 2026-06-10.)*
- [ ] C# plugin-side unit tests once the kerbcam plugin migrates out of the OCISLY-spike branch. xUnit or NUnit against mocked `MuMechModuleHullCamera`.

## Won't test (intentional)

- Unity API surface itself (`AsyncGPUReadback`, `Graphics.Blit`, etc.): trusted.
- Hullcam VDS shader contents: if they change upstream our Path A breaks, but that's a manual-test-on-update problem, not a CI problem.
- Cross-platform perf parity. Tier-2 platforms (macOS, Windows) get conformance tests but not perf assertions, because the dev machine isn't all of them.
