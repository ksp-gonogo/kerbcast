# Publishing `@ksp-gonogo/kerbcast`

Packages are published to **public npmjs.com**. The two halves of
the wire contract (the Rust sidecar's `Cargo.toml` and this
TypeScript `package.json`) share a single SemVer line; CI verifies
they agree before letting a tag publish.

Publish auth is **npm OIDC trusted publishing**: npmjs.com trusts
this repo's `release.yml` workflow directly, so nothing is stored
or rotated. The workflow requests a short-lived id-token via the
`id-token: write` permission and npm exchanges it for a publish
grant at publish time. No `NPM_TOKEN` secret exists for this repo.

## Cutting a release

```sh
# 1. Bump both halves atomically. Cargo.toml + package.json move in
#    lockstep; the script also seeds a CHANGELOG header.
./scripts/bump-version.sh 0.1.1

# 2. Fill in the CHANGELOG body the script left as TODO.
vim client-sdk/typescript/CHANGELOG.md

# 3. Commit + tag.
git add sidecar/Cargo.toml sidecar/Cargo.lock \
        client-sdk/typescript/package.json \
        client-sdk/typescript/CHANGELOG.md \
        client-sdk/react/package.json \
        client-sdk/react/CHANGELOG.md \
        web/dist/index.html
git commit -m "release: v0.1.1"
git tag -s v0.1.1 -m "v0.1.1"
git push origin main --follow-tags
```

CI then runs the `publish-sdk` job in `release.yml`:

1. Regenerates the typeshare bindings from Rust (the published
   types always come from a fresh regen, never the committed copy).
2. Verifies the tag, `Cargo.toml`, and both package.json files all
   carry `0.1.1`.
3. Builds the workspace with `tsc`.
4. `npm publish` for `@ksp-gonogo/kerbcast`, then
   `@ksp-gonogo/kerbcast-react`, to `registry.npmjs.org` via OIDC.

If any check fails the job exits without publishing, and the
GameData bundle job (which `needs` it) never creates a GitHub
Release, so a mismatched tag never produces a release in either
form.

## Installing as a consumer

`@ksp-gonogo/kerbcast` is hosted on public npm — no registry route,
no auth, no token:

```sh
npm add @ksp-gonogo/kerbcast
```

## Manual publish (fallback)

OIDC trusted publishing only works from the trusted GitHub Actions
workflow, so a manual publish needs a real npm account with publish
rights on the `@ksp-gonogo` scope (not a workaround for a broken
trusted-publisher config — fix that instead where possible):

```sh
cd client-sdk/typescript
pnpm install
pnpm run build

npm login   # account with publish rights on @ksp-gonogo
npm publish
```

## Versioning

Strict [SemVer](https://semver.org/) applied to the wire format:

- **Patch** (`0.1.x`): docstring / metadata changes only.
  Generated TS is byte-identical or trivially cosmetic.
- **Minor** (`0.x.0`): additive, meaning new message variants, new
  optional fields. Existing consumers keep working.
- **Major** (`x.0.0`): breaking, meaning renamed / removed fields,
  changed enum discriminators, changed tagging strategy.
  Consumers must update.

While the protocol is at `0.x`, the minor / breaking distinction
is loose: assume any minor bump might require consumer updates.
Tighten this to strict SemVer once the protocol stabilises at
`1.0.0`.

## Why public npm and OIDC

- Zero-friction installs: `npm add @ksp-gonogo/kerbcast` works for
  anyone, no `.npmrc` registry route or token — required for the
  eventual public KSP-mod marketplace use case.
- OIDC trusted publishing means no `NPM_TOKEN` to store, rotate, or
  leak; npmjs.com verifies the publish came from this exact repo's
  release workflow and stamps provenance automatically.
- Matches where the rest of the `@ksp-gonogo` scope (gonogo's own
  publishable packages) is heading.

The repo previously published to GitHub Packages under
`@jonpepler`, which kept consumer auth on the same GitHub login as
the source but required every installer to configure a registry
route and token even for a public package. That trade-off no longer
made sense once the project moved to the shared `@ksp-gonogo` scope
with a public-marketplace goal.
