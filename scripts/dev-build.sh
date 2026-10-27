#!/usr/bin/env bash
# Build a local dev bundle of this checkout's HEAD for testing on the gonogo rig.
#
# The sidecar is Linux-only, so it is built by dispatching sidecar-ci on the
# pushed branch; the plugin and npm packages are built locally. Output lands in
# <main checkout>/local_docs/dev-builds/<version>/ with a `latest` symlink.
#
# Usage:
#   scripts/dev-build.sh [--version X.Y.Z-dev.N] [--sidecar-from DIR] [--dev-provider]
#
#   --version       override the default <next minor>-dev.<shortsha>
#   --sidecar-from  reuse an already downloaded sidecar-ci artifact dir instead
#                   of dispatching CI (its build-info.txt must match HEAD)
#   --dev-provider  also stage Kerbcast.DevProvider.dll, the development
#                   world-model provider (dev builds only; it still needs the
#                   dev marker and devSignalSpeedMps on the install to run)

set -euo pipefail

KSP_SYNC="$HOME/personal/gonogo/local_docs/syncthing/kspdata"
KSP_MANAGED="$KSP_SYNC/KSP_Data/Managed"
KSP_GAMEDATA="$KSP_SYNC/GameData"
ARTIFACT_NAME="kerbcast-sidecar-linux-x64-dev"
READBACK_IN_ZIP="GameData/Kerbcast/Plugins/linux-x64/libAsyncGPUReadbackPlugin.so"

die() {
  echo "dev-build: $*" >&2
  exit 1
}

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

VERSION_OVERRIDE=""
SIDECAR_FROM=""
DEV_PROVIDER=0
while [ "$#" -gt 0 ]; do
  case "$1" in
    --version)
      VERSION_OVERRIDE="${2:?--version needs a value}"
      shift 2
      ;;
    --sidecar-from)
      SIDECAR_FROM="${2:?--sidecar-from needs a directory}"
      shift 2
      ;;
    --dev-provider)
      DEV_PROVIDER=1
      shift
      ;;
    -h | --help)
      sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *) die "unknown argument: $1 (see --help)" ;;
  esac
done
if [ -n "$SIDECAR_FROM" ]; then
  SIDECAR_FROM="$(cd "$SIDECAR_FROM" && pwd)" || die "no such directory: $SIDECAR_FROM"
fi

for tool in git gh jq dotnet pnpm npm tar unzip; do
  command -v "$tool" >/dev/null 2>&1 || die "missing required tool: $tool"
done

ROOT="$(git rev-parse --show-toplevel 2>/dev/null)" || die "run this from inside a kerbcast checkout"
cd "$ROOT"
[ -f sidecar/Cargo.toml ] && [ -f Plugin/Kerbcast.csproj ] || die "$ROOT does not look like a kerbcast checkout"

# The output lives beside the main checkout so every worktree shares one tree.
COMMON_DIR="$(cd "$(git rev-parse --git-common-dir)" && pwd)"
MAIN_CHECKOUT="$(dirname "$COMMON_DIR")"
OUT_ROOT="${KERBCAST_DEV_BUILDS_DIR:-$MAIN_CHECKOUT/local_docs/dev-builds}"

[ -d "$KSP_MANAGED" ] || die "KSP assemblies not found at $KSP_MANAGED"
[ -d "$KSP_GAMEDATA" ] || die "KSP GameData not found at $KSP_GAMEDATA"

# HEAD must be pushed: CI builds the branch tip on origin, not local commits.
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
  echo "dev-build: warning: tracked files have uncommitted changes; they are NOT in this build" >&2
fi
SHA="$(git rev-parse HEAD)"
SHORT_SHA="$(git rev-parse --short=7 HEAD)"
BRANCH="$(git symbolic-ref --quiet --short HEAD)" || die "HEAD is detached; check out a pushed branch"
git fetch --quiet origin main "$BRANCH" 2>/dev/null || git fetch --quiet origin main
REMOTE_SHA="$(git ls-remote origin "refs/heads/$BRANCH" | cut -f1)"
if [ -z "$REMOTE_SHA" ]; then
  die "branch '$BRANCH' is not on origin; push it first (git push -u origin $BRANCH)"
fi
if [ "$REMOTE_SHA" != "$SHA" ]; then
  die "origin/$BRANCH is at ${REMOTE_SHA:0:7} but HEAD is $SHORT_SHA; push (or pull) so they match"
fi

