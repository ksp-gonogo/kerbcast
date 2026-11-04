/*
 * Drives two headless viewers of the same cameras against a local sidecar:
 * one direct (the LAN viewer) and one forced through the shaped TURN relay
 * (the slow viewer). Prints each viewer's delivered size, frame rate and
 * freezes every 2 s, plus the sidecar's view of each link from GET /links.
 *
 * Usage: node rig.mjs --sidecar http://127.0.0.1:8088 --turn <lan ip>:3478
 *          --turn-user <user> --turn-pass <password>
 *          --cameras 101,102,103 --seconds 60 [--pin-at 20] [--unpin-at 40]
 *
 * --pin-at moves the relayed viewer's feeds to the Reduced tier through
 * POST /links/tier (the sidecar must run with KERBCAST_RIG_MANUAL_TIERS=1),
 * and --unpin-at moves them back, so a run shows a tier
 * switch on one viewer while the other keeps the full stream.
 */
import { chromium } from "playwright";

function arg(name, fallback) {
  const i = process.argv.indexOf(`--${name}`);
  return i >= 0 ? process.argv[i + 1] : fallback;
}

const sidecar = arg("sidecar", "http://127.0.0.1:8088");
const turn = arg("turn", "127.0.0.1:3478");
const turnUser = arg("turn-user", "kc");
const turnPass = arg("turn-pass", "kc");
const cameras = arg("cameras", "101").split(",").map(Number);
const seconds = Number(arg("seconds", "60"));
const pinAt = arg("pin-at") === undefined ? undefined : Number(arg("pin-at"));
const unpinAt = arg("unpin-at") === undefined ? undefined : Number(arg("unpin-at"));

/* Runs inside the page: a bare viewer speaking the sidecar's /offer protocol. */
async function viewer({ sidecar, cameras, relay, turn, turnUser, turnPass }) {
  const iceServers = relay
    ? [{ urls: `turn:${turn}?transport=udp`, username: turnUser, credential: turnPass }]
    : [];
  const pc = new RTCPeerConnection({
    iceServers,
    iceTransportPolicy: relay ? "relay" : "all",
  });
  pc.createDataChannel("kerbcast-control");
  for (let i = 0; i < cameras.length; i++) pc.addTransceiver("video", { direction: "recvonly" });
  window.__tracks = [];
  pc.ontrack = (e) => {
    const v = document.createElement("video");
    v.muted = true;
    v.autoplay = true;
    v.srcObject = new MediaStream([e.track]);
    document.body.appendChild(v);
    window.__tracks.push(e.track);
  };
  await pc.setLocalDescription(await pc.createOffer());
  await new Promise((r) => {
    if (pc.iceGatheringState === "complete") return r();
    pc.onicegatheringstatechange = () => pc.iceGatheringState === "complete" && r();
    setTimeout(r, 5000);
  });
  const res = await fetch(`${sidecar}/offer`, {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ sdp: pc.localDescription.sdp, cameras, slots: cameras.length }),
  });
  const answer = await res.json();
  await pc.setRemoteDescription({ type: "answer", sdp: answer.sdp });
  window.__pc = pc;
  return answer.cameras;
}

/* Runs inside the page: one line of receiver stats per feed. */
async function sample() {
  const report = await window.__pc.getStats();
  const out = [];
  report.forEach((s) => {
    if (s.type === "inbound-rtp" && s.kind === "video") {
      out.push({
        mid: s.mid,
        w: s.frameWidth ?? 0,
        h: s.frameHeight ?? 0,
        fps: Math.round(s.framesPerSecond ?? 0),
        freezes: s.freezeCount ?? 0,
        plis: s.pliCount ?? 0,
        kbytes: Math.round((s.bytesReceived ?? 0) / 1000),
      });
    }
  });
  out.sort((a, b) => String(a.mid).localeCompare(String(b.mid)));
  return { state: window.__pc.connectionState, feeds: out };
}

async function setTier(peerId, tier) {
  for (const flight of cameras) {
    const res = await fetch(`${sidecar}/links/tier`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ peerId, flightId: flight, tier }),
    });
    console.log(`pin peer ${peerId} camera ${flight} -> ${tier}: ${res.status} ${await res.text()}`);
  }
}

const browser = await chromium.launch({
  args: ["--autoplay-policy=no-user-gesture-required"],
});
const open = async (relay) => {
  const page = await browser.newPage();
  await page.goto(`${sidecar}/health`);
  const bound = await page.evaluate(viewer, { sidecar, cameras, relay, turn, turnUser, turnPass });
  return { page, bound };
};

const direct = await open(false);
const before = new Set(
  ((await (await fetch(`${sidecar}/links`)).json()).peers ?? []).map((p) => p.peerId),
);
const relayed = await open(true);
await new Promise((r) => setTimeout(r, 500));
const relayedPeer = ((await (await fetch(`${sidecar}/links`)).json()).peers ?? [])
  .map((p) => p.peerId)
  .find((id) => !before.has(id));
console.log(`direct bound ${direct.bound}, relayed bound ${relayed.bound}, relayed peer ${relayedPeer}`);

const line = (f) => `${f.w}x${f.h}@${f.fps} frz${f.freezes} pli${f.plis}`;
for (let t = 2; t <= seconds; t += 2) {
  await new Promise((r) => setTimeout(r, 2000));
  if (pinAt !== undefined && t === pinAt && relayedPeer) await setTier(relayedPeer, "reduced");
  if (unpinAt !== undefined && t === unpinAt && relayedPeer) await setTier(relayedPeer, "full");
  const d = await direct.page.evaluate(sample);
  const r = await relayed.page.evaluate(sample);
  const links = (await (await fetch(`${sidecar}/links`)).json()).peers ?? [];
  const link = links.find((p) => p.peerId === relayedPeer)?.link;
  const linkText = link
    ? `send ${Math.round(link.sendBps / 1000)}k loss ${(link.loss * 100).toFixed(1)}% rtt ${Math.round(link.rttMs ?? -1)} pli/s ${link.plisPerS.toFixed(2)}`
    : "no window yet";
  console.log(
    `t=${String(t).padStart(3)}s direct[${d.state}] ${d.feeds.map(line).join(" | ")}  ` +
      `relayed[${r.state}] ${r.feeds.map(line).join(" | ")}  link: ${linkText}`,
  );
}
await browser.close();
