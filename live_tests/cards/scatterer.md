# Scatterer integration test card

Pre:
- [ ] Test stack installed; Scatterer present (`modswap.sh status` shows `scatterer`).
- [ ] kerbcast deployed (current branch DLL + sidecar on the Deck).
- [ ] Settings: `EnableScatterer = true`.

Tier 1 (mod on, full stack, choppy is fine):
- [ ] Atmosphere / sky scattering appears on the near-cam stream (the blue limb and
      in-atmosphere haze) and at scaled-space range on the scaled-cam stream.
- [ ] Ocean (if over water, Kerbin/Laythe) renders on the near-cam stream.
- [ ] **SUNFLARE.** Point a near-cam roughly at the sun in atmosphere and in low
      orbit: the sun renders as a bright lens flare (with ghosts), NOT a dark blob.
      Driven by the ScattererSunflareUncull on the near clone raising the flare
      cull flag.
- [ ] **Sunflare occlusion defers to Scatterer.** With the sun clearly visible to
      the REAL (in-game) camera, the flare shows on the near-cam stream even though
      the clone sits on the vessel. Now let the in-game view lose the sun behind
      terrain/body: the flare should drop on the stream too. The visibility
      decision follows Scatterer's real-camera raycast, not the on-vessel clone.
- [ ] Log confirms the sunflare path resolved: `grep "\[Kerbcast-Scatterer\] integration enabled" KSP.log`
      shows `sunflare=True`. If `sunflare=False`, the copy no-oped (version
      mismatch) and the dark blob is expected; record the Scatterer version.
- [ ] **HARD GATE: the player's MAIN view is unchanged.** Watch the in-game view while
      kerbcast is streaming: the sky must not flicker, jump, or misalign, the sun flare
      must not jitter or double, and nothing must corrupt when feeds start/stop. A
      missed swap restore would point Scatterer's singleton at a kerbcast clone and
      visibly corrupt the main view; a leaked cull flag would flicker the main-view
      flare. This is the critical safety test.
- [ ] No new Kerbcast exceptions: `grep -i "\[Kerbcast-Scatterer\]\|exception" KSP.log`
      shows `[Kerbcast-Scatterer] integration enabled` and no swap/restore/uncull errors.
- [ ] In DX11 / unified-camera mode (if testable): far layer is skipped without error
      (`unifiedCameraMode` gate).

Tier 2 (isolation, only if Tier 1 fails):
- [ ] Set `EnableScatterer = false`, re-enter flight scene: Scatterer effects drop from
      the streams; main view definitely unaffected (confirms attribution).
- [ ] `modswap.sh disable scatterer`, restart: clean Scatterer-absent baseline.

Follow-up (record, do not block this slice): note any remaining Scatterer effects that
look off on the stream (e.g. exact sky uniforms). The sunflare position/scale are
computed by Scatterer for its own scaled camera FOV, so a flare on a clone with a very
different FOV may sit slightly off-centre; record if noticeable.

Perf notes: /metrics near/scaled phase delta with EnableScatterer on vs off; note the
MSAA-off effect on overall cost.

Result: pass | fail
Notes:
