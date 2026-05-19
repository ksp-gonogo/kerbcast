//! Minimal WebRTC peer for the kerbcam sidecar. One H.264 video track,
//! offer/answer SDP exchange via async API calls (the caller does the
//! actual transport — stdin/stdout for the dev spike, HTTP signalling
//! later). webrtc-rs handles ICE/DTLS/SRTP/RTP packetisation internally.

use std::sync::Arc;
use std::time::Duration;

use anyhow::{anyhow, Result};
use tokio::sync::Notify;
use tracing::{debug, info, warn};

use webrtc::api::media_engine::{MediaEngine, MIME_TYPE_H264};
use webrtc::api::APIBuilder;
use webrtc::ice_transport::ice_server::RTCIceServer;
use webrtc::media::Sample;
use webrtc::peer_connection::configuration::RTCConfiguration;
use webrtc::peer_connection::peer_connection_state::RTCPeerConnectionState;
use webrtc::peer_connection::sdp::session_description::RTCSessionDescription;
use webrtc::peer_connection::RTCPeerConnection;
use webrtc::rtp_transceiver::rtp_codec::RTCRtpCodecCapability;
use webrtc::track::track_local::track_local_static_sample::TrackLocalStaticSample;
use webrtc::track::track_local::TrackLocal;

use crate::encoder::Nal;

pub struct KerbcamPeer {
    pc: Arc<RTCPeerConnection>,
    video_track: Arc<TrackLocalStaticSample>,
    connected: Arc<Notify>,
}

impl KerbcamPeer {
    /// Build a peer with a single H.264 video track and the default Google
    /// STUN server. The caller drives SDP exchange via `create_offer` +
    /// `set_remote_answer`, and pushes frames via `send_h264_nals`.
    pub async fn new() -> Result<Self> {
        let mut media_engine = MediaEngine::default();
        media_engine.register_default_codecs()?;
        let api = APIBuilder::new().with_media_engine(media_engine).build();

        let config = RTCConfiguration {
            ice_servers: vec![RTCIceServer {
                urls: vec!["stun:stun.l.google.com:19302".to_owned()],
                ..Default::default()
            }],
            ..Default::default()
        };

        let pc = Arc::new(api.new_peer_connection(config).await?);

        let video_track = Arc::new(TrackLocalStaticSample::new(
            RTCRtpCodecCapability {
                mime_type: MIME_TYPE_H264.to_owned(),
                ..Default::default()
            },
            "video".to_owned(),
            "kerbcam".to_owned(),
        ));

        let rtp_sender = pc
            .add_track(video_track.clone() as Arc<dyn TrackLocal + Send + Sync>)
            .await?;

        // webrtc-rs requires us to drain the RTCP-feedback stream from the
        // sender, otherwise the receiver-driven mechanisms (NACK, PLI for
        // keyframe requests, REMB for bandwidth estimation) silently break.
        // We don't process the packets yet — just drain to keep the channel
        // flowing. A future iteration wires PLI back to the encoder's
        // request_keyframe() so receivers can recover from packet loss.
        tokio::spawn(async move {
            let mut rtcp_buf = vec![0u8; 1500];
            while let Ok((_, _)) = rtp_sender.read(&mut rtcp_buf).await {}
            debug!("RTCP drain loop exited");
        });

        let connected = Arc::new(Notify::new());
        let connected_for_handler = connected.clone();
        pc.on_peer_connection_state_change(Box::new(move |state: RTCPeerConnectionState| {
            info!(?state, "peer connection state");
            if state == RTCPeerConnectionState::Connected {
                connected_for_handler.notify_waiters();
            }
            Box::pin(async {})
        }));

        Ok(Self {
            pc,
            video_track,
            connected,
        })
    }

    /// Create an SDP offer for this peer, set it as the local description,
    /// and wait for ICE gathering to finish so the returned SDP carries all
    /// candidates (no trickle needed in this dev path). Returns the SDP
    /// string the caller hands to the browser.
    pub async fn create_offer(&self) -> Result<String> {
        let offer = self.pc.create_offer(None).await?;
        self.pc.set_local_description(offer).await?;

        // Wait until ICE gathering completes. With trickle this would emit
        // candidates incrementally; for the manual-SDP spike we bundle them
        // into the offer.
        let mut gather_complete = self.pc.gathering_complete_promise().await;
        let _ = gather_complete.recv().await;

        let local = self
            .pc
            .local_description()
            .await
            .ok_or_else(|| anyhow!("local description missing after set_local_description"))?;
        Ok(local.sdp)
    }

    /// Set the SDP answer received from the browser.
    pub async fn set_remote_answer(&self, sdp: String) -> Result<()> {
        let answer = RTCSessionDescription::answer(sdp)?;
        self.pc.set_remote_description(answer).await?;
        Ok(())
    }

    /// Push one frame's worth of encoded NAL units to the video track. We
    /// concatenate the NALs into a single Annex-B bytestream — webrtc-rs's
    /// H.264 packetiser handles RFC 6184 fragmentation/aggregation from
    /// there.
    pub async fn send_h264_nals(&self, nals: &[Nal], duration: Duration) -> Result<()> {
        if nals.is_empty() {
            return Ok(());
        }
        let total_len: usize = nals.iter().map(|n| n.0.len()).sum();
        let mut combined = Vec::with_capacity(total_len);
        for nal in nals {
            combined.extend_from_slice(&nal.0);
        }
        let sample = Sample {
            data: combined.into(),
            duration,
            ..Default::default()
        };
        self.video_track.write_sample(&sample).await.map_err(|e| {
            warn!(error = %e, "write_sample failed");
            anyhow!("write_sample: {e}")
        })?;
        Ok(())
    }

    /// Block until the peer reaches the Connected state. Useful for the
    /// dev binary's "don't start streaming until the browser is actually
    /// listening" handshake.
    pub async fn wait_connected(&self) {
        self.connected.notified().await;
    }

    /// Tear down the peer cleanly. Idempotent.
    pub async fn close(&self) -> Result<()> {
        self.pc.close().await?;
        Ok(())
    }
}
