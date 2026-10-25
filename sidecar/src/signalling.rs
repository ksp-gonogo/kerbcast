//! HTTP signalling endpoint. Two endpoints + the bundled web UI:
//!
//! - `GET /cameras` returns a JSON list of currently-attached cameras
//!   (with `part_title`, `vessel_name`, etc); the browser fetches this
//!   to populate its picker before opening a peer connection.
//! - `POST /offer` takes `{ sdp, cameras: [flight_id, ...] }`, creates a
//!   `KerbcastPeer` with one video track per selected camera AND a
//!   "kerbcast-control" data channel, answers the SDP, and returns the
//!   answer. Unknown camera IDs are dropped with a warning rather than
//!   failing the whole request. An offer whose video has no H.264 is
//!   refused with 422 and an `ErrorPayload` body (`code: codec-unsupported`).
//! - `GET /` serves the bundled kerbcast web UI (web/dist/index.html,
//!   embedded at compile time via include_str!).
//! - `GET /assets/mediabunny.min.mjs` serves the Mediabunny remux-trim
//!   package the web page's grouped-recording flow dynamic-imports (also
//!   embedded at compile time, from web/dist/assets/mediabunny.min.mjs --
//!   see web/vite.config.ts's copyMediabunnyAsset step). Kept out of
//!   index.html's own single-file bundle so a page load that never touches
//!   grouped recording never pays for it; served locally so the trim works
//!   offline/LAN with no CDN.
//!
//! Per-camera operational state (layer mask, render size, future zoom)
//! is no longer exposed over HTTP — it's owned by the data channel
//! protocol in `crate::protocol`. The protocol is bidirectional so the
//! sidecar can also push state changes back (adaptive shed events,
//! vessel changes) without the client polling.

use std::sync::Arc;

use anyhow::anyhow;
use axum::extract::State;
use axum::http::StatusCode;
use axum::response::IntoResponse;
use axum::routing::{get, post};
use axum::{Json, Router};
use serde::{Deserialize, Serialize};
use tokio::sync::{oneshot, RwLock};
use tower_http::cors::{Any, CorsLayer};
use tracing::{info, warn};

use crate::cameras::{CameraInfo, CameraRegistry, StatusLogEntry};
use crate::encoder::EncoderChoice;
use crate::protocol::{ErrorCode, ErrorPayload, ErrorSource};
use crate::webrtc::KerbcastPeer;

#[derive(Clone)]
pub struct AppState {
    pub registry: Arc<CameraRegistry>,
    pub peers: Arc<RwLock<Vec<Arc<KerbcastPeer>>>>,
    /// Carried through so peers and the consume loop initialise encoders
    /// against the same settings. Encoders themselves live in the
    /// registry; AppState just plumbs the configuration.
    pub encoder_choice: EncoderChoice,
    pub fps: u32,
    pub bitrate_bps: u32,
}

#[derive(Debug, Deserialize)]
pub struct OfferRequest {
    pub sdp: String,
    /// flight IDs the browser wants tracks for. With no `slots` field, empty
    /// = subscribe to the currently-known cameras, as many as the offer has
    /// video sections for. With
    /// `slots` set (the dynamic model) these are the *initial* subscription
    /// bound to the first slots; empty then means "no initial subscription".
    #[serde(default)]
    pub cameras: Vec<u32>,
    /// Slot-pool size = the number of recv-only video transceivers in the
    /// offer. Absent (older clients) → one slot per initial camera, i.e. the
    /// old one-track-per-camera behaviour. When set, spare slots beyond the
    /// initial subscription stay idle until a runtime `Subscribe`.
    #[serde(default)]
    pub slots: Option<u32>,
}

#[derive(Debug, Serialize)]
pub struct AnswerResponse {
    pub sdp: String,
    /// Echo of the cameras actually subscribed (after filtering unknown
    /// IDs). The browser uses this to render the right number of video
    /// elements.
    pub cameras: Vec<u32>,
}

#[derive(Debug, Serialize)]
pub struct CamerasResponse {
    pub cameras: Vec<CameraInfo>,
}

