#!/usr/bin/env bash
# Verify a libAsyncGPUReadbackPlugin.so is shippable: an x86_64 ELF shared
# object exporting every entry point the plugin binds, needing no glibc newer
# than 2.35 (the sidecar's floor, which Steam's pressure-vessel runtime meets).
#
# Usage: check.sh <path-to-so>
set -euo pipefail
so="$1"

fail() { echo "::error::$so: $*"; exit 1; }

[ -f "$so" ] || fail "missing"
header="$(readelf -h "$so" 2>/dev/null)" || fail "not a valid ELF file"
grep -q 'Class:.*ELF64' <<<"$header" || fail "not a 64-bit ELF"
grep -q 'Type:.*DYN' <<<"$header" || fail "not a shared object"
grep -q 'Machine:.*X86-64' <<<"$header" || fail "not x86_64"

exports="$(nm -D --defined-only "$so" 2>/dev/null | awk '{print $NF}')" || fail "cannot read dynamic symbols"
for sym in RequestTextureMainThread RequestComputeBufferMainThread \
           GetKickstartFunctionPtr GetUpdateRenderThreadFunctionPtr \
           UpdateMainThread GetData TaskError TaskExists TaskDone \
           KerbcastGlState; do
  grep -qx "$sym" <<<"$exports" || fail "does not export $sym"
done

max_glibc="$(objdump -T "$so" | { grep -oE 'GLIBC_[0-9]+\.[0-9]+' || true; } | sed 's/GLIBC_//' | sort -uV | tail -1)"
[ -n "$max_glibc" ] || fail "no glibc version requirements found"
awk -v m="$max_glibc" 'BEGIN { split(m, a, "."); exit !(a[1] < 2 || (a[1] == 2 && a[2] <= 35)) }' \
  || fail "needs GLIBC_$max_glibc, above the 2.35 ceiling"

# Steam's soldier runtime (Debian 10) provides GLIBCXX_3.4.25.
max_glibcxx="$(objdump -T "$so" | { grep -oE 'GLIBCXX_[0-9]+\.[0-9]+(\.[0-9]+)?' || true; } | sed 's/GLIBCXX_//' | sort -uV | tail -1)"
if [ -n "$max_glibcxx" ]; then
  [ "$(printf '%s\n3.4.25\n' "$max_glibcxx" | sort -V | tail -1)" = "3.4.25" ] \
    || fail "needs GLIBCXX_$max_glibcxx, above the 3.4.25 ceiling"
fi

echo "OK: $so (x86_64 ELF shared object, all entry points exported, max GLIBC_$max_glibc, max GLIBCXX_${max_glibcxx:-none})"
