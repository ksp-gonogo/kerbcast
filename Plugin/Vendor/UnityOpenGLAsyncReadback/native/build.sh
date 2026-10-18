#!/usr/bin/env bash
# Build libAsyncGPUReadbackPlugin.so from pinned upstream source plus the
# kerbcast self-init patch, then verify it with check.sh. Run on ubuntu-22.04
# (glibc 2.35), the same floor as the Linux sidecar, so the result loads inside
# Steam's pressure-vessel runtime.
#
# Usage: build.sh <out-dir>
# Needs: git cmake g++ make libgl1-mesa-dev libglu1-mesa-dev binutils
set -euo pipefail

UPSTREAM_REPO=https://github.com/yangrc1234/UnityOpenGLAsyncReadback.git
UPSTREAM_SHA=aff05c25f2c12e929d009948ebf5e84bcd1ac21d

here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$1"
out="$(cd "$1" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

git clone --quiet "$UPSTREAM_REPO" "$work/upstream"
git -C "$work/upstream" checkout --quiet "$UPSTREAM_SHA"
git -C "$work/upstream" apply "$here/kerbcast-self-init.patch"

cd "$work/upstream/NativePlugin"
# Upstream predates modern gcc's warnings; -Werror would fail the build on them.
sed -i 's/ -Werror//g' CMakeLists.txt
#
# libstdc++ stays dynamic on purpose: the library only needs GLIBCXX_3.4.11,
# while linking it statically pulls in symbols that raise the glibc floor
# from 2.32 to 2.34. check.sh enforces both ceilings.
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release -DOpenGL_GL_PREFERENCE=LEGACY
cmake --build build --config Release -j
strip --strip-unneeded build/libAsyncGPUReadbackPlugin.so

cp build/libAsyncGPUReadbackPlugin.so "$out/"
{
  echo "upstream=$UPSTREAM_REPO@$UPSTREAM_SHA"
  echo "patch=kerbcast-self-init.patch sha256:$(sha256sum "$here/kerbcast-self-init.patch" | cut -d' ' -f1)"
  echo "built_on=$(. /etc/os-release && echo "$PRETTY_NAME"), $(ldd --version | head -1)"
  echo "sha256=$(sha256sum "$out/libAsyncGPUReadbackPlugin.so" | cut -d' ' -f1)"
} > "$out/build-info.txt"
cat "$out/build-info.txt"

"$here/check.sh" "$out/libAsyncGPUReadbackPlugin.so"
