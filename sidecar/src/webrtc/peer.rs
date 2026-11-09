//! WebRTC peer for the kerbcast sidecar. One `KerbcastPeer` per browser
//! connection; each carries one H.264 video track per camera the browser
//! subscribed to AND a "kerbcast-control" data channel for bidirectional
//! protocol messages (see `crate::protocol`). webrtc-rs handles
//! ICE/DTLS/SRTP/RTP packetisation internally.
//!
//! Track lifecycle: the peer owns Arcs to its tracks for the duration of
//! its RTCPeerConnection. Each camera's registry entry holds a Weak ref
//! to the same track + a subscriber count. When the peer is dropped,
//! the Arcs go with it; the camera-side consume loop notices the dead
//! Weaks on its next tick and decrements its subscriber count.
//!
//! Data channel: created by the browser side (the offer SDP includes a
//! datachannel m-section). When it opens, the handler dispatches
//! incoming `ClientMessage` JSON; the server replies / pushes
//! `ServerMessage` JSON back. The plugin reads the mutated control
//! state via its periodic `*.control.json` poll, so layer/render-size
//! changes propagate within ~1s of the data-channel write.

use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::{Arc, Mutex as StdMutex, MutexGuard};
use std::time::Duration;

/// Monotonic counter for per-peer identifiers. Identifies a peer for
/// logging and slot bookkeeping (SetDegrade arrives via the data
/// channel rather than RTCP, so there's no SSRC to lean on).
static NEXT_PEER_ID: AtomicU32 = AtomicU32::new(1);

use anyhow::{anyhow, Result};
use tokio::sync::{Mutex, Notify};
/* tokio's clock, not std's, so tests can freeze and advance it. */
use tokio::time::Instant;
use tracing::{info, warn};

use webrtc::api::interceptor_registry::register_default_interceptors;
use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_H264};
use webrtc::api::setting_engine::SettingEngine;
use webrtc::api::APIBuilder;
use webrtc::data_channel::data_channel_message::DataChannelMessage;
use webrtc::data_channel::RTCDataChannel;
use webrtc::interceptor::registry::Registry as InterceptorRegistry;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtcp;
use webrtc::rtp_transceiver::rtp_codec::{
    RTCRtpCodecCapability, RTCRtpCodecParameters, RTPCodecType,
};
use webrtc::rtp_transceiver::rtp_sender::RTCRtpSender;
use webrtc::rtp_transceiver::RTCPFeedback;
use webrtc::rtp_transceiver::RTCRtpTransceiver;
use webrtc::track::track_local::TrackLocal;

use super::outbox::{ControlChannel, Outbox};
use super::video_track::VideoTrack;

use crate::cameras::{CameraRegistry, CommandFov};
use crate::encoder::selected_backend_name;
use crate::ladder::{Ladder, LadderConfig, LadderState, SlotTier, SlotView, Tier, TierChange};
use crate::link::{LinkMonitor, LinkWindow, SlotCounters, LINK_WINDOW};
use crate::protocol::{
    CameraSnapshotPayload, CameraStateChangedPayload, ClientMessage, ConfirmSlotsPayload,
    DestroyCommandCameraPayload, ErrorCode, ErrorPayload, ErrorSource, FlightIdPayload,
    HelloPayload, Layer, QualityPreset, ReportDisplaySizePayload, SceneStateChangedPayload,
    ServerMessage, SetBackgroundCaptureFpsPayload, SetDegradePayload,
    SetForceFullResolutionPayload, SetFovPayload, SetFrameStampsPayload, SetLayersPayload,
    SetPanPayload, SetPanRatePayload, SetQualityPayload, SetRenderSizePayload, SetSizeHoldPayload,
    SetThrottleMainScreenPayload, SetTrackTargetPayload, SetZoomRatePayload, SlotMapPayload,
};

const CONTROL_CHANNEL_LABEL: &str = "kerbcast-control";

/// Names an extra address to offer as a server-reflexive candidate, for the
/// shaped-relay test rig only.
const RIG_ICE_IP_ENV: &str = "KERBCAST_RIG_ICE_IP";

/// An unguessable name for a peer's session: 128 bits from a CSPRNG seeded
/// by the operating system.
fn session_token() -> String {
    format!("{:032x}", rand::random::<u128>())
}

/// The most video slots one connection may hold, counting slots added by
/// `KerbcastPeer::grow`. A safety net against a runaway client, not a viewing
/// limit: each bound slot costs the game a camera render, and the shed ladder
/// already sheds that load.
pub const MAX_SLOTS_PER_PEER: usize = 32;

/// The one video format Kerbcast sends: H.264 constrained baseline,
/// packetization-mode=1.
///
/// Packetization-mode=0 permits single-NAL packets only, and our encoder
/// emits fragmented and aggregated NALs, so a mode-0 payload type yields RTP
/// the browser accepts and cannot decode: traffic on the wire, readyState
/// stuck at 0, and not one error anywhere in the stack. Constrained baseline
/// at this level is the H.264 every browser offers.
const SENT_H264_FMTP: &str =
    "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f";

/// The payload type the sent format is registered under. The answer carries
/// the offerer's payload type for it instead, so this only has to be a valid
/// dynamic one.
const SENT_H264_PAYLOAD_TYPE: u8 = 125;

/// The sent format's capability, shared by the media engine and every slot
/// track so the track always binds to the payload type the answer names. Its
/// feedback only feeds the engine: the answer echoes the offer's own rtcp-fb.
fn sent_h264() -> RTCRtpCodecCapability {
    RTCRtpCodecCapability {
        mime_type: MIME_TYPE_H264.to_owned(),
        clock_rate: 90000,
        channels: 0,
        sdp_fmtp_line: SENT_H264_FMTP.to_owned(),
        rtcp_feedback: [
            ("goog-remb", ""),
            ("ccm", "fir"),
            ("nack", ""),
            ("nack", "pli"),
        ]
        .into_iter()
        .map(|(typ, parameter)| RTCPFeedback {
            typ: typ.to_owned(),
            parameter: parameter.to_owned(),
        })
        .collect(),
    }
}

/// A media engine that knows only the format Kerbcast sends.
///
/// The answer lists every codec of the offer the engine matches, so with
/// webrtc-rs's defaults it named VP8, VP9, AV1 and several H.264 variants
/// for a stream that is only ever one of them. Anything that picks the
/// answer's first codec (a relay terminating the connection, say) would then
/// label our H.264 as VP8. Registering one format leaves only H.264 in the
/// answer, and `prefer_sent_format` narrows that to one payload type.
fn sent_media_engine() -> Result<MediaEngine> {
    let mut media_engine = MediaEngine::default();
    media_engine.register_codec(
        RTCRtpCodecParameters {
            capability: sent_h264(),
            payload_type: SENT_H264_PAYLOAD_TYPE,
            ..Default::default()
        },
        RTPCodecType::Video,
    )?;
    Ok(media_engine)
}

/// An SDP refused because its video does not carry the format Kerbcast sends,
/// in every live video section and under one payload type.
#[derive(Debug)]
pub struct FormatRefused;

impl std::fmt::Display for FormatRefused {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(
            "the offer's video does not carry H.264 with packetization-mode=1 and \
             profile-level-id 42e0xx in every section under one payload type, the only \
             video Kerbcast sends",
        )
    }
}

impl std::error::Error for FormatRefused {}

/// One m= section of an SDP: its kind, whether its port is 0, the payload
/// types its m= line lists, and its attribute lines.
struct MediaSection<'a> {
    video: bool,
    port_zero: bool,
    formats: Vec<&'a str>,
    lines: Vec<&'a str>,
}

impl<'a> MediaSection<'a> {
    /// The value of the first `a=<attr>:<pt> <value>` line for `pt`.
    fn attribute(&self, attr: &str, pt: &str) -> Option<&'a str> {
        self.attributes(attr, pt).next()
    }

    /// The values of every `a=<attr>:<pt> <value>` line for `pt`.
    fn attributes<'s>(&'s self, attr: &'s str, pt: &'s str) -> impl Iterator<Item = &'a str> + 's {
        self.lines.iter().filter_map(move |l| {
            let (p, value) = l.strip_prefix(attr)?.split_once(' ')?;
            (p == pt).then_some(value)
        })
    }

    /// Whether `pt` is listed on this section's m= line as the sent format.
    fn is_sent_format(&self, pt: &str) -> bool {
        self.formats.contains(&pt)
            && self
                .attribute("a=rtpmap:", pt)
                .is_some_and(|codec| codec.to_ascii_lowercase().starts_with("h264/"))
            && self.attribute("a=fmtp:", pt).is_some_and(is_sent_fmtp)
    }

    /// The first payload type on the m= line that is the sent format, the one
    /// webrtc-rs's exact match lands on.
    fn first_sent_format(&self) -> Option<&'a str> {
        self.formats
            .iter()
            .copied()
            .find(|pt| self.is_sent_format(pt))
    }

    /// The rtcp-fb lines for `pt`, wildcard ones included.
    fn feedback(&self, pt: &str) -> Vec<RTCPFeedback> {
        self.attributes("a=rtcp-fb:", pt)
            .chain(self.attributes("a=rtcp-fb:", "*"))
            .map(|value| {
                let (typ, parameter) = value.trim().split_once(' ').unwrap_or((value.trim(), ""));
                RTCPFeedback {
                    typ: typ.to_owned(),
                    parameter: parameter.trim().to_owned(),
                }
            })
            .collect()
    }

    /// A section with port 0 is rejected or stopped, unless it is
    /// bundle-only, which uses port 0 and still carries media over the
    /// bundle.
    fn live_video(&self) -> bool {
        self.video && (!self.port_zero || self.lines.contains(&"a=bundle-only"))
    }
}

fn media_sections(sdp: &str) -> Vec<MediaSection<'_>> {
    let mut sections: Vec<MediaSection<'_>> = Vec::new();
    for line in sdp.lines().map(str::trim) {
        if let Some(m) = line.strip_prefix("m=") {
            let mut fields = m.split_whitespace();
            let kind = fields.next().unwrap_or_default();
            let port = fields.next().unwrap_or_default();
            sections.push(MediaSection {
                video: kind == "video",
                port_zero: port == "0",
                formats: fields.skip(1).collect(),
                lines: Vec::new(),
            });
        } else if let Some(section) = sections.last_mut() {
            section.lines.push(line);
        }
    }
    sections
}

/// The section webrtc-rs takes a connection's video codecs from: the first
/// m=video, whatever its port.
fn codec_source<'s, 'a>(sections: &'s [MediaSection<'a>]) -> Option<&'s MediaSection<'a>> {
    sections.iter().find(|s| s.video)
}

/// Mirrors webrtc-rs's exact H.264 match: the same packetization-mode, and a
/// profile-level-id whose profile bytes agree. Any level is taken, as
/// webrtc-rs takes it; browsers decode above the level they offer.
fn is_sent_fmtp(params: &str) -> bool {
    let mut mode1 = false;
    let mut constrained_baseline = false;
    for (key, value) in params.split(';').filter_map(|p| p.trim().split_once('=')) {
        let value = value.trim();
        if key.trim().eq_ignore_ascii_case("packetization-mode") {
            mode1 = value == "1";
        } else if key.trim().eq_ignore_ascii_case("profile-level-id") {
            constrained_baseline = value.len() == 6
                && value
                    .get(..4)
                    .is_some_and(|p| p.eq_ignore_ascii_case("42e0"));
        }
    }
    mode1 && constrained_baseline
}

/// Whether every live video section of `sdp` carries the sent format under
/// payload type `pt`.
fn carries_sent_format(sdp: &str, pt: &str) -> bool {
    let sections = media_sections(sdp);
    let mut video = sections.iter().filter(|s| s.live_video()).peekable();
    video.peek().is_some() && video.all(|s| s.is_sent_format(pt))
}

/// Whether an offer's video can be answered with the format Kerbcast sends.
///
/// webrtc-rs picks a connection's video codecs from the first video section
/// alone, whatever its port, and then answers every section with them. So
/// that section has to carry the sent format, and every live one has to carry
/// it under the same payload type, or the answer names a payload type that
/// section never offered. Plain baseline (42001f) is not taken: every browser
/// that offers it offers constrained baseline beside it.
pub fn offers_sent_h264(sdp: &str) -> bool {
    offered_payload_type(sdp).is_some_and(|pt| carries_sent_format(sdp, &pt))
}

/// The first payload type the codec-source section of an offer lists as the
/// sent format, the one webrtc-rs's exact match lands on.
fn offered_payload_type(sdp: &str) -> Option<String> {
    codec_source(&media_sections(sdp))
        .and_then(MediaSection::first_sent_format)
        .map(str::to_owned)
}

/// The sent format under payload type `pt` as the offer describes it: the
/// offerer's own fmtp and feedback, so the answer echoes what it asked for
/// rather than claiming a level or feedback it never offered.
fn offered_sent_format(sdp: &str, pt: &str) -> Option<RTCRtpCodecParameters> {
    let sections = media_sections(sdp);
    let section = sections.iter().find(|s| s.video && s.is_sent_format(pt))?;
    let sent = sent_h264();
    Some(RTCRtpCodecParameters {
        capability: RTCRtpCodecCapability {
            mime_type: sent.mime_type,
            clock_rate: sent.clock_rate,
            channels: sent.channels,
            sdp_fmtp_line: section.attribute("a=fmtp:", pt)?.trim().to_owned(),
            rtcp_feedback: section.feedback(pt),
        },
        payload_type: pt.parse().ok()?,
        ..Default::default()
    })
}

/// The payload type the first answered video section names, which is the
/// one every video section of the connection sends under.
fn answered_payload_type(answer: &str) -> Option<String> {
    media_sections(answer)
        .iter()
        .find(|s| s.live_video())
        .and_then(|s| s.formats.first())
        .map(|pt| (*pt).to_owned())
}

/// Checks that every answered video section names one payload type, and that
/// the matching offer section lists it as the sent format. Anything else is a
/// feed the browser cannot decode or a section it cannot accept.
fn check_answer(offer: &str, answer: &str) -> Result<()> {
    let offered = media_sections(offer);
    let answered = media_sections(answer);
    if offered.len() != answered.len() {
        return Err(anyhow!(
            "the answer has {} media sections for an offer of {}",
            answered.len(),
            offered.len()
        ));
    }
    let named_sent_format = offered
        .iter()
        .zip(&answered)
        .filter(|(o, a)| o.live_video() && a.live_video())
        .all(|(o, a)| matches!(a.formats.as_slice(), [pt] if o.is_sent_format(pt)));
    if named_sent_format {
        Ok(())
    } else {
        Err(FormatRefused.into())
    }
}

/// A slot's send-track, named by its index in the pool.
fn slot_track(i: usize, outbox: &Outbox) -> Arc<VideoTrack> {
    Arc::new(VideoTrack::new(
        sent_h264(),
        format!("video-slot{i}"),
        format!("kerbcast-slot{i}"),
        Some(outbox.clone()),
    ))
}

/// One pre-negotiated video slot. The track stays on the peer connection
/// for the connection's lifetime; `bound` records which camera currently
/// feeds it (`None` = idle/silent) and `mid` is the transceiver mid the
/// browser keys its `SlotMap` routing on (filled once the answer is set).
/// Only a `ready` slot takes a camera: a slot added by `KerbcastPeer::grow`
/// waits for the browser's `ConfirmSlots`, since until then the browser may
/// not have applied the answer that lets it play the slot.
struct Slot {
    track: Arc<VideoTrack>,
    mid: Option<String>,
    bound: Option<u32>,
    ready: bool,
    /// The transceiver `grow` added for this slot; the initial pool's slots
    /// are always confirmed and never need it.
    transceiver: Option<Arc<RTCRtpTransceiver>>,
}

/// How long a peer may sit in `Disconnected` before the reaper takes it.
/// `Disconnected` is recoverable: webrtc-ice raises it after 5s of silence and
/// only escalates to `Failed` 25s later (its `disconnected_timeout` +
/// `failed_timeout`), while a real network blip heals in well under a second.
/// `Failed` is the authority on a dead connection, so this window sits just
/// past the point where the agent would have delivered that verdict: it is the
/// backstop for a peer that never gets one, not the primary teardown path.
const DISCONNECTED_GRACE: Duration = Duration::from_secs(30);

/// How long a freshly built peer has to reach `Connected`. Same shape as the
/// grace window: webrtc-ice fails a never-connected agent after ~30s of
/// checks, so `Failed` still decides a negotiation that actually ran. This
/// covers the peer whose checks never started, e.g. a client that hangs up
/// mid-`/offer` leaves the agent with no local description, so it sits in
/// `New` forever holding its initial cameras subscribed.
const NEGOTIATION_TIMEOUT: Duration = Duration::from_secs(60);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Liveness {
    /// Built, never yet connected. Bounded by `NEGOTIATION_TIMEOUT`.
    Negotiating,
    Connected,
    /// Was connected, ICE dropped out. Recoverable, bounded by
    /// `DISCONNECTED_GRACE`.
    Disconnected {
        since: Instant,
    },
    /// Terminal: `Failed`, `Closed`, or a signalling exchange that failed.
    Dead,
}

/// Peer liveness as the reaper sees it. A plain state machine over
/// `RTCPeerConnectionState` plus two deadlines, kept free of any webrtc
/// plumbing so the timeout rules are testable without a browser.
struct PeerLiveness {
    state: Liveness,
    created_at: Instant,
}

impl PeerLiveness {
    fn new(now: Instant) -> Self {
        Self {
            state: Liveness::Negotiating,
            created_at: now,
        }
    }

    /// Track `state`, returning true when it is a recovery from
    /// `Disconnected`.
    fn observe(&mut self, state: RTCPeerConnectionState, now: Instant) -> bool {
        /* Dead is sticky: the reaper may already have closed this peer, and a
        late callback must not resurrect one whose cameras are released. */
        if self.state == Liveness::Dead {
            return false;
        }
        let recovered = matches!(self.state, Liveness::Disconnected { .. });
        match state {
            RTCPeerConnectionState::Connected => {
                self.state = Liveness::Connected;
                return recovered;
            }
            /* Only the first Disconnected records a timestamp: repeat
            notifications must not keep pushing the deadline out. */
            RTCPeerConnectionState::Disconnected
                if !matches!(self.state, Liveness::Disconnected { .. }) =>
            {
                self.state = Liveness::Disconnected { since: now }
            }
            RTCPeerConnectionState::Failed | RTCPeerConnectionState::Closed => {
                self.state = Liveness::Dead
            }
            _ => {}
        }
        false
    }

    fn is_alive(&self, now: Instant) -> bool {
        match self.state {
            Liveness::Dead => false,
            Liveness::Connected => true,
            Liveness::Negotiating => {
                now.saturating_duration_since(self.created_at) < NEGOTIATION_TIMEOUT
            }
            Liveness::Disconnected { since } => {
                now.saturating_duration_since(since) < DISCONNECTED_GRACE
            }
        }
    }
}

/// Shared handle on a peer's `PeerLiveness`: the connection-state callback
/// writes it, the reaper reads it. Lock poisoning is shrugged off because the
/// guarded value is two `Copy` fields that no panic can leave half-written.
#[derive(Clone)]
struct LivenessHandle(Arc<StdMutex<PeerLiveness>>);

impl LivenessHandle {
    fn new(now: Instant) -> Self {
        Self(Arc::new(StdMutex::new(PeerLiveness::new(now))))
    }

    fn guard(&self) -> MutexGuard<'_, PeerLiveness> {
        self.0
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
    }

    fn observe(&self, state: RTCPeerConnectionState, now: Instant) -> bool {
        self.guard().observe(state, now)
    }

    fn kill(&self) {
        self.guard().state = Liveness::Dead;
    }

    fn is_alive(&self, now: Instant) -> bool {
        self.guard().is_alive(now)
    }
}

pub struct KerbcastPeer {
    pc: Arc<RTCPeerConnection>,
    /// Stable identifier for this peer for the lifetime of the
    /// connection. Used for logging and slot bookkeeping.
    pub peer_id: u32,
    /// Names this peer to `POST /renegotiate`. Unlike `peer_id` it is unique
    /// across sidecar restarts, so a client still holding a peer from a
    /// previous process can never renegotiate someone else's.
    pub session: String,
    registry: Arc<CameraRegistry>,
    /// Held across a whole `grow`, so two re-offers never interleave.
    renegotiation: Mutex<()>,
    /// The pre-negotiated slot pool. `Subscribe` binds a camera to a free
    /// slot; `Unsubscribe` frees one — no renegotiation. Dropping the peer
    /// drops the slot-track Arcs; the matching Weak refs in each camera's
    /// `tracks` list go stale and are pruned on the next encode tick.
    slots: Arc<Mutex<Vec<Slot>>>,
    /// flight_ids bound at connect time — echoed (in slot order) in the
    /// `/offer` answer so the browser maps its initial cameras to slots.
    pub subscribed: Vec<u32>,
    /// Initial cameras that could not be bound, sent to the browser as typed
    /// errors right after Hello.
    pub refused: Arc<Vec<ErrorPayload>>,
    /// Every control-channel send for this peer goes through here, so none
    /// of them ever waits on the data channel.
    outbox: Outbox,
    connected: Arc<Notify>,
    /// Connection liveness, fed by the RTCPeerConnection state callback. The
    /// daemon polls `is_alive()` each consume tick and reaps what it reports
    /// dead.
    liveness: LivenessHandle,
    /// This viewer's connection quality, sampled every `LINK_WINDOW`.
    link: SharedLink,
}

/// A viewer's link measurements and the tier decisions made from them,
/// applied only when lighter streams for slow viewers is on.
#[derive(Debug)]
struct LinkState {
    monitor: LinkMonitor,
    ladder: Ladder,
}

type SharedLink = Arc<StdMutex<LinkState>>;

fn lock_link(link: &SharedLink) -> MutexGuard<'_, LinkState> {
    link.lock().unwrap_or_else(|e| e.into_inner())
}

