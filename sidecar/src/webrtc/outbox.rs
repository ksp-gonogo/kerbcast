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

use crate::anchors::anchors_for;
use crate::protocol::{
    CameraSnapshotPayload, CameraState, CameraStateChangedPayload, FrameStampPayload, ServerMessage,
};
use crate::shared_mem::CaptureStamp;

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
 * The most bytes one control message may take. webrtc-rs refuses a message
 * over 64 KiB, and a refused control message tears the peer down, so a
 * snapshot of many cameras is split and a command camera listing hundreds
 * of craft is cut to fit, with room to spare for the framing.
 */
pub const MAX_CONTROL_MESSAGE: usize = 60 * 1024;

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
    /// Each item is one or more messages that go out back to back.
    control_tx: mpsc::Sender<Vec<String>>,
    stamp_tx: mpsc::Sender<FrameStampPayload>,
    /// Whether the client asked for frame stamps. Off by default: a client
    /// that predates them would only pay to receive and discard them.
    stamps_wanted: Arc<AtomicBool>,
    /// Whether the client asked for anchor results on its stamps. Off by
    /// default for the same reason, and because a 1.11 client that asked for
    /// stamps must not start receiving game-owned anchors.
    anchors_wanted: Arc<AtomicBool>,
    /// Whether the client asked for a command camera's per-frame state.
    command_wanted: Arc<AtomicBool>,
    /// The owner tag this client's anchor results carry in the frame ring.
    owner: Arc<str>,
    dead: Arc<AtomicBool>,
    shutdown: Arc<watch::Sender<bool>>,
}

impl Outbox {
    /// Start the peer's sender task on `control`. It runs until `shutdown`.
    /// `owner` is the tag the client's own anchor results carry.
    pub fn spawn(control: ControlChannel, owner: String) -> Self {
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
            stamps_wanted: Arc::new(AtomicBool::new(false)),
            anchors_wanted: Arc::new(AtomicBool::new(false)),
            command_wanted: Arc::new(AtomicBool::new(false)),
            owner: owner.into(),
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
        let bodies = match fitted_messages(msg, MAX_CONTROL_MESSAGE) {
            Ok(b) => b,
            Err(e) => {
                warn!(error = %e, "control channel: serialise failed");
                return;
            }
        };
        if self.control_tx.try_send(bodies).is_err() && !self.dead.swap(true, Ordering::SeqCst) {
            warn!(
                queued = CONTROL_QUEUE,
                "control channel: peer is not draining its messages, tearing it down"
            );
        }
    }

    /**
     * Queue the stamp of the frame about to go out under `rtp_timestamp`, or
     * drop it when the peer did not ask for stamps or is behind. A frame
     * without a UT has no stamp. Anchor results are filtered to what this
     * peer may see, and only for a peer that asked for them; so is a command
     * camera's state. The live bound and boundary go on every stamp.
     */
    pub fn offer_stamp(&self, flight_id: u32, rtp_timestamp: u32, capture: &CaptureStamp) {
        if !self.stamps_wanted.load(Ordering::Relaxed) {
            return;
        }
        let Some(ut) = capture.ut else { return };
        let anchors = if self.anchors_wanted.load(Ordering::Relaxed) {
            capture
                .anchors
                .as_deref()
                .map(|all| anchors_for(&self.owner, all))
                .filter(|mine| !mine.is_empty())
        } else {
            None
        };
        let _ = self.stamp_tx.try_send(FrameStampPayload {
            flight_id,
            rtp_timestamp,
            capture_ut: ut,
            capture_epoch: capture.epoch,
            fov: capture.fov,
            width: capture.size.map(|s| s.0),
            height: capture.size.map(|s| s.1),
            anchors,
            live_light_time_s: capture.live_light_time_s,
            boundary_m: capture.boundary_m,
            command: if self.command_wanted.load(Ordering::Relaxed) {
                capture.command.as_deref().cloned()
            } else {
                None
            },
        });
    }

