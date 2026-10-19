//! Everything the sidecar sends a peer over its control channel goes through
//! that peer's `Outbox`. A send on a data channel waits while its SCTP
//! association is backed up (webrtc-sctp blocks once 128 KiB is queued, and a
//! dead association never drains), so no caller may await one: the consume
//! loop feeds every viewer, and one stalled viewer must never hold it up.
//! Callers enqueue without waiting; one task per peer does the sending.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use tokio::sync::{mpsc, watch, Mutex};
use tracing::warn;
use webrtc::data_channel::data_channel_state::RTCDataChannelState;
use webrtc::data_channel::RTCDataChannel;

use crate::protocol::{FrameStampPayload, ServerMessage};

/// The peer's control channel, filled once the browser opens it.
pub type ControlChannel = Arc<Mutex<Option<Arc<RTCDataChannel>>>>;

/**
 * Control messages a peer may have waiting. A healthy peer drains them in
 * milliseconds; one this far behind has stopped acknowledging, and is torn
 * down rather than left to miss state it relies on.
 */
pub const CONTROL_QUEUE: usize = 1024;

/// Stamps waiting to be sent; more than this are dropped.
const STAMP_QUEUE: usize = 256;

/**
 * Bytes the control channel may hold unacknowledged before stamps are
 * skipped. A healthy link stays far below it, and it sits well under the
 * 128 KiB at which webrtc-sctp blocks a send, so stamps alone never back the
 * channel up far enough to delay a control message.
 */
const STAMP_BUFFER_LIMIT: usize = 16 * 1024;

/**
 * A peer's send side of the control channel. Control messages are sent in
 * the order they were queued and are never dropped once the channel is open:
 * if they cannot be delivered (queue overflow, failed send) the peer is
 * marked dead so the reaper tears it down and the browser reconnects to a
 * fresh snapshot. Frame stamps are lower priority and simply skipped when the
 * channel is behind. Cloning shares the same queues.
 */
#[derive(Clone)]
pub struct Outbox {
    control_tx: mpsc::Sender<String>,
    stamp_tx: mpsc::Sender<FrameStampPayload>,
    dead: Arc<AtomicBool>,
    shutdown: Arc<watch::Sender<bool>>,
}

impl Outbox {
    /// Start the peer's sender task on `control`. It runs until `shutdown`.
    pub fn spawn(control: ControlChannel) -> Self {
        let (control_tx, control_rx) = mpsc::channel(CONTROL_QUEUE);
        let (stamp_tx, stamp_rx) = mpsc::channel(STAMP_QUEUE);
        let (shutdown, shutdown_rx) = watch::channel(false);
        let dead = Arc::new(AtomicBool::new(false));
        tokio::spawn(run(
            control,
            control_rx,
            stamp_rx,
            dead.clone(),
            shutdown_rx,
        ));
        Self {
            control_tx,
            stamp_tx,
            dead,
            shutdown: Arc::new(shutdown),
        }
    }

    /**
     * Queue a control message. Never waits. A message queued before the
     * browser has opened the channel is dropped by the sender task: the
     * browser's Hello is answered with a full snapshot that supersedes it.
     */
    pub fn send(&self, msg: &ServerMessage) {
        let body = match serde_json::to_string(msg) {
            Ok(s) => s,
            Err(e) => {
                warn!(error = %e, "control channel: serialise failed");
                return;
            }
        };
        if self.control_tx.try_send(body).is_err() && !self.dead.swap(true, Ordering::SeqCst) {
            warn!(
                queued = CONTROL_QUEUE,
                "control channel: peer is not draining its messages, tearing it down"
            );
        }
    }

    /// Queue a frame stamp, or drop it when the peer is behind.
    pub fn offer_stamp(&self, stamp: FrameStampPayload) {
        let _ = self.stamp_tx.try_send(stamp);
    }

    /// True once a control message could not be delivered.
    pub fn is_dead(&self) -> bool {
        self.dead.load(Ordering::SeqCst)
    }

    /// Stop the sender task, abandoning anything still queued or in flight.
    /// Only for a peer being torn down.
    pub fn shutdown(&self) {
        let _ = self.shutdown.send(true);
    }
}

async fn run(
    control: ControlChannel,
    mut control_rx: mpsc::Receiver<String>,
    mut stamp_rx: mpsc::Receiver<FrameStampPayload>,
    dead: Arc<AtomicBool>,
    mut shutdown: watch::Receiver<bool>,
) {
    /* Sends are never cancelled while the peer lives: dropping one part way
    can leave a hole in the channel's ordered sequence that wedges every later
    message. Shutdown abandons them, since the association goes with it. */
    let work = async {
        loop {
            tokio::select! {
                biased;
                Some(body) = control_rx.recv() => {
                    /* Not open yet means the browser has not sent Hello, whose
                    reply carries the full state, so the message is superseded. */
                    let dc = control.lock().await.clone();
                    let Some(dc) = dc.filter(|dc| {
                        dc.ready_state() != RTCDataChannelState::Connecting
                    }) else {
                        continue;
                    };
                    if let Err(e) = dc.send_text(body).await {
                        if !dead.swap(true, Ordering::SeqCst) {
                            warn!(error = %e, "control channel: send failed, tearing the peer down");
                        }
                    }
                }
                Some(stamp) = stamp_rx.recv() => {
                    let dc = control.lock().await.clone();
                    if let Some(dc) = dc {
                        send_stamp(&dc, stamp).await;
                    }
                }
                else => break,
            }
        }
    };
    tokio::select! {
        _ = work => {}
        _ = shutdown.wait_for(|stop| *stop) => {}
    }
}

/// A channel that is not open, or is not keeping up, just misses the stamp;
/// the browser falls back to its estimated clock for that frame.
async fn send_stamp(dc: &Arc<RTCDataChannel>, stamp: FrameStampPayload) {
    if dc.ready_state() != RTCDataChannelState::Open
        || dc.buffered_amount().await > STAMP_BUFFER_LIMIT
    {
        return;
    }
    let body = match serde_json::to_string(&ServerMessage::FrameStamp(stamp)) {
        Ok(s) => s,
        Err(e) => {
            warn!(error = %e, "frame stamp: serialise failed");
            return;
        }
    };
    let _ = dc.send_text(body).await;
}