#[derive(Debug, Serialize)]
pub struct DumpLogsResponse {
    pub entries: Vec<StatusLogEntry>,
}

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/", get(serve_index))
        .route("/assets/mediabunny.min.mjs", get(serve_mediabunny))
        .route("/health", get(health))
        .route("/cameras", get(cameras))
        .route("/offer", post(offer))
        .route("/dumpLogs", get(dump_logs))
        .route("/dumpLogs/reset", post(reset_logs))
        .route("/profile", get(profile))
        .route("/profile/render", post(profile_render))
        .layer(
            CorsLayer::new()
                .allow_origin(Any)
                .allow_methods(Any)
                .allow_headers(Any),
        )
        .with_state(state)
}

async fn health() -> impl IntoResponse {
    (StatusCode::OK, "ok\n")
}

async fn serve_index() -> impl IntoResponse {
    (
        StatusCode::OK,
        [("content-type", "text/html; charset=utf-8")],
        include_str!("../../web/dist/index.html"),
    )
}

/// Serves the Mediabunny bundle the embedded page's grouped-recording trim
/// dynamic-imports (`web/src/mediabunnyAsset.ts`). Mirrors `serve_index`:
/// embedded at compile time, no filesystem read at request time.
async fn serve_mediabunny() -> impl IntoResponse {
    (
        StatusCode::OK,
        [("content-type", "text/javascript; charset=utf-8")],
        include_str!("../../web/dist/assets/mediabunny.min.mjs"),
    )
}

async fn cameras(State(state): State<AppState>) -> impl IntoResponse {
    let list = state.registry.list().await;
    (StatusCode::OK, Json(CamerasResponse { cameras: list })).into_response()
}

/// Serves the plugin's latest `global.status.json` (the per-phase render/
/// readback timings + GC counters written when `EnableTelemetry` is on).
/// The sidecar runs on the same machine as KSP, so this is the egress for
/// reading that Deck-local tmpfs file remotely — hit it per scene to build a
/// profile. Returns 404 with a hint when the file isn't there yet (telemetry
/// off, or no camera has rendered).
async fn profile(State(state): State<AppState>) -> impl IntoResponse {
    let path = state.registry.shm_dir().join("global.status.json");
    match tokio::fs::read_to_string(&path).await {
        Ok(body) => (
            StatusCode::OK,
            [("content-type", "application/json")],
            body,
        )
            .into_response(),
        Err(e) => (
            StatusCode::NOT_FOUND,
            format!(
                "no telemetry at {}: {e} \
                 (is EnableTelemetry=true and a camera rendering? try POST /profile/render?on=true)",
                path.display()
            ),
        )
            .into_response(),
    }
}

#[derive(Debug, Deserialize)]
pub struct RenderParams {
    /// true → keep every live camera subscribed so it renders without a peer
    /// (profiling); false → release the override, back to normal.
    pub on: bool,
}

/// Profiling override: force the plugin to render every camera (full per-frame
/// cost) without a streaming client, so per-scene perf can be measured from
/// anywhere. Render-only — no peer means no encode, so this isolates the
/// plugin's KSP-frametime cost. `POST /profile/render?on=true` to engage,
/// `?on=false` to release.
async fn profile_render(
    State(state): State<AppState>,
    axum::extract::Query(params): axum::extract::Query<RenderParams>,
) -> impl IntoResponse {
    state.registry.set_force_render(params.on);
    (StatusCode::OK, format!("force_render = {}\n", params.on)).into_response()
}

/// Returns every `global.status.json` snapshot the sidecar has seen
/// since the last `POST /dumpLogs/reset` (or since startup). The
/// harness fires this after `[BASELINE-DONE]` to capture the full
/// kspFps / shedLevel / per-camera timeline without polling during
/// the measurement window. Dedup-by-equality keeps the buffer small.
async fn dump_logs(State(state): State<AppState>) -> impl IntoResponse {
    let entries = state.registry.dump_run_logs().await;
    (StatusCode::OK, Json(DumpLogsResponse { entries })).into_response()
}

/// Clears the in-memory status-log ring. The harness fires this
/// immediately before AG1 so each run gets a clean window.
async fn reset_logs(State(state): State<AppState>) -> impl IntoResponse {
    state.registry.reset_run_logs().await;
    (StatusCode::OK, "ok\n")
}

