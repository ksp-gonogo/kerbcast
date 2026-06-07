#!/usr/bin/env bash
# Bump the kerbcam version across all parts of the wire contract atomically.
# The Rust sidecar's Cargo.toml is the source of truth; the TypeScript and
# React package.json files are kept in lockstep so consumers and the sidecar
# binary share one SemVer line.
#
# CI's protocol-ci + publish-protocol workflows verify they all match; this
# script makes the bump a one-line operation so they can't drift.
#
# Usage:
#   ./scripts/bump-version.sh 0.2.0
#   ./scripts/bump-version.sh 1.0.0-rc.1
#
# After bumping:
#   git add sidecar/Cargo.toml sidecar/Cargo.lock \
#           client-sdk/typescript/package.json \
#           client-sdk/typescript/CHANGELOG.md \
#           client-sdk/react/package.json \
#           client-sdk/react/CHANGELOG.md \
#           web/dist/index.html
#   git commit -m "release: vX.Y.Z"
#   git tag -s vX.Y.Z -m "vX.Y.Z"
#   git push origin main --follow-tags
#
# `git tag -s` GPG-signs the tag with user.signingkey. Commits sign
# via commit.gpgsign=true; the script asks for `-s` explicitly so a
# fresh-clone contributor signs tags even without tag.gpgsign set in
# their config.
#
# CI on the tag publishes @jonpepler/kerbcam and @jonpepler/kerbcam-react to
# GitHub Packages. The sidecar binary's `--version` reflects the same string
# via env!("CARGO_PKG_VERSION").

set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <new-version>" >&2
  echo "  e.g. $0 0.2.0" >&2
  exit 2
fi

NEW="$1"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CARGO="$ROOT/sidecar/Cargo.toml"
TS_PKG="$ROOT/client-sdk/typescript/package.json"
REACT_PKG="$ROOT/client-sdk/react/package.json"
TS_CHANGELOG="$ROOT/client-sdk/typescript/CHANGELOG.md"
REACT_CHANGELOG="$ROOT/client-sdk/react/CHANGELOG.md"

# Loose SemVer check; accepts pre-release suffixes like -rc.1.
if ! printf '%s' "$NEW" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$'; then
  echo "error: '$NEW' doesn't look like SemVer (expected X.Y.Z or X.Y.Z-pre)" >&2
  exit 3
fi

CURRENT_CARGO="$(grep -m1 '^version = ' "$CARGO" | sed -E 's/version = "(.*)"/\1/')"
CURRENT_TS="$(node -p "require('$TS_PKG').version")"
CURRENT_REACT="$(node -p "require('$REACT_PKG').version")"

echo "current: cargo=$CURRENT_CARGO typescript=$CURRENT_TS react=$CURRENT_REACT"
echo "    new: $NEW"

if [ "$CURRENT_CARGO" = "$NEW" ] && [ "$CURRENT_TS" = "$NEW" ] && [ "$CURRENT_REACT" = "$NEW" ]; then
  echo "already at $NEW -- nothing to do"
  exit 0
fi

# Cargo.toml -- only the first `version = ...` line under [package].
# Targeting the exact current value avoids accidentally rewriting a
# dependency's pinned version line.
sed -i.bak -E "s/^version = \"$CURRENT_CARGO\"/version = \"$NEW\"/" "$CARGO"
rm "$CARGO.bak"

# package.json files -- `npm version` would also create a git commit + tag,
# which we don't want here (commit + tag is the caller's job). Use
# `npm pkg set` instead.
( cd "$(dirname "$TS_PKG")" && npm pkg set version="$NEW" >/dev/null )
( cd "$(dirname "$REACT_PKG")" && npm pkg set version="$NEW" >/dev/null )

# Cargo.lock -- rerun cargo to refresh the lockfile's [[package]] entry for
# kerbcam-sidecar.
if command -v cargo >/dev/null 2>&1; then
  ( cd "$(dirname "$CARGO")" && cargo update -p kerbcam-sidecar --precise "$NEW" >/dev/null 2>&1 || true )
fi

# Rebuild the embedded web UI so the shipped binary contains the page at the
# bumped version. Requires pnpm; fails with a clear error if it is absent so a
# release cannot silently ship a stale page.
if ! command -v pnpm >/dev/null 2>&1; then
  echo "error: pnpm not found -- install pnpm and re-run so the web build is current" >&2
  exit 4
fi
echo "rebuilding web UI..."
( cd "$ROOT" && pnpm --filter kerbcam-web build >/dev/null )
echo "  $ROOT/web/dist/index.html"

# Prepend a placeholder version header to each CHANGELOG. Caller fills body.
bump_changelog() {
  local changelog="$1"
  local today="$2"
  local tmp
  tmp="$(mktemp)"
  {
    echo "# Changelog"
    echo ""
    echo "## $NEW - $today"
    echo ""
    echo "<!-- TODO: fill in. -->"
    echo ""
    # Skip the existing top-level "# Changelog" line; keep everything below.
    awk 'NR==1 && /^# Changelog/ { next } { print }' "$changelog"
  } > "$tmp"
  mv "$tmp" "$changelog"
}

TODAY="$(date +%Y-%m-%d)"
bump_changelog "$TS_CHANGELOG" "$TODAY"
bump_changelog "$REACT_CHANGELOG" "$TODAY"

echo ""
echo "bumped to $NEW:"
echo "  $CARGO"
echo "  $TS_PKG"
echo "  $REACT_PKG"
echo "  $TS_CHANGELOG (placeholder header -- fill the body before committing)"
echo "  $REACT_CHANGELOG (placeholder header -- fill the body before committing)"
echo "  $ROOT/web/dist/index.html (rebuilt)"
echo ""
echo "next:"
echo "  vim $TS_CHANGELOG $REACT_CHANGELOG  # describe the changes"
echo "  git add sidecar/Cargo.toml sidecar/Cargo.lock \\"
echo "          client-sdk/typescript/package.json client-sdk/typescript/CHANGELOG.md \\"
echo "          client-sdk/react/package.json client-sdk/react/CHANGELOG.md \\"
echo "          web/dist/index.html"
echo "  git commit -m \"release: v$NEW\""
echo "  git tag -s v$NEW -m \"v$NEW\""
echo "  git push origin main --follow-tags"
