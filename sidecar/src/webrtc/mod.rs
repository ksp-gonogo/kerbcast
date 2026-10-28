//! WebRTC peer plumbing — wraps `webrtc-rs` to expose just the surface the
//! sidecar uses: build a peer, add one H.264 video track, exchange SDP,
//! push encoded NAL samples.
//!
//! The transport is RFC 6184 H.264-over-RTP. `VideoTrack` packetises each
//! buffer of Annex-B NAL units with webrtc-rs's own H.264 payloader, keeping
//! hold of the RTP timestamp so every frame's capture UT can be published
//! under it.

pub mod outbox;
pub mod peer;
pub mod video_track;

pub use peer::{resync_after_camera_churn, GrowRefused, KerbcastPeer, MAX_SLOTS_PER_PEER};
