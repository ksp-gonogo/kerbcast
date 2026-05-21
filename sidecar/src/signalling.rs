//! HTTP signalling endpoint. Two endpoints + a test page:
//!
//! - `GET /cameras` returns a JSON list of currently-attached cameras
//!   (with `part_title`, `vessel_name`, etc); the browser fetches this
//!   to populate its picker before opening a peer connection.
//! - `POST /offer` takes `{ sdp, cameras: [flight_id, ...] }`, creates a
//!   `KerbcamPeer` with one video track per selected camera AND a
//!   "kerbcam-control" data channel, answers the SDP, and returns the
//!   answer. Unknown camera IDs are dropped with a warning rather than
//!   failing the whole request.
//! - `GET /` serves the bundled test page.
//!
//! Per-camera operational state (layer mask, render size, future zoom)
//! is no longer exposed over HTTP — it's owned by the data channel
//! protocol in `crate::protocol`. The protocol is bidirectional so the
//! sidecar can also push state changes back (adaptive shed events,
//! vessel changes) without the client polling.

use std::sync::Arc;

use axum::extract::State;
use axum::http::StatusCode;
use axum::response::IntoResponse;
use axum::routing::{get, post};
use axum::{Json, Router};
use serde::{Deserialize, Serialize};
use tokio::sync::RwLock;
use tower_http::cors::{Any, CorsLayer};
use tracing::{info, warn};

use crate::cameras::{CameraInfo, CameraRegistry};
use crate::encoder::EncoderChoice;
use crate::webrtc::KerbcamPeer;

#[derive(Clone)]
pub struct AppState {
    pub registry: Arc<CameraRegistry>,
    pub peers: Arc<RwLock<Vec<Arc<KerbcamPeer>>>>,
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
    /// flight IDs the browser wants tracks for. Empty = subscribe to
    /// every currently-known camera (useful for the dev test page).
    #[serde(default)]
    pub cameras: Vec<u32>,
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

pub fn router(state: AppState) -> Router {
    Router::new()
        .route("/", get(serve_index))
        .route("/health", get(health))
        .route("/cameras", get(cameras))
        .route("/offer", post(offer))
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
        include_str!("./signalling_index.html"),
    )
}

async fn cameras(State(state): State<AppState>) -> impl IntoResponse {
    let list = state.registry.list().await;
    (StatusCode::OK, Json(CamerasResponse { cameras: list })).into_response()
}

async fn offer(State(state): State<AppState>, Json(req): Json<OfferRequest>) -> impl IntoResponse {
    match handle_offer(state, req).await {
        Ok(resp) => (StatusCode::OK, Json(resp)).into_response(),
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

async fn handle_offer(state: AppState, req: OfferRequest) -> anyhow::Result<AnswerResponse> {
    // Resolve selection: if the browser didn't ask for specific cameras,
    // subscribe to all of them. Useful for the v0.2 test page which
    // populates the picker from /cameras but lets the user click
    // "connect to all" without an explicit selection.
    let requested: Vec<u32> = if req.cameras.is_empty() {
        state
            .registry
            .list()
            .await
            .iter()
            .map(|c| c.flight_id)
            .collect()
    } else {
        req.cameras
    };

    let peer = Arc::new(KerbcamPeer::new(state.registry.clone(), &requested).await?);
    let answer_sdp = peer.answer_to_offer(req.sdp).await?;
    let subscribed = peer.subscribed.clone();

    let peer_count = {
        let mut peers = state.peers.write().await;
        peers.push(peer);
        peers.len()
    };
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