impl KerbcastPeer {
    /// Build a peer with a pool of `slot_count` pre-negotiated H.264 video
    /// slots + a "kerbcast-control" data channel. The cameras in `initial`
    /// are bound to the first slots immediately (skipping unknown/destroyed
    /// ones); the rest stay idle until the browser `Subscribe`s a camera
    /// into them. `slot_count` must equal the number of recv-only video
    /// transceivers in the browser's offer or SDP negotiation won't match.
    pub async fn new(
        registry: Arc<CameraRegistry>,
        initial: &[u32],
        slot_count: usize,
    ) -> Result<Self> {
        let peer_id = NEXT_PEER_ID.fetch_add(1, Ordering::Relaxed);
        let mut media_engine = sent_media_engine()?;
        // Install the default interceptors — critically the NACK responder,
        // which retransmits packets the browser reports lost (via the `nack`
        // feedback the browser offers, which the answer echoes). Without it a
        // single lost packet breaks the H.264 reference chain with no recovery
        // until the next scheduled keyframe (~2s GOP) — feeds "cut to static
        // for a moment then return". The responder recovers the loss in ~1 RTT.
        // No REMB interceptor is in this set, and the encode side ignores
        // bandwidth estimates anyway, so the old REMB-driven reinit death spiral
        // (see main.rs) cannot recur.
        let mut interceptor_registry = InterceptorRegistry::new();
        interceptor_registry =
            register_default_interceptors(interceptor_registry, &mut media_engine)?;
        // Disable mDNS candidate obfuscation: the default QueryAndGather mode
        // generates .local hostnames that desktop Chrome resolves but Android
        // Chrome cannot, preventing streams from loading on mobile.
        let mut setting_engine = SettingEngine::default();
        setting_engine.set_ice_multicast_dns_mode(ice::mdns::MulticastDnsMode::QueryOnly);
        /* A test rig whose relay runs behind a container's NAT can only reach
        the sidecar at the address the NAT gives the host, so it names that
        address here and every peer also offers it as a server-reflexive
        candidate. Host candidates are untouched. */
        if let Ok(raw) = std::env::var(RIG_ICE_IP_ENV) {
            match raw.parse::<std::net::IpAddr>() {
                Ok(ip) => setting_engine.set_nat_1to1_ips(
                    vec![ip.to_string()],
                    webrtc::ice_transport::ice_candidate_type::RTCIceCandidateType::Srflx,
                ),
                Err(_) => warn!(value = %raw, "{RIG_ICE_IP_ENV} is not an IP address; ignored"),
            }
        }
        let api = APIBuilder::new()
            .with_media_engine(media_engine)
            .with_setting_engine(setting_engine)
            .with_interceptor_registry(interceptor_registry)
            .build();

        // No external STUN server: this is a LAN-only tool and host candidates
        // are sufficient. The Google STUN URL was unreachable over IPv6 from
        // the Deck (Network is unreachable) and its timeout caused 1-2 minute
        // delays before mobile ICE proceeded with what it had.
        let config = RTCConfiguration::default();

        let pc = Arc::new(api.new_peer_connection(config).await?);

        // Build the slot pool: one send-track per slot, added to the PC so
        // the answer carries `slot_count` video m-lines matching the offer's
        // recv-only transceivers. Each sender's RTCP must be drained or
        // webrtc-rs's NACK/PLI pipelines stall (see `drain_rtcp_sink`); the
        // senders are held here and the per-slot drain tasks are spawned once
        // the slot pool is shared (below), so each drain can resolve the camera
        // bound to its slot when it honors a PLI.
        let control_channel: ControlChannel = Arc::new(Mutex::new(None));
        let outbox = Outbox::spawn(control_channel.clone(), registry.anchor_owner(peer_id));
        let mut slots: Vec<Slot> = Vec::with_capacity(slot_count);
        let mut senders: Vec<Arc<RTCRtpSender>> = Vec::with_capacity(slot_count);
        for i in 0..slot_count {
            let track = slot_track(i, &outbox);
            let rtp_sender = pc
                .add_track(track.rtp_track() as Arc<dyn TrackLocal + Send + Sync>)
                .await?;
            senders.push(rtp_sender);
            slots.push(Slot {
                track,
                mid: None,
                bound: None,
                ready: true,
                transceiver: None,
            });
        }

        // Bind the initial subscription onto the first free slots. Camera ->
        // slot binding is just `cam.add_track`; the consume loop then feeds
        // the slot from that camera's encoder. set_subscribed(true) wakes the
        // plugin's per-camera render on its next ~1Hz control poll.
        let mut subscribed = Vec::new();
        /* Refusals can't be sent yet: the control channel doesn't exist until
        the browser opens it. They are held and sent after Hello, in the same
        shape as a refused Subscribe. */
        let mut refusals = Vec::new();
        let mut seen = std::collections::HashSet::new();
        for &flight_id in initial {
            // A repeated id is handled once: bound, or refused, a single time.
            if !seen.insert(flight_id) {
                continue;
            }
            let cam = match registry.get(flight_id).await {
                Some(c) => c,
                None => {
                    warn!(flight_id, "initial camera not found, skipping");
                    refusals.push(refusal(
                        flight_id,
                        ErrorCode::CameraNotFound,
                        format!("no live camera with flight_id={flight_id}"),
                    ));
                    continue;
                }
            };
            if cam.destroyed.load(Ordering::Acquire) {
                warn!(flight_id, "skipping destroyed initial camera");
                refusals.push(refusal(
                    flight_id,
                    ErrorCode::CameraDestroyed,
                    format!("no live camera with flight_id={flight_id}"),
                ));
                continue;
            }
            let Some(slot) = slots.iter_mut().find(|s| s.ready && s.bound.is_none()) else {
                warn!(flight_id, "no free slot for initial subscription");
                refusals.push(refusal(
                    flight_id,
                    ErrorCode::NoFreeSlot,
                    NO_FREE_SLOT_MESSAGE.into(),
                ));
                continue;
            };
            cam.add_track(slot.track.clone()).await;
            registry.set_subscribed(flight_id, true).await;
            slot.bound = Some(flight_id);
            subscribed.push(flight_id);
        }

        let slots = Arc::new(Mutex::new(slots));
        let link: SharedLink = Arc::new(StdMutex::new(LinkState {
            monitor: LinkMonitor::new(),
            ladder: Ladder::new(LadderConfig::default()),
        }));

        // Spawn the per-slot RTCP drains now that the slot pool is shared. Each
        // drain pumps its sender's RTCP (driving the NACK responder) and honors
        // PLI/FIR by forcing a keyframe on the camera currently bound to its
        // slot.
        for (slot_idx, sender) in senders.into_iter().enumerate() {
            let slots = slots.clone();
            let registry = registry.clone();
            let link = link.clone();
            tokio::spawn(async move {
                drain_rtcp_sink(sender, slot_idx, slots, registry, link).await;
            });
        }

        let registry_for_dc = registry.clone();
        let control_channel_for_handler = control_channel.clone();
        let slots_for_handler = slots.clone();
        let refusals = Arc::new(refusals);
        let refused = refusals.clone();
        let outbox_for_handler = outbox.clone();
        pc.on_data_channel(Box::new(move |dc: Arc<RTCDataChannel>| {
            let registry = registry_for_dc.clone();
            let control_channel = control_channel_for_handler.clone();
            let slots = slots_for_handler.clone();
            let refusals = refusals.clone();
            let outbox = outbox_for_handler.clone();
            Box::pin(async move {
                if dc.label() != CONTROL_CHANNEL_LABEL {
                    info!(label = %dc.label(), "ignoring unexpected data channel");
                    return;
                }
                info!(label = %dc.label(), "control channel opened");
                *control_channel.lock().await = Some(dc.clone());

                dc.on_message(Box::new(move |msg: DataChannelMessage| {
                    let registry = registry.clone();
                    let slots = slots.clone();
                    let refusals = refusals.clone();
                    let outbox = outbox.clone();
                    Box::pin(async move {
                        handle_client_message(registry, peer_id, slots, refusals, outbox, msg)
                            .await;
                    })
                }));
            })
        }));

        let connected = Arc::new(Notify::new());
        let liveness = LivenessHandle::new(Instant::now());
        let connected_for_handler = connected.clone();
        let liveness_for_handler = liveness.clone();
        let slots_for_state = slots.clone();
        let outbox_for_state = outbox.clone();
        let registry_for_state = registry.clone();
        pc.on_peer_connection_state_change(Box::new(move |state: RTCPeerConnectionState| {
            info!(?state, "peer connection state");
            if state == RTCPeerConnectionState::Connected {
                connected_for_handler.notify_waiters();
            }
            if !liveness_for_handler.observe(state, Instant::now()) {
                return Box::pin(async {});
            }
            /* A 1.11 client drops every feed's stream when its connection
            reports disconnected and never takes it back on reconnect, so its
            feeds stay static. It still holds each slot's track and re-sources a
            feed from a slot map naming the slot, so announce every binding
            again. This keys on the sidecar seeing Disconnected, which ICE
            raises after 5 s of silence, so a shorter blip that only the
            browser notices still leaves a 1.11 client static. */
            let slots = slots_for_state.clone();
            let outbox = outbox_for_state.clone();
            let registry = registry_for_state.clone();
            Box::pin(async move {
                info!("peer reconnected, announcing its slot bindings again");
                send_slot_maps(&slots, &outbox).await;
                /* Whatever was lost during the outage left each feed's
                reference chain broken, so every bound feed starts over from
                an IDR, on the tier that feeds it. */
                for (flight_id, track) in bound_tracks(&slots).await {
                    if let Some(cam) = registry.get(flight_id).await {
                        cam.request_keyframe_for(&track).await;
                    }
                }
            })
        }));

        tokio::spawn(sample_link(
            Arc::downgrade(&pc),
            registry.clone(),
            peer_id,
            liveness.clone(),
            slots.clone(),
            link.clone(),
        ));

        Ok(Self {
            pc,
            peer_id,
            session: session_token(),
            registry,
            renegotiation: Mutex::new(()),
            slots,
            subscribed,
            refused,
            outbox,
            connected,
            liveness,
            link,
        })
    }

    /// Browser-initiated SDP flow used by the HTTP signalling endpoint.
    /// Browser POSTs its offer, we set it as the remote description, then
    /// create + return our answer.
    pub async fn answer_to_offer(&self, offer_sdp: String) -> Result<String> {
        /* webrtc-rs answers an offer without the sent format using whatever
        H.264 it offered, packetization-mode=0 included, which is a black feed
        that negotiates cleanly. */
        let sent_under = offered_payload_type(&offer_sdp);
        let offers_video = codec_source(&media_sections(&offer_sdp)).is_some();
        if offers_video && !offers_sent_h264(&offer_sdp) {
            return Err(FormatRefused.into());
        }
        self.pc
            .set_remote_description(RTCSessionDescription::offer(offer_sdp.clone())?)
            .await?;
        if let Some(pt) = &sent_under {
            self.prefer_sent_format(&offer_sdp, pt).await?;
        }

        let answer = self.pc.create_answer(None).await?;
        check_answer(&offer_sdp, &answer.sdp)?;
        self.pc.set_local_description(answer).await?;

        let mut gather_complete = self.pc.gathering_complete_promise().await;
        let _ = gather_complete.recv().await;

        // Record each slot's negotiated transceiver mid so SlotMap addresses
        // slots by the same stable mid the browser sees. Slot-tracks were
        // add_track'd in order, so get_transceivers() is index-aligned with
        // the pool (Unified Plan preserves m-line order).
        let transceivers = self.pc.get_transceivers().await;
        {
            let mut slots = self.slots.lock().await;
            if slots.len() != transceivers.len() {
                warn!(
                    slots = slots.len(),
                    transceivers = transceivers.len(),
                    "slot/transceiver count mismatch — slot mids may misalign",
                );
            }
            for (slot, tr) in slots.iter_mut().zip(transceivers.iter()) {
                slot.mid = tr.mid().map(|m| m.to_string());
            }
            /* A slot with no mid never matched an m-line, so its track was
            never bound to a sender. write_sample() on such a track returns
            Ok and silently discards the frame — the exact shape of a black
            feed with a healthy-looking pipeline. Loud, because nothing
            downstream can tell you this happened. */
            let unbound: Vec<usize> = slots
                .iter()
                .enumerate()
                .filter(|(_, s)| s.mid.is_none())
                .map(|(i, _)| i)
                .collect();
            if !unbound.is_empty() {
                warn!(
                    unbound_slots = ?unbound,
                    total_slots = slots.len(),
                    transceivers = transceivers.len(),
                    "slots have NO negotiated mid — their tracks are unbound and \
                     every write_sample on them is silently dropped"
                );
            }
            for (i, (slot, tr)) in slots.iter().zip(transceivers.iter()).enumerate() {
                info!(
                    slot = i,
                    mid = ?slot.mid,
                    direction = ?tr.direction(),
                    current_direction = ?tr.current_direction(),
                    "slot negotiation result"
                );
            }
        }

        let local = self
            .pc
            .local_description()
            .await
            .ok_or_else(|| anyhow!("local description missing after set_local_description"))?;

        /* Which H.264 payload type and fmtp got negotiated, for diagnosing a
        feed that does not play. */
        for line in local.sdp.lines() {
            if line.starts_with("a=rtpmap:") && line.to_ascii_uppercase().contains("H264") {
                info!(sdp_line = line, "negotiated H264 rtpmap");
            }
            if line.starts_with("a=fmtp:") {
                info!(sdp_line = line, "negotiated fmtp");
            }
        }
        Ok(local.sdp)
    }

    /// Narrows every video transceiver to the sent format under payload type
    /// `pt`, as `offer_sdp` describes it. webrtc-rs's exact match ignores the
    /// level, so an offer of constrained baseline at two levels would
    /// otherwise be answered with both; with one preference the answer names
    /// one.
    async fn prefer_sent_format(&self, offer_sdp: &str, pt: &str) -> Result<()> {
        let preference = offered_sent_format(offer_sdp, pt).ok_or(FormatRefused)?;
        for t in self.pc.get_transceivers().await {
            if t.kind() == RTPCodecType::Video {
                t.set_codec_preferences(vec![preference.clone()]).await?;
            }
        }
        Ok(())
    }

    /// Slots in the pool, confirmed or not.
    pub async fn slot_count(&self) -> usize {
        self.slots.lock().await.len()
    }

    /// Slots the browser has confirmed it can play. A re-offer must offer
    /// more than these; slots past them are the browser's to re-offer or drop.
    pub async fn confirmed_slot_count(&self) -> usize {
        self.slots.lock().await.iter().filter(|s| s.ready).count()
    }

    /// Answer a re-offer that adds video transceivers, growing the slot pool
    /// to `slot_count`. Every confirmed slot keeps its mid and binding, so
    /// running feeds carry on untouched. The new slots take no camera until
    /// the browser confirms it applied the answer (`confirm_slots`): a re-offer
    /// whose answer never lands leaves them idle, never carrying a camera the
    /// browser cannot play.
    ///
    /// A re-offer may follow one whose answer the browser never applied (lost
    /// on the way, or refused), so the pool can already hold unconfirmed
    /// slots. One whose mid the re-offer still carries is the browser's too
    /// and is answered again as it is. Any other is unknown to the browser:
    /// its transceiver is stopped and a fresh one takes its place, so the pool
    /// always counts exactly the video sections offered.
    pub async fn grow(&self, slot_count: usize, offer_sdp: String) -> Result<String> {
        let _turn = self.renegotiation.lock().await;
        if slot_count > MAX_SLOTS_PER_PEER {
            return Err(GrowRefused(format!(
                "a connection holds at most {MAX_SLOTS_PER_PEER} video slots, asked for {slot_count}"
            ))
            .into());
        }
        let offered = offered_video_mids(&offer_sdp);
        let (current, stale, kept) = {
            let slots = self.slots.lock().await;
            let confirmed = slots.iter().filter(|s| s.ready).count();
            if slot_count <= confirmed {
                return Err(GrowRefused(format!(
                    "a re-offer must add slots: asked for {slot_count}, the pool already has {confirmed}"
                ))
                .into());
            }
            let stale: Vec<usize> = (0..slots.len())
                .filter(|&i| {
                    !slots[i].ready && !slots[i].mid.as_ref().is_some_and(|m| offered.contains(m))
                })
                .collect();
            let kept_beyond = (slot_count..slots.len()).any(|i| !stale.contains(&i));
            if kept_beyond {
                return Err(GrowRefused(format!(
                    "a re-offer for {slot_count} slots drops slots the browser still holds"
                ))
                .into());
            }
            /* webrtc-rs matches an offered mid to a transceiver without
            looking at whether it is stopped, so a mid that a stopped
            transceiver holds would land on it and leave that slot dead. */
            let live_mids: Vec<&String> = (0..slots.len())
                .filter(|i| !stale.contains(i))
                .filter_map(|i| slots[i].mid.as_ref())
                .collect();
            for t in self.pc.get_transceivers().await {
                let Some(mid) = t.mid().map(|m| m.to_string()) else {
                    continue;
                };
                if offered.contains(&mid) && !live_mids.contains(&&mid) {
                    return Err(GrowRefused(format!(
                        "the re-offer reuses mid {mid}, which a retired slot held"
                    ))
                    .into());
                }
            }
            let kept = (0..slots.len())
                .filter(|&i| !slots[i].ready && !stale.contains(&i))
                .count();
            (slots.len(), stale, kept)
        };
        /* Running senders stay bound to the payload type they started under,
        so the re-offer must carry that payload type. Refused here, before the
        exchange: a failure after set_remote_description would leave the
        connection unable to take another re-offer. Checked once the
        re-offer's slot counts are known good and before any slot changes. */
        let sent_under = match self.pc.current_local_description().await {
            Some(local) => answered_payload_type(&local.sdp),
            None => None,
        };
        let carried = match &sent_under {
            Some(pt) => carries_sent_format(&offer_sdp, pt),
            None => offers_sent_h264(&offer_sdp),
        };
        if !carried {
            return Err(FormatRefused.into());
        }
        let sent_under = sent_under.or_else(|| offered_payload_type(&offer_sdp));
        for &i in &stale {
            self.retire_slot(i).await;
        }

        /* Each new slot takes a transceiver of its own: `add_track` would hand
        back a stopped one whose first track had the same id. */
        let fresh: Vec<usize> = stale
            .iter()
            .copied()
            .filter(|&i| i < slot_count)
            .chain(current..slot_count)
            .collect();
        let mut added = Vec::with_capacity(fresh.len());
        for &i in &fresh {
            let track = slot_track(i, &self.outbox);
            match self
                .pc
                .add_transceiver_from_track(
                    track.rtp_track() as Arc<dyn TrackLocal + Send + Sync>,
                    None,
                )
                .await
            {
                Ok(transceiver) => added.push((i, track, transceiver)),
                Err(e) => {
                    stop_transceivers(&added).await;
                    return Err(e.into());
                }
            }
        }

        let answer = async {
            self.pc
                .set_remote_description(RTCSessionDescription::offer(offer_sdp.clone())?)
                .await?;
            if let Some(pt) = &sent_under {
                self.prefer_sent_format(&offer_sdp, pt).await?;
            }
            let answer = self.pc.create_answer(None).await?;
            check_answer(&offer_sdp, &answer.sdp)?;
            self.pc.set_local_description(answer).await?;
            self.pc
                .local_description()
                .await
                .ok_or_else(|| anyhow!("local description missing after set_local_description"))
        }
        .await;
        let answer = match answer {
            Ok(a) => a,
            Err(e) => {
                stop_transceivers(&added).await;
                return Err(e);
            }
        };

        /* Every added slot joins the pool, mid or not, so the pool's length
        stays the count the browser offered. One without a mid can never be
        confirmed, so it never takes a camera; its RTCP is drained like any
        other sender's. */
        let mut slots = self.slots.lock().await;
        slots.truncate(slot_count);
        for (slot_idx, track, transceiver) in added {
            let mid = transceiver.mid().map(|m| m.to_string());
            if mid.is_none() {
                warn!(
                    slot = slot_idx,
                    "grown slot has NO negotiated mid and stays idle"
                );
            }
            let slot = Slot {
                track,
                mid,
                bound: None,
                ready: false,
                transceiver: Some(transceiver.clone()),
            };
            if slot_idx < slots.len() {
                slots[slot_idx] = slot;
            } else {
                slots.push(slot);
            }
            let sender = transceiver.sender().await;
            let pool = self.slots.clone();
            let registry = self.registry.clone();
            let link = self.link.clone();
            tokio::spawn(async move {
                drain_rtcp_sink(sender, slot_idx, pool, registry, link).await;
            });
        }
        info!(
            peer_id = self.peer_id,
            slots = slots.len(),
            kept,
            replaced = stale.len(),
            "slot pool grown, awaiting confirmation"
        );
        Ok(answer.sdp)
    }

    /// Stop the transceiver of an unconfirmed slot the browser does not hold.
    /// Stopping is irreversible, so nothing can pick its sender up again.
    async fn retire_slot(&self, slot_idx: usize) {
        let transceiver = self.slots.lock().await[slot_idx].transceiver.clone();
        if let Some(t) = transceiver {
            if let Err(e) = t.stop().await {
                warn!(slot = slot_idx, error = %e, "could not stop a stale slot's transceiver");
            }
        }
    }

    /// False once the connection is terminal (`Failed`/`Closed`), or once it
    /// has overstayed one of the two deadlines: `DISCONNECTED_GRACE` for a
    /// connection that dropped out, `NEGOTIATION_TIMEOUT` for one that never
    /// connected at all. A transient `Disconnected` stays alive, so a network
    /// blip no longer tears the viewer's feed down.
    pub fn is_alive(&self) -> bool {
        !self.outbox.is_dead() && self.liveness.is_alive(Instant::now())
    }

    /// Mark the peer terminal without waiting for a deadline. Used by the
    /// signalling handler when the SDP exchange fails: the connection can
    /// never come up, so the reaper should collect it on its next tick rather
    /// than hold its cameras subscribed for the whole negotiation window.
    pub fn mark_dead(&self) {
        self.liveness.kill();
    }

    /// Flight IDs currently bound to this peer's slots. Unlike the
    /// construction-time `subscribed` list, this reflects dynamic
    /// `Subscribe`/`Unsubscribe` (which mutate the slots, not that list),
    /// so the dead-peer cleanup can zero rates on exactly the cameras this
    /// peer was driving when it dropped.
    pub async fn bound_flight_ids(&self) -> Vec<u32> {
        self.slots
            .lock()
            .await
            .iter()
            .filter_map(|s| s.bound)
            .collect()
    }

    /// Release every camera this peer's slots are bound to (immediate
    /// subscriber-count decrement + `set_subscribed(false)` at zero). Called by
    /// the reaper when the peer is reaped, and shares the exact logic of the
    /// graceful `Disconnect` message — so a dropped viewer's cameras stop
    /// rendering on the next reaper tick instead of waiting for the consume
    /// loop's lazy dead-Weak prune (which a staggered camera can starve).
    pub async fn release_all(&self, registry: &Arc<CameraRegistry>) {
        release_all_bound(registry, &self.slots).await;
    }

    /// Tracks of this peer's slots currently bound to `flight_id` (0 or 1
    /// in practice; Subscribe never double-binds). Used by the consume
    /// loop to rebind a surviving subscription when a camera's ring
    /// disappears and reappears across a KSP scene change: the slot stays
    /// bound through the gap, but the re-attached ring gets a brand-new
    /// `CameraState` that doesn't know about this track yet.
    pub async fn tracks_bound_to(&self, flight_id: u32) -> Vec<Arc<VideoTrack>> {
        self.slots
            .lock()
            .await
            .iter()
            .filter(|s| s.bound == Some(flight_id))
            .map(|s| s.track.clone())
            .collect()
    }

    /**
     * Puts each bound slot's track back on its camera if the camera no longer
     * feeds it, and returns how many it put back. A subscribe racing a rescan
     * can land on the entry the rescan just replaced, after the churn resync
     * has run, leaving a viewer the live entry has never heard of. Checked under
     * the slots' lock, since an unsubscribe unbinds its slot under it before
     * removing the track, so a slot found bound here cannot be left fed once freed.
     */
    pub async fn rebind_lost_tracks(&self, registry: &Arc<CameraRegistry>) -> usize {
        let mut rebound = Vec::new();
        {
            let slots = self.slots.lock().await;
            for slot in slots.iter() {
                let Some(flight_id) = slot.bound else {
                    continue;
                };
                let Some(cam) = registry.get(flight_id).await else {
                    continue;
                };
                if cam.destroyed.load(Ordering::Acquire) || cam.tier_of(&slot.track).await.is_some()
                {
                    continue;
                }
                cam.full.request_keyframe();
                cam.add_track(slot.track.clone()).await;
                rebound.push(flight_id);
            }
        }
        for &flight_id in &rebound {
            warn!(
                flight_id,
                peer_id = self.peer_id,
                "bound slot was not being fed by its camera; put its track back"
            );
            registry.set_subscribed(flight_id, true).await;
        }
        rebound.len()
    }

    /// Push a fresh camera snapshot over the control channel (no-op until
    /// the channel opens). Server-initiated counterpart of the Hello-time
    /// snapshot, used when registry membership churns mid-connection so a
    /// browser's camera list drains and repopulates across scene changes.
    pub async fn push_camera_snapshot(&self, registry: &Arc<CameraRegistry>) {
        send_camera_snapshot(registry, &self.outbox).await;
    }

    /**
     * Server-initiated push to the browser. Never waits on the data channel.
     * Dropped until the browser opens the control channel (its Hello is
     * answered with a full snapshot); after that, a message this peer cannot
     * take marks it dead for the reaper instead of being lost.
     */
    pub fn push_message(&self, msg: &ServerMessage) {
        self.outbox.send(msg);
    }

    /// The last closed window of this viewer's connection quality, `None`
    /// until two samples have been taken.
    pub fn link_window(&self) -> Option<LinkWindow> {
        lock_link(&self.link).monitor.latest()
    }

    /// The tier each of this viewer's feeds is actually sent on.
    pub async fn delivery_tiers(&self) -> Vec<SlotTier> {
        let mut out = Vec::new();
        for (slot, flight_id, track) in self.bound_slot_tracks().await {
            let Some(cam) = self.registry.get(flight_id).await else {
                continue;
            };
            if let Some(tier) = cam.tier_of(&track).await {
                out.push(SlotTier {
                    slot,
                    flight_id,
                    tier,
                });
            }
        }
        out
    }

    /// Move this viewer's feed of `flight_id` onto `tier`, by hand. Returns
    /// false when the viewer is not watching that camera.
    pub async fn set_tier(&self, flight_id: u32, tier: Tier) -> bool {
        let Some(cam) = self.registry.get(flight_id).await else {
            return false;
        };
        let mut moved = false;
        for (_, f, track) in self.bound_slot_tracks().await {
            if f == flight_id {
                moved |= cam.set_track_tier(&track, tier).await;
            }
        }
        if moved {
            info!(
                peer_id = self.peer_id,
                flight_id,
                ?tier,
                "feed tier set by hand"
            );
        }
        moved
    }

    async fn bound_slot_tracks(&self) -> Vec<(usize, u32, Arc<VideoTrack>)> {
        bound_tracks_by_slot(&self.slots).await
    }

    /// The tiers the detector has chosen for this viewer's feeds.
    pub fn ladder_state(&self) -> LadderState {
        lock_link(&self.link).ladder.state()
    }

    /// Block until the peer reaches the Connected state. Used by tests.
    #[allow(dead_code)]
    pub async fn wait_connected(&self) {
        self.connected.notified().await;
    }

    /// Tear down the peer cleanly. Idempotent. Releases the RTCRtpSenders'
    /// strong refs to the slot tracks (see `close_releases_track_arc`), so each
    /// camera's Weak goes dead and the consume loop prunes it — dropping the
    /// subscriber count to zero and flushing `set_subscribed(false)`. Must be
    /// called when reaping a dropped peer, or cameras keep rendering for a
    /// viewer that's already gone.
    pub async fn close(&self) -> Result<()> {
        self.outbox.shutdown();
        self.pc.close().await?;
        Ok(())
    }
}

fn send_error(dc: &Outbox, message: String) {
    send_server_message(
        dc,
        &ServerMessage::Error(ErrorPayload {
            message,
            source: ErrorSource::Sidecar,
            ..Default::default()
        }),
    );
}

/// A network request the sidecar refused before it reached the game.
fn send_network_refusal(
    dc: &Outbox,
    token: String,
    network_id: Option<String>,
    (code, text): crate::commands::NetworkRefusal,
) {
    send_server_message(
        dc,
        &ServerMessage::NetworkResult(crate::protocol::NetworkResultPayload {
            token,
            ok: false,
            network_id,
            code: Some(code.to_owned()),
            text: Some(text),
        }),
    );
}

/// Single-message dispatch. Pulled out of the closure so the error
/// paths and per-variant logic don't drown the construction code.
async fn handle_client_message(
    registry: Arc<CameraRegistry>,
    peer_id: u32,
    slots: Arc<Mutex<Vec<Slot>>>,
    refusals: Arc<Vec<ErrorPayload>>,
    dc: Outbox,
    msg: DataChannelMessage,
) {
    let text = match std::str::from_utf8(&msg.data) {
        Ok(s) => s,
        Err(e) => {
            warn!(error = %e, "control channel: non-utf8 payload, dropping");
            return;
        }
    };
    let parsed: ClientMessage = match serde_json::from_str(text) {
        Ok(m) => m,
        Err(e) => {
            warn!(error = %e, payload = %text, "control channel: parse failed");
            send_server_message(
                &dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("parse failed: {e}"),
                    source: ErrorSource::Sidecar,
                    ..Default::default()
                }),
            );
            return;
        }
    };

    match parsed {
        ClientMessage::Hello => {
            info!("control channel: hello received");
            send_server_message(
                &dc,
                &ServerMessage::Hello(hello_payload(registry.last_networks().await.is_some())),
            );
            send_camera_snapshot(&registry, &dc).await;
            /* Prime the client with current global settings (throttle + the
             * mission-time capture clock, if the plugin has reported one). */
            send_server_message(
                &dc,
                &ServerMessage::SettingsState(registry.last_settings().await),
            );
            if let Some(sites) = registry.last_sites().await {
                send_server_message(&dc, &ServerMessage::SiteCatalog(sites));
            }
            if let Some(networks) = registry.last_networks().await {
                send_server_message(&dc, &ServerMessage::NetworkDefinitions(networks));
            }
            /* Prime scene state so a peer connecting out of flight shows the
             * standby immediately, not a stale live layout. */
            if let Some(in_flight) = registry.read_in_flight().await {
                send_server_message(
                    &dc,
                    &ServerMessage::SceneStateChanged(SceneStateChangedPayload { in_flight }),
                );
            }
            // Announce the initial slot bindings so the client maps its
            // initial cameras to slots by mid — uniform with dynamic
            // Subscribe, no reliance on the answer's camera order.
            send_slot_maps(&slots, &dc).await;
            // Initial cameras that could not be bound (see `KerbcastPeer::new`).
            for payload in refusals.iter() {
                send_server_message(&dc, &ServerMessage::Error(payload.clone()));
            }
        }
        ClientMessage::SetLayers(SetLayersPayload { flight_id, layers }) => {
            apply_layer_change(&registry, &dc, flight_id, layers).await;
        }
        ClientMessage::SetRenderSize(SetRenderSizePayload {
            flight_id,
            width,
            height,
        }) => {
            apply_render_size_change(&registry, &dc, flight_id, width, height).await;
        }
        // A consumer reporting its own display px for one camera. Recorded
        // keyed by peer_id into the per-camera MAX-across-consumers tracker;
        // forgotten on unsubscribe / disconnect / peer reap (below + in the
        // consume-loop reaper) so the max relaxes when it leaves.
        ClientMessage::ReportDisplaySize(ReportDisplaySizePayload {
            flight_id,
            width,
            height,
        }) => {
            registry
                .record_display_size(flight_id, peer_id, width, height)
                .await;
        }
        ClientMessage::SetFov(SetFovPayload { flight_id, fov }) => {
            apply_fov_change(&registry, &dc, flight_id, fov).await;
        }
        ClientMessage::SetPan(SetPanPayload {
            flight_id,
            yaw,
            pitch,
        }) => {
            apply_pan_change(&registry, &dc, flight_id, yaw, pitch).await;
        }
        ClientMessage::SetPanRate(SetPanRatePayload {
            flight_id,
            yaw_rate,
            pitch_rate,
        }) => {
            apply_pan_rate_change(&registry, &dc, flight_id, yaw_rate, pitch_rate).await;
        }
        ClientMessage::SetZoomRate(SetZoomRatePayload { flight_id, rate }) => {
            apply_zoom_rate_change(&registry, &dc, flight_id, rate).await;
        }
        ClientMessage::SetFrameStamps(SetFrameStampsPayload {
            enabled,
            anchors,
            command,
        }) => {
            dc.set_stamps_wanted(enabled, anchors.unwrap_or(false), command.unwrap_or(false));
        }
        // A client's own anchors on one camera. Keyed by peer_id; forgotten on
        // disconnect / peer reap (below + in the consume-loop reaper).
        ClientMessage::SetAnchors(payload) => {
            let flight_id = payload.flight_id;
            if let Err(message) = registry.set_anchors(peer_id, payload).await {
                send_server_message(
                    &dc,
                    &ServerMessage::Error(ErrorPayload {
                        message,
                        source: ErrorSource::Sidecar,
                        flight_id: Some(flight_id),
                        ..Default::default()
                    }),
                );
            }
        }
        ClientMessage::SetDegrade(SetDegradePayload { flight_id, level }) => {
            apply_degrade_change(&registry, &dc, peer_id, flight_id, level).await;
        }
        ClientMessage::SetQuality(SetQualityPayload { flight_id, preset }) => {
            apply_quality_change(&registry, &dc, flight_id, preset).await;
        }
        ClientMessage::SetTrackTarget(SetTrackTargetPayload { flight_id, mode }) => {
            /* Server-authoritative auto-track: store + flush the mode; the
            broadcast (mark_dirty) syncs every browser. A browser track
            overrides a kOS aim on the same camera. */
            if let Err(message) = registry.apply_track_target(flight_id, mode).await {
                send_server_message(
                    &dc,
                    &ServerMessage::Error(ErrorPayload {
                        message,
                        source: ErrorSource::Sidecar,
                        ..Default::default()
                    }),
                );
            }
        }
        ClientMessage::CreateCommandCamera(payload) => {
            /* The plugin builds it on its next read of global.commands.json;
            the client finds it in the camera list by its token. */
            let token = payload.token.clone();
            match registry.request_create_command_camera(payload).await {
                Ok(id) => info!(request = id, token, "command camera requested"),
                Err(message) => send_error(&dc, message),
            }
        }
        ClientMessage::DestroyCommandCamera(DestroyCommandCameraPayload { flight_id }) => {
            match registry.request_destroy_command_camera(flight_id).await {
                Ok(id) => info!(
                    request = id,
                    flight_id, "command camera destruction requested"
                ),
                Err(message) => send_error(&dc, message),
            }
        }
        ClientMessage::SaveNetwork(payload) => {
            let token = payload.token.clone();
            let network_id = payload.network_id.clone();
            match registry.request_save_network(payload).await {
                Ok(id) => info!(request = id, token, "tracking network save requested"),
                Err(refusal) => send_network_refusal(&dc, token, network_id, refusal),
            }
        }
        ClientMessage::DeleteNetwork(payload) => {
            let token = payload.token.clone();
            let network_id = Some(payload.network_id.clone());
            match registry.request_delete_network(payload).await {
                Ok(id) => info!(request = id, token, "tracking network delete requested"),
                Err(refusal) => send_network_refusal(&dc, token, network_id, refusal),
            }
        }
        ClientMessage::SetCommandAim(payload) => {
            let flight_id = payload.flight_id;
            match registry.request_command_aim(payload).await {
                Ok(id) => info!(request = id, flight_id, "command aim requested"),
                Err((message, code)) => send_server_message(
                    &dc,
                    &ServerMessage::Error(ErrorPayload {
                        message,
                        source: ErrorSource::Sidecar,
                        flight_id: Some(flight_id),
                        code,
                    }),
                ),
            }
        }
        ClientMessage::RequestKeyframe(FlightIdPayload { flight_id }) => {
            if let Some(cam) = registry.get(flight_id).await {
                let tracks: Vec<Arc<VideoTrack>> = bound_tracks(&slots)
                    .await
                    .into_iter()
                    .filter(|(f, _)| *f == flight_id)
                    .map(|(_, t)| t)
                    .collect();
                if tracks.is_empty() {
                    cam.request_keyframe();
                }
                for track in &tracks {
                    cam.request_keyframe_for(track).await;
                }
                info!(flight_id, "keyframe requested");
            }
        }
        ClientMessage::Subscribe(FlightIdPayload { flight_id }) => {
            handle_subscribe(&registry, &slots, &dc, flight_id).await;
        }
        ClientMessage::Unsubscribe(FlightIdPayload { flight_id }) => {
            handle_unsubscribe(&registry, peer_id, &slots, &dc, flight_id).await;
        }
        ClientMessage::Pong => {
            // No-op — the peer is alive by virtue of having sent this.
        }
        ClientMessage::SetThrottleMainScreen(SetThrottleMainScreenPayload { enabled }) => {
            /* Write global.control.json; the plugin polls it next to status. */
            if let Err(e) = registry.write_global_control(enabled).await {
                warn!(error = %e, "write_global_control failed");
            } else {
                info!(enabled, "global control: throttle_main_screen written");
            }
        }
        // A recording (or other consumer) forcing this camera to the ring
        // ceiling for as long as it holds the force. OR-aggregated per peer;
        // forgotten on unsubscribe / disconnect / peer reap (below + in the
        // consume-loop reaper) so the ceiling relaxes once no peer forces it.
        ClientMessage::SetForceFullResolution(SetForceFullResolutionPayload {
            flight_id,
            force,
        }) => {
            registry
                .set_forced_full_res(flight_id, peer_id, force)
                .await;
            if force {
                let bound = bound_tracks_by_slot(&slots).await;
                return_recorded_feeds_to_full(&registry, flight_id, &bound).await;
            }
        }
        // A recording holding this camera at its current size. Released with
        // the force below on unsubscribe / disconnect / peer reap.
        ClientMessage::SetSizeHold(SetSizeHoldPayload { flight_id, hold }) => {
            registry.set_size_hold(flight_id, peer_id, hold).await;
        }
        // A reconnecting client taking over the force and size holds its last
        // connection left behind (see `CameraRegistry::depart_peer`).
        ClientMessage::Resume(payload) => {
            registry.resume_client(peer_id, &payload).await;
        }
        ClientMessage::ConfirmSlots(ConfirmSlotsPayload { slots: count }) => {
            confirm_slots(&slots, count as usize).await;
        }
        // Background capture ("background capture"): this consumer asking that cameras it
        // isn't displaying keep ticking over instead of stopping. Per-peer and
        // MAX-aggregated; forgotten on disconnect / peer reap so a departed
        // consumer can't pin background capture on with nobody watching.
        ClientMessage::SetBackgroundCaptureFps(SetBackgroundCaptureFpsPayload { fps }) => {
            if registry.set_background_request(peer_id, fps).await {
                // The aggregate moved, so every camera's requested rate has to
                // be recomputed and pushed down to the plugin.
                registry.reflush_all_capture_rates().await;
            }
        }
        ClientMessage::Disconnect => {
            // Graceful teardown: release every camera this peer is feeding now,
            // so they sleep immediately instead of waiting for the ICE drop to
            // be detected. The client closes its connection after this; the
            // reaper still handles the close (and any ungraceful exit).
            info!(
                peer_id,
                "client requested graceful disconnect — releasing cameras"
            );
            release_all_bound(&registry, &slots).await;
            // Peer-scoped display-size clear: sweeps EVERY camera this peer
            // reported a size for, including any it never bound a slot to.
            registry.forget_display_size_all(peer_id).await;
            // Same peer-scoped sweep for any force this peer held, so a feed
            // never stays pinned at the ceiling after its forcer leaves. A
            // client that says goodbye is not coming back, so nothing is
            // kept for a resume.
            registry.forget_client(peer_id).await;
            registry.forget_forced_all(peer_id).await;
            // And background capture request, or a departed consumer keeps background
            // capture running for the rest of the session with nobody watching
            // — the v1.6.3 pin-high class again.
            if registry.forget_background_request(peer_id).await {
                registry.reflush_all_capture_rates().await;
            }
            // Anchors go with their client, on every camera it set them on.
            registry.forget_anchors_all(peer_id).await;
        }
    }
}

/// A re-offer the sidecar will not answer however often it is sent, as
/// opposed to one that failed along the way.
#[derive(Debug, thiserror::Error)]
#[error("{0}")]
pub struct GrowRefused(String);

/// Take back the transceivers a re-offer added before it failed, so the
/// failure leaves no live send-tracks behind and none can be reused.
async fn stop_transceivers(added: &[(usize, Arc<VideoTrack>, Arc<RTCRtpTransceiver>)]) {
    for (slot_idx, _, t) in added {
        if let Err(e) = t.stop().await {
            warn!(slot = slot_idx, error = %e, "could not stop a slot transceiver after a failed re-offer");
        }
    }
}

/// The mids of an SDP's video sections.
fn offered_video_mids(sdp: &str) -> Vec<String> {
    let mut mids = Vec::new();
    let mut in_video = false;
    for line in sdp.lines().map(str::trim) {
        if line.starts_with("m=") {
            in_video = line.starts_with("m=video");
        } else if let Some(mid) = line.strip_prefix("a=mid:").filter(|_| in_video) {
            mids.push(mid.to_owned());
        }
    }
    mids
}

/// The browser applied the answer for a pool of `count` slots, so every slot
/// below that with a mid can now take a camera.
async fn confirm_slots(slots: &Arc<Mutex<Vec<Slot>>>, count: usize) {
    let mut guard = slots.lock().await;
    for slot in guard.iter_mut().take(count) {
        if slot.mid.is_some() {
            slot.ready = true;
        }
    }
}

/// Bind a camera to a free slot (or re-announce if already bound). On a fresh
/// bind the camera's subscriber count goes 0 -> 1, waking the plugin render;
/// the consume loop then feeds the slot's track from the camera's encoder.
async fn handle_subscribe(
    registry: &Arc<CameraRegistry>,
    slots: &Arc<Mutex<Vec<Slot>>>,
    dc: &Outbox,
    flight_id: u32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) if !c.destroyed.load(Ordering::Acquire) => c,
        found => {
            let code = if found.is_some() {
                ErrorCode::CameraDestroyed
            } else {
                ErrorCode::CameraNotFound
            };
            send_server_message(
                dc,
                &ServerMessage::Error(refusal(
                    flight_id,
                    code,
                    format!("no live camera with flight_id={flight_id}"),
                )),
            );
            return;
        }
    };

    // Choose a slot under the lock, then release it before the awaiting camera
    // ops. `fresh` distinguishes a new bind (add_track once) from a re-subscribe
    // of an already-bound camera (re-announce only — never double add_track).
    let (track, mid, fresh) = {
        let mut guard = slots.lock().await;
        if let Some(slot) = guard.iter().find(|s| s.bound == Some(flight_id)) {
            (slot.track.clone(), slot.mid.clone(), false)
        } else if let Some(slot) = guard.iter_mut().find(|s| s.ready && s.bound.is_none()) {
            slot.bound = Some(flight_id);
            (slot.track.clone(), slot.mid.clone(), true)
        } else {
            drop(guard);
            send_server_message(
                dc,
                &ServerMessage::Error(refusal(
                    flight_id,
                    ErrorCode::NoFreeSlot,
                    NO_FREE_SLOT_MESSAGE.into(),
                )),
            );
            return;
        }
    };

    if fresh {
        // Keyframe BEFORE add_track so the next encode emits an IDR (with
        // SPS/PPS) to every track of this camera, including the freshly-added
        // slot — otherwise the slot's first frame is a mid-GOP P-frame that
        // decodes as garbage until the next periodic IDR. A new binding always
        // starts on the Full tier.
        cam.full.request_keyframe();
        cam.add_track(track).await;
        registry.set_subscribed(flight_id, true).await;
        info!(flight_id, "subscribed camera to slot");
    }

    match mid {
        Some(mid) => {
            send_server_message(
                dc,
                &ServerMessage::SlotMap(SlotMapPayload {
                    mid,
                    flight_id: Some(flight_id),
                }),
            );
        }
        None => warn!(
            flight_id,
            "slot has no negotiated mid yet — SlotMap skipped"
        ),
    }
}

/// Release a camera's slot. The slot-track stays alive (reused for a later
/// subscribe); the camera sleeps once its last viewer leaves.
async fn handle_unsubscribe(
    registry: &Arc<CameraRegistry>,
    peer_id: u32,
    slots: &Arc<Mutex<Vec<Slot>>>,
    dc: &Outbox,
    flight_id: u32,
) {
    // Clear-on-departure FIRST, before the slot early-out: an explicit
    // Unsubscribe(X) must forget this peer's reported display size for X even
    // when X was never bound to a slot (a report is not binding-gated), so the
    // MAX relaxes and can't pin high. Also covers the rebind-away path (a slot
    // only rebinds after an unsubscribe).
    registry.forget_display_size(flight_id, peer_id).await;
    // Same clear-on-departure for a force-full-resolution pin: an explicit
    // Unsubscribe(X) must release this peer's force on X too, or a feed stays
    // pinned to the ring ceiling forever once its forcer unsubscribes without
    // a graceful Disconnect (e.g. switching feeds mid-recording). Mirrors the
    // display-size clear above; the Disconnect/reaper paths already call
    // forget_forced_all for the whole-peer-departure case.
    registry.forget_forced(flight_id, peer_id).await;

    let (track, mid) = {
        let mut guard = slots.lock().await;
        match guard.iter_mut().find(|s| s.bound == Some(flight_id)) {
            Some(slot) => {
                slot.bound = None;
                (slot.track.clone(), slot.mid.clone())
            }
            None => return, // not bound to any slot — nothing more to do
        }
    };

    if let Some(cam) = registry.get(flight_id).await {
        let remaining = cam.remove_track(&track).await;
        if remaining == 0 {
            // Last viewer — sleep the camera promptly (the consume loop's
            // refresh_idle_subscriptions would also catch this next tick).
            registry.set_subscribed(flight_id, false).await;
        }
        info!(flight_id, remaining, "unsubscribed camera from slot");
    }

    if let Some(mid) = mid {
        send_server_message(
            dc,
            &ServerMessage::SlotMap(SlotMapPayload {
                mid,
                flight_id: None,
            }),
        );
    }
}

/// Release EVERY camera this peer's slots are bound to — the shared teardown
/// used by both the graceful `Disconnect` message and the drop-detection
/// reaper. Mirrors `handle_unsubscribe`'s release (unbind the slot, drop the
/// camera's track ref by pointer, flush `set_subscribed(false)` when the last
/// viewer leaves) but over all slots at once, and without the per-slot SlotMap
/// reply (on a graceful Disconnect the client is leaving; on a reap the channel
/// is already gone). Decrements immediately — no dependence on the consume
/// loop's lazy dead-Weak prune (which a staggered camera's reduced frame
/// cadence can starve). Idempotent: a second call finds nothing bound.
async fn release_all_bound(registry: &Arc<CameraRegistry>, slots: &Arc<Mutex<Vec<Slot>>>) {
    // NOTE: display-size clear-on-departure is NOT done here — it is
    // peer-scoped (forget_display_size_all), not binding-scoped, because a
    // report can exist for a camera this peer never bound. Callers on the
    // peer-death paths (graceful Disconnect, the reaper) invoke
    // forget_display_size_all separately.
    let bound: Vec<(u32, Arc<VideoTrack>)> = {
        let mut guard = slots.lock().await;
        let mut v = Vec::new();
        for slot in guard.iter_mut() {
            if let Some(flight_id) = slot.bound.take() {
                v.push((flight_id, slot.track.clone()));
            }
        }
        v
    };
    for (flight_id, track) in bound {
        if let Some(cam) = registry.get(flight_id).await {
            let remaining = cam.remove_track(&track).await;
            if remaining == 0 {
                registry.set_subscribed(flight_id, false).await;
            }
            info!(flight_id, remaining, "released camera on peer teardown");
        }
    }
}

/// Resync long-lived peers after registry membership churn (the consume loop
/// calls this on every rescan that attached or removed rings). Two halves:
///
///   1. Rebind: a peer that stayed connected across a KSP scene change has
///      slots still bound to flight ids whose rings just re-attached, but the
///      re-attach built a brand-new `CameraState` with an empty track list and
///      `subscribers == 0`. Re-add each bound slot's track and re-flush
///      `set_subscribed(true)` so the plugin resumes rendering and the
///      browser's existing video element picks the stream back up (the fresh
///      encoder session opens with an IDR, so the decoder resyncs cleanly).
///
///   2. Snapshot push: send every peer a fresh `camera-snapshot` so browser
///      camera lists drain and repopulate with the scene; clients already
///      handle this message (it's the Hello-time priming snapshot).
pub async fn resync_after_camera_churn(
    registry: &Arc<CameraRegistry>,
    peers: &[Arc<KerbcastPeer>],
    attached: &[u32],
) {
    if peers.is_empty() {
        return;
    }
    for &flight_id in attached {
        let Some(cam) = registry.get(flight_id).await else {
            continue;
        };
        let mut rebound = 0usize;
        for peer in peers {
            for track in peer.tracks_bound_to(flight_id).await {
                cam.add_track(track).await;
                rebound += 1;
            }
        }
        if rebound > 0 {
            registry.set_subscribed(flight_id, true).await;
            /* A camera reattached in place kept its viewers' sizes, which its
            new control block has yet to carry. */
            if let Err(e) = registry.flush_effective_render_size(flight_id).await {
                warn!(flight_id, error = %e, "render size flush after rebind failed");
            }
            info!(
                flight_id,
                tracks = rebound,
                "re-attached camera rebound to surviving peer subscriptions",
            );
        }
    }
    for peer in peers {
        peer.push_camera_snapshot(registry).await;
    }
}

/// Announce the currently-bound slots, so the client maps its cameras to slots
/// by mid (the same mechanism dynamic Subscribe uses): on a freshly opened
/// control channel, and again when the connection recovers. Idle slots and
/// slots without a negotiated mid are skipped.
async fn send_slot_maps(slots: &Arc<Mutex<Vec<Slot>>>, dc: &Outbox) {
    /* Queued under the lock: an unsubscribe that frees a slot between reading
    its binding and announcing it would leave the client mapping the camera
    onto the freed slot. */
    let guard = slots.lock().await;
    for payload in slot_maps(&guard) {
        send_server_message(dc, &ServerMessage::SlotMap(payload));
    }
}

/// Every bound slot's camera and track.
async fn bound_tracks(slots: &Arc<Mutex<Vec<Slot>>>) -> Vec<(u32, Arc<VideoTrack>)> {
    slots
        .lock()
        .await
        .iter()
        .filter_map(|s| s.bound.map(|f| (f, s.track.clone())))
        .collect()
}

/// The slot map of every bound slot that has a negotiated mid.
fn slot_maps(slots: &[Slot]) -> Vec<SlotMapPayload> {
    slots
        .iter()
        .filter_map(|s| match (&s.mid, s.bound) {
            (Some(mid), Some(flight_id)) => Some(SlotMapPayload {
                mid: mid.clone(),
                flight_id: Some(flight_id),
            }),
            _ => None,
        })
        .collect()
}

async fn apply_layer_change(
    registry: &Arc<CameraRegistry>,
    dc: &Outbox,
    flight_id: u32,
    layers: Vec<Layer>,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
                    ..Default::default()
                }),
            );
            return;
        }
    };

    {
        let mut ctrl = cam.control.lock().await;
        ctrl.layers = layers.clone();
    }

    if let Err(e) = registry.flush_control(flight_id).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }

    info!(flight_id, layers = ?layers, "data-channel set-layers applied");
    push_camera_state(registry, dc, flight_id).await;
}

async fn apply_render_size_change(
    registry: &Arc<CameraRegistry>,
    dc: &Outbox,
    flight_id: u32,
    width: u32,
    height: u32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
                    ..Default::default()
                }),
            );
            return;
        }
    };

    // Cap at the ring's allocated max — anything bigger would be
    // rejected by the plugin's MmapFrameRing.Produce on the next emit.
    let w = make_even(width.min(cam.max_width));
    let h = make_even(height.min(cam.max_height));

    // SetRenderSize is now a CAP, not a direct set: it bounds the auto
    // MAX-across-consumers resolution rather than clobbering it. The effective
    // size flushed to the plugin is min(auto, this cap).
    if let Err(e) = registry
        .set_manual_render_size(flight_id, Some((w, h)))
        .await
    {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }

    info!(
        flight_id,
        width = w,
        height = h,
        "data-channel set-render-size applied (manual cap)"
    );
    push_camera_state(registry, dc, flight_id).await;
}

/* `networks` is whether the plugin has listed its networks: a plugin too old for them never writes the file. */
fn hello_payload(networks: bool) -> HelloPayload {
    HelloPayload {
        sidecar_version: crate::VERSION.to_owned(),
        encoder_backend: selected_backend_name().to_owned(),
        frame_stamps: Some(true),
        anchors: Some(true),
        frame_command: Some(true),
        max_anchors_per_camera: Some(crate::protocol::MAX_ANCHORS_PER_CAMERA as u32),
        size_hold: Some(true),
        resume: Some(true),
        renegotiate: Some(true),
        max_slots: Some(MAX_SLOTS_PER_PEER as u32),
        networks: Some(networks),
    }
}

async fn apply_fov_change(registry: &Arc<CameraRegistry>, dc: &Outbox, flight_id: u32, fov: f32) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
                    ..Default::default()
                }),
            );
            return;
        }
    };
    let clamped = match registry.command_fov(&cam, fov).await {
        CommandFov::NotCommand => {
            if !cam.supports_zoom {
                send_server_message(
                    dc,
                    &ServerMessage::Error(ErrorPayload {
                        message: format!("camera {flight_id} does not support zoom"),
                        source: ErrorSource::Sidecar,
                        ..Default::default()
                    }),
                );
                return;
            }
            // Clamp to the part's declared FoV range so the plugin doesn't get
            // a value the Hullcam module would reject.
            fov.clamp(cam.fov_min, cam.fov_max)
        }
        CommandFov::Apply(fov) => fov,
        CommandFov::Refused(message, code) => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message,
                    code,
                    source: ErrorSource::Sidecar,
                    flight_id: Some(flight_id),
                }),
            );
            return;
        }
    };

    {
        let mut ctrl = cam.control.lock().await;
        ctrl.fov = Some(clamped);
        // Bump so the plugin applies this absolute even mid zoom-rate hold
        // (and distinguishes it from the same value re-serialised on an
        // unrelated write). Wrapping is fine — the plugin compares for
        // change, not magnitude.
        ctrl.fov_seq = ctrl.fov_seq.wrapping_add(1);
    }

    if let Err(e) = registry.flush_control(flight_id).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }

    info!(flight_id, fov = clamped, "data-channel set-fov applied");
    push_camera_state(registry, dc, flight_id).await;
}

async fn apply_pan_change(
    registry: &Arc<CameraRegistry>,
    dc: &Outbox,
    flight_id: u32,
    yaw: f32,
    pitch: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
                    ..Default::default()
                }),
            );
            return;
        }
    };
    if !cam.supports_pan {
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!(
                    "camera {flight_id} does not support pan/tilt (yet — \
                     planned mod extension)"
                ),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }
    let yaw_clamped = yaw.clamp(cam.pan_yaw_min, cam.pan_yaw_max);
    let pitch_clamped = pitch.clamp(cam.pan_pitch_min, cam.pan_pitch_max);

    {
        let mut ctrl = cam.control.lock().await;
        ctrl.pan_yaw = Some(yaw_clamped);
        ctrl.pan_pitch = Some(pitch_clamped);
        // Bump so the plugin applies this absolute even mid pan-rate hold
        // (see `ControlState::pan_seq`). Covers both yaw and pitch.
        ctrl.pan_seq = ctrl.pan_seq.wrapping_add(1);
    }

    if let Err(e) = registry.flush_control(flight_id).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }

    info!(
        flight_id,
        yaw = yaw_clamped,
        pitch = pitch_clamped,
        "data-channel set-pan applied"
    );
    push_camera_state(registry, dc, flight_id).await;
}

/// Persistent pan velocity. Mirrors `apply_pan_change` but stores a
/// normalised rate the plugin integrates per frame rather than an absolute
/// target. A rate holds until superseded (including by zero), so the on-disk
/// `pan_yaw_rate`/`pan_pitch_rate` carry the last command.
async fn apply_pan_rate_change(
    registry: &Arc<CameraRegistry>,
    dc: &Outbox,
    flight_id: u32,
    yaw_rate: f32,
    pitch_rate: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
                    ..Default::default()
                }),
            );
            return;
        }
    };
    if !cam.supports_pan {
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!(
                    "camera {flight_id} does not support pan/tilt (yet — \
                     planned mod extension)"
                ),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }
    let yaw_clamped = yaw_rate.clamp(-1.0, 1.0);
    let pitch_clamped = pitch_rate.clamp(-1.0, 1.0);

    {
        let mut ctrl = cam.control.lock().await;
        ctrl.pan_yaw_rate = Some(yaw_clamped);
        ctrl.pan_pitch_rate = Some(pitch_clamped);
    }

    if let Err(e) = registry.flush_control(flight_id).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }

    info!(
        flight_id,
        yaw_rate = yaw_clamped,
        pitch_rate = pitch_clamped,
        "data-channel set-pan-rate applied"
    );
    push_camera_state(registry, dc, flight_id).await;
}

/// Persistent zoom velocity. Mirrors `apply_fov_change` but stores a
/// normalised rate (+1 = zoom in) the plugin integrates into the FoV target
/// per frame, rather than an absolute FoV. Holds until superseded.
async fn apply_zoom_rate_change(
    registry: &Arc<CameraRegistry>,
    dc: &Outbox,
    flight_id: u32,
    rate: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
                    ..Default::default()
                }),
            );
            return;
        }
    };
    if !cam.supports_zoom {
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("camera {flight_id} does not support zoom"),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }
    if let Some(message) = registry.command_zoom_refusal(&cam, rate).await {
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message,
                code: Some(ErrorCode::CommandLocked),
                source: ErrorSource::Sidecar,
                flight_id: Some(flight_id),
            }),
        );
        return;
    }
    let clamped = rate.clamp(-1.0, 1.0);

    {
        let mut ctrl = cam.control.lock().await;
        ctrl.zoom_rate = Some(clamped);
    }

    if let Err(e) = registry.flush_control(flight_id).await {
        warn!(flight_id, error = %e, "control file flush failed");
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message: format!("control file flush failed: {e}"),
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }

    info!(
        flight_id,
        rate = clamped,
        "data-channel set-zoom-rate applied"
    );
    push_camera_state(registry, dc, flight_id).await;
}

/// Viewer quality preset. Validation is structural (an unknown preset
/// string already fails the serde parse) plus the unknown-camera check
/// inside `apply_viewer_quality`; the preset can only LOWER quality below
/// the operator ceiling, so there is no upper bound to clamp here. Replies
/// with the authoritative state; the registry's dirty mark makes the
/// consume loop broadcast the same state to every other peer (last write
/// wins, all UIs converge).
async fn apply_quality_change(
    registry: &Arc<CameraRegistry>,
    dc: &Outbox,
    flight_id: u32,
    preset: Option<QualityPreset>,
) {
    if let Err(message) = registry.apply_viewer_quality(flight_id, preset).await {
        send_server_message(
            dc,
            &ServerMessage::Error(ErrorPayload {
                message,
                source: ErrorSource::Sidecar,
                ..Default::default()
            }),
        );
        return;
    }
    info!(flight_id, ?preset, "data-channel set-quality applied");
    push_camera_state(registry, dc, flight_id).await;
}

async fn apply_degrade_change(
    registry: &Arc<CameraRegistry>,
    dc: &Outbox,
    peer_id: u32,
    flight_id: u32,
    level: f32,
) {
    let cam = match registry.get(flight_id).await {
        Some(c) => c,
        None => {
            send_server_message(
                dc,
                &ServerMessage::Error(ErrorPayload {
                    message: format!("no camera with flight_id={flight_id}"),
                    source: ErrorSource::Sidecar,
                    ..Default::default()
                }),
            );
            return;
        }
    };
    cam.set_degrade(level);
    info!(
        flight_id,
        peer_id,
        level = level.clamp(0.0, 1.0),
        effective = cam.current_degrade(),
        "set-degrade applied",
    );
    push_camera_state(registry, dc, flight_id).await;
}

async fn send_camera_snapshot(registry: &Arc<CameraRegistry>, dc: &Outbox) {
    // Per-camera state via the registry's shared snapshot builder: the
    // last plugin-reported effective state when one exists, optimistic
    // operator-equals-effective defaults before the first status write.
    // `list()` supplies the stable flight-id ordering.
    let cams = registry.list().await;
    let mut cameras = Vec::with_capacity(cams.len());
    for c in cams {
        if let Some(state) = registry.protocol_state(c.flight_id).await {
            cameras.push(state);
        }
    }
    send_server_message(
        dc,
        &ServerMessage::CameraSnapshot(CameraSnapshotPayload {
            cameras,
            follows: None,
        }),
    );
}

async fn push_camera_state(registry: &Arc<CameraRegistry>, dc: &Outbox, flight_id: u32) {
    let Some(state) = registry.protocol_state(flight_id).await else {
        return;
    };
    send_server_message(
        dc,
        &ServerMessage::CameraStateChanged(CameraStateChangedPayload { state }),
    );
}

const NO_FREE_SLOT_MESSAGE: &str = "no free slot - negotiate more video transceivers";

/// An error about one camera that a client can act on without parsing `message`.
fn refusal(flight_id: u32, code: ErrorCode, message: String) -> ErrorPayload {
    ErrorPayload {
        message,
        source: ErrorSource::Sidecar,
        flight_id: Some(flight_id),
        code: Some(code),
    }
}

fn send_server_message(dc: &Outbox, msg: &ServerMessage) {
    dc.send(msg);
}

fn make_even(v: u32) -> u32 {
    if v.is_multiple_of(2) {
        v
    } else {
        v.saturating_sub(1)
    }
}

/// Number of keyframe requests in a batch of received RTCP: Picture Loss
/// Indications (PLI) and Full Intra Requests (FIR). The browser sends these
/// when it hits a loss it can't recover from retransmission; honoring one forces
/// an IDR so the decoder unwedges in ~1 RTT instead of waiting for the next
/// scheduled keyframe (a full GOP of static — the "feeds cut to static" bug).
fn rtcp_keyframe_request_count(packets: &[Box<dyn rtcp::packet::Packet + Send + Sync>]) -> usize {
    use rtcp::payload_feedbacks::full_intra_request::FullIntraRequest;
    use rtcp::payload_feedbacks::picture_loss_indication::PictureLossIndication;
    packets
        .iter()
        .filter(|p| {
            let any = p.as_any();
            any.is::<PictureLossIndication>() || any.is::<FullIntraRequest>()
        })
        .count()
}

/// The lowest bandwidth estimate among the REMB packets in one RTCP read.
fn rtcp_remb_bps(packets: &[Box<dyn rtcp::packet::Packet + Send + Sync>]) -> Option<u64> {
    use rtcp::payload_feedbacks::receiver_estimated_maximum_bitrate::ReceiverEstimatedMaximumBitrate;
    packets
        .iter()
        .filter_map(|p| p.as_any().downcast_ref::<ReceiverEstimatedMaximumBitrate>())
        .map(|r| r.bitrate.max(0.0) as u64)
        .min()
}

/**
 * Samples one peer's outbound stream stats every `LINK_WINDOW` into its link
 * monitor, until the peer dies. Holds the connection weakly so it never keeps
 * a reaped peer alive.
 */
async fn sample_link(
    pc: std::sync::Weak<RTCPeerConnection>,
    registry: Arc<CameraRegistry>,
    peer_id: u32,
    liveness: LivenessHandle,
    slots: Arc<Mutex<Vec<Slot>>>,
    link: SharedLink,
) {
    /* One summary line per this many windows keeps a rig pass's log readable
    while still showing how each viewer's link moved. */
    const SUMMARY_EVERY: u32 = 15;
    let mut ticker = tokio::time::interval(LINK_WINDOW);
    ticker.set_missed_tick_behavior(tokio::time::MissedTickBehavior::Delay);
    let mut windows = 0u32;
    loop {
        ticker.tick().await;
        if !liveness.is_alive(Instant::now()) {
            return;
        }
        let Some(pc) = pc.upgrade() else { return };
        let report = pc.get_stats().await;
        drop(pc);
        let counters = slot_counters(&report);
        let now = std::time::Instant::now();
        let window = lock_link(&link).monitor.observe(now, &counters);
        let Some(w) = window else { continue };
        if w.packets_sent == 0 {
            continue;
        }
        tracing::debug!(peer_id, window = ?w, "viewer link window");
        let views = slot_views(&registry, peer_id, &slots).await;
        let changes = lock_link(&link).ladder.step(now, &w, &views);
        if !changes.is_empty() {
            let bound = bound_tracks_by_slot(&slots).await;
            act_on_tier_changes(
                &registry,
                peer_id,
                &bound,
                &changes,
                &w,
                registry.lighter_streams(),
            )
            .await;
        }
        windows += 1;
        if windows % SUMMARY_EVERY == 1 {
            let cameras: Vec<u32> = slots.lock().await.iter().filter_map(|s| s.bound).collect();
            info!(
                peer_id,
                ?cameras,
                send_kbps = w.send_bps / 1000,
                loss_pct = format!("{:.1}", w.loss * 100.0),
                rtt_ms = w.rtt_ms.map(|r| r.round() as u64),
                min_rtt_ms = w.min_rtt_ms.map(|r| r.round() as u64),
                nacks_per_s = format!("{:.1}", w.nacks_per_s),
                plis_per_s = format!("{:.2}", w.plis_per_s),
                remb_kbps = w.remb_bps.map(|b| b / 1000),
                "viewer link"
            );
        }
    }
}