/// An offer refused for a reason the client can act on, answered with 422 and
/// the payload as the JSON body instead of a 500.
#[derive(Debug)]
struct OfferRefused(ErrorPayload);

impl std::fmt::Display for OfferRefused {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0.message)
    }
}

impl std::error::Error for OfferRefused {}

async fn offer(State(state): State<AppState>, Json(req): Json<OfferRequest>) -> impl IntoResponse {
    match handle_offer(state, req).await {
        Ok(resp) => (StatusCode::OK, Json(resp)).into_response(),
        Err(e) if e.is::<OfferRefused>() => {
            let Ok(OfferRefused(payload)) = e.downcast::<OfferRefused>() else {
                unreachable!("checked by is::<OfferRefused>()")
            };
            warn!(code = ?payload.code, message = %payload.message, "offer refused");
            (StatusCode::UNPROCESSABLE_ENTITY, Json(payload)).into_response()
        }
        Err(e) => {
            warn!(error = %e, "offer handling failed");
            (
                StatusCode::INTERNAL_SERVER_ERROR,
                format!("offer handling failed: {e}"),
            )
                .into_response()
        }
    }
}

/// Runs the whole exchange on a spawned task rather than inline.
///
/// Axum drops a handler's future the moment the client hangs up, and every
/// step below binds resources: `KerbcastPeer::new` subscribes the initial
/// cameras (which wakes the plugin's per-camera render), and `answer_to_offer`
/// then awaits ICE gathering. Dropped inline, that leaves cameras rendering
/// with no peer in the list for the reaper to find, for the life of the
/// process. The task cannot be cancelled, so the peer always reaches the peer
/// list and the reaper always gets its chance at it.
async fn handle_offer(state: AppState, req: OfferRequest) -> anyhow::Result<AnswerResponse> {
    let (tx, rx) = oneshot::channel();
    tokio::spawn(async move {
        let _ = tx.send(negotiate(state, req).await);
    });
    match rx.await {
        Ok(result) => result,
        Err(_) => Err(anyhow!("offer task died before answering")),
    }
}

async fn negotiate(state: AppState, req: OfferRequest) -> anyhow::Result<AnswerResponse> {
    /* Refused before the peer exists, so no camera is subscribed for an
    exchange that would only fail later with webrtc-rs's "codec is not
    supported by remote". */
    if offered_video_sections(&req.sdp) > 0 && !offers_h264_mode1(&req.sdp) {
        return Err(OfferRefused(ErrorPayload {
            message: "the browser offered no H.264 video (packetization-mode=1), the only \
                      video Kerbcast sends; enable H.264 in the browser (Firefox: the OpenH264 \
                      plugin) or use another browser"
                .to_owned(),
            source: ErrorSource::Sidecar,
            flight_id: None,
            code: Some(ErrorCode::CodecUnsupported),
        })
        .into());
    }
    // Resolve selection: if the browser didn't ask for specific cameras,
    // subscribe to all of them. Useful for the v0.2 test page which
    // populates the picker from /cameras but lets the user click
    // "connect to all" without an explicit selection.
    // Legacy/test-page path: no explicit pool size AND no cameras = subscribe
    // to all. With an explicit `slots` (the dynamic model), an empty `cameras`
    // means "no initial subscription".
    let requested: Vec<u32> = if req.slots.is_none() && req.cameras.is_empty() {
        /* Only as many cameras as the offer has video sections to carry them.
        The SDK's legacy connect() offers one, so binding every camera left the
        rest on slots no m-line matches: rendering, with every frame dropped. */
        state
            .registry
            .list()
            .await
            .iter()
            .map(|c| c.flight_id)
            .take(offered_video_sections(&req.sdp))
            .collect()
    } else {
        req.cameras
    };

    // Pool size = the offer's recv-only video transceiver count. Absent
    // (older clients) → one slot per initial camera (the old behaviour).
    let slot_count = req.slots.map(|s| s as usize).unwrap_or(requested.len());

    let peer = Arc::new(KerbcastPeer::new(state.registry.clone(), &requested, slot_count).await?);

    /* Register BEFORE the SDP exchange, not after it succeeds. The peer holds
    camera subscriptions from construction, and the peer list is the only thing
    that makes it reachable: a peer registered on the success path alone is
    invisible to the reaper on every other path, so a negotiation that never
    completes pins its cameras subscribed forever. Registered here it is always
    collectable, by its own connection state or by the negotiation deadline. */
    let peer_count = {
        let mut peers = state.peers.write().await;
        peers.push(peer.clone());
        peers.len()
    };

    let answer_sdp = match peer.answer_to_offer(req.sdp).await {
        Ok(sdp) => sdp,
        Err(e) => {
            /* This connection can never come up, so don't make its cameras wait
            out the negotiation deadline. */
            peer.mark_dead();
            return Err(e);
        }
    };
    let subscribed = peer.subscribed.clone();
    info!(
        peer_count,
        cameras = ?subscribed,
        "peer registered, returning answer",
    );

    Ok(AnswerResponse {
        sdp: answer_sdp,
        cameras: subscribed,
    })
}

