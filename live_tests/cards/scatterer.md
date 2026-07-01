# Scatterer integration test card

Pre:
- [ ] Test stack installed; Scatterer present (`modswap.sh status` shows `scatterer`).
- [ ] kerbcast deployed (current branch DLL + sidecar on the Deck).
- [ ] Settings: `EnableScatterer = true`.

Tier 1 (mod on, full stack, choppy is fine):
- [ ] Atmosphere / sky scattering appears on the near-cam stream (the blue limb and
      in-atmosphere haze) and at scaled-space range on the scaled-cam stream.
- [ ] Ocean (if over water, Kerbin/Laythe) renders on the near-cam stream.
- [ ] **SUNFLARE (slice-2 fix).** The sun renders as a bright lens flare on the
      NEAR-cam stream, not a dark blob. Point a camera roughly at the sun in
      atmosphere and in low orbit; the flare + ghosts should appear. This is driven
      by SunflareCameraHook copies on the near clone.
- [ ] **Sunflare occlusion.** Move terrain / a part body between the near cam and
      the sun: the flare should be hidden by the obstruction, not shine through.
      (Confirms `useDbufferOnCamera = 1` + the clone depth pass work.)
- [ ] Log confirms the sunflare path resolved: `grep "\[Kerbcast-Scatterer\] integration enabled" KSP.log`
      shows `sunflare=True`. If it shows `sunflare=False`, the flare copy no-oped
      (version mismatch) and the dark blob is expected - record the Scatterer version.
- [ ] **HARD GATE: the player's MAIN view is unchanged.** Watch the in-game view while
      kerbcast is streaming: the sky must not flicker, jump, or misalign, the sun flare
      must not jitter or double, and nothing must corrupt when feeds start/stop. A
      missed swap restore would point Scatterer's singleton at a kerbcast clone and
      visibly corrupt the main view; a leaked sunflare material flag would flicker the
      main-view flare. This is the critical safety test.
- [ ] No new Kerbcast exceptions: `grep -i "\[Kerbcast-Scatterer\]\|exception" KSP.log`
      shows `[Kerbcast-Scatterer] integration enabled` and no swap/restore/apply errors.
- [ ] In DX11 / unified-camera mode (if testable): far layer is skipped without error
      (`unifiedCameraMode` gate); sunflare still appears on the near-cam stream
      (unified mode uses the standard camera depth, no DepthToDistanceCommandBuffer).

Tier 2 (isolation, only if Tier 1 fails):
- [ ] Set `EnableScatterer = false`, re-enter flight scene: Scatterer effects drop from
      the streams; main view definitely unaffected (confirms attribution).
- [ ] `modswap.sh disable scatterer`, restart: clean Scatterer-absent baseline.

Follow-up (record, do not block): the sunflare is now driven (slice 2). Two hooks
remain deliberately deferred and are only worth revisiting if a specific artifact is
seen: per-body SkyNode scattering-uniform drift on the stream (the swap already feeds
SkyNode the clone camera, so this should look correct), and DepthToDistanceCommandBuffer
(only relevant in split / !unified camera mode, where the sunflare and ocean use a
merged distance buffer; risky because it writes a process-static RT sized to the stock
camera). Record any residual sunflare or sky-scattering mismatch here.

Perf notes: /metrics near/scaled phase delta with EnableScatterer on vs off; note the
MSAA-off effect on overall cost.

Result: pass | fail
Notes:
