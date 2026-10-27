//! Global requests a client makes of the plugin that are not about one
//! existing camera's control block: creating, destroying and aiming command
//! cameras. The sidecar validates each, then
//! appends it to `global.commands.json`, which the plugin re-reads a few
//! times a second.
//!
//! Every request gets an id one above the last, seeded from the file on the
//! first write so a restarted sidecar never reuses one. The plugin applies a
//! request only when its id is above the last it applied, so re-reading the
//! file (or a replay of it) applies nothing twice, and a new game scene skips
//! everything already in the file. The file keeps the most recent
//! `MAX_KEPT` requests, enough to bridge the plugin's poll interval.
//!
//! Beside the requests the file carries `anchors`, every browser client's
//! anchors as a whole table rather than a queue: the plugin replaces its copy
//! on every read, so a client's anchors survive a scene change and vanish
//! the moment the sidecar drops them. A restarted sidecar starts the table
//! empty, since the clients that set it went with the old process.

use std::collections::VecDeque;

use serde::{Deserialize, Serialize};

use crate::anchors::AnchorSet;
use crate::protocol::{
    CommandAim, CommandAimKind, CommandVantage, CommandVantageKind, CreateCommandCameraPayload,
    DestroyCommandCameraPayload, SetCommandAimPayload,
};

pub const COMMANDS_FILE: &str = "global.commands.json";

/// Requests kept in the file. The plugin reads it every 0.25 s, so this only
/// runs out if a client sends more than this many in that window.
pub const MAX_KEPT: usize = 32;

const MAX_TOKEN_LEN: usize = 128;
const MAX_NAME_LEN: usize = 128;

/// One request as the plugin reads it: `{"id": n, "op": "...", ...payload}`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CommandRequest {
    pub id: u64,
    #[serde(flatten)]
    pub op: CommandOp,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(tag = "op", rename_all = "camelCase")]
/* At most `MAX_KEPT` of these live at once, so the create variant's size is
 * not worth boxing. */
#[allow(clippy::large_enum_variant)]
pub enum CommandOp {
    Create(CreateCommandCameraPayload),
    Destroy(DestroyCommandCameraPayload),
    Aim(SetCommandAimPayload),
}

#[derive(Debug, Default, Serialize, Deserialize)]
pub struct CommandFile {
    pub requests: Vec<CommandRequest>,
    /// Every browser client's anchors. Plugins before 1.12 ignore it.
    #[serde(default)]
    pub anchors: Vec<AnchorSet>,
}

/// The sidecar's copy of the file, seeded from disk on first use.
#[derive(Debug, Default)]
pub struct CommandQueue {
    seeded: bool,
    last_id: u64,
    kept: VecDeque<CommandRequest>,
    anchors: Vec<AnchorSet>,
}

impl CommandQueue {
    pub fn is_seeded(&self) -> bool {
        self.seeded
    }

    /// Adopt what a previous sidecar left on disk, so ids carry on from it.
    /// Unreadable bytes start again from id 1. Its anchors are not adopted.
    pub fn seed(&mut self, on_disk: Option<&[u8]>) {
        self.seeded = true;
        let Some(bytes) = on_disk else { return };
        let Ok(file) = serde_json::from_slice::<CommandFile>(bytes) else {
            return;
        };
        self.last_id = file.requests.iter().map(|r| r.id).max().unwrap_or(0);
        self.kept = file.requests.into_iter().collect();
        while self.kept.len() > MAX_KEPT {
            self.kept.pop_front();
        }
    }

    /// Append `op` under the next id and return it, dropping the oldest
    /// request beyond `MAX_KEPT`.
    pub fn push(&mut self, op: CommandOp) -> u64 {
        self.last_id += 1;
        self.kept.push_back(CommandRequest {
            id: self.last_id,
            op,
        });
        while self.kept.len() > MAX_KEPT {
            self.kept.pop_front();
        }
        self.last_id
    }

    /// Replace the anchor table written with the requests.
    pub fn set_anchors(&mut self, anchors: Vec<AnchorSet>) {
        self.anchors = anchors;
    }

