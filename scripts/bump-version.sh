#!/usr/bin/env bash
# Bump the kerbcam version across both halves of the wire contract
# atomically. The Rust sidecar's Cargo.toml is the source of truth;
# the TS package.json is kept in lockstep so consumers and the
# sidecar binary share one SemVer line.
#
# CI's protocol-ci + publish-protocol workflows verify the two
# match; this script makes the bump a one-line operation so they
# can't drift.
#
# Usage:
#   ./scripts/bump-version.sh 0.2.0
#   ./scripts/bump-version.sh 1.0.0-rc.1
#
# After bumping:
#   git add sidecar/Cargo.toml sidecar/Cargo.lock \
#           client-sdk/typescript/package.json \
#           client-sdk/typescript/CHANGELOG.md
#   git commit -m "release: vX.Y.Z"
#   git tag -a vX.Y.Z -m "vX.Y.Z"
#   git push origin main --follow-tags
#
# CI on the tag publishes @jonpepler/kerbcam-protocol to GitHub
# Packages. The sidecar binary's `--version` reflects the same
# string via env!("CARGO_PKG_VERSION").

set -euo pipefail

if [ "$#" -ne 1 ]; then
  echo "usage: $0 <new-version>" >&2
  echo "  e.g. $0 0.2.0" >&2
  exit 2
fi

NEW="$1"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CARGO="$ROOT/sidecar/Cargo.toml"
PKG="$ROOT/client-sdk/typescript/package.json"
CHANGELOG="$ROOT/client-sdk/typescript/CHANGELOG.md"

# Loose SemVer check — accepts pre-release suffixes like -rc.1.
if ! printf '%s' "$NEW" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.-]+)?$'; then
  echo "error: '$NEW' doesn't look like SemVer (expected X.Y.Z or X.Y.Z-pre)" >&2
  exit 3
fi

CURRENT_CARGO="$(grep -m1 '^version = ' "$CARGO" | sed -E 's/version = "(.*)"/\1/')"
CURRENT_PKG="$(node -p "require('$PKG').version")"

echo "current: cargo=$CURRENT_CARGO package=$CURRENT_PKG"
echo "    new: $NEW"

if [ "$CURRENT_CARGO" = "$NEW" ] && [ "$CURRENT_PKG" = "$NEW" ]; then
  echo "already at $NEW — nothing to do"
  exit 0
fi

# Cargo.toml — only the first `version = ...` line under [package].
# Using a sed that targets the exact current value avoids accidentally
# rewriting a dependency's pinned version line.
sed -i.bak -E "s/^version = \"$CURRENT_CARGO\"/version = \"$NEW\"/" "$CARGO"
rm "$CARGO.bak"

# package.json — `npm version` would also create a git commit + tag,
# which we don't want here (commit + tag is the caller's job). Use
# `npm pkg set` instead.
( cd "$(dirname "$PKG")" && npm pkg set version="$NEW" >/dev/null )

# Cargo.lock — rerun cargo to refresh the lockfile's [[package]]
# entry for kerbcam-sidecar. Skip with --no-cargo-lock if you've
# only got the lockfile-free Cargo registry handy.
if command -v cargo >/dev/null 2>&1; then
  ( cd "$(dirname "$CARGO")" && cargo update -p kerbcam-sidecar --precise "$NEW" >/dev/null 2>&1 || true )
fi

# CHANGELOG.md gets a placeholder header. Caller fills the body.
TODAY="$(date +%Y-%m-%d)"
TMP="$(mktemp)"
{
  echo "# Changelog"
  echo ""
  echo "## $NEW — $TODAY"
  echo ""
  echo "<!-- TODO: fill in. -->"
  echo ""
  # Skip the existing top-level "# Changelog" line; keep everything below.
  awk 'NR==1 && /^# Changelog/ { next } { print }' "$CHANGELOG"
} > "$TMP"
mv "$TMP" "$CHANGELOG"

echo ""
echo "bumped to $NEW:"
echo "  $CARGO"
echo "  $PKG"
echo "  $CHANGELOG (placeholder header — fill the body before committing)"
echo ""
echo "next:"
echo "  vim $CHANGELOG  # describe the changes"
echo "  git add sidecar/Cargo.toml sidecar/Cargo.lock client-sdk/typescript/package.json client-sdk/typescript/CHANGELOG.md"
echo "  git commit -m \"release: v$NEW\""
echo "  git tag -a v$NEW -m \"v$NEW\""
echo "  git push origin main --follow-tags"
