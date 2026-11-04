#!/usr/bin/env bash
#
# Shaped-relay rig: a slow viewer and a LAN viewer watching the same cameras,
# on one Mac, without root.
#
# A coturn relay runs in a container that shapes its downlink to the viewer
# with tc (NET_ADMIN inside the container's VM, nothing on the host), the way
# a phone's slow downlink would. Only packets leaving the relay's TURN port
# are shaped. Rootless podman hands traffic forwarded from the host to the
# container over its loopback, so loopback is shaped as well as the
# container's own interface. The sidecar runs
# natively against fake test-pattern cameras. rig.mjs opens one headless
# viewer directly and one forced through the relay, and prints what each gets.
#
#   scripts/shaped-relay/run.sh [--rate 1500kbit] [--delay 60ms] [--loss 0%]
#                               [--cameras 101,102,103] [--seconds 60]
#                               [--pin-at 20] [--unpin-at 40]
#   scripts/shaped-relay/run.sh --clean     # also remove the relay image
#
# Reshape a running relay from another shell:
#   podman exec kc-shaped-turn /shape.sh 800kbit 80ms 1% [queue packets, default 100]
#
# RIG_LOG_DIR=<dir> keeps the relay and sidecar logs after the run.
#
# Needs podman (or CONTAINER_RUNTIME=docker), cargo and node. Playwright is
# installed on first use into ~/.cache/kerbcast-shaped-relay.

set -euo pipefail

RT=${CONTAINER_RUNTIME:-podman}
IMAGE=kerbcast-shaped-turn:4
NAME=kc-shaped-turn
HERE=$(cd "$(dirname "$0")" && pwd)
ROOT=$(cd "$HERE/../.." && pwd)
CACHE=${HOME}/.cache/kerbcast-shaped-relay
export CARGO_TARGET_DIR=${CARGO_TARGET_DIR:-$HOME/personal/kerbcast-target}

RATE=1500kbit DELAY=60ms LOSS=0% CAMERAS=101,102,103 SECONDS_=60 PIN_AT="" UNPIN_AT=""
PORT=18088
while [ $# -gt 0 ]; do
  case "$1" in
    --rate) RATE=$2; shift 2 ;;
    --delay) DELAY=$2; shift 2 ;;
    --loss) LOSS=$2; shift 2 ;;
    --cameras) CAMERAS=$2; shift 2 ;;
    --seconds) SECONDS_=$2; shift 2 ;;
    --pin-at) PIN_AT=$2; shift 2 ;;
    --unpin-at) UNPIN_AT=$2; shift 2 ;;
    --clean) "$RT" rm -f "$NAME" >/dev/null 2>&1 || true; "$RT" rmi -f "$IMAGE" >/dev/null 2>&1 || true; echo "removed $NAME and $IMAGE"; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

# Fresh relay credentials each run: the relay is reachable on the LAN.
TURN_USER=kc$(openssl rand -hex 4)
TURN_PASS=$(openssl rand -hex 12)

SHM=$(mktemp -d "${TMPDIR:-/tmp}/kc-shaped-relay.XXXXXX")
PIDS=()
cleanup() {
  for p in "${PIDS[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null || true; done
  if [ -n "${RIG_LOG_DIR:-}" ]; then
    mkdir -p "$RIG_LOG_DIR"
    "$RT" logs "$NAME" > "$RIG_LOG_DIR/turn.log" 2>&1 || true
    "$RT" exec "$NAME" sh -c 'ip -s link; tc -s qdisc show' > "$RIG_LOG_DIR/turn-net.txt" 2>&1 || true
    cp "$SHM/sidecar.log" "$RIG_LOG_DIR/" 2>/dev/null || true
  fi
  "$RT" rm -f "$NAME" >/dev/null 2>&1 || true
  rm -rf "$SHM"
}
trap cleanup EXIT

if ! "$RT" image exists "$IMAGE" 2>/dev/null && ! "$RT" image inspect "$IMAGE" >/dev/null 2>&1; then
  echo "building $IMAGE"
  "$RT" build -t "$IMAGE" -f - "$HERE" <<'EOF'