# Version: next minor of sidecar/Cargo.toml plus -dev.<shortsha>.
version_of() {
  awk -F'"' '/^version = /{print $2; exit}'
}
CARGO_VERSION="$(version_of <sidecar/Cargo.toml)"
[ -n "$CARGO_VERSION" ] || die "could not read the version from sidecar/Cargo.toml"
if [ -n "$VERSION_OVERRIDE" ]; then
  DEV_VERSION="$VERSION_OVERRIDE"
else
  IFS=. read -r V_MAJOR V_MINOR _ <<<"${CARGO_VERSION%%-*}"
  DEV_VERSION="${V_MAJOR}.$((V_MINOR + 1)).0-dev.${SHORT_SHA}"
fi
[[ "$DEV_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "not a SemVer version: $DEV_VERSION"
MAIN_VERSION="$(git show origin/main:sidecar/Cargo.toml | version_of)"

OUT="$OUT_ROOT/$DEV_VERSION"
CACHE="$OUT_ROOT/_cache"
if [ -e "$OUT" ]; then
  die "$OUT already exists; remove it or pass --version"
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/kerbcast-dev-build.XXXXXX")"
cleanup() {
  rm -rf "${WORK:?}"
}
trap cleanup EXIT

echo "dev-build: $DEV_VERSION from $BRANCH @ $SHORT_SHA"

# Sidecar via sidecar-ci (Linux only).
SIDECAR_DIR="$WORK/sidecar"
RUN_URL=""
if [ -n "$SIDECAR_FROM" ]; then
  cp -R "$SIDECAR_FROM" "$SIDECAR_DIR"
  RUN_URL="local:$SIDECAR_FROM"
else
  echo "dev-build: dispatching sidecar-ci on $BRANCH"
  list_runs() {
    gh run list --workflow sidecar-ci.yml --event workflow_dispatch --commit "$SHA" \
      --limit 20 --json databaseId,createdAt
  }
  BEFORE_IDS="$(list_runs | jq '[.[].databaseId]')"
  DISPATCH_OUT="$(gh workflow run sidecar-ci.yml --ref "$BRANCH" -f version="$DEV_VERSION" 2>&1)" ||
    die "dispatch failed (the branch needs the sidecar-ci 'version' input): $DISPATCH_OUT"
  RUN_ID="$(grep -oE 'actions/runs/[0-9]+' <<<"$DISPATCH_OUT" | head -1 | cut -d/ -f3 || true)"
  attempt=0
  while [ -z "$RUN_ID" ]; do
    attempt=$((attempt + 1))
    [ "$attempt" -le 30 ] || die "could not find the dispatched sidecar-ci run for $SHORT_SHA"
    sleep 4
    RUN_ID="$(list_runs | jq -r --argjson before "$BEFORE_IDS" \
      '[.[] | select(.databaseId as $id | $before | index($id) | not)]
       | sort_by(.createdAt) | last | .databaseId // empty')"
  done
  RUN_URL="$(gh run view "$RUN_ID" --json url -q .url)"
  echo "dev-build: watching $RUN_URL"
  gh run watch "$RUN_ID" --exit-status --interval 15 >/dev/null || die "sidecar-ci failed: $RUN_URL"
  gh run download "$RUN_ID" -n "$ARTIFACT_NAME" -D "$SIDECAR_DIR"
fi

[ -f "$SIDECAR_DIR/kerbcast-sidecar" ] || die "sidecar artifact has no kerbcast-sidecar binary"
[ -f "$SIDECAR_DIR/build-info.txt" ] || die "sidecar artifact has no build-info.txt"
BUILT_SHA="$(sed -n 's/^git_sha=//p' "$SIDECAR_DIR/build-info.txt")"
[ "$BUILT_SHA" = "$SHA" ] || die "sidecar artifact is for ${BUILT_SHA:-unknown}, not HEAD $SHA"
BUILT_VERSION="$(sed -n 's/^version_override=//p' "$SIDECAR_DIR/build-info.txt")"
if [ -z "$SIDECAR_FROM" ] && [ "$BUILT_VERSION" != "$DEV_VERSION" ]; then
  die "sidecar was built with version '${BUILT_VERSION}', expected $DEV_VERSION"
fi
chmod +x "$SIDECAR_DIR/kerbcast-sidecar"

# Plugin DLL. KSP assemblies are only read from the syncthing tree.
echo "dev-build: building Kerbcast.dll"
PLUGIN_LOG="$WORK/plugin-build.log"
if ! dotnet build Plugin/Kerbcast.csproj -c Release --no-incremental -warnaserror \
  "/p:KspManaged=$KSP_MANAGED" "/p:KspGameData=$KSP_GAMEDATA" >"$PLUGIN_LOG" 2>&1; then
  cat "$PLUGIN_LOG" >&2
  die "plugin build failed"
fi
if ! grep -qE '^ *0 Warning\(s\)' "$PLUGIN_LOG"; then
  cat "$PLUGIN_LOG" >&2
  die "plugin build produced warnings (zero-warnings rule)"
fi
DLL="Plugin/obj/Release/Kerbcast.dll"
[ -f "$DLL" ] || die "expected $DLL after the build"
API_DLL="Plugin/Kerbcast.API/bin/Release/Kerbcast.API.dll"
[ -f "$API_DLL" ] || die "expected $API_DLL after the build"

DEV_DLL="Plugin/Kerbcast.DevProvider/bin/Release/Kerbcast.DevProvider.dll"
if [ "$DEV_PROVIDER" = "1" ]; then
  echo "dev-build: building Kerbcast.DevProvider.dll"
  DEV_LOG="$WORK/dev-provider-build.log"
  if ! dotnet build Plugin/Kerbcast.DevProvider/Kerbcast.DevProvider.csproj -c Release --no-incremental -warnaserror \
    "/p:KspManaged=$KSP_MANAGED" >"$DEV_LOG" 2>&1; then
    cat "$DEV_LOG" >&2
    die "dev provider build failed"
  fi
  [ -f "$DEV_DLL" ] || die "expected $DEV_DLL after the build"
fi

# Readback .so: not built here, taken from the latest release and cached.
RELEASE_TAG="$(gh release view --json tagName -q .tagName)"
SO_CACHE="$CACHE/readback/$RELEASE_TAG/libAsyncGPUReadbackPlugin.so"
if [ ! -f "$SO_CACHE" ]; then
  echo "dev-build: fetching libAsyncGPUReadbackPlugin.so from $RELEASE_TAG"
  mkdir -p "$WORK/release"
  gh release download "$RELEASE_TAG" --pattern "kerbcast-${RELEASE_TAG}.zip" -D "$WORK/release"
  unzip -q -o "$WORK/release/kerbcast-${RELEASE_TAG}.zip" "$READBACK_IN_ZIP" -d "$WORK/release/x"
  mkdir -p "$(dirname "$SO_CACHE")"
  cp "$WORK/release/x/$READBACK_IN_ZIP" "$SO_CACHE.tmp"
  mv "$SO_CACHE.tmp" "$SO_CACHE"
fi

# Plugin tgz, extracted at GameData/Kerbcast/ (same layout as the release zip).
STAGE="$WORK/stage"
mkdir -p "$STAGE/Plugins/linux-x64" "$STAGE/Sidecar/linux-x64"
cp "$DLL" "$STAGE/Plugins/Kerbcast.dll"
cp "$API_DLL" "$STAGE/Plugins/Kerbcast.API.dll"
cp Plugin/Kerbcast.API/LICENSE "$STAGE/Plugins/Kerbcast.API.LICENSE.txt"
if [ "$DEV_PROVIDER" = "1" ]; then cp "$DEV_DLL" "$STAGE/Plugins/Kerbcast.DevProvider.dll"; fi
cp "$SO_CACHE" "$STAGE/Plugins/linux-x64/libAsyncGPUReadbackPlugin.so"
chmod 644 "$STAGE/Plugins/linux-x64/libAsyncGPUReadbackPlugin.so"
cp -R "$SIDECAR_DIR"/. "$STAGE/Sidecar/linux-x64/"
chmod +x "$STAGE/Sidecar/linux-x64/kerbcast-sidecar"
cp Plugin/settings.cfg "$STAGE/settings.cfg.reference"

# The dev marker turns on a development signal speed; it is placed by hand on a test install, never shipped.
[ ! -e "$STAGE/PluginData" ] || die "PluginData must never ship"

mkdir -p "$WORK/out"
PLUGIN_TGZ="kerbcast-plugin-${DEV_VERSION}.tgz"
COPYFILE_DISABLE=1 tar -czf "$WORK/out/$PLUGIN_TGZ" -C "$STAGE" Plugins Sidecar settings.cfg.reference

# npm packages at DEV_VERSION, packed from temp copies so the tree is untouched.
echo "dev-build: building npm packages"
[ -d node_modules ] || pnpm install --frozen-lockfile >/dev/null
pnpm --filter @ksp-gonogo/kerbcast run build >/dev/null
pnpm --filter @ksp-gonogo/kerbcast-react run build >/dev/null

pack_pkg() {
  local src="$1" name="$2" deps_filter="$3" copy
  copy="$WORK/pkg-$name"
  mkdir -p "$copy"
  (cd "$src" && tar -cf - --exclude node_modules .) | tar -xf - -C "$copy"
  jq --arg v "$DEV_VERSION" ".version = \$v | $deps_filter" "$src/package.json" >"$copy/package.json"
  (cd "$copy" && npm pack --ignore-scripts --silent --pack-destination "$WORK/out" >/dev/null)
}
pack_pkg client-sdk/typescript kerbcast "."
# shellcheck disable=SC2016 # $v is a jq variable
pack_pkg client-sdk/react kerbcast-react '.dependencies["@ksp-gonogo/kerbcast"] = $v'
SDK_TGZ="ksp-gonogo-kerbcast-${DEV_VERSION}.tgz"
REACT_TGZ="ksp-gonogo-kerbcast-react-${DEV_VERSION}.tgz"
[ -f "$WORK/out/$SDK_TGZ" ] && [ -f "$WORK/out/$REACT_TGZ" ] || die "npm pack did not produce the expected tarballs"
if grep -q 'workspace:' <(tar -xOzf "$WORK/out/$REACT_TGZ" package/package.json); then
  die "packed react package still has a workspace: dependency"
fi

# Manifest.
MERGE_BASE="$(git merge-base HEAD origin/main)"
DESCRIBE="$(git describe --tags --always HEAD)"
FILES_JSON="$(
  for f in "$PLUGIN_TGZ" "$SDK_TGZ" "$REACT_TGZ"; do
    jq -n --arg f "$f" --arg s "$(sha256 "$WORK/out/$f")" '{($f): $s}'
  done | jq -s 'add'
)"
INNER_JSON="$(jq -n \
  --arg dll "$(sha256 "$STAGE/Plugins/Kerbcast.dll")" \
  --arg apidll "$(sha256 "$STAGE/Plugins/Kerbcast.API.dll")" \
  --arg sidecar "$(sha256 "$STAGE/Sidecar/linux-x64/kerbcast-sidecar")" \
  --arg so "$(sha256 "$STAGE/Plugins/linux-x64/libAsyncGPUReadbackPlugin.so")" \
  '{"Plugins/Kerbcast.dll": $dll,
    "Plugins/Kerbcast.API.dll": $apidll,
    "Sidecar/linux-x64/kerbcast-sidecar": $sidecar,
    "Plugins/linux-x64/libAsyncGPUReadbackPlugin.so": $so}')"
