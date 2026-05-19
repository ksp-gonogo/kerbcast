//! WebRTC peer plumbing — wraps `webrtc-rs` to expose just the surface the
//! sidecar uses: build a peer, add one H.264 video track, exchange SDP,
//! push encoded NAL samples.
//!
//! The transport is RFC 6184 H.264-over-RTP. webrtc-rs handles the
//! packetisation internally when we use `TrackLocalStaticSample` with
//! `MIME_TYPE_H264`; we just hand it a buffer of Annex-B NAL units plus a
//! presentation duration and the crate fragments / aggregates as needed.

pub mod peer;

pub use peer::KerbcamPeer;