/// The number of video m-lines in an SDP offer.
fn offered_video_sections(sdp: &str) -> usize {
    sdp.lines().filter(|l| l.starts_with("m=video")).count()
}

/// Whether any video section of an SDP offer has an H.264 payload type with
/// packetization-mode=1, the mode every slot's track is pinned to.
fn offers_h264_mode1(sdp: &str) -> bool {
    let mut sections: Vec<(bool, Vec<&str>)> = Vec::new();
    for line in sdp.lines().map(str::trim) {
        if line.starts_with("m=") {
            sections.push((line.starts_with("m=video"), Vec::new()));
        } else if let Some((_, attrs)) = sections.last_mut() {
            attrs.push(line);
        }
    }
    sections
        .iter()
        .filter(|(video, _)| *video)
        .any(|(_, attrs)| {
            let payload_types = |prefix: &str, keep: fn(&str) -> bool| -> Vec<&str> {
                attrs
                    .iter()
                    .filter_map(|l| l.strip_prefix(prefix)?.split_once(' '))
                    .filter(|(_, rest)| keep(rest))
                    .map(|(pt, _)| pt)
                    .collect()
            };
            let h264 = payload_types("a=rtpmap:", |codec| {
                codec.to_ascii_lowercase().starts_with("h264/")
            });
            let mode1 = payload_types("a=fmtp:", |params| {
                params
                    .split(';')
                    .any(|p| p.trim().eq_ignore_ascii_case("packetization-mode=1"))
            });
            h264.iter().any(|pt| mode1.contains(pt))
        })
}

#[cfg(test)]
mod tests {
    use std::path::PathBuf;

    use axum::body::{to_bytes, Body};
    use axum::http::Request;
    use tower::ServiceExt;

    use super::*;

    /// An `AppState` with no attached cameras or peers -- enough to exercise
    /// the static routes (`/`, `/assets/mediabunny.min.mjs`, `/health`),
    /// which read no registry/peer state.
    fn empty_state() -> AppState {
        AppState {
            registry: Arc::new(CameraRegistry::new(PathBuf::from("/tmp"))),
            peers: Arc::new(RwLock::new(Vec::new())),
            encoder_choice: EncoderChoice::Auto,
            fps: 30,
            bitrate_bps: 2_000_000,
        }
    }

    #[tokio::test]
    async fn serves_mediabunny_with_the_right_content_type_and_a_nonempty_body() {
        let app = router(empty_state());
        let response = app
            .oneshot(
                Request::builder()
                    .uri("/assets/mediabunny.min.mjs")
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::OK);
        let content_type = response
            .headers()
            .get("content-type")
            .expect("content-type header")
            .to_str()
            .unwrap();
        assert!(
            content_type.starts_with("text/javascript"),
            "unexpected content-type: {content_type}",
        );

        let body = to_bytes(response.into_body(), usize::MAX).await.unwrap();
        // The real pre-minified Mediabunny bundle is several hundred KB; a
        // missing/truncated embed would be nowhere close, so this also
        // guards against an accidentally-empty include_str!.
        assert!(
            body.len() > 400_000,
            "expected the real Mediabunny bundle, got {} bytes",
            body.len(),
        );
    }

