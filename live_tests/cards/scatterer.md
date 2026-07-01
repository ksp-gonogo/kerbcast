# Scatterer integration test card

Pre:
- [ ] Test stack installed; Scatterer present (`modswap.sh status` shows `scatterer`).
- [ ] kerbcast deployed (current branch DLL + sidecar on the Deck).
- [ ] Settings: `EnableScatterer = true`.

Tier 1 (mod on, full stack, choppy is fine):
- [ ] Atmosphere / sky scattering appears on the near-cam stream (the blue limb and
      in-atmosphere haze) and at scaled-space range on the scaled-cam stream.
- [ ] Ocean (if over water, Kerbin/Laythe) renders on the near-cam stream.
- [ ] Record which Scatterer effects appear and which do NOT: in particular the
      SUNFLARE (lens flare) is expected to need a follow-up hook copy; note whether it
      appears or is missing.
- [ ] **HARD GATE: the player's MAIN view is unchanged.** Watch the in-game view while
      kerbcast is streaming: the sky must not flicker, jump, or misalign, the sun flare
      must not jitter, and nothing must corrupt when feeds start/stop. A missed swap
      restore would point Scatterer's singleton at a kerbcast clone and visibly corrupt
      the main view. This is the critical test for this slice.
- [ ] No new Kerbcast exceptions: `grep -i "\[Kerbcast-Scatterer\]\|exception" KSP.log`
      shows `[Kerbcast-Scatterer] integration enabled` and no swap/restore errors.
- [ ] In DX11 / unified-camera mode (if testable): far layer is skipped without error
      (`unifiedCameraMode` gate).

Tier 2 (isolation, only if Tier 1 fails):
- [ ] Set `EnableScatterer = false`, re-enter flight scene: Scatterer effects drop from
      the streams; main view definitely unaffected (confirms attribution).
- [ ] `modswap.sh disable scatterer`, restart: clean Scatterer-absent baseline.

Follow-up (record, do not block this slice): list the Scatterer effects that were
missing on the stream (e.g. sunflare, exact sky uniforms) so a later pass can copy the
specific stock-camera hooks (SunflareCameraHook, per-camera SkyNode uniforms,
DepthToDistanceCommandBuffer) the build-sheet identified.

Perf notes: /metrics near/scaled phase delta with EnableScatterer on vs off; note the
MSAA-off effect on overall cost.

Result: pass | fail
Notes:
