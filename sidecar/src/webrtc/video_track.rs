//! One slot's outbound H.264 track, packetised here rather than inside
//! webrtc-rs so the sidecar knows the RTP timestamp every frame goes out
//! with. `TrackLocalStaticSample` starts its timestamp at a private random
//! value, which makes the timestamp a browser reports for a presented frame
//! (`requestVideoFrameCallback` metadata `rtpTimestamp`) impossible to match
//! back to the frame. Owning the packetizer lets each frame's mission-time
//! stamp be published under the exact RTP timestamp it carries.

use std::sync::Arc;
use std::time::Duration;

use tokio::sync::Mutex;
use webrtc::rtp::codecs::h264::H264Payloader;
use webrtc::rtp::packetizer::{new_packetizer, Packetizer};
use webrtc::rtp::sequence::new_random_sequencer;
use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;
use webrtc::track::track_local::track_local_static_rtp::TrackLocalStaticRTP;

use super::outbox::Outbox;
use crate::protocol::FrameStampPayload;
use crate::shared_mem::CaptureStamp;

/// RTP clock for video, fixed by RFC 6184 for H.264.
pub const H264_CLOCK_RATE: u32 = 90_000;

/// Same outbound MTU webrtc-rs uses for its own sample tracks.
const RTP_OUTBOUND_MTU: usize = 1200;

pub struct VideoTrack {
    rtp: Arc<TrackLocalStaticRTP>,
    packetizer: Mutex<Box<dyn Packetizer + Send + Sync>>,
    /// Where frame stamps for this track go. `None` sends none (tests).
    stamps: Option<Outbox>,
}

impl VideoTrack {
    pub fn new(
        codec: RTCRtpCodecCapability,
        id: String,
        stream_id: String,
        stamps: Option<Outbox>,
    ) -> Self {
        let packetizer = new_packetizer(
            RTP_OUTBOUND_MTU,
            0, // payload type and SSRC are set per binding at write time
            0,
            Box::new(H264Payloader::default()),
            Box::new(new_random_sequencer()),
            H264_CLOCK_RATE,
        );
        Self {
            rtp: Arc::new(TrackLocalStaticRTP::new(codec, id, stream_id)),
            packetizer: Mutex::new(Box::new(packetizer)),
            stamps,
        }
    }

    /// The webrtc-rs track to `add_track` onto the peer connection.
    pub fn rtp_track(&self) -> Arc<TrackLocalStaticRTP> {
        self.rtp.clone()
    }

    /**
     * Packetise and send one encoded frame (Annex-B NALs) lasting `duration`.
     * When `capture` carries a UT, its stamp is queued for the control channel
     * before the frame's packets are written. Never waits on the data channel.
     * Returns the frame's RTP timestamp, or `None` when nothing was sent.
     */
    pub async fn write_frame(
        &self,
        data: &[u8],
        duration: Duration,
        flight_id: u32,
        capture: CaptureStamp,
    ) -> webrtc::error::Result<Option<u32>> {
        /* Unbound or paused everywhere (`all` is true with no bindings): send
        nothing and leave the sequence untouched, matching
        TrackLocalStaticSample. */
        if self.rtp.all_binding_paused().await {
            return Ok(None);
        }
        let packets = self.packetize(data, duration).await?;
        let Some(first) = packets.first() else {
            return Ok(None);
        };
        let rtp_timestamp = first.header.timestamp;

        if let (Some(ut), Some(stamps)) = (capture.ut, &self.stamps) {
            stamps.offer_stamp(FrameStampPayload {
                flight_id,
                rtp_timestamp,
                capture_ut: ut,
                capture_epoch: capture.epoch,
            });
        }

        for p in &packets {
            self.rtp.write_rtp_with_extensions(p, &[]).await?;
        }
        Ok(Some(rtp_timestamp))
    }

    /// Every packet of one frame shares the timestamp; the next frame's is
    /// this one's plus `duration` in RTP ticks.
    async fn packetize(
        &self,
        data: &[u8],
        duration: Duration,
    ) -> webrtc::error::Result<Vec<webrtc::rtp::packet::Packet>> {
        let mut packetizer = self.packetizer.lock().await;
        Ok(packetizer.packetize(&data.to_vec().into(), rtp_samples(duration))?)
    }
}

