#!/usr/bin/env bash
# Release gate: the core zip must carry a valid readback library at the exact
# path the plugin loads it from, or Linux OpenGL installs refuse to start.
# With build-info.txt from build.sh, the zipped copy must also match the built
# file's sha256, which catches a damaged copy whose ELF headers still parse.
#
# Usage: check-zip.sh <kerbcast-vX.Y.Z.zip> [build-info.txt]
set -euo pipefail
zip="$1"
info="${2:-}"
here="$(cd "$(dirname "$0")" && pwd)"
rel=GameData/Kerbcast/Plugins/linux-x64/libAsyncGPUReadbackPlugin.so

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
unzip -q "$zip" -d "$work"

if [ ! -f "$work/$rel" ]; then
  echo "::error::$zip does not contain $rel; Linux OpenGL installs would refuse to start"
  exit 1
fi

if [ -n "$info" ]; then
  want="$(sed -n 's/^sha256=//p' "$info")"
  got="$(sha256sum "$work/$rel" | cut -d' ' -f1)"
  if [ -z "$want" ] || [ "$want" != "$got" ]; then
    echo "::error::$zip: $rel sha256 $got does not match the built library (${want:-no sha256 in $info})"
    exit 1
  fi
fi

"$here/check.sh" "$work/$rel"
