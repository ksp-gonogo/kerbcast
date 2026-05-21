# Publishing `@jonpepler/kerbcam`

Packages are published to **GitHub Packages**, not npmjs.com. The
two halves of the wire contract (the Rust sidecar's `Cargo.toml`
and this TypeScript `package.json`) share a single SemVer line —
CI verifies they agree before letting a tag publish.

The npm scope is `@jonpepler` because GitHub Packages requires the
scope to match the repo owner. If the project later moves to an
umbrella GitHub org, the scope changes to that org's name in one
search-replace — no other restructuring needed.

No manual secret setup. The workflow uses the built-in
`GITHUB_TOKEN` to write to the repo's own Packages registry.

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
        client-sdk/typescript/CHANGELOG.md
git commit -m "release: v0.1.1"
git tag -a v0.1.1 -m "v0.1.1"
git push origin main --follow-tags
```

CI then runs `publish-protocol.yml`:

1. Drift-checks the typeshare output (stale generated TS can't
   ship under a release tag).
2. Verifies the tag, `Cargo.toml`, and `package.json` all carry
   `0.1.1`.
3. Builds with `tsc`.
4. `npm publish --provenance` to `npm.pkg.github.com`.

If any check fails the workflow exits without publishing, so a
mismatched tag never produces a release.

## Installing as a consumer

`@jonpepler/kerbcam` is hosted on GitHub Packages, which requires
the installing machine (or CI job) to authenticate even for public
packages.

Add an `.npmrc` next to your `package.json`:

```ini
@jonpepler:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=${GITHUB_TOKEN}
```

For local dev, generate a Personal Access Token at GitHub's
`Settings → Developer settings → Personal access tokens (classic)`
with `read:packages` scope, export it as `GITHUB_TOKEN`, then:

```sh
pnpm add @jonpepler/kerbcam
```

In other GitHub Actions workflows, the auto-injected
`secrets.GITHUB_TOKEN` already has `read:packages` permission.

## Manual publish (fallback)

If CI isn't an option (token rotation, urgent fix, etc.):

```sh
cd client-sdk/typescript
pnpm install
pnpm run build

# Auth: PAT with write:packages scope on the jonpepler/kerbcam repo.
export GITHUB_TOKEN=ghp_…
npm publish
```

## Versioning

Strict [SemVer](https://semver.org/) applied to the wire format:

- **Patch** (`0.1.x`): docstring / metadata changes only.
  Generated TS is byte-identical or trivially cosmetic.
- **Minor** (`0.x.0`): additive — new message variants, new
  optional fields. Existing consumers keep working.
- **Major** (`x.0.0`): breaking — renamed / removed fields,
  changed enum discriminators, changed tagging strategy.
  Consumers must update.

While the protocol is at `0.x`, the minor / breaking distinction
is loose — assume any minor bump might require consumer updates.
Tighten this to strict SemVer once the protocol stabilises at
`1.0.0`.

## Why GitHub Packages and not npmjs.com

- One auth surface (GitHub login + PAT covers both source and the
  registry); no separate npm account to manage.
- The workflow's built-in `GITHUB_TOKEN` publishes with zero
  per-repo secret setup.
- Provenance attestation is first-class.
- Public packages don't cost anything on the GitHub Free tier;
  storage / bandwidth caps don't apply to public scope.

The trade-off is that consumers have to configure their `.npmrc`
to pull from the GitHub registry. Acceptable for a niche
KSP-streaming protocol package; npmjs.com is the move if the
consumer surface ever broadens enough to make that friction
matter.