/// RTP clock ticks a frame of `duration` spans.
fn rtp_samples(duration: Duration) -> u32 {
    (duration.as_secs_f64() * H264_CLOCK_RATE as f64) as u32
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::ServerMessage;
    use crate::webrtc::outbox::ControlChannel;
    use webrtc::api::media_engine::MIME_TYPE_H264;

    fn track() -> VideoTrack {
        VideoTrack::new(
            RTCRtpCodecCapability {
                mime_type: MIME_TYPE_H264.to_owned(),
                ..Default::default()
            },
            "video".to_owned(),
            "stream".to_owned(),
            None,
        )
    }

    /// One IDR-ish NAL: start code + a type-5 header byte + payload.
    fn nal(len: usize) -> Vec<u8> {
        let mut v = vec![0, 0, 0, 1, 0x65];
        v.resize(len, 0xAB);
        v
    }

    async fn frame_timestamps(t: &VideoTrack, data: &[u8], ms: u64) -> Vec<u32> {
        t.packetize(data, Duration::from_millis(ms))
            .await
            .unwrap()
            .iter()
            .map(|p| p.header.timestamp)
            .collect()
    }

    #[tokio::test]
    async fn timestamps_advance_by_each_frames_duration() {
        let t = track();
        let a = frame_timestamps(&t, &nal(64), 33).await;
        let b = frame_timestamps(&t, &nal(64), 50).await;
        let c = frame_timestamps(&t, &nal(64), 33).await;
        assert_eq!(
            b[0].wrapping_sub(a[0]),
            rtp_samples(Duration::from_millis(33))
        );
        assert_eq!(
            c[0].wrapping_sub(b[0]),
            rtp_samples(Duration::from_millis(50))
        );
    }

    #[tokio::test]
    async fn fragmented_frame_shares_one_timestamp_across_packets() {
        let t = track();
        let ts = frame_timestamps(&t, &nal(5000), 33).await;
        assert!(ts.len() > 1, "a 5 kB frame spans several FU-A packets");
        assert!(ts.iter().all(|&x| x == ts[0]));
    }

    #[tokio::test]
    async fn unbound_track_sends_nothing() {
        let t = track();
        let stamp = CaptureStamp {
            ut: Some(1.0),
            epoch: 0,
        };
        let got = t
            .write_frame(&nal(64), Duration::from_millis(33), 1, stamp)
            .await
            .unwrap();
        assert_eq!(got, None);
    }

    /**
     * End to end over a real loopback connection: the stamp a viewer receives
     * on the control channel names the RTP timestamp the frame's packets
     * actually arrive with.
     */
    #[tokio::test]
    async fn stamp_names_the_rtp_timestamp_the_viewer_receives() {
        let (sent, stamps, rtp_seen) = loopback_stamps(true).await;
        let matched: Vec<_> = stamps
            .iter()
            .filter(|(ts, _)| rtp_seen.contains(ts))
            .collect();
        assert!(
            !matched.is_empty(),
            "a stamped frame arrived: stamps={stamps:?} rtp={rtp_seen:?}"
        );
        for (ts, ut) in &matched {
            assert!(
                sent.contains(&(*ts, *ut)),
                "stamp ({ts}, {ut}) pairs the UT with the timestamp that frame was sent under"
            );
        }
    }

    /// A viewer that never asks for stamps (any SDK before 1.11) gets its
    /// frames and no stamps.
    #[tokio::test]
    async fn a_viewer_that_never_asks_gets_no_stamps() {
        let (_, stamps, rtp_seen) = loopback_stamps(false).await;
        assert!(!rtp_seen.is_empty(), "frames still arrive");
        assert!(stamps.is_empty(), "no stamps sent: {stamps:?}");
    }

    /**
     * Send stamped frames over a real loopback connection whose viewer has
     * (or has not) asked for stamps. Returns what was sent as (RTP timestamp,
     * UT), the stamps the viewer received, and the RTP timestamps it saw.
     */
    #[allow(clippy::type_complexity)]
    async fn loopback_stamps(wanted: bool) -> (Vec<(u32, f64)>, Vec<(u32, f64)>, Vec<u32>) {
        use tokio::sync::mpsc;
        use webrtc::api::media_engine::MediaEngine;
        use webrtc::api::APIBuilder;
        use webrtc::data_channel::data_channel_message::DataChannelMessage;
        use webrtc::peer_connection::configuration::RTCConfiguration;
        use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
        use webrtc::rtp_transceiver::rtp_codec::RTPCodecType;
        use webrtc::track::track_local::TrackLocal;

        async fn pc() -> Arc<webrtc::peer_connection::RTCPeerConnection> {
            let mut me = MediaEngine::default();
            me.register_default_codecs().unwrap();
            let api = APIBuilder::new().with_media_engine(me).build();
            Arc::new(
                api.new_peer_connection(RTCConfiguration::default())
                    .await
                    .unwrap(),
            )
        }

        #[derive(Debug)]
        enum Seen {
            Stamp(u32, f64),
            Rtp(u32),
        }
        let (tx, mut rx) = mpsc::unbounded_channel::<Seen>();

        /* Viewer: recv-only video plus the control channel, like the SDK. */
        let viewer = pc().await;
        viewer
            .add_transceiver_from_kind(RTPCodecType::Video, None)
            .await
            .unwrap();
        let viewer_dc = viewer
            .create_data_channel("kerbcast-control", None)
            .await
            .unwrap();
        let tx_dc = tx.clone();
        viewer_dc.on_message(Box::new(move |m: DataChannelMessage| {
            let tx = tx_dc.clone();
            Box::pin(async move {
                let msg: ServerMessage = serde_json::from_slice(&m.data).unwrap();
                if let ServerMessage::FrameStamp(p) = msg {
                    let _ = tx.send(Seen::Stamp(p.rtp_timestamp, p.capture_ut));
                }
            })
        }));
        let tx_rtp = tx.clone();
        viewer.on_track(Box::new(move |track, _, _| {
            let tx = tx_rtp.clone();
            Box::pin(async move {
                tokio::spawn(async move {
                    while let Ok((pkt, _)) = track.read_rtp().await {
                        let _ = tx.send(Seen::Rtp(pkt.header.timestamp));
                    }
                });
            })
        }));

        /* Sidecar side: one VideoTrack whose stamps go to the opened channel. */
        let sidecar = pc().await;
        let control: ControlChannel = Arc::new(Mutex::new(None));
        let vt = Arc::new(VideoTrack::new(
            RTCRtpCodecCapability {
                mime_type: webrtc::api::media_engine::MIME_TYPE_H264.to_owned(),
                sdp_fmtp_line:
                    "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"
                        .to_owned(),
                ..Default::default()
            },
            "video".to_owned(),
            "stream".to_owned(),
            Some({
                let outbox = Outbox::spawn(control.clone());
                outbox.set_stamps_wanted(wanted);
                outbox
            }),
        ));
        sidecar
            .add_track(vt.rtp_track() as Arc<dyn TrackLocal + Send + Sync>)
            .await
            .unwrap();
        let control_for_dc = control.clone();
        sidecar.on_data_channel(Box::new(move |dc| {
            let control = control_for_dc.clone();
            Box::pin(async move {
                let ready = dc.clone();
                dc.on_open(Box::new(move || {
                    Box::pin(async move {
                        *control.lock().await = Some(ready);
                    })
                }));
            })
        }));
        let (conn_tx, mut conn_rx) = mpsc::unbounded_channel();
        sidecar.on_peer_connection_state_change(Box::new(move |s| {
            if s == RTCPeerConnectionState::Connected {
                let _ = conn_tx.send(());
            }
            Box::pin(async {})
        }));

        let offer = viewer.create_offer(None).await.unwrap();
        let mut gather = viewer.gathering_complete_promise().await;
        viewer.set_local_description(offer).await.unwrap();
        let _ = gather.recv().await;
        sidecar
            .set_remote_description(viewer.local_description().await.unwrap())
            .await
            .unwrap();
        let answer = sidecar.create_answer(None).await.unwrap();
        let mut gather = sidecar.gathering_complete_promise().await;
        sidecar.set_local_description(answer).await.unwrap();
        let _ = gather.recv().await;
        viewer
            .set_remote_description(sidecar.local_description().await.unwrap())
            .await
            .unwrap();

        tokio::time::timeout(Duration::from_secs(10), conn_rx.recv())
            .await
            .expect("loopback connects");
        for _ in 0..100 {
            if control.lock().await.is_some() {
                break;
            }
            tokio::time::sleep(Duration::from_millis(20)).await;
        }
        assert!(control.lock().await.is_some(), "control channel opened");

        /* Keep sending until a stamp and its frame have both been seen. */
        let mut sent = Vec::new();
        let mut stamps = Vec::new();
        let mut rtp_seen = Vec::new();
        let deadline = tokio::time::Instant::now() + Duration::from_secs(10);
        let mut n = 0u32;
        while tokio::time::Instant::now() < deadline {
            let ut = 1000.0 + n as f64;
            let stamp = CaptureStamp {
                ut: Some(ut),
                epoch: 2,
            };
            if let Some(ts) = vt
                .write_frame(&nal(64), Duration::from_millis(33), 9, stamp)
                .await
                .unwrap()
            {
                sent.push((ts, ut));
            }
            n += 1;
            tokio::time::sleep(Duration::from_millis(33)).await;
            while let Ok(seen) = rx.try_recv() {
                match seen {
                    Seen::Stamp(ts, ut) => stamps.push((ts, ut)),
                    Seen::Rtp(ts) => rtp_seen.push(ts),
                }
            }
            let done = if wanted {
                stamps.iter().any(|(ts, _)| rtp_seen.contains(ts))
            } else {
                /* Long enough for a stamp to have overtaken its frame. */
                rtp_seen.len() >= 10
            };
            if done {
                break;
            }
        }

        viewer.close().await.ok();
        sidecar.close().await.ok();
        (sent, stamps, rtp_seen)
    }

    mod churn {
        use super::*;
        use std::net::IpAddr;
        use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
        use webrtc::api::media_engine::MediaEngine;
        use webrtc::api::setting_engine::SettingEngine;
        use webrtc::api::APIBuilder;
        use webrtc::data_channel::data_channel_message::DataChannelMessage;
        use webrtc::data_channel::data_channel_state::RTCDataChannelState;
        use webrtc::data_channel::RTCDataChannel;
        use webrtc::peer_connection::configuration::RTCConfiguration;
        use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
        use webrtc::peer_connection::RTCPeerConnection;
        use webrtc::rtp_transceiver::rtp_codec::RTPCodecType;
        use webrtc::track::track_local::TrackLocal;
        use webrtc::util::vnet::chunk::Chunk;
        use webrtc::util::vnet::net::{Net, NetConfig};
        use webrtc::util::vnet::router::{Router, RouterConfig};

        const TRACKS_PER_VIEWER: usize = 3;
        const FRAME: Duration = Duration::from_millis(33);
        /// 30 s of 30 fps video.
        const FRAMES_PER_PHASE: u32 = 900;

        /// What one viewer has received so far.
        #[derive(Default)]
        struct Seen {
            rtp_packets: AtomicUsize,
            stamps: AtomicUsize,
            rtp_timestamps: Mutex<Vec<u32>>,
            stamp_pairs: Mutex<Vec<(u32, f64)>>,
            /// Sequence numbers of the stand-in status messages, in arrival order.
            status: Mutex<Vec<u64>>,
        }

        struct Viewer {
            pc: Arc<RTCPeerConnection>,
            dc: Arc<RTCDataChannel>,
            seen: Arc<Seen>,
        }

        /// The sidecar's half of one browser connection.
        struct SidecarPeer {
            pc: Arc<RTCPeerConnection>,
            tracks: Vec<Arc<VideoTrack>>,
            outbox: Outbox,
        }

        impl SidecarPeer {
            /// What the reaper does to a dead peer.
            async fn reap(&self) {
                self.outbox.shutdown();
                self.pc.close().await.ok();
            }
        }

        /// Padding that brings a status message near a real camera-state diff.
        const STATUS_PAD: usize = 400;

        /// A stand-in for a state broadcast, numbered so loss or reordering shows.
        fn status_message(seq: u64) -> ServerMessage {
            ServerMessage::Error(crate::protocol::ErrorPayload {
                message: format!("status {seq} {}", "x".repeat(STATUS_PAD)),
                ..Default::default()
            })
        }

        fn status_seq(msg: &ServerMessage) -> Option<u64> {
            let ServerMessage::Error(e) = msg else {
                return None;
            };
            e.message
                .strip_prefix("status ")?
                .split(' ')
                .next()?
                .parse()
                .ok()
        }

        async fn net_on(router: &Arc<Mutex<Router>>, ip: &str) -> Arc<Net> {
            let net = Arc::new(Net::new(Some(NetConfig {
                static_ips: vec![ip.to_owned()],
                ..Default::default()
            })));
            let nic = net.get_nic().unwrap();
            router.lock().await.add_net(nic.clone()).await.unwrap();
            nic.lock().await.set_router(router.clone()).await.unwrap();
            net
        }

        async fn pc_on(net: Arc<Net>) -> Arc<RTCPeerConnection> {
            let mut me = MediaEngine::default();
            me.register_default_codecs().unwrap();
            let mut se = SettingEngine::default();
            se.set_vnet(Some(net));
            Arc::new(
                APIBuilder::new()
                    .with_media_engine(me)
                    .with_setting_engine(se)
                    .build()
                    .new_peer_connection(RTCConfiguration::default())
                    .await
                    .unwrap(),
            )
        }

        /**
         * One viewer (recv-only video transceivers plus the control channel,
         * like the SDK) connected to a fresh sidecar peer whose tracks stamp
         * onto that channel, as `KerbcastPeer::new` wires them.
         */
        async fn connect(viewer_net: Arc<Net>, sidecar_net: Arc<Net>) -> (Viewer, SidecarPeer) {
            let seen = Arc::new(Seen::default());
            let viewer = pc_on(viewer_net).await;
            for _ in 0..TRACKS_PER_VIEWER {
                viewer
                    .add_transceiver_from_kind(RTPCodecType::Video, None)
                    .await
                    .unwrap();
            }
            let dc = viewer
                .create_data_channel("kerbcast-control", None)
                .await
                .unwrap();
            let seen_dc = seen.clone();
            dc.on_message(Box::new(move |m: DataChannelMessage| {
                let seen = seen_dc.clone();
                Box::pin(async move {
                    match serde_json::from_slice::<ServerMessage>(&m.data) {
                        Ok(ServerMessage::FrameStamp(p)) => {
                            seen.stamps.fetch_add(1, Ordering::SeqCst);
                            seen.stamp_pairs
                                .lock()
                                .await
                                .push((p.rtp_timestamp, p.capture_ut));
                        }
                        Ok(msg) => {
                            if let Some(seq) = status_seq(&msg) {
                                seen.status.lock().await.push(seq);
                            }
                        }
                        Err(_) => {}
                    }
                })
            }));
            let seen_rtp = seen.clone();
            viewer.on_track(Box::new(move |track, _, _| {
                let seen = seen_rtp.clone();
                Box::pin(async move {
                    tokio::spawn(async move {
                        while let Ok((pkt, _)) = track.read_rtp().await {
                            seen.rtp_packets.fetch_add(1, Ordering::SeqCst);
                            seen.rtp_timestamps.lock().await.push(pkt.header.timestamp);
                        }
                    });
                })
            }));

            let sidecar = pc_on(sidecar_net).await;
            let control: ControlChannel = Arc::new(Mutex::new(None));
            let outbox = Outbox::spawn(control.clone());
            outbox.set_stamps_wanted(true);
            let mut tracks = Vec::new();
            for i in 0..TRACKS_PER_VIEWER {
                let vt = Arc::new(VideoTrack::new(
                    RTCRtpCodecCapability {
                        mime_type: webrtc::api::media_engine::MIME_TYPE_H264.to_owned(),
                        sdp_fmtp_line:
                            "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"
                                .to_owned(),
                        ..Default::default()
                    },
                    format!("video-slot{i}"),
                    format!("kerbcast-slot{i}"),
                    Some(outbox.clone()),
                ));
                sidecar
                    .add_track(vt.rtp_track() as Arc<dyn TrackLocal + Send + Sync>)
                    .await
                    .unwrap();
                tracks.push(vt);
            }
            let control_for_dc = control.clone();
            sidecar.on_data_channel(Box::new(move |dc| {
                let control = control_for_dc.clone();
                Box::pin(async move {
                    let ready = dc.clone();
                    dc.on_open(Box::new(move || {
                        Box::pin(async move {
                            *control.lock().await = Some(ready);
                        })
                    }));
                })
            }));
            let (conn_tx, mut conn_rx) = tokio::sync::mpsc::unbounded_channel();
            sidecar.on_peer_connection_state_change(Box::new(move |s| {
                if s == RTCPeerConnectionState::Connected {
                    let _ = conn_tx.send(());
                }
                Box::pin(async {})
            }));

            let offer = viewer.create_offer(None).await.unwrap();
            let mut gather = viewer.gathering_complete_promise().await;
            viewer.set_local_description(offer).await.unwrap();
            let _ = gather.recv().await;
            sidecar
                .set_remote_description(viewer.local_description().await.unwrap())
                .await
                .unwrap();
            let answer = sidecar.create_answer(None).await.unwrap();
            let mut gather = sidecar.gathering_complete_promise().await;
            sidecar.set_local_description(answer).await.unwrap();
            let _ = gather.recv().await;
            viewer
                .set_remote_description(sidecar.local_description().await.unwrap())
                .await
                .unwrap();

            tokio::time::timeout(Duration::from_secs(10), conn_rx.recv())
                .await
                .expect("viewer connects");
            for _ in 0..250 {
                if control.lock().await.is_some() {
                    break;
                }
                tokio::time::sleep(Duration::from_millis(20)).await;
            }
            assert!(control.lock().await.is_some(), "control channel opened");

            (
                Viewer {
                    pc: viewer,
                    dc,
                    seen,
                },
                SidecarPeer {
                    pc: sidecar,
                    tracks,
                    outbox,
                },
            )
        }

        /**
         * Drive `frames` frames through every live track the way the consume
         * loop's fan-out does: one task, each track in turn, plus
         * `status_per_tick` state broadcasts to every peer numbered from
         * `*status_seq`. Anything that waits on a viewer stalls every viewer,
         * so each tick is bounded.
         */
        async fn fan_out(
            peers: &[&SidecarPeer],
            frames: u32,
            ut_base: f64,
            status_per_tick: usize,
            status_seq: &mut u64,
        ) {
            for n in 0..frames {
                let stamp = CaptureStamp {
                    ut: Some(ut_base + n as f64),
                    epoch: 1,
                };
                let first_status = *status_seq;
                *status_seq += status_per_tick as u64;
                let tick = async {
                    for p in peers {
                        for seq in first_status..first_status + status_per_tick as u64 {
                            p.outbox.send(&status_message(seq));
                        }
                    }
                    for t in peers.iter().flat_map(|p| p.tracks.iter()) {
                        let _ = t.write_frame(&nal(64), FRAME, 9, stamp).await;
                    }
                };
                tokio::time::timeout(Duration::from_secs(2), tick)
                    .await
                    .unwrap_or_else(|_| panic!("fan-out stalled at frame {n} of {frames}"));
                tokio::time::sleep(Duration::from_millis(2)).await;
            }
        }

        /// Wait for in-flight packets and stamps to land, then assert a viewer
        /// got video plus correctly paired stamps since `before`.
        async fn assert_receiving(name: &str, v: &Viewer, before: (usize, usize)) {
            tokio::time::sleep(Duration::from_millis(500)).await;
            let rtp = v.seen.rtp_packets.load(Ordering::SeqCst) - before.0;
            let stamps = v.seen.stamps.load(Ordering::SeqCst) - before.1;
            let want = (FRAMES_PER_PHASE as usize * TRACKS_PER_VIEWER) * 9 / 10;
            assert!(rtp >= want, "{name}: {rtp} RTP packets, want >= {want}");
            assert!(stamps >= want, "{name}: {stamps} stamps, want >= {want}");
            assert_eq!(
                v.dc.ready_state(),
                RTCDataChannelState::Open,
                "{name}: control channel still open"
            );
            let rtp_ts = v.seen.rtp_timestamps.lock().await;
            let pairs = v.seen.stamp_pairs.lock().await;
            let matched = pairs.iter().filter(|(ts, _)| rtp_ts.contains(ts)).count();
            assert!(
                matched >= want,
                "{name}: {matched} stamps name a received RTP timestamp"
            );
        }

        /// Every status message sent in `range` arrived, once and in order.
        async fn assert_status_intact(name: &str, v: &Viewer, range: std::ops::Range<u64>) {
            tokio::time::sleep(Duration::from_millis(500)).await;
            let got: Vec<u64> = v
                .seen
                .status
                .lock()
                .await
                .iter()
                .copied()
                .filter(|s| range.contains(s))
                .collect();
            let want: Vec<u64> = range.collect();
            assert!(
                got == want,
                "{name}: status messages lost or reordered: got {} of {}, first gap after {:?}",
                got.len(),
                want.len(),
                got.iter().zip(&want).find(|(g, w)| g != w).map(|(g, _)| g),
            );
        }

        fn counts(v: &Viewer) -> (usize, usize) {
            (
                v.seen.rtp_packets.load(Ordering::SeqCst),
                v.seen.stamps.load(Ordering::SeqCst),
            )
        }

        /// Three viewers on one virtual LAN with the sidecar; the first can be
        /// cut off without closing.
        struct Lan {
            router: Arc<Mutex<Router>>,
            sidecar: Arc<Net>,
            viewers: [Arc<Net>; 3],
            blackhole: Arc<AtomicBool>,
        }

        async fn lan() -> Lan {
            let router = Arc::new(Mutex::new(
                Router::new(RouterConfig {
                    cidr: "10.0.0.0/24".to_owned(),
                    ..Default::default()
                })
                .unwrap(),
            ));
            let sidecar = net_on(&router, "10.0.0.1").await;
            let viewers = [
                net_on(&router, "10.0.0.11").await,
                net_on(&router, "10.0.0.12").await,
                net_on(&router, "10.0.0.13").await,
            ];
            let vanished: IpAddr = "10.0.0.11".parse().unwrap();
            let blackhole = Arc::new(AtomicBool::new(false));
            let bh = blackhole.clone();
            router
                .lock()
                .await
                .add_chunk_filter(Box::new(move |c: &(dyn Chunk + Send + Sync)| {
                    !(bh.load(Ordering::SeqCst) && c.get_source_ip() == vanished)
                }))
                .await;
            router.lock().await.start().await.unwrap();
            Lan {
                router,
                sidecar,
                viewers,
                blackhole,
            }
        }

        /**
         * A viewer that vanishes without closing (a reloaded page, a dropped
         * Wi-Fi link) leaves its sidecar peer looking alive until ICE gives
         * up. Frames and stamps keep flowing to it meanwhile, and none of
         * that may hold up the viewers that replace it: every later viewer
         * keeps receiving video and stamps through 30 s of frames.
         */
        #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
        async fn later_viewers_keep_receiving_after_a_viewer_vanishes() {
            let lan = lan().await;
            let mut seq = 0;

            let (a, a_side) = connect(lan.viewers[0].clone(), lan.sidecar.clone()).await;
            let before = counts(&a);
            fan_out(&[&a_side], FRAMES_PER_PHASE, 0.0, 0, &mut seq).await;
            assert_receiving("first viewer", &a, before).await;

            /* A vanishes. Its sidecar peer is not reaped yet. */
            lan.blackhole.store(true, Ordering::SeqCst);
            let (b, b_side) = connect(lan.viewers[1].clone(), lan.sidecar.clone()).await;
            let before_b = counts(&b);
            fan_out(&[&a_side, &b_side], FRAMES_PER_PHASE, 10_000.0, 0, &mut seq).await;
            assert_receiving("second viewer", &b, before_b).await;

            /* The reaper finally closes A; a third viewer joins B. */
            a_side.reap().await;
            let (c, c_side) = connect(lan.viewers[2].clone(), lan.sidecar.clone()).await;
            let (before_b, before_c) = (counts(&b), counts(&c));
            fan_out(&[&b_side, &c_side], FRAMES_PER_PHASE, 20_000.0, 0, &mut seq).await;
            assert_receiving("second viewer, later", &b, before_b).await;
            assert_receiving("third viewer", &c, before_c).await;

            for pc in [&a.pc, &b.pc, &c.pc, &b_side.pc, &c_side.pc] {
                pc.close().await.ok();
            }
            lan.router.lock().await.stop().await.ok();
        }

        /**
         * The same vanished viewer while every tick also broadcasts state to
         * every peer. The fan-out never waits; the vanished peer's backlog
         * marks it dead for the reaper instead of silently dropping state;
         * live viewers get every broadcast once and in order, with video and
         * stamps still flowing.
         */
        #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
        async fn a_vanished_viewer_under_heavy_status_traffic_is_reaped_not_waited_on() {
            const STATUS_PER_TICK: usize = 2;
            let lan = lan().await;
            let mut seq = 0;

            let (a, a_side) = connect(lan.viewers[0].clone(), lan.sidecar.clone()).await;
            let (b, b_side) = connect(lan.viewers[1].clone(), lan.sidecar.clone()).await;
            let start = seq;
            let (before_a, before_b) = (counts(&a), counts(&b));
            fan_out(
                &[&a_side, &b_side],
                FRAMES_PER_PHASE,
                0.0,
                STATUS_PER_TICK,
                &mut seq,
            )
            .await;
            assert_receiving("viewer A, before it vanishes", &a, before_a).await;
            assert_receiving("viewer B", &b, before_b).await;
            assert_status_intact("viewer A", &a, start..seq).await;
            assert_status_intact("viewer B", &b, start..seq).await;
            assert!(!a_side.outbox.is_dead() && !b_side.outbox.is_dead());

            lan.blackhole.store(true, Ordering::SeqCst);
            let start = seq;
            let before_b = counts(&b);
            fan_out(
                &[&a_side, &b_side],
                FRAMES_PER_PHASE,
                10_000.0,
                STATUS_PER_TICK,
                &mut seq,
            )
            .await;
            assert!(
                a_side.outbox.is_dead(),
                "a peer that stopped taking state broadcasts is marked dead"
            );
            assert!(!b_side.outbox.is_dead(), "a live peer is never marked dead");
            assert_receiving("viewer B, alongside the vanished one", &b, before_b).await;
            assert_status_intact("viewer B, alongside the vanished one", &b, start..seq).await;

            a_side.reap().await;
            let (c, c_side) = connect(lan.viewers[2].clone(), lan.sidecar.clone()).await;
            let start = seq;
            let (before_b, before_c) = (counts(&b), counts(&c));
            fan_out(
                &[&b_side, &c_side],
                FRAMES_PER_PHASE,
                20_000.0,
                STATUS_PER_TICK,
                &mut seq,
            )
            .await;
            assert_receiving("viewer B, after the reap", &b, before_b).await;
            assert_receiving("viewer C", &c, before_c).await;
            assert_status_intact("viewer B, after the reap", &b, start..seq).await;
            assert_status_intact("viewer C", &c, start..seq).await;
            assert!(!b_side.outbox.is_dead() && !c_side.outbox.is_dead());

            for pc in [&a.pc, &b.pc, &c.pc, &b_side.pc, &c_side.pc] {
                pc.close().await.ok();
            }
            lan.router.lock().await.stop().await.ok();
        }
    }
}
