# EVE integration test card

Pre:
- [ ] Test stack installed (`live_tests/modtest.md` step 1); EVE present
      (`modswap.sh status` shows `eve` present). A cloud config (Spectra) is
      installed so clouds actually exist.
- [ ] kerbcast deployed (current branch DLL + sidecar on the Deck).
- [ ] Settings: `EnableEVE = true` in settings.cfg.

Tier 1 (mod on, full stack, choppy is fine):
- [ ] Clouds appear on the near-cam stream over the planet, with SOFT edges at the
      horizon and against terrain (not hard-clipped). Soft edges confirm the depth
      texture is set; hard edges mean depthTextureMode did not take.
- [ ] Clouds appear on the scaled-cam stream when zoomed out to scaled-space range.
- [ ] At night over a populated area: city lights appear on the near-cam stream.
- [ ] Celestial shadows (a moon shadow on the planet) appear on the stream if the
      geometry is present.
- [ ] Player MAIN view is unchanged: no flicker, no doubled clouds, correct sky.
- [ ] No new Kerbcast exceptions: `grep -i "\[Kerbcast-EVE\]\|exception" KSP.log`
      shows `[Kerbcast-EVE] integration enabled` and no errors.

Tier 2 (isolation, only if Tier 1 fails):
- [ ] Set `EnableEVE = false`, re-enter flight scene: clouds should drop to hard
      edges / city lights+shadows gone (confirms the integration, not EVE itself, was
      providing them). Note: base clouds may still appear via cullingMask even with
      the integration off (expected; the integration only adds depth + local effects).
- [ ] `modswap.sh disable eve`, restart: no clouds at all (clean EVE-absent baseline).

Known limitation (note, not a failure): the local components (city lights, celestial
shadows) are replicated at camera setup. If EVE applies them to the main camera only
after kerbcast's cameras are built (e.g. after a body change mid-flight), the clone may
miss them until the camera is rebuilt. Clouds (cullingMask path) are unaffected.

Perf notes: /metrics near/scaled phase delta with EnableEVE on vs off.

Result: pass | fail
Notes:
