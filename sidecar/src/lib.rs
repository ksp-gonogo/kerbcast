//! kerbcast sidecar library — exposes the encoder backend trait, control
//! protocol types, and shared-memory ring buffer used by both `main.rs`
//! (binary entry) and integration tests.
//!
//! See `CLAUDE.md` for the architectural context and the gonogo repo's
//! `local_docs/ocisly_state_and_rebuild.md` §2.3 for the encoder-backend-
//! trait design.

pub mod cameras;
pub mod encoder;
pub mod heartbeat;
pub mod protocol;
pub mod shared_mem;
pub mod signalling;
pub mod webrtc;

/// Crate version exposed for `--version` and the `/health` reporting endpoint.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");
