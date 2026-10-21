//! kerbcast sidecar library — exposes the encoder backend trait, control
//! protocol types, and shared-memory ring buffer used by both `main.rs`
//! (binary entry) and integration tests.

pub mod cameras;
pub mod encoder;
pub mod heartbeat;
pub mod protocol;
pub mod rate;
pub mod shared_mem;
pub mod signalling;
pub mod webrtc;

/**
 * Crate version exposed for `--version` and the `/health` reporting endpoint.
 * A non-empty `KERBCAST_VERSION_OVERRIDE` at compile time replaces the Cargo
 * version, so dev builds report their own version string.
 */
pub const VERSION: &str = pick_version(
    option_env!("KERBCAST_VERSION_OVERRIDE"),
    env!("CARGO_PKG_VERSION"),
);

const fn pick_version(over: Option<&'static str>, pkg: &'static str) -> &'static str {
    match over {
        Some(v) if !v.is_empty() => v,
        _ => pkg,
    }
}

#[cfg(test)]
mod version_tests {
    use super::*;

    #[test]
    fn override_wins_only_when_non_empty() {
        assert_eq!(
            pick_version(Some("9.9.0-dev.abc"), "1.0.0"),
            "9.9.0-dev.abc"
        );
        assert_eq!(pick_version(Some(""), "1.0.0"), "1.0.0");
        assert_eq!(pick_version(None, "1.0.0"), "1.0.0");
    }

    #[test]
    fn version_matches_compile_time_env() {
        match option_env!("KERBCAST_VERSION_OVERRIDE") {
            Some(v) if !v.is_empty() => assert_eq!(VERSION, v),
            _ => assert_eq!(VERSION, env!("CARGO_PKG_VERSION")),
        }
    }
}
