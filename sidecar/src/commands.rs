//! Global requests a client makes of the plugin that are not about one
//! existing camera's control block: creating and destroying command cameras,
//! and pointing one at a look-at point. The sidecar validates each, then
//! appends it to `global.commands.json`, which the plugin re-reads a few
//! times a second.
//!
//! Every request gets an id one above the last, seeded from the file on the
//! first write so a restarted sidecar never reuses one. The plugin applies a
//! request only when its id is above the last it applied, so re-reading the
//! file (or a replay of it) applies nothing twice, and a new game scene skips
//! everything already in the file. The file keeps the most recent
//! `MAX_KEPT` requests, enough to bridge the plugin's poll interval.

use std::collections::VecDeque;

use serde::{Deserialize, Serialize};

use crate::protocol::{
    CommandLookAt, CommandLookAtKind, CommandVantage, CommandVantageKind,
    CreateCommandCameraPayload, DestroyCommandCameraPayload, SetCommandLookAtPayload,
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
    LookAt(SetCommandLookAtPayload),
}

#[derive(Debug, Default, Serialize, Deserialize)]
pub struct CommandFile {
    pub requests: Vec<CommandRequest>,
}

/// The sidecar's copy of the file, seeded from disk on first use.
#[derive(Debug, Default)]
pub struct CommandQueue {
    seeded: bool,
    last_id: u64,
    kept: VecDeque<CommandRequest>,
}

impl CommandQueue {
    pub fn is_seeded(&self) -> bool {
        self.seeded
    }

    /// Adopt what a previous sidecar left on disk, so ids carry on from it.
    /// Unreadable bytes start again from id 1.
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

    pub fn to_json(&self) -> serde_json::Result<String> {
        serde_json::to_string(&CommandFile {
            requests: self.kept.iter().cloned().collect(),
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

pub fn validate_look_at(l: &CommandLookAt) -> Result<(), String> {
    if !l.framing_margin_m.is_none_or(|m| finite(m) && m >= 0.0) {
        return Err("framingMarginM must be a finite number, 0 or more".into());
    }
    match l.kind {
        CommandLookAtKind::Vessel => {
            if l.persistent_id.unwrap_or(0) == 0 {
                return Err("a vessel look-at needs `persistentId`".into());
            }
        }
        CommandLookAtKind::Surface => {
            if !non_empty(&l.body) {
                return Err("a surface look-at needs `body`".into());
            }
            if !latitude_ok(l.latitude) {
                return Err("a surface look-at needs `latitude` within -90..90".into());
            }
            if !l.longitude.is_some_and(finite) || !l.altitude.is_none_or(finite) {
                return Err("a surface look-at needs a finite `longitude` and `altitude`".into());
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
    if let Some(l) = &p.look_at {
        validate_look_at(l)?;
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
            target_persistent_id: None,
            look_at: None,
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
        p.look_at = Some(CommandLookAt {
            kind: CommandLookAtKind::Surface,
            persistent_id: None,
            body: Some("Kerbin".into()),
            latitude: Some(-0.1),
            longitude: None,
            altitude: Some(70.0),
            framing_margin_m: None,
        });
        assert!(
            validate_create(&p).is_err(),
            "surface look-at needs a longitude"
        );
        p.look_at.as_mut().unwrap().longitude = Some(-74.5);
        assert!(validate_create(&p).is_ok());
        p.look_at.as_mut().unwrap().framing_margin_m = Some(-5.0);
        assert!(validate_create(&p).is_err());
    }

    #[test]
    fn request_file_matches_the_plugin_reader() {
        let mut q = CommandQueue::default();
        q.seed(None);
        q.push(CommandOp::Create(create("tok-1", surface(-0.05))));
        q.push(CommandOp::Destroy(DestroyCommandCameraPayload {
            flight_id: 2147483905,
        }));
        q.push(CommandOp::LookAt(SetCommandLookAtPayload {
            flight_id: 2147483905,
            look_at: None,
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
        assert_eq!(reqs[2]["op"], "lookAt");
        assert!(reqs[2]["lookAt"].is_null());
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