    pub fn to_json(&self) -> serde_json::Result<String> {
        serde_json::to_string(&CommandFile {
            requests: self.kept.iter().cloned().collect(),
            anchors: self.anchors.clone(),
        })
    }
}

fn finite(v: f64) -> bool {
    v.is_finite()
}

fn latitude_ok(v: Option<f64>) -> bool {
    v.is_some_and(|l| finite(l) && (-90.0..=90.0).contains(&l))
}

fn non_empty(v: &Option<String>) -> bool {
    v.as_deref().is_some_and(|s| !s.trim().is_empty())
}

/// Mirrors the plugin's `CommandVantage.Invalid`: shape only. Whether the
/// named site, station, body or craft exists is checked in game.
pub fn validate_vantage(v: &CommandVantage) -> Result<(), String> {
    let opt_finite = |x: Option<f64>| x.is_none_or(finite);
    if !opt_finite(v.height) {
        return Err("height must be finite".into());
    }
    match v.kind {
        CommandVantageKind::LaunchSite => {
            if !v.distance.is_none_or(|d| finite(d) && d >= 0.0) {
                return Err("distance must be a finite number of metres, 0 or more".into());
            }
            if !opt_finite(v.bearing) {
                return Err("bearing must be finite".into());
            }
        }
        CommandVantageKind::Station => {
            if !non_empty(&v.station) {
                return Err("a station vantage needs `station`, the station's node name".into());
            }
        }
        CommandVantageKind::Surface => {
            if !non_empty(&v.body) {
                return Err("a surface vantage needs `body`".into());
            }
            if !latitude_ok(v.latitude) {
                return Err("a surface vantage needs `latitude` within -90..90".into());
            }
            if !v.longitude.is_some_and(finite) {
                return Err("a surface vantage needs a finite `longitude`".into());
            }
        }
        CommandVantageKind::Vessel => {
            if v.persistent_id.unwrap_or(0) == 0 {
                return Err("a vessel vantage needs `persistentId`".into());
            }
        }
    }
    Ok(())
}

/// Mirrors the plugin's aim checks: shape and finite numbers. Whether the
/// named body or craft exists is checked in game.
pub fn validate_aim(a: &CommandAim) -> Result<(), String> {
    let opt_finite = |x: Option<f64>| x.is_none_or(finite);
    let all_finite = |xs: &[Option<f64>]| xs.iter().all(|x| x.is_some_and(finite));
    let vec3 = |v: &Option<Vec<f64>>| {
        v.as_ref()
            .is_some_and(|v| v.len() == 3 && v.iter().all(|x| finite(*x)))
    };
    if !a.framing_margin_m.is_none_or(|m| finite(m) && m >= 0.0) {
        return Err("framingMarginM must be a finite number, 0 or more".into());
    }
    match a.kind {
        CommandAimKind::Vessel => {
            if a.persistent_id.unwrap_or(0) == 0 {
                return Err("a vessel aim needs `persistentId`".into());
            }
        }
        CommandAimKind::Orbit => {
            if !non_empty(&a.body) {
                return Err("an orbit aim needs `body`".into());
            }
            if !all_finite(&[
                a.inclination,
                a.eccentricity,
                a.semi_major_axis,
                a.lan,
                a.argument_of_periapsis,
                a.mean_anomaly_at_epoch,
                a.epoch,
            ]) {
                return Err("an orbit aim needs finite `inclination`, `eccentricity`, \
                     `semiMajorAxis`, `lan`, `argumentOfPeriapsis`, `meanAnomalyAtEpoch` and `epoch`"
                    .into());
            }
        }
        CommandAimKind::Surface => {
            if !non_empty(&a.body) {
                return Err("a surface aim needs `body`".into());
            }
            if !latitude_ok(a.latitude) {
                return Err("a surface aim needs `latitude` within -90..90".into());
            }
            if !a.longitude.is_some_and(finite) || !opt_finite(a.altitude) {
                return Err("a surface aim needs a finite `longitude` and `altitude`".into());
            }
        }
        CommandAimKind::PositionAt => {
            if !non_empty(&a.body) {
                return Err("a positionAt aim needs `body`".into());
            }
            if !a.ut.is_some_and(finite) || !vec3(&a.position) || !vec3(&a.velocity) {
                return Err(
                    "a positionAt aim needs a finite `ut` and `position` and `velocity` of 3 finite numbers"
                        .into(),
                );
            }
        }
        CommandAimKind::Direction => {
            if !a.azimuth.is_some_and(finite) || !latitude_ok(a.elevation) {
                return Err(
                    "a direction aim needs a finite `azimuth` and `elevation` within -90..90"
                        .into(),
                );
            }
        }
    }
    Ok(())
}