/**
 * Carry out the detector's moves, or with `apply` off only log them. A move
 * goes through `set_track_tier`, which replaces any move still pending on the
 * same feed, so a feed is never left waiting on two tiers.
 */
async fn act_on_tier_changes(
    registry: &Arc<CameraRegistry>,
    peer_id: u32,
    bound: &[(usize, u32, Arc<VideoTrack>)],
    changes: &[TierChange],
    w: &LinkWindow,
    apply: bool,
) {
    for c in changes {
        let loss_pct = format!("{:.1}", w.loss * 100.0);
        let rtt_ms = w.rtt_ms.map(|r| r.round() as u64);
        if !apply {
            info!(
                peer_id,
                flight_id = c.flight_id,
                slot = c.slot,
                to = ?c.to,
                reason = ?c.reason,
                loss_pct,
                rtt_ms,
                "shadow tier change (not applied)"
            );
            continue;
        }
        let track = bound
            .iter()
            .find(|(slot, flight_id, _)| (*slot, *flight_id) == (c.slot, c.flight_id))
            .map(|(_, _, t)| t);
        let Some(track) = track else { continue };
        let Some(cam) = registry.get(c.flight_id).await else {
            continue;
        };
        if cam.set_track_tier(track, c.to).await {
            info!(
                peer_id,
                flight_id = c.flight_id,
                slot = c.slot,
                to = ?c.to,
                reason = ?c.reason,
                loss_pct,
                rtt_ms,
                "viewer tier change (applied)"
            );
        }
    }
}

/// A recording needs the full stream: when a viewer starts forcing full
/// resolution on a camera, its feed of that camera leaves Reduced at once
/// rather than at the next link window.
async fn return_recorded_feeds_to_full(
    registry: &Arc<CameraRegistry>,
    flight_id: u32,
    bound: &[(usize, u32, Arc<VideoTrack>)],
) {
    let Some(cam) = registry.get(flight_id).await else {
        return;
    };
    for (_, f, track) in bound {
        if *f == flight_id && cam.tier_of(track).await == Some(Tier::Reduced) {
            cam.set_track_tier(track, Tier::Full).await;
            info!(flight_id, "recording started, feed back to the full stream");
        }
    }
}

/// Every bound slot's index, camera and track.
async fn bound_tracks_by_slot(slots: &Arc<Mutex<Vec<Slot>>>) -> Vec<(usize, u32, Arc<VideoTrack>)> {
    slots
        .lock()
        .await
        .iter()
        .enumerate()
        .filter_map(|(i, s)| s.bound.map(|f| (i, f, s.track.clone())))
        .collect()
}

/// The viewer's bound slots as the tier detector sees them.
async fn slot_views(
    registry: &Arc<CameraRegistry>,
    peer_id: u32,
    slots: &Arc<Mutex<Vec<Slot>>>,
) -> Vec<SlotView> {
    let bound: Vec<(usize, u32)> = slots
        .lock()
        .await
        .iter()
        .enumerate()
        .filter_map(|(i, s)| s.bound.map(|f| (i, f)))
        .collect();
    let mut views = Vec::with_capacity(bound.len());
    for (slot, flight_id) in bound {
        let Some(cam) = registry.get(flight_id).await else {
            continue;
        };
        views.push(SlotView {
            slot,
            flight_id,
            display_area: cam.display_area_for(peer_id).await,
            pinned_full: cam.forced_full_res.lock().await.contains(&peer_id),
        });
    }
    views
}

/// Each video slot's cumulative counters from a `get_stats` report, joining
/// the outbound stream to the browser's receiver report for it.
fn slot_counters(report: &webrtc::stats::StatsReport) -> Vec<SlotCounters> {
    use webrtc::stats::StatsReportType;
    let mut remote = std::collections::HashMap::new();
    for stat in report.reports.values() {
        if let StatsReportType::RemoteInboundRTP(r) = stat {
            remote.insert(r.ssrc, (r.packets_lost, r.round_trip_time));
        }
    }
    report
        .reports
        .values()
        .filter_map(|stat| match stat {
            StatsReportType::OutboundRTP(o) if o.kind == "video" => {
                let (lost, rtt) = remote.get(&o.ssrc).copied().unwrap_or((0, None));
                Some(SlotCounters {
                    ssrc: o.ssrc,
                    packets_sent: o.packets_sent,
                    bytes_sent: o.bytes_sent,
                    nacks: o.nack_count,
                    plis: o.pli_count.unwrap_or(0),
                    remote_lost: lost,
                    rtt_ms: rtt,
                })
            }
            _ => None,
        })
        .collect()
}