    /// Start or stop frame stamps for this peer, and anchor results and
    /// command camera state on them.
    pub fn set_stamps_wanted(&self, wanted: bool, anchors: bool, command: bool) {
        self.stamps_wanted.store(wanted, Ordering::Relaxed);
        self.anchors_wanted
            .store(wanted && anchors, Ordering::Relaxed);
        self.command_wanted
            .store(wanted && command, Ordering::Relaxed);
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

/**
 * `msg` as one or more JSON messages, each within `limit` bytes. A snapshot
 * too large for one message keeps the cameras that fit, in order, and says
 * how many follow; each of the rest goes out on its own as a camera state
 * change. Each message is then fitted by `fitted_json`.
 */
pub fn fitted_messages(msg: &ServerMessage, limit: usize) -> serde_json::Result<Vec<String>> {
    let body = serde_json::to_string(msg)?;
    let snapshot = match msg {
        ServerMessage::CameraSnapshot(s) if body.len() > limit && s.cameras.len() > 1 => s,
        _ if body.len() <= limit => return Ok(vec![body]),
        _ => return Ok(vec![fitted_json(msg, limit)?]),
    };
    /* Room for the follow count's digits whatever it turns out to be. */
    let mut used = serde_json::to_string(&ServerMessage::CameraSnapshot(CameraSnapshotPayload {
        cameras: Vec::new(),
        follows: Some(u32::MAX),
    }))?
    .len();
    let mut head = 0;
    for state in &snapshot.cameras {
        let len = serde_json::to_string(state)?.len() + 1;
        if head > 0 && used + len > limit {
            break;
        }
        used += len;
        head += 1;
    }
    let (first, rest) = snapshot.cameras.split_at(head);
    let mut out = Vec::with_capacity(1 + rest.len());
    out.push(fitted_json(
        &ServerMessage::CameraSnapshot(CameraSnapshotPayload {
            cameras: first.to_vec(),
            follows: Some(rest.len() as u32),
        }),
        limit,
    )?);
    for state in rest {
        out.push(fitted_json(
            &ServerMessage::CameraStateChanged(CameraStateChangedPayload {
                state: state.clone(),
            }),
            limit,
        )?);
    }
    Ok(out)
}

/**
 * `msg` as JSON within `limit` bytes where it can be. The command cameras'
 * target lists, the only part of a message that grows with the save, are
 * all cut to one common length, the longest that fits, from the end (the
 * least eligible and farthest craft), so a short list keeps every craft;
 * each keeps its feed's subject and notes how many it lost. A message still over the limit with no
 * targets left goes out as it is.
 */
pub fn fitted_json(msg: &ServerMessage, limit: usize) -> serde_json::Result<String> {
    let body = serde_json::to_string(msg)?;
    if body.len() <= limit || target_count(msg) == 0 {
        return Ok(body);
    }
    let mut msg = msg.clone();
    /* Each list's length and its bytes per target, to estimate a cap's size without serialising. */
    let mut lists = Vec::new();
    for c in states_mut(&mut msg).filter_map(|s| s.command_centre.as_mut()) {
        let n = c.targets.len();
        if n > 0 {
            lists.push((
                n,
                serde_json::to_string(&c.targets)?.len() as f64 / n as f64,
            ));
        }
    }
    let all: f64 = lists.iter().map(|(n, per)| *n as f64 * per).sum();
    let rest = body.len() as f64 - all;
    let fits = |cap: usize| {
        rest + lists
            .iter()
            .map(|(n, per)| (*n).min(cap) as f64 * per)
            .sum::<f64>()
            <= limit as f64
    };
    let longest = lists.iter().map(|(n, _)| *n).max().unwrap_or(0);
    let mut cap = (0..longest).rev().find(|&cap| fits(cap)).unwrap_or(0);
    loop {
        let mut trial = msg.clone();
        for c in states_mut(&mut trial).filter_map(|s| s.command_centre.as_mut()) {
            let n = c.targets.len();
            if n > cap {
                /* The feed's own subject stays listed wherever it sat. */
                let subject = c
                    .targets
                    .iter()
                    .skip(cap)
                    .position(|t| {
                        t.persistent_id == c.subject_persistent_id && t.persistent_id != 0
                    })
                    .map(|i| c.targets[cap + i].clone());
                c.targets.truncate(cap);
                let kept = cap + usize::from(subject.is_some());
                c.targets.extend(subject);
                c.targets_omitted = Some(c.targets_omitted.unwrap_or(0) + (n - kept) as u32);
            }
        }
        let json = serde_json::to_string(&trial)?;
        if json.len() <= limit || cap == 0 {
            return Ok(json);
        }
        /* The estimate was a little short: a few fewer. */
        cap = cap.saturating_sub(cap / 16 + 1);
    }
}

fn target_count(msg: &ServerMessage) -> usize {
    match msg {
        ServerMessage::CameraSnapshot(p) => p.cameras.iter().map(state_targets).sum(),
        ServerMessage::CameraStateChanged(p) => state_targets(&p.state),
        _ => 0,
    }
}

fn state_targets(s: &CameraState) -> usize {
    s.command_centre.as_ref().map_or(0, |c| c.targets.len())
}

fn states_mut(msg: &mut ServerMessage) -> impl Iterator<Item = &mut CameraState> {
    let states: Vec<&mut CameraState> = match msg {
        ServerMessage::CameraSnapshot(p) => p.cameras.iter_mut().collect(),
        ServerMessage::CameraStateChanged(p) => vec![&mut p.state],
        _ => Vec::new(),
    };
    states.into_iter()
}

async fn run(
    control: ControlChannel,
    mut control_rx: mpsc::Receiver<Vec<String>>,
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
                Some(bodies) = control_rx.recv() => {
                    /* Not open yet means the browser has not sent Hello, whose
                    reply carries the full state, so the message is superseded. */
                    let dc = control.lock().await.clone();
                    let Some(dc) = dc.filter(|dc| {
                        dc.ready_state() != RTCDataChannelState::Connecting
                    }) else {
                        continue;
                    };
                    for body in bodies {
                        if let Err(e) = dc.send_text(body).await {
                            if !dead.swap(true, Ordering::SeqCst) {
                                warn!(error = %e, "control channel: send failed, tearing the peer down");
                            }
                            break;
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::{
        CameraKind, CameraSnapshotPayload, CameraStateChangedPayload, CommandCentreInfo,
    };

    fn centre(targets: usize) -> CommandCentreInfo {
        let list: Vec<String> = (0..targets)
            .map(|i| {
                format!(
                    r#"{{"persistentId":{i},"vesselName":"Untitled Space Craft Debris {i}","vesselType":"Debris","rangeM":12300000.0,"eligibility":"ok","eligibilityText":"Tracked"}}"#
                )
            })
            .collect();
        serde_json::from_str(&format!(
            r#"{{"stationId":"ground:KSC","nodeName":"KSC","displayName":"KSC","body":"Kerbin","latitude":0.0,"longitude":0.0,"altitude":0.0,"subjectPersistentId":0,"trackingLevel":3,"targets":[{}]}}"#,
            list.join(",")
        ))
        .unwrap()
    }

    fn command_camera(flight_id: u32, targets: usize) -> CameraState {
        CameraState {
            flight_id,
            kind: CameraKind::Command,
            command_centre: Some(centre(targets)),
            ..Default::default()
        }
    }

    fn targets_of(json: &str) -> Vec<(usize, Option<u32>)> {
        let msg: ServerMessage = serde_json::from_str(json).unwrap();
        let states = match msg {
            ServerMessage::CameraSnapshot(p) => p.cameras,
            ServerMessage::CameraStateChanged(p) => vec![p.state],
            _ => Vec::new(),
        };
        states
            .iter()
            .filter_map(|s| s.command_centre.as_ref())
            .map(|c| (c.targets.len(), c.targets_omitted))
            .collect()
    }

    #[test]
    fn a_small_message_goes_out_whole() {
        let msg = ServerMessage::CameraStateChanged(CameraStateChangedPayload {
            state: command_camera(1, 30),
        });
        let json = fitted_json(&msg, MAX_CONTROL_MESSAGE).unwrap();
        assert_eq!(json, serde_json::to_string(&msg).unwrap());
        assert_eq!(targets_of(&json), vec![(30, None)]);
    }

    #[test]
    fn hundreds_of_targets_are_cut_from_the_end_to_fit() {
        let msg = ServerMessage::CameraStateChanged(CameraStateChangedPayload {
            state: command_camera(1, 530),
        });
        assert!(serde_json::to_string(&msg).unwrap().len() > 64 * 1024);
        let json = fitted_json(&msg, MAX_CONTROL_MESSAGE).unwrap();
        assert!(json.len() <= MAX_CONTROL_MESSAGE, "{} bytes", json.len());
        let [(kept, omitted)] = targets_of(&json)[..] else {
            panic!("one command camera")
        };
        assert!(kept > 300, "kept {kept}");
        assert_eq!(kept + omitted.unwrap() as usize, 530);
        assert!(
            json.contains("\"persistentId\":0,"),
            "the head of the list stays"
        );
    }

    #[test]
    fn a_cut_list_keeps_the_feeds_subject() {
        let mut state = command_camera(1, 530);
        state.command_centre.as_mut().unwrap().subject_persistent_id = 529;
        let msg = ServerMessage::CameraStateChanged(CameraStateChangedPayload { state });
        let json = fitted_json(&msg, MAX_CONTROL_MESSAGE).unwrap();
        assert!(json.len() <= MAX_CONTROL_MESSAGE, "{} bytes", json.len());
        let [(kept, omitted)] = targets_of(&json)[..] else {
            panic!("one command camera")
        };
        assert_eq!(kept + omitted.unwrap() as usize, 530);
        assert!(
            json.contains("\"persistentId\":529,"),
            "the subject stays listed"
        );
    }

    #[test]
    fn a_snapshot_of_several_command_cameras_fits_together() {
        let msg = ServerMessage::CameraSnapshot(CameraSnapshotPayload {
            cameras: vec![
                command_camera(1, 530),
                command_camera(2, 530),
                command_camera(3, 10),
            ],
            follows: None,
        });
        let json = fitted_json(&msg, MAX_CONTROL_MESSAGE).unwrap();
        assert!(json.len() <= MAX_CONTROL_MESSAGE, "{} bytes", json.len());
        for (kept, omitted) in targets_of(&json) {
            assert!(kept > 0);
            let total = kept + omitted.unwrap_or(0) as usize;
            assert!(
                total == 530 || total == 10,
                "kept {kept} omitted {omitted:?}"
            );
        }
    }
    fn part_camera(flight_id: u32) -> CameraState {
        CameraState {
            flight_id,
            kind: CameraKind::Part,
            part_name: "hc.navcam".into(),
            part_title: "Hullcam VDS Navigation Camera".into(),
            camera_name: format!("Navcam {flight_id}"),
            vessel_name: format!("Relay Network Satellite {flight_id}"),
            quality_limited_by: Some("throttled".into()),
            ..Default::default()
        }
    }

    /// Every camera of a split snapshot, in the order a client applies them.
    fn cameras_of(bodies: &[String]) -> Vec<u32> {
        let mut ids = Vec::new();
        for (i, body) in bodies.iter().enumerate() {
            match serde_json::from_str(body).unwrap() {
                ServerMessage::CameraSnapshot(p) => {
                    assert_eq!(i, 0, "the snapshot leads");
                    assert_eq!(p.follows.unwrap_or(0) as usize, bodies.len() - 1);
                    ids.extend(p.cameras.iter().map(|c| c.flight_id));
                }
                ServerMessage::CameraStateChanged(p) => {
                    assert!(i > 0, "the rest follow it");
                    ids.push(p.state.flight_id);
                }
                other => panic!("unexpected {other:?}"),
            }
        }
        ids
    }

    #[test]
    fn a_snapshot_that_fits_goes_out_as_one_whole_message() {
        let msg = ServerMessage::CameraSnapshot(CameraSnapshotPayload {
            cameras: (1..=20).map(part_camera).collect(),
            follows: None,
        });
        let bodies = fitted_messages(&msg, MAX_CONTROL_MESSAGE).unwrap();
        assert_eq!(bodies, vec![serde_json::to_string(&msg).unwrap()]);
    }

    #[test]
    fn a_snapshot_of_150_cameras_is_split_into_messages_that_each_fit() {
        let msg = ServerMessage::CameraSnapshot(CameraSnapshotPayload {
            cameras: (1..=150).map(part_camera).collect(),
            follows: None,
        });
        assert!(serde_json::to_string(&msg).unwrap().len() > 64 * 1024);
        let bodies = fitted_messages(&msg, MAX_CONTROL_MESSAGE).unwrap();
        assert!(bodies.len() > 1);
        for body in &bodies {
            assert!(body.len() <= MAX_CONTROL_MESSAGE, "{} bytes", body.len());
        }
        assert_eq!(cameras_of(&bodies), (1..=150).collect::<Vec<_>>());
        assert!(
            bodies.len() < 100,
            "the snapshot keeps as many cameras as fit, not one: {} messages",
            bodies.len()
        );
    }

    #[test]
    fn a_split_snapshot_still_cuts_long_target_lists() {
        let mut cameras: Vec<CameraState> = (1..=150).map(part_camera).collect();
        cameras.push(command_camera(151, 530));
        cameras.push(command_camera(152, 530));
        let msg = ServerMessage::CameraSnapshot(CameraSnapshotPayload {
            cameras,
            follows: None,
        });
        let bodies = fitted_messages(&msg, MAX_CONTROL_MESSAGE).unwrap();
        for body in &bodies {
            assert!(body.len() <= MAX_CONTROL_MESSAGE, "{} bytes", body.len());
        }
        assert_eq!(cameras_of(&bodies), (1..=152).collect::<Vec<_>>());
        let lists: Vec<_> = bodies.iter().flat_map(|b| targets_of(b)).collect();
        assert_eq!(lists.len(), 2);
        for (kept, omitted) in lists {
            assert!(kept > 300, "kept {kept}");
            assert_eq!(kept + omitted.unwrap_or(0) as usize, 530);
        }
    }
}