pub fn validate_create(p: &CreateCommandCameraPayload) -> Result<(), String> {
    let token = p.token.trim();
    if token.is_empty() || token.chars().count() > MAX_TOKEN_LEN {
        return Err(format!("token must be 1 to {MAX_TOKEN_LEN} characters"));
    }
    if p.name
        .as_deref()
        .is_some_and(|n| n.chars().count() > MAX_NAME_LEN)
    {
        return Err(format!("name must be at most {MAX_NAME_LEN} characters"));
    }
    validate_vantage(&p.vantage)?;
    if let Some(a) = &p.aim {
        validate_aim(a)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn surface(lat: f64) -> CommandVantage {
        CommandVantage {
            kind: CommandVantageKind::Surface,
            site: None,
            distance: None,
            bearing: None,
            height: Some(3.0),
            station: None,
            body: Some("Kerbin".into()),
            latitude: Some(lat),
            longitude: Some(-74.6),
            persistent_id: None,
        }
    }

    fn launch_site() -> CommandVantage {
        CommandVantage {
            kind: CommandVantageKind::LaunchSite,
            site: None,
            distance: None,
            bearing: None,
            height: None,
            station: None,
            body: None,
            latitude: None,
            longitude: None,
            persistent_id: None,
        }
    }

    fn create(token: &str, vantage: CommandVantage) -> CreateCommandCameraPayload {
        CreateCommandCameraPayload {
            token: token.into(),
            name: None,
            vantage,
            aim: None,
        }
    }

    fn aim(kind: CommandAimKind) -> CommandAim {
        CommandAim {
            kind,
            persistent_id: None,
            body: None,
            inclination: None,
            eccentricity: None,
            semi_major_axis: None,
            lan: None,
            argument_of_periapsis: None,
            mean_anomaly_at_epoch: None,
            epoch: None,
            latitude: None,
            longitude: None,
            altitude: None,
            ut: None,
            position: None,
            velocity: None,
            azimuth: None,
            elevation: None,
            subject_persistent_id: None,
            framing_margin_m: None,
            allow_uncertain: None,
        }
    }

    #[test]
    fn vantage_validation() {
        assert!(validate_vantage(&launch_site()).is_ok());
        assert!(validate_vantage(&surface(-0.1)).is_ok());
        assert!(validate_vantage(&surface(91.0)).is_err());
        let mut v = surface(0.0);
        v.body = Some("  ".into());
        assert!(validate_vantage(&v).is_err());
        let mut v = launch_site();
        v.distance = Some(-1.0);
        assert!(validate_vantage(&v).is_err());
        let mut v = launch_site();
        v.height = Some(f64::NAN);
        assert!(validate_vantage(&v).is_err());
        let mut v = launch_site();
        v.kind = CommandVantageKind::Station;
        assert!(validate_vantage(&v).is_err());
        v.station = Some("KSC".into());
        assert!(validate_vantage(&v).is_ok());
        let mut v = launch_site();
        v.kind = CommandVantageKind::Vessel;
        assert!(validate_vantage(&v).is_err());
        v.persistent_id = Some(7);
        assert!(validate_vantage(&v).is_ok());
    }

    #[test]
    fn create_validation() {
        assert!(validate_create(&create("t", launch_site())).is_ok());
        assert!(validate_create(&create("", launch_site())).is_err());
        assert!(validate_create(&create(&"x".repeat(129), launch_site())).is_err());
        let mut p = create("t", launch_site());
        p.aim = Some(CommandAim {
            body: Some("Kerbin".into()),
            latitude: Some(-0.1),
            altitude: Some(70.0),
            ..aim(CommandAimKind::Surface)
        });
        assert!(
            validate_create(&p).is_err(),
            "surface aim needs a longitude"
        );
        p.aim.as_mut().unwrap().longitude = Some(-74.5);
        assert!(validate_create(&p).is_ok());
        p.aim.as_mut().unwrap().framing_margin_m = Some(-5.0);
        assert!(validate_create(&p).is_err());
    }

    #[test]
    fn aim_validation() {
        assert!(validate_aim(&aim(CommandAimKind::Vessel)).is_err());

        let search: CommandAim =
            serde_json::from_str(r#"{"kind":"vessel","persistentId":7,"allowUncertain":true}"#)
                .unwrap();
        assert_eq!(search.allow_uncertain, Some(true));
        assert!(validate_aim(&search).is_ok());
        let json = serde_json::to_string(&search).unwrap();
        assert!(json.contains(r#""allowUncertain":true"#), "{json}");
        assert_eq!(serde_json::from_str::<CommandAim>(&json).unwrap(), search);
        let plain: CommandAim =
            serde_json::from_str(r#"{"kind":"vessel","persistentId":7}"#).unwrap();
        assert_eq!(plain.allow_uncertain, None, "absent is false");
        assert!(!serde_json::to_string(&plain)
            .unwrap()
            .contains("allowUncertain"));
        assert!(validate_aim(&CommandAim {
            persistent_id: Some(7),
            ..aim(CommandAimKind::Vessel)
        })
        .is_ok());

        let direction = CommandAim {
            azimuth: Some(270.0),
            elevation: Some(45.0),
            ..aim(CommandAimKind::Direction)
        };
        assert!(validate_aim(&direction).is_ok());
        assert!(validate_aim(&CommandAim {
            elevation: Some(91.0),
            ..direction.clone()
        })
        .is_err());
        assert!(validate_aim(&CommandAim {
            azimuth: None,
            ..direction
        })
        .is_err());

        let position = CommandAim {
            body: Some("Kerbin".into()),
            ut: Some(10.0),
            position: Some(vec![1.0, 2.0, 3.0]),
            velocity: Some(vec![0.0, 0.0, 0.0]),
            subject_persistent_id: Some(9),
            ..aim(CommandAimKind::PositionAt)
        };
        assert!(validate_aim(&position).is_ok());
        assert!(validate_aim(&CommandAim {
            velocity: Some(vec![0.0, 0.0]),
            ..position.clone()
        })
        .is_err());
        assert!(validate_aim(&CommandAim {
            position: Some(vec![1.0, f64::NAN, 3.0]),
            ..position
        })
        .is_err());

        let orbit = CommandAim {
            body: Some("Kerbin".into()),
            inclination: Some(0.0),
            eccentricity: Some(0.01),
            semi_major_axis: Some(700_000.0),
            lan: Some(0.0),
            argument_of_periapsis: Some(0.0),
            mean_anomaly_at_epoch: Some(0.0),
            epoch: Some(0.0),
            ..aim(CommandAimKind::Orbit)
        };
        assert!(validate_aim(&orbit).is_ok());
        assert!(validate_aim(&CommandAim {
            epoch: None,
            ..orbit
        })
        .is_err());
    }

    #[test]
    fn request_file_matches_the_plugin_reader() {
        let mut q = CommandQueue::default();
        q.seed(None);
        q.push(CommandOp::Create(create("tok-1", surface(-0.05))));
        q.push(CommandOp::Destroy(DestroyCommandCameraPayload {
            flight_id: 2147483905,
        }));
        q.push(CommandOp::Aim(SetCommandAimPayload {
            flight_id: 2147483905,
            aim: None,
        }));
        let json: serde_json::Value = serde_json::from_str(&q.to_json().unwrap()).unwrap();
        let reqs = json["requests"].as_array().unwrap();
        assert_eq!(reqs[0]["id"], 1);
        assert_eq!(reqs[0]["op"], "create");
        assert_eq!(reqs[0]["token"], "tok-1");
        assert_eq!(reqs[0]["vantage"]["kind"], "surface");
        assert_eq!(reqs[0]["vantage"]["latitude"], -0.05);
        assert!(
            reqs[0]["vantage"].get("site").is_none(),
            "absent fields are omitted"
        );
        assert_eq!(reqs[1]["op"], "destroy");
        assert_eq!(reqs[1]["flightId"], 2147483905u32);
        assert_eq!(reqs[2]["op"], "aim");
        assert!(reqs[2]["aim"].is_null());
        assert_eq!(json["anchors"], serde_json::json!([]));
    }

    /// Anchors left on disk belong to clients of the sidecar that wrote them,
    /// so a restarted sidecar keeps the request ids and drops the anchors.
    #[test]
    fn anchors_ride_beside_the_requests_and_do_not_survive_a_restart() {
        use crate::protocol::{Anchor, AnchorKind};
        let mut first = CommandQueue::default();
        first.seed(None);
        first.push(CommandOp::Destroy(DestroyCommandCameraPayload {
            flight_id: 1,
        }));
        first.set_anchors(vec![AnchorSet {
            owner: "peer:1:1".into(),
            flight_id: 1,
            anchors: vec![Anchor {
                id: "a".into(),
                kind: AnchorKind::Vessel,
                vessel: Some(5),
                part: None,
                offset: None,
                body: None,
                latitude: None,
                longitude: None,
                altitude: None,
                direction: None,
                vector: None,
                orientation: None,
                occlusion: None,
                kinematics: None,
                margin_m: None,
            }],
        }]);
        let on_disk = first.to_json().unwrap();
        let json: serde_json::Value = serde_json::from_str(&on_disk).unwrap();
        assert_eq!(json["anchors"][0]["owner"], "peer:1:1");
        assert_eq!(json["anchors"][0]["anchors"][0]["kind"], "vessel");

        let mut restarted = CommandQueue::default();
        restarted.seed(Some(on_disk.as_bytes()));
        let json: serde_json::Value = serde_json::from_str(&restarted.to_json().unwrap()).unwrap();
        assert_eq!(json["requests"].as_array().unwrap().len(), 1);
        assert_eq!(json["anchors"], serde_json::json!([]));
    }

    #[test]
    fn ids_continue_from_disk_and_only_grow() {
        let mut first = CommandQueue::default();
        first.seed(None);
        for i in 0..3 {
            first.push(CommandOp::Destroy(DestroyCommandCameraPayload {
                flight_id: i,
            }));
        }
        let on_disk = first.to_json().unwrap();

        /* A restarted sidecar continues the sequence rather than reusing ids. */
        let mut restarted = CommandQueue::default();
        restarted.seed(Some(on_disk.as_bytes()));
        assert_eq!(
            restarted.push(CommandOp::Destroy(DestroyCommandCameraPayload {
                flight_id: 9
            })),
            4
        );

        let mut garbage = CommandQueue::default();
        garbage.seed(Some(b"not json"));
        assert!(garbage.is_seeded());
        assert_eq!(
            garbage.push(CommandOp::Destroy(DestroyCommandCameraPayload {
                flight_id: 1
            })),
            1
        );
    }

    #[test]
    fn keeps_only_the_most_recent_requests() {
        let mut q = CommandQueue::default();
        q.seed(None);
        for i in 0..(MAX_KEPT as u32 + 5) {
            q.push(CommandOp::Destroy(DestroyCommandCameraPayload {
                flight_id: i,
            }));
        }
        let file: CommandFile = serde_json::from_str(&q.to_json().unwrap()).unwrap();
        assert_eq!(file.requests.len(), MAX_KEPT);
        assert_eq!(file.requests.first().unwrap().id, 6);
        assert_eq!(file.requests.last().unwrap().id, MAX_KEPT as u64 + 5);
    }
}
