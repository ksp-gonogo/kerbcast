#!/usr/bin/env bash
# Run the exact CI steps (fmt + clippy + test) inside an Ubuntu 22.04
# container so Linux-specific code (#[cfg(target_os = "linux")]) is
# checked before pushing.
#
# Usage:
#   ./scripts/ci-check.sh          # run all steps
#   ./scripts/ci-check.sh fmt      # fmt only
#   ./scripts/ci-check.sh clippy   # clippy only
#   ./scripts/ci-check.sh test     # test only
#
# Requires: podman (or docker — set CONTAINER_RUNTIME=docker)
set -euo pipefail

RUNTIME="${CONTAINER_RUNTIME:-podman}"
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STEP="${1:-all}"
IMAGE="ubuntu:22.04"

# Inline setup script — mirrors .github/workflows/sidecar-ci.yml exactly.
SETUP='
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends \
  curl ca-certificates pkg-config clang libclang-dev \
  libavcodec-dev libavutil-dev libavformat-dev \
  libavfilter-dev libswscale-dev libva-dev 2>/dev/null
curl --proto "=https" --tlsv1.2 -sSf https://sh.rustup.rs \
  | sh -s -- -y --default-toolchain stable \
    --component rustfmt,clippy --profile minimal 2>/dev/null
source "$HOME/.cargo/env"
'

case "$STEP" in
  fmt)    CMDS='cargo fmt --check' ;;
  clippy) CMDS='cargo clippy --all-targets -- -D warnings' ;;
  test)   CMDS='cargo test --all-targets' ;;
  all)    CMDS='cargo fmt --check && cargo clippy --all-targets -- -D warnings && cargo test --all-targets' ;;
  *)      echo "unknown step: $STEP (fmt|clippy|test|all)"; exit 1 ;;
esac

echo "=== ci-check: $STEP (in $IMAGE via $RUNTIME) ==="
"$RUNTIME" run --rm \
  -v "$REPO_ROOT/sidecar:/work/sidecar:ro" \
  -e CARGO_TARGET_DIR=/tmp/kerbcast-target \
  -w /work/sidecar \
  "$IMAGE" \
  bash -c "$SETUP source \"\$HOME/.cargo/env\" && $CMDS"