    #[tokio::test]
    async fn serves_index_html_at_the_root() {
        let app = router(empty_state());
        let response = app
            .oneshot(Request::builder().uri("/").body(Body::empty()).unwrap())
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::OK);
        let content_type = response
            .headers()
            .get("content-type")
            .expect("content-type header")
            .to_str()
            .unwrap();
        assert!(content_type.starts_with("text/html"));

        let body = to_bytes(response.into_body(), usize::MAX).await.unwrap();
        assert!(!body.is_empty());
        // index.html must stay lean: nowhere near Mediabunny's own bundle
        // size, i.e. it was never inlined into the single-file page.
        assert!(
            body.len() < 500_000,
            "index.html grew to {} bytes -- did Mediabunny get inlined?",
            body.len(),
        );
    }

    fn offer_that_cannot_be_answered() -> OfferRequest {
        OfferRequest {
            sdp: "not an sdp".to_owned(),
            cameras: Vec::new(),
            slots: Some(1),
        }
    }

    /// A peer holds camera subscriptions from the moment it is built, so it
    /// must be in the peer list before anything that can fail. Registering it
    /// only on the success path left a failed exchange holding its cameras
    /// with nothing able to see it.
    #[tokio::test]
    async fn a_failed_exchange_leaves_its_peer_registered_and_reapable() {
        let state = empty_state();

        let result = handle_offer(state.clone(), offer_that_cannot_be_answered()).await;

        assert!(result.is_err(), "an unparseable offer must not answer");
        let peers = state.peers.read().await;
        assert_eq!(peers.len(), 1, "the peer must still be registered");
        assert!(
            !peers[0].is_alive(),
            "a peer whose exchange failed must be reapable at once, not after \
             the negotiation deadline"
        );
    }

    /// A legacy offer with no camera list binds only as many cameras as it has
    /// video sections, so no camera is bound to a slot no m-line matches.
    #[tokio::test]
    async fn a_subscribe_all_offer_binds_only_what_it_can_carry() {
        use crate::shared_mem::{MmapFrameRing, MmapRingConfig};

        let dir = tempfile::tempdir().expect("tempdir");
        let cfg = MmapRingConfig {
            slot_count: 4,
            max_width: 64,
            max_height: 64,
        };
        for id in [1, 2, 3] {
            MmapFrameRing::create(&dir.path().join(format!("{id}.ring")), cfg).expect("ring");
        }
        let registry = Arc::new(CameraRegistry::new(dir.path().to_path_buf()));
        registry.rescan().await;
        let state = AppState {
            registry,
            ..empty_state()
        };

        let req = OfferRequest {
            sdp: "v=0\r\nm=video 9 UDP/TLS/RTP/SAVPF 96\r\na=rtpmap:96 H264/90000\r\na=fmtp:96 packetization-mode=1\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\n"
                .to_owned(),
            cameras: Vec::new(),
            slots: None,
        };
        let _ = handle_offer(state.clone(), req).await;

        let peers = state.peers.read().await;
        assert_eq!(peers.len(), 1);
        assert_eq!(
            peers[0].subscribed.len(),
            1,
            "one video section, one camera"
        );
    }

    /// The video section of a Firefox offer built without OpenH264.
    const VP8_ONLY_OFFER: &str = "v=0\r\no=mozilla...THIS_IS_SDPARTA-99.0 1 0 IN IP4 0.0.0.0\r\ns=-\r\nt=0 0\r\na=group:BUNDLE 0 1\r\nm=video 9 UDP/TLS/RTP/SAVPF 120 124 121 125 126 127 97 98\r\nc=IN IP4 0.0.0.0\r\na=recvonly\r\na=mid:0\r\na=fmtp:126 profile-level-id=42e01f;level-asymmetry-allowed=1\r\na=fmtp:120 max-fs=12288;max-fr=60\r\na=rtpmap:120 VP8/90000\r\na=rtpmap:121 VP9/90000\r\na=rtpmap:124 rtx/90000\r\na=rtpmap:126 AV1/90000\r\nm=application 9 UDP/DTLS/SCTP webrtc-datachannel\r\na=mid:1\r\na=sctp-port:5000\r\n";

    #[test]
    fn detects_h264_with_packetization_mode_1() {
        assert!(!offers_h264_mode1(VP8_ONLY_OFFER));
        let chrome = "v=0\r\nm=video 9 X 96 102 104\r\na=rtpmap:96 VP8/90000\r\na=rtpmap:102 H264/90000\r\na=fmtp:102 level-asymmetry-allowed=1;packetization-mode=0;profile-level-id=42001f\r\na=rtpmap:104 h264/90000\r\na=fmtp:104 level-asymmetry-allowed=1; packetization-mode=1;profile-level-id=42e01f\r\n";
        assert!(offers_h264_mode1(chrome));
        let mode0_only = "v=0\r\nm=video 9 X 102\r\na=rtpmap:102 H264/90000\r\na=fmtp:102 packetization-mode=0;profile-level-id=42e01f\r\n";
        assert!(!offers_h264_mode1(mode0_only));
        /* Payload types are per section: an fmtp in another section does not count. */
        let split = "v=0\r\nm=video 9 X 102\r\na=rtpmap:102 H264/90000\r\nm=video 9 X 102\r\na=rtpmap:102 VP8/90000\r\na=fmtp:102 packetization-mode=1\r\n";
        assert!(!offers_h264_mode1(split));
    }

    /// A browser with no H.264 gets a 422 naming the cause, and no peer is
    /// built for it, instead of a 500 from deep inside the SDP exchange.
    #[tokio::test]
    async fn an_offer_without_h264_is_refused_with_a_reason() {
        let state = empty_state();
        let app = router(state.clone());
        let body = serde_json::json!({ "sdp": VP8_ONLY_OFFER, "cameras": [], "slots": 1 });
        let response = app
            .oneshot(
                Request::builder()
                    .method("POST")
                    .uri("/offer")
                    .header("content-type", "application/json")
                    .body(Body::from(body.to_string()))
                    .unwrap(),
            )
            .await
            .unwrap();

        assert_eq!(response.status(), StatusCode::UNPROCESSABLE_ENTITY);
        let body = to_bytes(response.into_body(), usize::MAX).await.unwrap();
        let refusal: ErrorPayload = serde_json::from_slice(&body).expect("ErrorPayload body");
        assert_eq!(refusal.code, Some(ErrorCode::CodecUnsupported));
        assert!(refusal.message.contains("H.264"), "{}", refusal.message);
        assert!(
            state.peers.read().await.is_empty(),
            "no peer for a refused offer"
        );
    }

    #[test]
    fn counts_video_sections_only() {
        let sdp =
            "v=0\r\nm=video 9 X 96\r\nm=audio 9 X 0\r\nm=video 9 X 96\r\nm=application 9 X y\r\n";
        assert_eq!(offered_video_sections(sdp), 2);
        assert_eq!(offered_video_sections("not an sdp"), 0);
    }

    /// The orphan the reaper could never collect: axum drops the handler future
    /// when the client hangs up mid-request, and the peer built before that
    /// point kept its cameras subscribed for the life of the process. The work
    /// runs on its own task now, so cancelling the request cannot strand it.
    #[tokio::test]
    async fn a_cancelled_request_still_registers_its_peer() {
        use std::future::Future;
        use std::pin::pin;
        use std::task::{Context, Poll, Waker};

        let state = empty_state();

        // Poll once (far enough to spawn the work), then drop: a client that
        // hung up before the answer came back.
        {
            let mut request = pin!(handle_offer(state.clone(), offer_that_cannot_be_answered()));
            let mut cx = Context::from_waker(Waker::noop());
            assert!(
                matches!(request.as_mut().poll(&mut cx), Poll::Pending),
                "the request must still be in flight when it is dropped"
            );
        }

        let mut registered = false;
        for _ in 0..200 {
            if state.peers.read().await.len() == 1 {
                registered = true;
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        assert!(
            registered,
            "a cancelled request must still leave its peer in the list, or its \
             cameras stay subscribed with nothing able to reap them"
        );
    }
}