/// Per-slot RTCP drain. webrtc-rs's NACK/PLI pipelines stall silently if a
/// sender's RTCP isn't read, so every slot-track's sender must be drained for
/// the connection's lifetime regardless of which camera (if any) currently
/// feeds the slot. Draining also pumps the NACK responder interceptor, which
/// does the actual retransmission. Exits when the sender closes (peer torn
/// down).
///
/// On top of draining, a PLI/FIR keyframe request is honored: the browser sends
/// one when a loss broke the reference chain and retransmission didn't recover
/// it, so we ask the camera bound to this slot for an IDR. The camera's own
/// `MIN_KEYFRAME_GAP` turns a sustained loss (a PLI every few hundred ms, from
/// any number of viewers) into at most one IDR a second rather than a storm
/// that spikes the bitrate into more loss. The NACK responder recovers most
/// losses with no PLI at all, so this rarely fires.
///
/// A REMB is recorded against the whole peer, never a camera: it estimates the
/// viewer's link, which every slot shares. It never drives an encoder (REMB
/// driven reinits caused a death spiral, see `main.rs`). A PLI is
/// instantaneous, so it pins to the slot's *current* binding cleanly (a stale
/// rebind just costs one wasted IDR).
async fn drain_rtcp_sink(
    sender: Arc<webrtc::rtp_transceiver::rtp_sender::RTCRtpSender>,
    slot_idx: usize,
    slots: Arc<Mutex<Vec<Slot>>>,
    registry: Arc<CameraRegistry>,
    link: SharedLink,
) {
    while let Ok((packets, _)) = sender.read_rtcp().await {
        if let Some(bps) = rtcp_remb_bps(&packets) {
            let first = lock_link(&link)
                .monitor
                .note_remb(std::time::Instant::now(), bps);
            if first {
                info!(slot_idx, remb_bps = bps, "viewer sends REMB");
            }
        }
        let requests = rtcp_keyframe_request_count(&packets);
        if requests == 0 {
            continue;
        }
        let bound = {
            let guard = slots.lock().await;
            guard
                .get(slot_idx)
                .and_then(|s| s.bound.map(|f| (f, s.track.clone())))
        };
        let Some((flight_id, track)) = bound else {
            continue;
        };
        let Some(cam) = registry.get(flight_id).await else {
            continue;
        };
        cam.note_keyframe_requests(requests as u64);
        cam.request_keyframe_for(&track).await;
    }
    // read_rtcp errors when the sender closes (peer torn down) — the normal
    // exit. If it ever happens mid-connection this slot loses its NACK pump and
    // PLI handling for the rest of the session, so leave a breadcrumb.
    info!(slot_idx, "RTCP drain exited (sender closed)");
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;
    use std::sync::Weak;
    use std::time::Duration;

    #[test]
    fn hello_offers_networks_only_when_the_plugin_reports_them() {
        assert_eq!(hello_payload(false).networks, Some(false));
        assert_eq!(hello_payload(true).networks, Some(true));
    }

    /// The browser's peer connection, with webrtc-rs's default codecs standing
    /// in for a browser's.
    async fn build_pc() -> Arc<RTCPeerConnection> {
        let mut media_engine = MediaEngine::default();
        media_engine
            .register_default_codecs()
            .expect("register default codecs");
        let api = APIBuilder::new().with_media_engine(media_engine).build();
        Arc::new(
            api.new_peer_connection(RTCConfiguration::default())
                .await
                .expect("create peer connection"),
        )
    }

    /// The browser's side of a slot pool: a client offer of `n` recv-only
    /// video transceivers plus the control channel, as the SDK builds it.
    async fn browser_offer(browser: &RTCPeerConnection, n: usize) -> String {
        use webrtc::rtp_transceiver::rtp_codec::RTPCodecType;
        use webrtc::rtp_transceiver::rtp_transceiver_direction::RTCRtpTransceiverDirection;
        use webrtc::rtp_transceiver::RTCRtpTransceiverInit;
        for _ in 0..n {
            browser
                .add_transceiver_from_kind(
                    RTPCodecType::Video,
                    Some(RTCRtpTransceiverInit {
                        direction: RTCRtpTransceiverDirection::Recvonly,
                        send_encodings: vec![],
                    }),
                )
                .await
                .expect("add transceiver");
        }
        let offer = browser.create_offer(None).await.expect("offer");
        browser
            .set_local_description(offer.clone())
            .await
            .expect("set local offer");
        offer.sdp
    }

    #[tokio::test]
    async fn a_re_offer_grows_the_pool_and_keeps_existing_slots() {
        let dir = tempfile::tempdir().expect("tempdir");
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        let peer = KerbcastPeer::new(registry, &[], 2).await.expect("peer");

        let browser = build_pc().await;
        browser
            .create_data_channel(CONTROL_CHANNEL_LABEL, None)
            .await
            .expect("control channel");
        let offer = browser_offer(&browser, 2).await;
        let answer = peer.answer_to_offer(offer).await.expect("answer");
        browser
            .set_remote_description(RTCSessionDescription::answer(answer).unwrap())
            .await
            .expect("set answer");
        let before: Vec<Option<String>> = peer
            .slots
            .lock()
            .await
            .iter()
            .map(|s| s.mid.clone())
            .collect();

        let offer = browser_offer(&browser, 3).await;
        let answer = peer.grow(5, offer).await.expect("grow");
        browser
            .set_remote_description(RTCSessionDescription::answer(answer).unwrap())
            .await
            .expect("set grown answer");

        let mids: Vec<Option<String>> = peer
            .slots
            .lock()
            .await
            .iter()
            .map(|s| s.mid.clone())
            .collect();
        assert_eq!(mids.len(), 5);
        assert_eq!(mids[..2], before[..], "existing slots keep their mids");
        let browser_mids: Vec<Option<String>> = browser
            .get_transceivers()
            .await
            .iter()
            .map(|t| t.mid().map(|m| m.to_string()))
            .collect();
        assert_eq!(
            mids, browser_mids,
            "every slot is addressable by the browser's mid"
        );

        let ready = |slots: &Vec<Slot>| slots.iter().map(|s| s.ready).collect::<Vec<_>>();
        assert_eq!(
            ready(&*peer.slots.lock().await),
            [true, true, false, false, false],
            "grown slots wait for the browser to confirm"
        );
        confirm_slots(&peer.slots, 4).await;
        assert_eq!(
            ready(&*peer.slots.lock().await),
            [true, true, true, true, false]
        );

        let err = peer.grow(4, String::new()).await.unwrap_err();
        assert!(
            err.to_string().contains("must add slots"),
            "a re-offer must add to the confirmed slots: {err}"
        );
        assert!(
            peer.grow(MAX_SLOTS_PER_PEER + 1, String::new())
                .await
                .is_err(),
            "the pool stops at the per-connection ceiling"
        );
    }

    /* Three cameras on one viewer's connection, as a phone holds them: releasing
    the third slot and resizing the first must leave the second's packets
    arriving on the mid they always came on. Run over a real connection, since a
    reload clearing a stall points at state only a connection holds. Each frame
    carries its camera's id as its payload, so a packet landing on the wrong mid
    fails as surely as one that never arrives. */
    #[tokio::test]
    async fn releasing_one_slot_and_resizing_another_keeps_the_third_feed_arriving() {
        use crate::shared_mem::{CaptureStamp, MmapFrameRing, MmapRingConfig};
        use crate::webrtc::video_track::CaptureTime;
        use std::collections::HashMap;
        use std::time::Duration;
        use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;

        const LAUNCHPAD: u32 = 11;
        const HULL: u32 = 22;
        const FACE: u32 = 33;
        type Arrivals = Arc<std::sync::Mutex<HashMap<(String, u8), usize>>>;

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 512,
            max_height: 512,
        };
        for id in [LAUNCHPAD, HULL, FACE] {
            MmapFrameRing::create(&dir.path().join(format!("{id}.ring")), cfg).expect("ring");
        }
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;
        let peer = KerbcastPeer::new(registry.clone(), &[], 4)
            .await
            .expect("peer");

        let viewer = build_pc().await;
        viewer
            .create_data_channel(CONTROL_CHANNEL_LABEL, None)
            .await
            .expect("control channel");
        let arrivals: Arrivals = Default::default();
        let seen = arrivals.clone();
        viewer.on_track(Box::new(move |track, _, transceiver| {
            let seen = seen.clone();
            Box::pin(async move {
                let mid = transceiver.mid().map(|m| m.to_string()).unwrap_or_default();
                tokio::spawn(async move {
                    while let Ok((packet, _)) = track.read_rtp().await {
                        let Some(&camera) = packet.payload.last() else {
                            continue;
                        };
                        *seen
                            .lock()
                            .unwrap()
                            .entry((mid.clone(), camera))
                            .or_default() += 1;
                    }
                });
            })
        }));
        let (up_tx, mut up_rx) = tokio::sync::mpsc::channel::<()>(1);
        viewer.on_peer_connection_state_change(Box::new(move |state| {
            let up_tx = up_tx.clone();
            Box::pin(async move {
                if state == RTCPeerConnectionState::Connected {
                    let _ = up_tx.try_send(());
                }
            })
        }));
        let mut gathered = viewer.gathering_complete_promise().await;
        browser_offer(&viewer, 4).await;
        let _ = gathered.recv().await;
        let offer = viewer.local_description().await.expect("offer").sdp;
        let answer = peer.answer_to_offer(offer).await.expect("answer");
        viewer
            .set_remote_description(RTCSessionDescription::answer(answer).unwrap())
            .await
            .expect("set answer");
        tokio::time::timeout(Duration::from_secs(15), up_rx.recv())
            .await
            .expect("viewer connects");

        for id in [LAUNCHPAD, HULL, FACE] {
            handle_subscribe(&registry, &peer.slots, &peer.outbox, id).await;
        }
        let slots_of = |slots: &Vec<Slot>| -> Vec<(Option<String>, Option<u32>)> {
            slots.iter().map(|s| (s.mid.clone(), s.bound)).collect()
        };
        let before = slots_of(&*peer.slots.lock().await);
        let mid_of = |id: u32| {
            before
                .iter()
                .find(|(_, bound)| *bound == Some(id))
                .and_then(|(mid, _)| mid.clone())
                .expect("bound with a mid")
        };
        let (hull_mid, face_mid, launchpad_mid) = (mid_of(HULL), mid_of(FACE), mid_of(LAUNCHPAD));

        let mut clock = 0.0;
        let send = |clock: f64| {
            let registry = registry.clone();
            async move {
                for id in [LAUNCHPAD, HULL, FACE] {
                    let cam = registry.get(id).await.expect("camera");
                    let tracks: Vec<_> = cam
                        .full
                        .tracks
                        .read()
                        .await
                        .iter()
                        .filter_map(|w| w.upgrade())
                        .collect();
                    for track in tracks {
                        let mut frame = vec![0, 0, 0, 1, 0x65];
                        frame.resize(64, id as u8);
                        track
                            .write_frame(
                                &frame,
                                CaptureTime {
                                    game_clock_ms: clock,
                                    picked_up: std::time::Instant::now(),
                                },
                                true,
                                id,
                                &CaptureStamp::at(Some(clock / 1000.0), 0),
                            )
                            .await
                            .expect("write");
                    }
                }
            }
        };
        let count = |mid: &str, id: u32| {
            *arrivals
                .lock()
                .unwrap()
                .get(&(mid.to_owned(), id as u8))
                .unwrap_or(&0)
        };
        /* Sends until the feed's count has risen by `want`, or the deadline. */
        for _ in 0..200 {
            clock += 33.0;
            send(clock).await;
            tokio::time::sleep(Duration::from_millis(10)).await;
            if count(&hull_mid, HULL) >= 30
                && count(&launchpad_mid, LAUNCHPAD) >= 30
                && count(&face_mid, FACE) >= 30
            {
                break;
            }
        }
        assert!(
            count(&hull_mid, HULL) >= 30,
            "hull flows before the release"
        );
        assert!(count(&face_mid, FACE) >= 30, "and the face camera");

        handle_unsubscribe(&registry, peer.peer_id, &peer.slots, &peer.outbox, FACE).await;
        registry
            .record_display_size(LAUNCHPAD, peer.peer_id, 368, 208)
            .await;

        let after = slots_of(&*peer.slots.lock().await);
        let expected: Vec<_> = before
            .iter()
            .map(|(mid, bound)| {
                let bound = bound.filter(|&id| id != FACE);
                (mid.clone(), bound)
            })
            .collect();
        assert_eq!(after, expected, "only the released slot changes");
        let hull = registry.get(HULL).await.expect("hull");
        assert_eq!(
            hull.subscribers.load(Ordering::Acquire),
            1,
            "hull keeps its viewer"
        );
        assert!(hull.control.lock().await.subscribed, "and stays awake");
        assert!(
            !registry
                .get(FACE)
                .await
                .unwrap()
                .control
                .lock()
                .await
                .subscribed,
            "the released camera sleeps"
        );

        tokio::time::sleep(Duration::from_millis(100)).await;
        let (hull_before, launchpad_before, face_before) = (
            count(&hull_mid, HULL),
            count(&launchpad_mid, LAUNCHPAD),
            count(&face_mid, FACE),
        );
        for _ in 0..200 {
            clock += 33.0;
            send(clock).await;
            tokio::time::sleep(Duration::from_millis(10)).await;
            if count(&hull_mid, HULL) >= hull_before + 60
                && count(&launchpad_mid, LAUNCHPAD) >= launchpad_before + 60
            {
                break;
            }
        }
        assert!(
            count(&hull_mid, HULL) >= hull_before + 60,
            "hull kept arriving on its own mid: {} then {}",
            hull_before,
            count(&hull_mid, HULL)
        );
        assert!(
            count(&launchpad_mid, LAUNCHPAD) >= launchpad_before + 60,
            "the resized camera too"
        );
        assert_eq!(
            count(&face_mid, FACE),
            face_before,
            "the released one stops"
        );
        let strays = arrivals
            .lock()
            .unwrap()
            .keys()
            .filter(|(mid, id)| {
                !matches!(
                    (mid.as_str(), *id as u32),
                    (m, HULL) if m == hull_mid
                ) && !matches!((mid.as_str(), *id as u32), (m, LAUNCHPAD) if m == launchpad_mid)
                    && !matches!((mid.as_str(), *id as u32), (m, FACE) if m == face_mid)
            })
            .count();
        assert_eq!(strays, 0, "no feed reached another camera's mid");
        viewer.close().await.expect("close viewer");
        peer.close().await.expect("close peer");
    }

    /// A peer with two confirmed slots and the browser that negotiated them.
    async fn connected_pair() -> (KerbcastPeer, Arc<RTCPeerConnection>, tempfile::TempDir) {
        let dir = tempfile::tempdir().expect("tempdir");
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        let peer = KerbcastPeer::new(registry, &[], 2).await.expect("peer");
        let browser = build_pc().await;
        browser
            .create_data_channel(CONTROL_CHANNEL_LABEL, None)
            .await
            .expect("control channel");
        let offer = browser_offer(&browser, 2).await;
        let answer = peer.answer_to_offer(offer).await.expect("answer");
        browser
            .set_remote_description(RTCSessionDescription::answer(answer).unwrap())
            .await
            .expect("set answer");
        (peer, browser, dir)
    }

    async fn pool_transceivers(peer: &KerbcastPeer) -> Vec<Option<Arc<RTCRtpTransceiver>>> {
        peer.slots
            .lock()
            .await
            .iter()
            .map(|s| s.transceiver.clone())
            .collect()
    }

    fn is_stopped(t: &RTCRtpTransceiver) -> bool {
        use webrtc::rtp_transceiver::rtp_transceiver_direction::RTCRtpTransceiverDirection;
        t.direction() == RTCRtpTransceiverDirection::Inactive
    }

    #[tokio::test]
    async fn a_re_offer_after_a_lost_answer_reanswers_the_slots_it_still_offers() {
        let (peer, browser, _dir) = connected_pair().await;
        let offer = browser_offer(&browser, 2).await;
        peer.grow(4, offer.clone()).await.expect("first grow");
        let first = pool_transceivers(&peer).await;

        /* The answer never reached the browser, which offers the same
        sections again. */
        let answer = peer.grow(4, offer).await.expect("retry");
        let again = pool_transceivers(&peer).await;
        for i in 2..4 {
            let (a, b) = (first[i].as_ref().unwrap(), again[i].as_ref().unwrap());
            assert!(Arc::ptr_eq(a, b), "slot {i} keeps its transceiver");
            assert!(!is_stopped(b));
        }
        browser
            .set_remote_description(RTCSessionDescription::answer(answer).unwrap())
            .await
            .expect("set answer");
        confirm_slots(&peer.slots, 4).await;
        let slots = peer.slots.lock().await;
        assert!(slots.iter().all(|s| s.ready), "every slot confirmed");
        let browser_mids: Vec<Option<String>> = browser
            .get_transceivers()
            .await
            .iter()
            .map(|t| t.mid().map(|m| m.to_string()))
            .collect();
        let mids: Vec<Option<String>> = slots.iter().map(|s| s.mid.clone()).collect();
        assert_eq!(mids, browser_mids);
    }

    #[tokio::test]
    async fn a_re_offer_with_new_mids_replaces_the_unconfirmed_slots() {
        let (peer, browser, _dir) = connected_pair().await;
        let offer = browser_offer(&browser, 2).await;
        peer.grow(4, offer.clone()).await.expect("first grow");
        let first = pool_transceivers(&peer).await;
        let lost: Vec<String> = peer.slots.lock().await[2..]
            .iter()
            .map(|s| s.mid.clone().expect("mid"))
            .collect();

        /* A browser that rolled back its offer gives the added sections new
        mids on the next one. */
        let mut retry = offer;
        for (n, mid) in lost.iter().enumerate() {
            retry = retry
                .replace(&format!("a=mid:{mid}\r\n"), &format!("a=mid:again{n}\r\n"))
                .replace(&format!(" {mid} "), &format!(" again{n} "))
                .replace(&format!(" {mid}\r\n"), &format!(" again{n}\r\n"));
        }
        let answer = peer.grow(4, retry).await.expect("retry");
        assert!(answer.contains("a=mid:again0") && answer.contains("a=mid:again1"));

        let again = pool_transceivers(&peer).await;
        assert_eq!(again.len(), 4, "the pool counts the sections offered");
        for i in 2..4 {
            let old = first[i].as_ref().unwrap();
            let new = again[i].as_ref().unwrap();
            assert!(is_stopped(old), "the stale slot {i} is stopped");
            assert!(
                !Arc::ptr_eq(old, new),
                "slot {i} never reuses its stopped transceiver"
            );
            assert!(!is_stopped(new));
        }
        confirm_slots(&peer.slots, 4).await;
        let slots = peer.slots.lock().await;
        let mids: Vec<Option<String>> = slots[2..].iter().map(|s| s.mid.clone()).collect();
        assert_eq!(mids, [Some("again0".to_owned()), Some("again1".to_owned())]);
        assert!(slots.iter().all(|s| s.ready));
    }

    #[tokio::test]
    async fn a_re_offer_reusing_a_retired_mid_is_refused() {
        let (peer, browser, _dir) = connected_pair().await;
        let offer = browser_offer(&browser, 2).await;
        peer.grow(4, offer.clone()).await.expect("first grow");
        let lost: Vec<String> = peer.slots.lock().await[2..]
            .iter()
            .map(|s| s.mid.clone().expect("mid"))
            .collect();
        let mut retry = offer.clone();
        for (n, mid) in lost.iter().enumerate() {
            retry = retry
                .replace(&format!("a=mid:{mid}\r\n"), &format!("a=mid:again{n}\r\n"))
                .replace(&format!(" {mid} "), &format!(" again{n} "))
                .replace(&format!(" {mid}\r\n"), &format!(" again{n}\r\n"));
        }
        peer.grow(4, retry)
            .await
            .expect("retry retires the first slots");
        let before = pool_transceivers(&peer).await;

        /* The first offer again: its mids now belong to stopped transceivers. */
        let err = peer.grow(4, offer).await.unwrap_err();
        assert!(err.is::<GrowRefused>(), "{err}");
        let after = pool_transceivers(&peer).await;
        for (a, b) in before.iter().zip(&after) {
            match (a, b) {
                (Some(a), Some(b)) => {
                    assert!(Arc::ptr_eq(a, b));
                    assert!(!is_stopped(b), "a refused re-offer retires nothing");
                }
                (None, None) => {}
                _ => panic!("the pool changed"),
            }
        }
    }

    #[tokio::test]
    async fn a_failed_re_offer_leaves_nothing_a_later_one_reuses() {
        let (peer, browser, _dir) = connected_pair().await;
        let offer = browser_offer(&browser, 2).await;
        /* Carries the sent format, so it gets as far as the SDP exchange. */
        let broken = offer.replacen("v=0", "v=garbage", 1);
        assert!(peer.grow(4, broken).await.is_err());
        assert_eq!(peer.slot_count().await, 2, "a failed re-offer adds nothing");
        let stopped_before = peer
            .pc
            .get_transceivers()
            .await
            .into_iter()
            .filter(|t| is_stopped(t))
            .collect::<Vec<_>>();
        assert_eq!(stopped_before.len(), 2, "its transceivers are stopped");

        peer.grow(4, offer).await.expect("grow");
        for t in pool_transceivers(&peer).await.into_iter().flatten() {
            assert!(!is_stopped(&t));
            assert!(stopped_before.iter().all(|s| !Arc::ptr_eq(s, &t)));
        }
    }

    /// What a recovered peer is sent again: one map per bound slot with a mid,
    /// none for an idle slot or one the browser has not negotiated yet.
    #[tokio::test]
    async fn slot_maps_name_only_bound_slots_with_a_mid() {
        let dc = Outbox::spawn(Arc::new(Mutex::new(None)), "test".to_owned());
        let slot = |i: usize, mid: Option<&str>, bound: Option<u32>| Slot {
            track: slot_track(i, &dc),
            mid: mid.map(str::to_owned),
            bound,
            ready: true,
            transceiver: None,
        };
        let slots = [
            slot(0, Some("0"), Some(101)),
            slot(1, Some("1"), None),
            slot(2, None, Some(103)),
            slot(3, Some("3"), Some(104)),
        ];
        let maps: Vec<(String, Option<u32>)> = slot_maps(&slots)
            .into_iter()
            .map(|p| (p.mid, p.flight_id))
            .collect();
        assert_eq!(
            maps,
            vec![("0".to_owned(), Some(101)), ("3".to_owned(), Some(104))]
        );
    }

    #[tokio::test]
    async fn a_subscribe_never_lands_on_an_unconfirmed_slot() {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        MmapFrameRing::create(&dir.path().join("9002.ring"), cfg).expect("ring");
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;
        let dc = Outbox::spawn(Arc::new(Mutex::new(None)), registry.anchor_owner(7));
        let slots = Arc::new(Mutex::new(vec![Slot {
            track: slot_track(0, &dc),
            mid: Some("0".to_owned()),
            bound: None,
            ready: false,
            transceiver: None,
        }]));

        handle_subscribe(&registry, &slots, &dc, 9002).await;
        assert_eq!(
            slots.lock().await[0].bound,
            None,
            "unconfirmed slot stays idle"
        );

        confirm_slots(&slots, 1).await;
        handle_subscribe(&registry, &slots, &dc, 9002).await;
        assert_eq!(slots.lock().await[0].bound, Some(9002));
    }

    // F4 premise (deterministic, no real connection needed): `pc.close()` must
    // release the RTCRtpSender's strong ref to the track, so the camera's Weak
    // goes dead and the consume loop prunes it — dropping the subscriber count
    // to zero, which flushes `set_subscribed(false)` and lets the plugin stop
    // rendering. WITHOUT close() the sender holds the track Arc for the life of
    // the (already-dropped) peer, so the Weak never dies: that is the leak that
    // left cameras rendering after a clean disconnect (the reaper dropped the
    // Arc<KerbcastPeer> but never called close()).
    #[tokio::test]
    async fn close_releases_track_arc() {
        let pc = build_pc().await;
        use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
        let track = Arc::new(TrackLocalStaticSample::new(
            RTCRtpCodecCapability {
                mime_type: MIME_TYPE_H264.to_owned(),
                ..Default::default()
            },
            "video".to_owned(),
            "stream".to_owned(),
        ));
        pc.add_track(track.clone() as Arc<dyn TrackLocal + Send + Sync>)
            .await
            .expect("add_track");

        let weak: Weak<TrackLocalStaticSample> = Arc::downgrade(&track);
        drop(track); // release our ref; the sender inside pc still holds one

        assert!(
            weak.upgrade().is_some(),
            "while the peer is open the sender keeps the track alive — this is \
             exactly the leak when close() is never called on disconnect"
        );

        pc.close().await.expect("close");

        // close() winds sender tasks down asynchronously; poll briefly.
        let mut freed = false;
        for _ in 0..40 {
            if weak.upgrade().is_none() {
                freed = true;
                break;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
        assert!(
            freed,
            "pc.close() must release the sender's track Arc within ~2s — \
             this is the mechanism F4 relies on to free camera subscriptions"
        );
    }

    // Regression coverage for the v1.8 force-full-resolution leak:
    // `handle_unsubscribe` cleared a peer's reported display size on
    // Unsubscribe but never its force-full-resolution pin, so a feed a peer
    // had forced to the ring ceiling (e.g. a full-resolution recording)
    // stayed pinned at the ceiling forever once that peer unsubscribed
    // without a full graceful Disconnect (e.g. switching feeds mid-
    // recording). No slot is bound for this flight_id here, exercising the
    // early-out path in `handle_unsubscribe` -- the force-pin clear (like
    // the display-size clear beside it) must happen unconditionally, before
    // that binding check, since a report/force is never binding-gated.
    #[tokio::test]
    async fn handle_unsubscribe_releases_this_peers_force_pin() {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 1024,
            max_height: 1024,
        };
        let ring_path = dir.path().join("9001.ring");
        MmapFrameRing::create(&ring_path, cfg).expect("create ring");
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;

        let cam = registry.get(9001).await.expect("camera attached");
        let peer_id = 42u32;
        cam.set_forced(peer_id, true).await;
        assert_eq!(
            cam.effective_render_size().await,
            Some((1024, 1024)),
            "forced feed sizes to the ring ceiling"
        );

        let slots: Arc<Mutex<Vec<Slot>>> = Arc::new(Mutex::new(Vec::new()));
        let dc = Outbox::spawn(Arc::new(Mutex::new(None)), registry.anchor_owner(peer_id));

        handle_unsubscribe(&registry, peer_id, &slots, &dc, 9001).await;

        assert_eq!(
            cam.effective_render_size().await,
            None,
            "Unsubscribe must release this peer's force-full-resolution pin, \
             not just its display-size report -- otherwise the feed never \
             relaxes off the ring ceiling"
        );
    }

    #[tokio::test]
    async fn unbindable_initial_cameras_are_refused_with_a_code() {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        for id in [1, 2] {
            MmapFrameRing::create(&dir.path().join(format!("{id}.ring")), cfg).expect("ring");
        }
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;

        // One slot, two live cameras and one unknown id, with repeats.
        let peer = KerbcastPeer::new(registry, &[1, 2, 99, 1, 99], 1)
            .await
            .expect("peer");

        assert_eq!(peer.subscribed, vec![1]);
        let got: Vec<_> = peer.refused.iter().map(|e| (e.flight_id, e.code)).collect();
        assert_eq!(
            got,
            vec![
                (Some(2), Some(ErrorCode::NoFreeSlot)),
                (Some(99), Some(ErrorCode::CameraNotFound)),
            ],
            "a full slot pool must not hide the cameras after the first refusal"
        );
    }

    // The persistent-sidecar scene-change contract: a peer that stays
    // connected while every ring is deleted (flight exit) and recreated
    // (flight re-entry, same flight id) must get its subscription rebound
    // onto the brand-new CameraState by resync_after_camera_churn, so the
    // stream resumes without the browser doing anything.
    /// Cameras 1, 2 and 3, each fed to one slot of peer 9 whose reported
    /// display sizes are 640x360, 200x112 and 320x180.
    async fn three_watched_cameras() -> (
        tempfile::TempDir,
        Arc<CameraRegistry>,
        Vec<(usize, u32, Arc<VideoTrack>)>,
    ) {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};
        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 640,
            max_height: 360,
        };
        for id in 1..=3 {
            MmapFrameRing::create(&dir.path().join(format!("{id}.ring")), cfg).expect("ring");
        }
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;
        let outbox = Outbox::spawn(Arc::new(Mutex::new(None)), "test".to_owned());
        let mut bound = Vec::new();
        for (slot, (id, w, h)) in [(1, 640, 360), (2, 200, 112), (3, 320, 180)]
            .into_iter()
            .enumerate()
        {
            let cam = registry.get(id).await.expect("camera");
            let track = slot_track(slot, &outbox);
            cam.add_track(track.clone()).await;
            cam.record_display_size(9, w, h).await;
            bound.push((slot, id, track));
        }
        (dir, registry, bound)
    }

    async fn views(
        registry: &Arc<CameraRegistry>,
        bound: &[(usize, u32, Arc<VideoTrack>)],
    ) -> Vec<SlotView> {
        let mut out = Vec::new();
        for (slot, flight_id, _) in bound {
            let cam = registry.get(*flight_id).await.unwrap();
            out.push(SlotView {
                slot: *slot,
                flight_id: *flight_id,
                display_area: cam.display_area_for(9).await,
                pinned_full: cam.forced_full_res.lock().await.contains(&9),
            });
        }
        out
    }

    fn link(loss: f64, rtt: f64, min_rtt: f64, remb: Option<u64>) -> LinkWindow {
        LinkWindow {
            span_ms: 2000,
            send_bps: 2_000_000,
            packets_sent: 600,
            loss,
            rtt_ms: Some(rtt),
            min_rtt_ms: Some(min_rtt),
            nacks_per_s: 0.0,
            plis_per_s: 0.0,
            remb_bps: remb,
        }
    }

    /// Drive the detector over `windows` and act on every change, the way the
    /// link sampler does.
    async fn drive(
        registry: &Arc<CameraRegistry>,
        bound: &[(usize, u32, Arc<VideoTrack>)],
        ladder: &mut Ladder,
        windows: &[LinkWindow],
        apply: bool,
        start: std::time::Instant,
    ) -> usize {
        let mut moves = 0;
        for (n, w) in windows.iter().enumerate() {
            let now = start + LINK_WINDOW * (n as u32 + 1);
            let changes = ladder.step(now, w, &views(registry, bound).await);
            moves += changes.len();
            act_on_tier_changes(registry, 9, bound, &changes, w, apply).await;
        }
        moves
    }

    async fn reduced_wanted(registry: &Arc<CameraRegistry>, id: u32) -> bool {
        registry.get(id).await.unwrap().reduced.wanted().await
    }

    #[tokio::test]
    async fn with_the_setting_off_no_feed_ever_moves() {
        let (_dir, registry, bound) = three_watched_cameras().await;
        let mut ladder = Ladder::new(LadderConfig::default());
        let lossy = vec![link(0.3, 40.0, 40.0, None); 4];
        let decided = drive(
            &registry,
            &bound,
            &mut ladder,
            &lossy,
            false,
            std::time::Instant::now(),
        )
        .await;
        assert!(decided > 0, "the detector still decides, in shadow");
        for id in 1..=3 {
            assert!(
                !reduced_wanted(&registry, id).await,
                "camera {id} stayed on Full"
            );
        }
    }

    /// The Deck's two clean LAN viewers move no feed even with the setting on.
    #[tokio::test]
    async fn measured_clean_lan_viewers_move_no_feed_with_the_setting_on() {
        use crate::ladder::measured::{DESKTOP, PHONE};
        for samples in [PHONE, DESKTOP] {
            let (_dir, registry, bound) = three_watched_cameras().await;
            let mut ladder = Ladder::new(LadderConfig::default());
            let windows: Vec<LinkWindow> = samples
                .iter()
                .flat_map(|&(send, remb, rtt, min_rtt)| {
                    let w = LinkWindow {
                        send_bps: send,
                        ..link(0.0, rtt, min_rtt, Some(remb))
                    };
                    [w, w]
                })
                .collect();
            let moves = drive(
                &registry,
                &bound,
                &mut ladder,
                &windows,
                true,
                std::time::Instant::now(),
            )
            .await;
            assert_eq!(moves, 0);
            for id in 1..=3 {
                assert!(!reduced_wanted(&registry, id).await);
            }
        }
    }

    /// With the setting on, sustained loss moves the smallest feed first, one
    /// feed per dwell, and a clean spell brings it back.
    #[tokio::test]
    async fn with_the_setting_on_congestion_moves_the_smallest_feed_and_recovery_returns_it() {
        let (_dir, registry, bound) = three_watched_cameras().await;
        let mut ladder = Ladder::new(LadderConfig::default());
        let start = std::time::Instant::now();
        let lossy = vec![link(0.08, 60.0, 40.0, None); 2];
        assert_eq!(
            drive(&registry, &bound, &mut ladder, &lossy, true, start).await,
            1
        );
        assert!(
            reduced_wanted(&registry, 2).await,
            "the 200x112 feed moves first"
        );
        assert!(!reduced_wanted(&registry, 1).await);
        assert!(!reduced_wanted(&registry, 3).await);

        /* The move lands on Reduced's keyframe. */
        let cam = registry.get(2).await.unwrap();
        for track in cam.reduced.take_pending().await {
            assert!(cam.move_track(&track, Tier::Reduced).await);
        }

        let clean = vec![link(0.0, 40.0, 40.0, None); 12];
        let later = start + LINK_WINDOW * 2;
        assert_eq!(
            drive(&registry, &bound, &mut ladder, &clean, true, later).await,
            1
        );
        assert!(cam.full.wanted().await, "on its way back to Full");
        assert_eq!(cam.full.pending.read().await.len(), 1);
    }

    /// A recording takes its feed off Reduced at once, and the detector
    /// never moves a recorded feed.
    #[tokio::test]
    async fn a_recorded_feed_returns_to_full_at_once_and_stays_there() {
        let (_dir, registry, bound) = three_watched_cameras().await;
        let (_, _, track) = &bound[1];
        let cam = registry.get(2).await.unwrap();
        assert!(cam.move_track(track, Tier::Reduced).await);

        registry.set_forced_full_res(2, 9, true).await;
        return_recorded_feeds_to_full(&registry, 2, &bound).await;
        assert_eq!(cam.full.pending.read().await.len(), 1, "moving back now");
        for t in cam.full.take_pending().await {
            assert!(cam.move_track(&t, Tier::Full).await);
        }

        let mut ladder = Ladder::new(LadderConfig::default());
        let severe = vec![link(0.3, 40.0, 40.0, None); 6];
        drive(
            &registry,
            &bound,
            &mut ladder,
            &severe,
            true,
            std::time::Instant::now(),
        )
        .await;
        assert!(
            !cam.reduced.wanted().await,
            "the recorded feed stays on Full"
        );
        assert!(reduced_wanted(&registry, 1).await, "the others still move");
        assert!(reduced_wanted(&registry, 3).await);
    }

    #[tokio::test]
    async fn reattached_ring_rebinds_surviving_subscription() {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};
        use std::sync::atomic::Ordering;

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        let ring_path = dir.path().join("1.ring");
        MmapFrameRing::create(&ring_path, cfg).expect("create ring");
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;

        let peer = Arc::new(
            KerbcastPeer::new(registry.clone(), &[1], 2)
                .await
                .expect("peer"),
        );
        let cam = registry.get(1).await.expect("camera attached");
        assert_eq!(
            cam.subscribers.load(Ordering::Acquire),
            1,
            "initial subscription bound"
        );

        // Scene exit: the plugin deletes the ring; rescan drops the camera.
        std::fs::remove_file(&ring_path).expect("delete ring");
        let outcome = registry.rescan().await;
        assert_eq!(outcome.removed, vec![1], "normal teardown reported");
        assert!(registry.get(1).await.is_none(), "camera gone from registry");

        // Scene re-entry: the same flight id reappears as a NEW CameraState
        // that knows nothing about the still-bound peer slot.
        MmapFrameRing::create(&ring_path, cfg).expect("recreate ring");
        let outcome = registry.rescan().await;
        assert_eq!(outcome.attached, vec![1], "re-attach reported");
        let cam2 = registry.get(1).await.expect("camera re-attached");
        assert_eq!(
            cam2.subscribers.load(Ordering::Acquire),
            0,
            "fresh CameraState starts unsubscribed"
        );

        resync_after_camera_churn(&registry, std::slice::from_ref(&peer), &outcome.attached).await;

        assert_eq!(
            cam2.subscribers.load(Ordering::Acquire),
            1,
            "subscription survives the scene change"
        );
        assert!(
            cam2.control.lock().await.subscribed,
            "plugin is told to resume rendering"
        );
    }

    /* A subscribe that looked the camera up just before a rescan replaced it adds
    its track to the discarded entry, and binds its slot only after the churn
    resync has already run. The live entry never hears of the viewer, so it goes
    idle and the plugin sleeps the camera, and a repeat subscribe only announces
    the slot again. Unix only: elsewhere a ring is told apart by its mtime, which
    a delete and recreate in the same tick can share. */
    #[cfg(unix)]
    #[tokio::test]
    async fn a_slot_bound_through_a_rescan_gets_its_track_back() {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};
        use std::sync::atomic::Ordering;

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        let ring_path = dir.path().join("1.ring");
        MmapFrameRing::create(&ring_path, cfg).expect("create ring");
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;
        let peer = Arc::new(
            KerbcastPeer::new(registry.clone(), &[], 2)
                .await
                .expect("peer"),
        );
        let discarded = registry.get(1).await.expect("camera attached");

        std::fs::remove_file(&ring_path).expect("delete ring");
        MmapFrameRing::create(&ring_path, cfg).expect("recreate ring");
        let outcome = registry.rescan().await;
        assert_eq!(outcome.attached, vec![1], "re-attached in place");
        resync_after_camera_churn(&registry, std::slice::from_ref(&peer), &outcome.attached).await;

        let track = {
            let mut slots = peer.slots.lock().await;
            slots[0].bound = Some(1);
            slots[0].track.clone()
        };
        discarded.add_track(track.clone()).await;

        let live = registry.get(1).await.expect("camera live");
        assert_eq!(
            live.subscribers.load(Ordering::Acquire),
            0,
            "the viewer is lost"
        );
        assert_eq!(peer.rebind_lost_tracks(&registry).await, 1);
        assert_eq!(
            live.subscribers.load(Ordering::Acquire),
            1,
            "the viewer is back"
        );
        assert!(live.tier_of(&track).await.is_some(), "and is fed");
        assert!(live.control.lock().await.subscribed, "and the camera wakes");
        assert!(
            live.full.keyframe_gate.lock().unwrap().pending(),
            "with a keyframe"
        );

        assert_eq!(
            peer.rebind_lost_tracks(&registry).await,
            0,
            "nothing left to do"
        );
        assert_eq!(
            live.subscribers.load(Ordering::Acquire),
            1,
            "and no double count"
        );

        handle_unsubscribe(&registry, 0, &peer.slots, &peer.outbox, 1).await;
        assert_eq!(
            peer.rebind_lost_tracks(&registry).await,
            0,
            "a freed slot stays free"
        );
        assert_eq!(live.subscribers.load(Ordering::Acquire), 0);
    }

    /// A ring recreated in place within one rescan keeps its viewers' sizes on
    /// the new entry, and the rebind passes them to the plugin's new control
    /// block along with the subscription.
    #[cfg(unix)]
    #[tokio::test]
    async fn a_set_fov_reaches_a_command_camera_nobody_subscribes_to() {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

        let dir = tempfile::tempdir().expect("tempdir");
        let id: u32 = 0x8000_0304;
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        MmapFrameRing::create(&dir.path().join(format!("{id}.ring")), cfg).expect("create ring");
        std::fs::write(
            dir.path().join(format!("{id}.info.json")),
            format!(
                r#"{{"lifecycle":"active","kind":"command","flight_id":{id},"camera_name":"Launchpad camera","supports_zoom":true,"fov":30,"fov_min":1,"fov_max":30,"station_id":"ground:LaunchPad","station_node_name":"LaunchPad"}}"#
            ),
        )
        .expect("write manifest");
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;
        let outbox = Outbox::spawn(Arc::new(Mutex::new(None)), "test".into());

        apply_fov_change(&registry, &outbox, id, 1.5).await;

        let bytes =
            std::fs::read(dir.path().join(format!("{id}.control.bin"))).expect("control written");
        let snap = crate::shared_mem::control::decode(&bytes).expect("decodes");
        assert!(!snap.subscribed, "no viewer subscribes to this camera");
        assert_eq!(snap.fov, Some(1.5));
    }

    #[tokio::test]
    async fn an_in_place_reattach_tells_the_plugin_the_viewers_size() {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 256,
            max_height: 256,
        };
        let ring_path = dir.path().join("1.ring");
        MmapFrameRing::create(&ring_path, cfg).expect("create ring");
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;
        let peer = Arc::new(
            KerbcastPeer::new(registry.clone(), &[1], 2)
                .await
                .expect("peer"),
        );
        registry
            .record_display_size(1, peer.peer_id, 160, 160)
            .await;

        std::fs::remove_file(&ring_path).expect("delete ring");
        std::fs::remove_file(dir.path().join("1.control.bin")).expect("delete control");
        MmapFrameRing::create(&ring_path, cfg).expect("recreate ring");
        let outcome = registry.rescan().await;
        resync_after_camera_churn(&registry, std::slice::from_ref(&peer), &outcome.attached).await;

        let bytes = std::fs::read(dir.path().join("1.control.bin")).expect("control written");
        let snap = crate::shared_mem::control::decode(&bytes).expect("decodes");
        assert!(snap.subscribed);
        assert_eq!((snap.width, snap.height), (Some(160), Some(160)));
    }

    /// A browser PLI is the signal that a feed is wedged on a broken reference
    /// and needs an IDR. It must be recognised as a keyframe request so the
    /// drain loop can force one (otherwise the feed stays static for a GOP).
    #[test]
    fn pli_is_recognised_as_a_keyframe_request() {
        use rtcp::payload_feedbacks::picture_loss_indication::PictureLossIndication;
        let pkts: Vec<Box<dyn rtcp::packet::Packet + Send + Sync>> =
            vec![Box::new(PictureLossIndication::default())];
        assert!(rtcp_keyframe_request_count(&pkts) > 0);
    }

    /// A Full Intra Request is the other keyframe-request RTCP feedback; some
    /// stacks send FIR instead of PLI, so both must force an IDR.
    #[test]
    fn fir_is_recognised_as_a_keyframe_request() {
        use rtcp::payload_feedbacks::full_intra_request::FullIntraRequest;
        let pkts: Vec<Box<dyn rtcp::packet::Packet + Send + Sync>> =
            vec![Box::new(FullIntraRequest::default())];
        assert!(rtcp_keyframe_request_count(&pkts) > 0);
    }

    /// Routine RTCP (receiver reports, NACKs) must NOT be mistaken for a
    /// keyframe request — those are handled by the NACK responder / reports and
    /// forcing an IDR on every one of them would be a keyframe storm.
    #[test]
    fn routine_rtcp_is_not_a_keyframe_request() {
        use rtcp::receiver_report::ReceiverReport;
        let pkts: Vec<Box<dyn rtcp::packet::Packet + Send + Sync>> =
            vec![Box::new(ReceiverReport::default())];
        assert_eq!(rtcp_keyframe_request_count(&pkts), 0);
    }

    /// Every PLI/FIR in a batch counts toward the camera's received-request
    /// counter; routine RTCP does not.
    #[test]
    fn remb_reads_the_lowest_estimate_in_a_read() {
        use rtcp::payload_feedbacks::receiver_estimated_maximum_bitrate::ReceiverEstimatedMaximumBitrate;
        use rtcp::receiver_report::ReceiverReport;
        let remb = |bitrate: f32| -> Box<dyn rtcp::packet::Packet + Send + Sync> {
            Box::new(ReceiverEstimatedMaximumBitrate {
                bitrate,
                ..Default::default()
            })
        };
        let pkts = vec![
            remb(900_000.0),
            Box::new(ReceiverReport::default()),
            remb(400_000.0),
        ];
        assert_eq!(rtcp_remb_bps(&pkts), Some(400_000));
        let none: Vec<Box<dyn rtcp::packet::Packet + Send + Sync>> =
            vec![Box::new(ReceiverReport::default())];
        assert_eq!(rtcp_remb_bps(&none), None);
    }

    #[test]
    fn keyframe_requests_are_counted_per_packet() {
        use rtcp::payload_feedbacks::full_intra_request::FullIntraRequest;
        use rtcp::payload_feedbacks::picture_loss_indication::PictureLossIndication;
        use rtcp::receiver_report::ReceiverReport;
        let pkts: Vec<Box<dyn rtcp::packet::Packet + Send + Sync>> = vec![
            Box::new(PictureLossIndication::default()),
            Box::new(ReceiverReport::default()),
            Box::new(FullIntraRequest::default()),
            Box::new(PictureLossIndication::default()),
        ];
        assert_eq!(rtcp_keyframe_request_count(&pkts), 3);
        assert_eq!(rtcp_keyframe_request_count(&pkts[1..2]), 0);
    }

    /// The regression this state machine exists for: `Disconnected` used to be
    /// treated as terminal, so the reaper closed the peer and the viewer lost
    /// the feed for a blip WebRTC would have healed on its own. ICE raises
    /// `Disconnected` after 5s of silence; recovery from there is routine.
    #[test]
    fn a_transient_disconnect_keeps_the_peer_alive() {
        let t0 = Instant::now();
        let mut live = PeerLiveness::new(t0);
        live.observe(RTCPeerConnectionState::Connected, t0);
        live.observe(RTCPeerConnectionState::Disconnected, t0);

        assert!(
            live.is_alive(t0 + DISCONNECTED_GRACE - Duration::from_secs(1)),
            "a peer inside the grace window must not be reaped"
        );
    }

    #[test]
    fn recovery_from_disconnected_clears_the_grace_window() {
        let t0 = Instant::now();
        let mut live = PeerLiveness::new(t0);
        live.observe(RTCPeerConnectionState::Connected, t0);
        live.observe(RTCPeerConnectionState::Disconnected, t0);
        live.observe(
            RTCPeerConnectionState::Connected,
            t0 + Duration::from_secs(2),
        );

        assert!(
            live.is_alive(t0 + DISCONNECTED_GRACE * 10),
            "a recovered peer is alive indefinitely, the earlier drop is spent"
        );
    }

    #[test]
    fn a_disconnect_that_never_recovers_is_reaped_after_the_grace() {
        let t0 = Instant::now();
        let mut live = PeerLiveness::new(t0);
        live.observe(RTCPeerConnectionState::Connected, t0);
        live.observe(RTCPeerConnectionState::Disconnected, t0);

        assert!(!live.is_alive(t0 + DISCONNECTED_GRACE + Duration::from_secs(1)));
    }

    /// A recovery from `Disconnected` is what re-announces the slot bindings
    /// (a 1.11 client needs them to take its feeds back). The first connect
    /// and repeat `Connected` notifications are not recoveries, so a client
    /// that never lost its feeds is not sent them again.
    #[test]
    fn only_a_return_from_disconnected_is_a_recovery() {
        let t0 = Instant::now();
        let mut live = PeerLiveness::new(t0);
        assert!(!live.observe(RTCPeerConnectionState::Connecting, t0));
        assert!(
            !live.observe(RTCPeerConnectionState::Connected, t0),
            "first connect"
        );
        assert!(
            !live.observe(RTCPeerConnectionState::Connected, t0),
            "repeat"
        );
        assert!(!live.observe(RTCPeerConnectionState::Disconnected, t0));
        assert!(!live.observe(RTCPeerConnectionState::Disconnected, t0));
        assert!(
            live.observe(RTCPeerConnectionState::Connected, t0),
            "recovered"
        );
        assert!(
            !live.observe(RTCPeerConnectionState::Connected, t0),
            "only once"
        );

        live.observe(RTCPeerConnectionState::Disconnected, t0);
        live.observe(RTCPeerConnectionState::Failed, t0);
        assert!(
            !live.observe(RTCPeerConnectionState::Connected, t0),
            "a dead peer never recovers"
        );
    }

    /// The grace window is measured from the FIRST `Disconnected`. webrtc-rs
    /// can re-raise the same state, and restarting the clock on each one would
    /// let a peer that never recovers hold its cameras forever.
    #[test]
    fn repeat_disconnected_notifications_do_not_extend_the_grace() {
        let t0 = Instant::now();
        let mut live = PeerLiveness::new(t0);
        live.observe(RTCPeerConnectionState::Connected, t0);
        live.observe(RTCPeerConnectionState::Disconnected, t0);
        live.observe(
            RTCPeerConnectionState::Disconnected,
            t0 + DISCONNECTED_GRACE - Duration::from_secs(1),
        );

        assert!(!live.is_alive(t0 + DISCONNECTED_GRACE + Duration::from_secs(1)));
    }

    /// `Failed` and `Closed` are the terminal states, and unlike `Disconnected`
    /// they get no grace at all: the connection is gone, so its cameras must
    /// stop rendering on the next reaper tick.
    #[test]
    fn failed_and_closed_are_terminal_immediately() {
        let t0 = Instant::now();
        for terminal in [
            RTCPeerConnectionState::Failed,
            RTCPeerConnectionState::Closed,
        ] {
            let mut live = PeerLiveness::new(t0);
            live.observe(RTCPeerConnectionState::Connected, t0);
            live.observe(terminal, t0);
            assert!(!live.is_alive(t0), "{terminal} must be terminal at once");
        }
    }

    /// Once the reaper has a dead verdict it closes the peer and releases its
    /// cameras. A late callback must not undo that: the peer it would revive
    /// no longer owns anything.
    #[test]
    fn dead_is_sticky() {
        let t0 = Instant::now();
        let mut live = PeerLiveness::new(t0);
        live.observe(RTCPeerConnectionState::Failed, t0);
        live.observe(
            RTCPeerConnectionState::Connected,
            t0 + Duration::from_secs(1),
        );

        assert!(!live.is_alive(t0 + Duration::from_secs(1)));
    }

    /// Bug 2's reaper half: a peer that never reaches `Connected` (client
    /// vanished mid-negotiation, so ICE never even started its checks) is
    /// collectable on a deadline rather than living for the process lifetime.
    #[test]
    fn a_peer_that_never_connects_expires() {
        let t0 = Instant::now();
        let live = PeerLiveness::new(t0);

        assert!(
            live.is_alive(t0 + NEGOTIATION_TIMEOUT - Duration::from_secs(1)),
            "negotiation must be given its full window"
        );
        assert!(!live.is_alive(t0 + NEGOTIATION_TIMEOUT + Duration::from_secs(1)));
    }

    /// The same deadline, through the real peer: proves `is_alive()` is wired
    /// to the state machine's clock and not to a construction-time flag.
    #[tokio::test]
    async fn a_real_peer_that_never_connects_stops_reporting_alive() {
        let registry = Arc::new(CameraRegistry::new(PathBuf::from("/tmp")));
        let peer = KerbcastPeer::new(registry, &[], 0)
            .await
            .expect("build peer");
        assert!(peer.is_alive(), "a fresh peer is alive while it negotiates");

        tokio::time::pause();
        tokio::time::advance(NEGOTIATION_TIMEOUT + Duration::from_secs(1)).await;

        assert!(
            !peer.is_alive(),
            "a peer that never connected must become reapable"
        );
    }

    /// A failed SDP exchange can never come up, so the signalling handler
    /// retires it immediately instead of leaving its cameras subscribed for
    /// the whole negotiation window.
    #[tokio::test]
    async fn mark_dead_retires_a_peer_without_waiting_for_the_deadline() {
        let registry = Arc::new(CameraRegistry::new(PathBuf::from("/tmp")));
        let peer = KerbcastPeer::new(registry, &[], 0)
            .await
            .expect("build peer");

        peer.mark_dead();

        assert!(!peer.is_alive());
    }

    /// Codec lines in the shape Chrome offers: VP8 first, packetization-mode=0
    /// H.264 ahead of mode 1, plain baseline and High beside constrained
    /// baseline, rtx for each, then AV1, red and ulpfec.
    const CHROME_CODECS: &str = "96 97 98 100 101 102 103 104 106 107 45 116 117|\
        a=rtpmap:96 VP8/90000\n\
        a=rtcp-fb:96 goog-remb\na=rtcp-fb:96 transport-cc\na=rtcp-fb:96 ccm fir\na=rtcp-fb:96 nack\na=rtcp-fb:96 nack pli\n\
        a=rtpmap:97 rtx/90000\na=fmtp:97 apt=96\n\
        a=rtpmap:98 VP9/90000\na=fmtp:98 profile-id=0\n\
        a=rtpmap:100 H264/90000\na=fmtp:100 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42e01f\n\
        a=rtpmap:101 H264/90000\na=fmtp:101 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42001f\n\
        a=rtpmap:102 H264/90000\na=fmtp:102 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f\n\
        a=rtpmap:103 rtx/90000\na=fmtp:103 apt=102\n\
        a=rtpmap:104 H264/90000\na=fmtp:104 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f\n\
        a=rtpmap:106 H264/90000\n\
        a=rtcp-fb:106 goog-remb\na=rtcp-fb:106 transport-cc\na=rtcp-fb:106 ccm fir\na=rtcp-fb:106 nack\na=rtcp-fb:106 nack pli\n\
        a=fmtp:106 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\n\
        a=rtpmap:107 rtx/90000\na=fmtp:107 apt=106\n\
        a=rtpmap:45 AV1/90000\n\
        a=rtpmap:116 red/90000\n\
        a=rtpmap:117 ulpfec/90000";

    /// Firefox with OpenH264: constrained baseline in both modes, mode 1 first.
    const FIREFOX_CODECS: &str = "120 124 121 125 126 127 97 98|\
        a=rtpmap:120 VP8/90000\na=fmtp:120 max-fs=12288;max-fr=60\n\
        a=rtpmap:124 rtx/90000\na=fmtp:124 apt=120\n\
        a=rtpmap:121 VP9/90000\na=fmtp:121 max-fs=12288;max-fr=60\n\
        a=rtpmap:125 rtx/90000\na=fmtp:125 apt=121\n\
        a=rtpmap:126 H264/90000\na=fmtp:126 profile-level-id=42e01f;level-asymmetry-allowed=1;packetization-mode=1\n\
        a=rtcp-fb:126 nack\na=rtcp-fb:126 nack pli\na=rtcp-fb:126 ccm fir\na=rtcp-fb:126 goog-remb\na=rtcp-fb:126 transport-cc\n\
        a=rtpmap:127 rtx/90000\na=fmtp:127 apt=126\n\
        a=rtpmap:97 H264/90000\na=fmtp:97 profile-level-id=42e01f;level-asymmetry-allowed=1\n\
        a=rtpmap:98 rtx/90000\na=fmtp:98 apt=97";

    /// Safari: High first, constrained baseline under its own payload types,
    /// with an uppercase profile-level-id at a different level.
    const SAFARI_CODECS: &str = "96 97 98 99 100 101 102|\
        a=rtpmap:96 H264/90000\na=fmtp:96 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=640c1f\n\
        a=rtpmap:97 rtx/90000\na=fmtp:97 apt=96\n\
        a=rtpmap:98 H264/90000\na=fmtp:98 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42E01F\n\
        a=rtpmap:99 rtx/90000\na=fmtp:99 apt=98\n\
        a=rtpmap:100 H264/90000\na=fmtp:100 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42e01f\n\
        a=rtpmap:101 VP8/90000\n\
        a=rtpmap:102 rtx/90000\na=fmtp:102 apt=101";

    /// `offer` with every video section's codecs replaced by `codecs`, given
    /// as "<payload types>|<rtpmap, fmtp and rtcp-fb lines>".
    fn with_codecs(offer: &str, codecs: &str) -> String {
        let (pts, lines) = codecs.split_once('|').expect("codec spec");
        let mut out = String::new();
        let mut in_video = false;
        for line in offer.lines() {
            if line.starts_with("m=") {
                in_video = line.starts_with("m=video");
                if in_video {
                    let proto: Vec<&str> = line.split(' ').take(3).collect();
                    out.push_str(&format!("{} {pts}\r\n", proto.join(" ")));
                    for codec_line in lines.lines() {
                        out.push_str(codec_line.trim());
                        out.push_str("\r\n");
                    }
                    continue;
                }
            } else if in_video
                && ["a=rtpmap:", "a=fmtp:", "a=rtcp-fb:"]
                    .iter()
                    .any(|p| line.starts_with(p))
            {
                continue;
            }
            out.push_str(line);
            out.push_str("\r\n");
        }
        out
    }

    /// The payload types of each video section's rtpmap lines.
    fn answered_video_codecs(sdp: &str) -> Vec<Vec<String>> {
        let mut sections = Vec::new();
        let mut in_video = false;
        for line in sdp.lines() {
            if line.starts_with("m=") {
                in_video = line.starts_with("m=video");
                if in_video {
                    sections.push(Vec::new());
                }
            } else if let Some(rtpmap) = line.strip_prefix("a=rtpmap:").filter(|_| in_video) {
                sections.last_mut().unwrap().push(rtpmap.to_owned());
            }
        }
        sections
    }

    fn fmtp_of(sdp: &str, pt: &str) -> String {
        sdp.lines()
            .find_map(|l| l.strip_prefix(&format!("a=fmtp:{pt} ")))
            .unwrap_or_default()
            .to_ascii_lowercase()
    }

    async fn answer_for(codecs: &str, slots: usize) -> (KerbcastPeer, Result<String>) {
        let registry = Arc::new(CameraRegistry::new(PathBuf::from("/tmp")));
        let peer = KerbcastPeer::new(registry, &[], slots).await.expect("peer");
        let browser = build_pc().await;
        browser
            .create_data_channel(CONTROL_CHANNEL_LABEL, None)
            .await
            .expect("control channel");
        let offer = with_codecs(&browser_offer(&browser, slots).await, codecs);
        let answer = peer.answer_to_offer(offer).await;
        (peer, answer)
    }

    /// Each browser's offer is answered with the sent format alone, under
    /// the payload type that browser gave it, in every video section.
    #[tokio::test]
    async fn the_answer_names_only_the_sent_format_under_the_offerers_payload_type() {
        for (browser, codecs, pt) in [
            ("chrome", CHROME_CODECS, "106"),
            ("firefox", FIREFOX_CODECS, "126"),
            ("safari", SAFARI_CODECS, "98"),
        ] {
            let (_peer, answer) = answer_for(codecs, 2).await;
            let answer = answer.unwrap_or_else(|e| panic!("{browser}: {e}"));
            assert_eq!(
                answered_video_codecs(&answer),
                vec![vec![format!("{pt} H264/90000")]; 2],
                "{browser}: {answer}"
            );
            let fmtp = fmtp_of(&answer, pt);
            assert!(
                fmtp.contains("packetization-mode=1") && fmtp.contains("profile-level-id=42e01f"),
                "{browser}: {fmtp}"
            );
            assert!(offers_sent_h264(&answer), "{browser}");
        }
    }

    /// A re-offer gets the same single format as the first answer did.
    #[tokio::test]
    async fn a_re_offer_is_answered_with_the_sent_format_alone() {
        let registry = Arc::new(CameraRegistry::new(PathBuf::from("/tmp")));
        let peer = KerbcastPeer::new(registry, &[], 1).await.expect("peer");
        let browser = build_pc().await;
        browser
            .create_data_channel(CONTROL_CHANNEL_LABEL, None)
            .await
            .expect("control channel");
        let offer = with_codecs(&browser_offer(&browser, 1).await, CHROME_CODECS);
        let answer = peer.answer_to_offer(offer).await.expect("answer");
        browser
            .set_remote_description(RTCSessionDescription::answer(answer).unwrap())
            .await
            .expect("set answer");

        let offer = with_codecs(&browser_offer(&browser, 2).await, CHROME_CODECS);
        let answer = peer.grow(3, offer).await.expect("grow");
        assert_eq!(
            answered_video_codecs(&answer),
            vec![vec!["106 H264/90000".to_owned()]; 3],
            "{answer}"
        );
        assert_eq!(
            fmtp_of(&answer, "106"),
            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"
        );
        let grown = pool_transceivers(&peer).await;
        for t in grown.iter().skip(1).flatten() {
            let codecs = t
                .sender()
                .await
                .get_parameters()
                .await
                .rtp_parameters
                .codecs;
            let pts: Vec<u8> = codecs.iter().map(|c| c.payload_type).collect();
            assert_eq!(
                pts,
                [106],
                "a grown slot sends under the answered type alone"
            );
        }
    }

    #[test]
    fn finds_the_sent_format_as_webrtc_rs_matches_it() {
        let section = |codecs: &str| {
            let (pts, lines) = codecs.split_once('|').unwrap();
            format!(
                "v=0\r\nm=video 9 X {pts}\r\n{}\r\n",
                lines.replace('\n', "\r\n")
            )
        };
        assert!(offers_sent_h264(&section(CHROME_CODECS)));
        assert!(offers_sent_h264(&section(FIREFOX_CODECS)));
        assert!(offers_sent_h264(&section(SAFARI_CODECS)));
        for (why, codecs) in [
            ("vp8 only", "96|a=rtpmap:96 VP8/90000"),
            (
                "mode 0 only",
                "100|a=rtpmap:100 H264/90000\na=fmtp:100 packetization-mode=0;profile-level-id=42e01f",
            ),
            (
                "baseline only",
                "102|a=rtpmap:102 H264/90000\na=fmtp:102 packetization-mode=1;profile-level-id=42001f",
            ),
            (
                "high only",
                "96|a=rtpmap:96 H264/90000\na=fmtp:96 packetization-mode=1;profile-level-id=640c1f",
            ),
            (
                "no profile",
                "96|a=rtpmap:96 H264/90000\na=fmtp:96 packetization-mode=1",
            ),
            (
                "fmtp on another codec",
                "96|a=rtpmap:96 VP8/90000\na=fmtp:96 packetization-mode=1;profile-level-id=42e01f",
            ),
        ] {
            assert!(!offers_sent_h264(&section(codecs)), "{why}");
        }
        /* Payload types are per section: an fmtp in another section does not count. */
        let split = "v=0\r\nm=video 9 X 102\r\na=rtpmap:102 H264/90000\r\nm=video 9 X 102\r\na=rtpmap:102 VP8/90000\r\na=fmtp:102 packetization-mode=1;profile-level-id=42e01f\r\n";
        assert!(!offers_sent_h264(split));
    }

    /// Below the signalling check, an offer with no sent format must not
    /// produce an answer that looks healthy and carries a black feed.
    #[tokio::test]
    async fn an_offer_without_the_sent_format_gets_no_answer() {
        for (why, codecs) in [
            ("vp8 only", "96|a=rtpmap:96 VP8/90000"),
            (
                "mode 0 only",
                "100 101|a=rtpmap:100 H264/90000\na=fmtp:100 packetization-mode=0;profile-level-id=42e01f\n\
                 a=rtpmap:101 H264/90000\na=fmtp:101 packetization-mode=0;profile-level-id=42001f",
            ),
            (
                "baseline only",
                "102|a=rtpmap:102 H264/90000\na=fmtp:102 packetization-mode=1;profile-level-id=42001f",
            ),
        ] {
            let (_peer, answer) = answer_for(codecs, 1).await;
            assert!(answer.is_err(), "{why}: {answer:?}");
        }
        let (_peer, answer) = answer_for(
            "100|a=rtpmap:100 H264/90000\na=fmtp:100 packetization-mode=0;profile-level-id=42e01f",
            1,
        )
        .await;
        let err = answer.unwrap_err().to_string();
        assert!(err.contains("packetization-mode=1"), "{err}");
    }

    #[tokio::test]
    async fn a_re_offer_without_the_sent_format_adds_no_slots() {
        let (peer, browser, _dir) = connected_pair().await;
        let offer = with_codecs(
            &browser_offer(&browser, 1).await,
            "100|a=rtpmap:100 H264/90000\na=fmtp:100 packetization-mode=0;profile-level-id=42e01f",
        );
        assert!(peer.grow(3, offer).await.is_err());
        assert_eq!(peer.slot_count().await, 2);
    }

    /// `offer` with its nth video section's codecs replaced by `specs[n]`, each
    /// in the "<payload types>|<lines>" form `with_codecs` takes.
    fn with_codecs_per_section(offer: &str, specs: &[&str]) -> String {
        let mut out = String::new();
        let mut in_video = false;
        let mut next = specs.iter();
        for line in offer.lines() {
            if line.starts_with("m=") {
                in_video = line.starts_with("m=video");
                if in_video {
                    let (pts, lines) = next
                        .next()
                        .expect("a spec per video section")
                        .split_once('|')
                        .unwrap();
                    let proto: Vec<&str> = line.split(' ').take(3).collect();
                    out.push_str(&format!("{} {pts}\r\n", proto.join(" ")));
                    for codec_line in lines.lines() {
                        out.push_str(codec_line.trim());
                        out.push_str("\r\n");
                    }
                    continue;
                }
            } else if in_video
                && ["a=rtpmap:", "a=fmtp:", "a=rtcp-fb:"]
                    .iter()
                    .any(|p| line.starts_with(p))
            {
                continue;
            }
            out.push_str(line);
            out.push_str("\r\n");
        }
        out
    }

    async fn answer_for_sections(specs: &[&str]) -> Result<String> {
        answer_for_adjusted_sections(specs, |offer| offer).await
    }

    /// Like `answer_for_sections`, with `adjust` applied to the offer last.
    async fn answer_for_adjusted_sections(
        specs: &[&str],
        adjust: impl Fn(String) -> String,
    ) -> Result<String> {
        let registry = Arc::new(CameraRegistry::new(PathBuf::from("/tmp")));
        let peer = KerbcastPeer::new(registry, &[], specs.len())
            .await
            .expect("peer");
        let browser = build_pc().await;
        browser
            .create_data_channel(CONTROL_CHANNEL_LABEL, None)
            .await
            .expect("control channel");
        let offer = with_codecs_per_section(&browser_offer(&browser, specs.len()).await, specs);
        peer.answer_to_offer(adjust(offer)).await
    }

    /// `offer` with its nth video section's port set to 0, and marked
    /// bundle-only when `bundle_only` is set.
    fn with_port_zero(offer: &str, n: usize, bundle_only: bool) -> String {
        let mut out = String::new();
        let mut video = 0;
        for line in offer.lines() {
            if let Some(rest) = line.strip_prefix("m=video ") {
                video += 1;
                if video == n + 1 {
                    let (_, rest) = rest.split_once(' ').unwrap();
                    out.push_str(&format!("m=video 0 {rest}\r\n"));
                    if bundle_only {
                        out.push_str("a=bundle-only\r\n");
                    }
                    continue;
                }
            }
            out.push_str(line);
            out.push_str("\r\n");
        }
        out
    }

    /* webrtc-rs takes a connection's video codecs from the first video section
    alone, so sections that differ are refused up front: a first section
    without the format would fail deep in the exchange, and a later one
    without it would be answered with a payload type it never offered. */
    #[tokio::test]
    async fn an_offer_whose_video_sections_differ_is_refused() {
        const VP8: &str = "96|a=rtpmap:96 VP8/90000";
        const RENUMBERED: &str =
            "108|a=rtpmap:108 H264/90000\na=fmtp:108 packetization-mode=1;profile-level-id=42e01f";
        for (why, specs) in [
            ("first section vp8 only", [VP8, CHROME_CODECS]),
            ("later section vp8 only", [CHROME_CODECS, VP8]),
            (
                "later section under another payload type",
                [CHROME_CODECS, RENUMBERED],
            ),
        ] {
            let answer = answer_for_sections(&specs).await;
            let err = answer.expect_err(why);
            assert!(err.is::<FormatRefused>(), "{why}: {err}");
        }

        /* Port 0 does not take a section out of the reckoning: webrtc-rs reads
        codecs from the first video section whatever its port, and a
        bundle-only section is live on port 0. */
        for (why, specs, n, bundle_only) in [
            (
                "first section on port 0, vp8 only",
                [VP8, CHROME_CODECS],
                0,
                false,
            ),
            (
                "first section bundle-only, vp8 only",
                [VP8, CHROME_CODECS],
                0,
                true,
            ),
            (
                "later section bundle-only, vp8 only",
                [CHROME_CODECS, VP8],
                1,
                true,
            ),
        ] {
            let answer =
                answer_for_adjusted_sections(&specs, |o| with_port_zero(&o, n, bundle_only)).await;
            let err = answer.expect_err(why);
            assert!(err.is::<FormatRefused>(), "{why}: {err}");
        }
    }

    #[test]
    fn port_zero_sections_count_as_webrtc_rs_counts_them() {
        const SENT: &str = "a=rtpmap:106 H264/90000\r\na=fmtp:106 packetization-mode=1;profile-level-id=42e01f\r\n";
        let vp8 = |port: &str, extra: &str| {
            format!("m=video {port} X 106\r\n{extra}a=rtpmap:106 VP8/90000\r\n")
        };
        let sent = |port: &str, extra: &str| format!("m=video {port} X 106\r\n{extra}{SENT}");
        let offer = |sections: &[String]| format!("v=0\r\n{}", sections.concat());

        /* A stopped later section is not answered with media, so it need
        not carry the format. */
        assert!(offers_sent_h264(&offer(&[sent("9", ""), vp8("0", "")])));
        assert!(!offers_sent_h264(&offer(&[
            sent("9", ""),
            vp8("0", "a=bundle-only\r\n")
        ])));
        assert!(offers_sent_h264(&offer(&[
            sent("9", ""),
            sent("0", "a=bundle-only\r\n")
        ])));
        /* The first video section is the codec source even on port 0. */
        assert!(!offers_sent_h264(&offer(&[vp8("0", ""), sent("9", "")])));
        assert_eq!(
            offered_payload_type(&offer(&[sent("0", ""), sent("9", "")])).as_deref(),
            Some("106")
        );
    }

    /// The answer check pairs each answered video section with its offer
    /// section, whatever webrtc-rs produced.
    #[test]
    fn check_answer_refuses_a_section_answered_with_anything_but_one_offered_sent_format() {
        const OFFER_SECTION: &str = "m=video 9 X 106 96\r\n\
            a=rtpmap:106 H264/90000\r\n\
            a=fmtp:106 packetization-mode=1;profile-level-id=42e01f\r\n\
            a=rtpmap:96 VP8/90000\r\n";
        let other_pt = OFFER_SECTION.replace("106", "108");
        let offer = |second: &str| format!("v=0\r\n{OFFER_SECTION}{second}m=application 9 X y\r\n");
        let answer = |first: &str, second: &str| {
            format!("v=0\r\nm=video 9 X {first}\r\nm=video 9 X {second}\r\nm=application 9 X y\r\n")
        };

        check_answer(&offer(OFFER_SECTION), &answer("106", "106")).expect("one sent format");
        for (why, offered, answered) in [
            (
                "two payload types",
                offer(OFFER_SECTION),
                answer("106", "106 96"),
            ),
            (
                "not the sent format",
                offer(OFFER_SECTION),
                answer("106", "96"),
            ),
            ("no payload type", offer(OFFER_SECTION), answer("106", "")),
            (
                "not offered on that section",
                offer(&other_pt),
                answer("106", "106"),
            ),
            (
                "a bundle-only section answered with what it never offered",
                offer("m=video 0 X 96\r\na=bundle-only\r\na=rtpmap:96 VP8/90000\r\n"),
                answer("106", "106"),
            ),
        ] {
            let err = check_answer(&offered, &answered).expect_err(why);
            assert!(err.is::<FormatRefused>(), "{why}: {err}");
        }

        /* A section stopped on both sides carries nothing to check. */
        let stopped = "m=video 0 X 96\r\na=rtpmap:96 VP8/90000\r\n";
        check_answer(
            &offer(stopped),
            "v=0\r\nm=video 9 X 106\r\nm=video 0 X 0\r\nm=application 9 X y\r\n",
        )
        .expect("stopped section");

        let short = check_answer(&offer(OFFER_SECTION), "v=0\r\nm=video 9 X 106\r\n")
            .expect_err("section count");
        assert!(!short.is::<FormatRefused>(), "{short}");
    }

    /// The answer echoes the offerer's own fmtp and feedback for the chosen
    /// payload type, so it never claims a level or feedback the offer lacked.
    #[tokio::test]
    async fn the_answer_echoes_the_offerers_fmtp_and_feedback() {
        let (peer, answer) = answer_for(
            "106|a=rtpmap:106 H264/90000\n\
             a=fmtp:106 packetization-mode=1;profile-level-id=42e00d\n\
             a=rtcp-fb:106 nack\n\
             a=rtcp-fb:106 nack pli",
            2,
        )
        .await;
        let answer = answer.expect("answer");
        assert_eq!(
            fmtp_of(&answer, "106"),
            "packetization-mode=1;profile-level-id=42e00d",
            "{answer}"
        );
        let feedback: Vec<&str> = answer
            .lines()
            .filter_map(|l| l.strip_prefix("a=rtcp-fb:106 "))
            .map(str::trim)
            .collect();
        assert_eq!(
            feedback,
            ["nack", "nack pli", "nack", "nack pli"],
            "{answer}"
        );
        for t in peer.pc.get_transceivers().await {
            let codecs = t
                .sender()
                .await
                .get_parameters()
                .await
                .rtp_parameters
                .codecs;
            assert_eq!(codecs.len(), 1, "{codecs:?}");
            assert_eq!(codecs[0].payload_type, 106);
        }
    }

    /// A re-offer that moves the sent format to a new payload type would leave
    /// running senders on the old one, so it is refused before any slot changes.
    #[tokio::test]
    async fn a_re_offer_that_renumbers_the_sent_format_is_refused() {
        let registry = Arc::new(CameraRegistry::new(PathBuf::from("/tmp")));
        let peer = KerbcastPeer::new(registry, &[], 1).await.expect("peer");
        let browser = build_pc().await;
        browser
            .create_data_channel(CONTROL_CHANNEL_LABEL, None)
            .await
            .expect("control channel");
        let offer = with_codecs(&browser_offer(&browser, 1).await, CHROME_CODECS);
        let answer = peer.answer_to_offer(offer).await.expect("answer");
        browser
            .set_remote_description(RTCSessionDescription::answer(answer).unwrap())
            .await
            .expect("set answer");

        let offer = with_codecs(
            &browser_offer(&browser, 1).await,
            "108|a=rtpmap:108 H264/90000\na=fmtp:108 packetization-mode=1;profile-level-id=42e01f",
        );
        let err = peer.grow(2, offer).await.expect_err("renumbered re-offer");
        assert!(err.is::<FormatRefused>(), "{err}");
        assert_eq!(peer.slot_count().await, 1);
    }

    /// The exact match ignores the level, so constrained baseline offered at
    /// two levels would put both in the answer; only the first is kept.
    #[tokio::test]
    async fn constrained_baseline_at_two_levels_is_answered_with_the_first() {
        let (_peer, answer) = answer_for(
            "106 108|a=rtpmap:106 H264/90000\n\
             a=fmtp:106 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f\n\
             a=rtcp-fb:106 nack\n\
             a=rtpmap:108 H264/90000\n\
             a=fmtp:108 level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e034\n\
             a=rtcp-fb:108 nack",
            2,
        )
        .await;
        let answer = answer.expect("answer");
        assert_eq!(
            answered_video_codecs(&answer),
            vec![vec!["106 H264/90000".to_owned()]; 2],
            "{answer}"
        );
        assert!(
            !answer.contains(":108 "),
            "no trace of the second level: {answer}"
        );
        assert!(answer
            .lines()
            .filter(|l| l.starts_with("m=video"))
            .all(|l| l.ends_with(" 106")));
    }

    /// The slot senders are narrowed with the answer, so the track binds to
    /// the payload type the answer names and to no other.
    #[tokio::test]
    async fn the_slot_senders_send_under_the_answered_payload_type_alone() {
        let (peer, answer) = answer_for(
            "108 106|a=rtpmap:108 H264/90000\n\
             a=fmtp:108 packetization-mode=1;profile-level-id=42e034\n\
             a=rtpmap:106 H264/90000\n\
             a=fmtp:106 packetization-mode=1;profile-level-id=42e01f",
            2,
        )
        .await;
        let answer = answer.expect("answer");
        assert_eq!(
            answered_video_codecs(&answer),
            vec![vec!["108 H264/90000".to_owned()]; 2],
            "{answer}"
        );
        for t in peer.pc.get_transceivers().await {
            let codecs = t
                .sender()
                .await
                .get_parameters()
                .await
                .rtp_parameters
                .codecs;
            let pts: Vec<u8> = codecs.iter().map(|c| c.payload_type).collect();
            assert_eq!(pts, [108], "{codecs:?}");
        }
    }

    /// One video section of a representative Chrome offer, written from the
    /// shape current Chrome produces for a recvonly video transceiver (not a
    /// capture): plain baseline ahead of constrained baseline, both
    /// packetization modes, Main and High, rtx for each, VP9, AV1, red and
    /// ulpfec, with Chrome's header extensions and feedback.
    fn representative_chrome_video_section(mid: u32) -> String {
        let mut section = format!(
            "m=video 9 UDP/TLS/RTP/SAVPF 96 97 102 103 104 105 106 107 108 109 127 125 39 40 98 99 100 101 45 46 112 113 116 117 118\r\n\
             c=IN IP4 0.0.0.0\r\n\
             a=rtcp:9 IN IP4 0.0.0.0\r\n\
             a=ice-ufrag:pZ3k\r\n\
             a=ice-pwd:Qd1rWm8Xv0bTgYk2Hc5nLs7e\r\n\
             a=ice-options:trickle\r\n\
             a=fingerprint:sha-256 FINGERPRINT\r\n\
             a=setup:actpass\r\n\
             a=mid:{mid}\r\n\
             a=extmap:1 urn:ietf:params:rtp-hdrext:toffset\r\n\
             a=extmap:2 http://www.webrtc.org/experiments/rtp-hdrext/abs-send-time\r\n\
             a=extmap:3 urn:3gpp:video-orientation\r\n\
             a=extmap:4 http://www.ietf.org/id/draft-holmer-rmcat-transport-wide-cc-extensions-01\r\n\
             a=extmap:5 http://www.webrtc.org/experiments/rtp-hdrext/playout-delay\r\n\
             a=extmap:6 http://www.webrtc.org/experiments/rtp-hdrext/video-content-type\r\n\
             a=extmap:7 http://www.webrtc.org/experiments/rtp-hdrext/video-timing\r\n\
             a=extmap:8 http://www.webrtc.org/experiments/rtp-hdrext/color-space\r\n\
             a=extmap:9 urn:ietf:params:rtp-hdrext:sdes:mid\r\n\
             a=extmap:10 urn:ietf:params:rtp-hdrext:sdes:rtp-stream-id\r\n\
             a=extmap:11 urn:ietf:params:rtp-hdrext:sdes:repaired-rtp-stream-id\r\n\
             a=recvonly\r\n\
             a=rtcp-mux\r\n\
             a=rtcp-rsize\r\n"
        );
        let codecs = [
            ("96", "VP8/90000", None, true),
            (
                "102",
                "H264/90000",
                Some("level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f"),
                true,
            ),
            (
                "104",
                "H264/90000",
                Some("level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42001f"),
                true,
            ),
            (
                "106",
                "H264/90000",
                Some("level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"),
                true,
            ),
            (
                "108",
                "H264/90000",
                Some("level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42e01f"),
                true,
            ),
            (
                "127",
                "H264/90000",
                Some("level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=4d001f"),
                true,
            ),
            (
                "39",
                "H264/90000",
                Some("level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=4d001f"),
                true,
            ),
            ("98", "VP9/90000", Some("profile-id=0"), true),
            ("100", "VP9/90000", Some("profile-id=2"), true),
            (
                "45",
                "AV1/90000",
                Some("level-idx=5;profile=0;tier=0"),
                true,
            ),
            (
                "112",
                "H264/90000",
                Some("level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f"),
                true,
            ),
            ("116", "red/90000", None, false),
            ("118", "ulpfec/90000", None, false),
        ];
        let rtx = [
            ("97", "96"),
            ("103", "102"),
            ("105", "104"),
            ("107", "106"),
            ("109", "108"),
            ("125", "127"),
            ("40", "39"),
            ("99", "98"),
            ("101", "100"),
            ("46", "45"),
            ("113", "112"),
            ("117", "116"),
        ];
        for (pt, codec, fmtp, feedback) in codecs {
            section.push_str(&format!("a=rtpmap:{pt} {codec}\r\n"));
            if feedback {
                for fb in ["goog-remb", "transport-cc", "ccm fir", "nack", "nack pli"] {
                    section.push_str(&format!("a=rtcp-fb:{pt} {fb}\r\n"));
                }
            }
            if let Some(fmtp) = fmtp {
                section.push_str(&format!("a=fmtp:{pt} {fmtp}\r\n"));
            }
            if let Some((rtx_pt, _)) = rtx.iter().find(|(_, apt)| *apt == pt) {
                section.push_str(&format!(
                    "a=rtpmap:{rtx_pt} rtx/90000\r\na=fmtp:{rtx_pt} apt={pt}\r\n"
                ));
            }
        }
        section
    }

    /// A whole representative Chrome offer: two recvonly video sections and
    /// the control data channel, bundled.
    fn representative_chrome_offer() -> String {
        let mut offer = "v=0\r\n\
             o=- 4611731400430051336 2 IN IP4 127.0.0.1\r\n\
             s=-\r\n\
             t=0 0\r\n\
             a=group:BUNDLE 0 1 2\r\n\
             a=extmap-allow-mixed\r\n\
             a=msid-semantic: WMS\r\n"
            .to_owned();
        offer.push_str(&representative_chrome_video_section(0));
        offer.push_str(&representative_chrome_video_section(1));
        offer.push_str(
            "m=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n\
             c=IN IP4 0.0.0.0\r\n\
             a=ice-ufrag:pZ3k\r\n\
             a=ice-pwd:Qd1rWm8Xv0bTgYk2Hc5nLs7e\r\n\
             a=ice-options:trickle\r\n\
             a=fingerprint:sha-256 FINGERPRINT\r\n\
             a=setup:actpass\r\n\
             a=mid:2\r\n\
             a=sctp-port:5000\r\n\
             a=max-message-size:262144\r\n",
        );
        offer.replace("FINGERPRINT", "0B:30:55:7A:9F:C4:E9:0E:33:58:7D:A2:C7:EC:11:36:5B:80:A5:CA:EF:14:39:5E:83:A8:CD:F2:17:3C:61:86")
    }

    /// A whole offer in Chrome's shape, header extensions, rtx and all, is
    /// answered with constrained baseline mode 1 alone in both video sections.
    #[tokio::test]
    async fn a_representative_chrome_offer_is_answered_with_the_sent_format_alone() {
        let offer = representative_chrome_offer();
        assert!(offers_sent_h264(&offer));
        let registry = Arc::new(CameraRegistry::new(PathBuf::from("/tmp")));
        let peer = KerbcastPeer::new(registry, &[], 2).await.expect("peer");
        let answer = peer.answer_to_offer(offer).await.expect("answer");
        assert_eq!(
            answered_video_codecs(&answer),
            vec![vec!["106 H264/90000".to_owned()]; 2],
            "{answer}"
        );
        let fmtp = fmtp_of(&answer, "106");
        assert!(
            fmtp.contains("packetization-mode=1") && fmtp.contains("profile-level-id=42e01f"),
            "{fmtp}"
        );
    }
}