FROM docker.io/library/ubuntu:22.04
RUN apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends coturn iproute2 \
 && rm -rf /var/lib/apt/lists/*
RUN printf '%s\n' '#!/bin/sh' \
  'for dev in lo $(ip route show default | awk "{print \$5; exit}"); do' \
  '  tc qdisc del dev "$dev" root 2>/dev/null' \
  '  tc qdisc add dev "$dev" root handle 1: prio || exit 1' \
  '  tc qdisc add dev "$dev" parent 1:3 handle 30: netem delay "$2" loss "$3" rate "$1" limit "${4:-100}" || exit 1' \
  '  tc filter add dev "$dev" parent 1:0 protocol ip prio 1 u32 match ip sport 3478 0xffff flowid 1:3 || exit 1' \
  '  echo "shaped $dev downlink: rate $1 delay $2 loss $3 queue ${4:-100} packets"' \
  'done' > /shape.sh && chmod +x /shape.sh
EOF
fi

# Chrome will not use a TURN server on loopback, so the relay is reached at
# the Mac's own LAN address.
TURN_HOST=${TURN_HOST:-$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1)}
[ -n "$TURN_HOST" ] || { echo "no LAN address for the relay; set TURN_HOST" >&2; exit 1; }
"$RT" rm -f "$NAME" >/dev/null 2>&1 || true
"$RT" run -d --name "$NAME" --cap-add NET_ADMIN \
  -p 3478:3478/udp -p 49160-49170:49160-49170/udp "$IMAGE" \
  sh -c "/shape.sh $RATE $DELAY $LOSS && exec turnserver -n --no-cli --no-tls --no-dtls \
    --listening-port=3478 --min-port=49160 --max-port=49170 --external-ip=$TURN_HOST \
    --lt-cred-mech --user=$TURN_USER:$TURN_PASS --realm=kerbcast --fingerprint --allow-loopback-peers \
    --log-file=stdout" >/dev/null
sleep 1
relay_log=$("$RT" logs "$NAME" 2>&1)
case "$relay_log" in
  *shaped*) echo "$relay_log" | grep shaped ;;
  *) echo "relay did not shape (tc missing NET_ADMIN?)"; echo "$relay_log"; exit 1 ;;
esac

(cd "$ROOT/sidecar" && cargo build -q --release --example fake_camera && cargo build -q --release)
IFS=, read -ra CAMS <<<"$CAMERAS"
for id in "${CAMS[@]}"; do
  "$CARGO_TARGET_DIR/release/examples/fake_camera" "$SHM" "$id" "Cam$id" >/dev/null &
  PIDS+=($!)
done
# The relay reaches the Mac through the container VM's NAT at this address.
HOST_IN_VM=$("$RT" run --rm docker.io/library/ubuntu:22.04 getent hosts host.containers.internal | awk '{print $1; exit}')
KERBCAST_RIG_ICE_IP=$HOST_IN_VM KERBCAST_RIG_MANUAL_TIERS=1 RUST_LOG=${RUST_LOG:-info} "$CARGO_TARGET_DIR/release/kerbcast-sidecar" --shm-dir "$SHM" \
  --http-bind "127.0.0.1:$PORT" > "$SHM/sidecar.log" 2>&1 &
PIDS+=($!)
for _ in $(seq 50); do curl -sf "http://127.0.0.1:$PORT/health" >/dev/null && break; sleep 0.2; done

if [ ! -d "$CACHE/node_modules/playwright" ]; then
  mkdir -p "$CACHE"
  npm install --silent --prefix "$CACHE" playwright@1.60.0 >/dev/null
  (cd "$CACHE" && npx --yes playwright install chromium >/dev/null)
fi
cp "$HERE/rig.mjs" "$CACHE/rig.mjs"
set +e
node "$CACHE/rig.mjs" --sidecar "http://127.0.0.1:$PORT" --turn "$TURN_HOST:3478" --turn-user "$TURN_USER" --turn-pass "$TURN_PASS" \
  --cameras "$CAMERAS" --seconds "$SECONDS_" ${PIN_AT:+--pin-at "$PIN_AT"} ${UNPIN_AT:+--unpin-at "$UNPIN_AT"}
rc=$?
set -e
echo "--- sidecar tier and link lines"
grep -E "tier|viewer link|REMB" "$SHM/sidecar.log" | sed -E 's/\x1b\[[0-9;]*m//g' | tail -40
exit $rc