if [ "$DEV_PROVIDER" = "1" ]; then
  INNER_JSON="$(jq --arg dev "$(sha256 "$STAGE/Plugins/Kerbcast.DevProvider.dll")" \
    '. + {"Plugins/Kerbcast.DevProvider.dll": $dev}' <<<"$INNER_JSON")"
fi
CHANGES_JSON="$(git log --format=%s origin/main..HEAD | jq -R . | jq -s .)"
jq -n \
  --arg version "$DEV_VERSION" \
  --arg sha "$SHA" \
  --arg branch "$BRANCH" \
  --arg describe "$DESCRIBE" \
  --arg merge_base "$MERGE_BASE" \
  --arg main_version "$MAIN_VERSION" \
  --arg built_at "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  --arg run_url "$RUN_URL" \
  --arg readback_tag "$RELEASE_TAG" \
  --arg plugin_tgz "$PLUGIN_TGZ" \
  --argjson files "$FILES_JSON" \
  --argjson inner "$INNER_JSON" \
  --argjson changes "$CHANGES_JSON" \
  '{version: $version,
    git_sha: $sha,
    branch: $branch,
    base: {describe: $describe, merge_base_origin_main: $merge_base, main_version: $main_version},
    built_at: $built_at,
    sidecar_ci_run: $run_url,
    readback_lib_from_release: $readback_tag,
    plugin_tgz_extract_to: "GameData/Kerbcast",
    sha256: $files,
    plugin_tgz_contents_sha256: $inner,
    changes: $changes}' >"$WORK/out/manifest.json"

mkdir -p "$OUT_ROOT"
mv "$WORK/out" "$OUT"
ln -sfn "$DEV_VERSION" "$OUT_ROOT/latest"

echo
echo "dev-build: done -> $OUT"
echo "  version   $DEV_VERSION ($BRANCH @ $SHORT_SHA, base $DESCRIBE)"
echo "  sidecar   $RUN_URL"
echo "  readback  .so from $RELEASE_TAG ($(sha256 "$SO_CACHE" | cut -c1-12))"
(cd "$OUT" && ls -1)
