//! Anchored markers: things a client asks to have marked on a camera's
//! picture. The sidecar validates each client's anchors, keeps them per
//! client and camera, and hands the whole table to the plugin in
//! `global.commands.json`. The plugin projects them through the camera that
//! rendered each frame and writes the results into the frame ring, and the
//! sidecar routes each result back to the client that owns it on that
//! frame's `FrameStamp`.
//!
//! Owners: a browser client is `peer:<boot>:<peer_id>`, where `boot` is
//! drawn once per sidecar process, so a restarted sidecar whose peer ids
//! start again from 1 never inherits a dead client's anchors. Anchors set in
//! game carry the owner their caller named and go to every client that asked
//! for anchors, tagged with that owner.

use std::collections::{BTreeMap, HashSet};

use serde::{Deserialize, Serialize};

use crate::protocol::{
    Anchor, AnchorKind, AnchorKinematics, AnchorKinematicsKind, FrameAnchor, MAX_ANCHORS_PER_CAMERA,
};

/// Prefix of every browser client's owner tag.
pub const PEER_OWNER_PREFIX: &str = "peer:";

const MAX_ID_LEN: usize = 64;
const MAX_BODY_LEN: usize = 128;

/// A browser client's owner tag.
pub fn peer_owner(boot: u32, peer_id: u32) -> String {
    format!("{PEER_OWNER_PREFIX}{boot}:{peer_id}")
}

/// A number drawn once per sidecar process to tell its peers from the
/// previous process's.
pub fn boot_nonce() -> u32 {
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.subsec_nanos() ^ (d.as_secs() as u32))
        .unwrap_or(0);
    nanos ^ std::process::id().rotate_left(16)
}

/**
 * The results one client receives from a frame's full set: its own anchors
 * with the owner stripped, and those set in game with their owner kept.
 * Other browser clients' anchors are left out.
 */
pub fn anchors_for(owner: &str, all: &[FrameAnchor]) -> Vec<FrameAnchor> {
    all.iter()
        .filter_map(|a| match a.owner.as_deref() {
            Some(o) if o == owner => Some(FrameAnchor {
                owner: None,
                ..a.clone()
            }),
            Some(o) if o.starts_with(PEER_OWNER_PREFIX) => None,
            _ => Some(a.clone()),
        })
        .collect()
}

fn finite(v: f64) -> bool {
    v.is_finite()
}

fn vec3(v: &Option<Vec<f64>>) -> bool {
    v.as_ref()
        .is_some_and(|v| v.len() == 3 && v.iter().all(|x| finite(*x)))
}

/// Mirrors the plugin's `AnchorSpec.Invalid`: shape only. Whether the craft,
/// part or body exists is the game's call, and one it can not find is simply
/// left out of the frame's results.
pub fn validate_anchor(a: &Anchor) -> Result<(), String> {
    let id_len = a.id.chars().count();
    if id_len == 0 || id_len > MAX_ID_LEN {
        return Err(format!("anchor id must be 1 to {MAX_ID_LEN} characters"));
    }
    let id = &a.id;
    if !a.margin_m.is_none_or(|m| finite(m) && m >= 0.0) {
        return Err(format!(
            "anchor {id}: marginM must be a finite number, 0 or more"
        ));
    }
    if a.kind != AnchorKind::Kinematic && a.kinematics.is_some() {
        return Err(format!(
            "anchor {id}: `kinematics` belongs on a kinematic anchor only"
        ));
    }
    match a.kind {
        AnchorKind::Part => {
            if a.part.unwrap_or(0) == 0 {
                return Err(format!(
                    "anchor {id}: a part anchor needs `part`, the part's persistentId"
                ));
            }
            if a.offset.is_some() && !vec3(&a.offset) {
                return Err(format!("anchor {id}: `offset` must be 3 finite numbers"));
            }
        }
        AnchorKind::Vessel => {
            if a.vessel.unwrap_or(0) == 0 {
                return Err(format!(
                    "anchor {id}: a vessel anchor needs `vessel`, the craft's persistentId"
                ));
            }
        }
        AnchorKind::Surface => {
            let body = a.body.as_deref().unwrap_or("").trim();
            if body.is_empty() || body.chars().count() > MAX_BODY_LEN {
                return Err(format!("anchor {id}: a surface anchor needs `body`"));
            }
            if !a
                .latitude
                .is_some_and(|l| finite(l) && (-90.0..=90.0).contains(&l))
            {
                return Err(format!(
                    "anchor {id}: a surface anchor needs `latitude` within -90..90"
                ));
            }
            if !a.longitude.is_some_and(finite) || !a.altitude.is_none_or(finite) {
                return Err(format!(
                    "anchor {id}: a surface anchor needs a finite `longitude` and `altitude`"
                ));
            }
        }
        AnchorKind::Direction => match (&a.direction, &a.vector) {
            (Some(_), None) => {}
            (None, Some(_)) => {
                if !vec3(&a.vector) || a.vector.as_ref().unwrap().iter().all(|x| *x == 0.0) {
                    return Err(format!(
                        "anchor {id}: `vector` must be 3 finite numbers, not all zero"
                    ));
                }
            }
            _ => {
                return Err(format!(
                    "anchor {id}: a direction anchor needs exactly one of `direction` and `vector`"
                ))
            }
        },
        AnchorKind::Kinematic => match &a.kinematics {
            Some(k) => validate_kinematics(id, k)?,
            None => {
                return Err(format!(
                    "anchor {id}: a kinematic anchor needs `kinematics`"
                ))
            }
        },
    }
    Ok(())
}

fn validate_kinematics(id: &str, k: &AnchorKinematics) -> Result<(), String> {
    let body = k.body.trim();
    if body.is_empty() || body.chars().count() > MAX_BODY_LEN {
        return Err(format!("anchor {id}: kinematics need `body`"));
    }
    let given = |v: Option<f64>| v.is_some_and(finite);
    match k.kind {
        AnchorKinematicsKind::Conic => {
            let all = [
                k.inclination,
                k.eccentricity,
                k.semi_major_axis,
                k.lan,
                k.argument_of_periapsis,
                k.mean_anomaly_at_epoch,
                k.epoch,
            ];
            if !all.iter().all(|v| given(*v)) {
                return Err(format!("anchor {id}: a conic needs every element, finite"));
            }
            let (e, sma) = (k.eccentricity.unwrap(), k.semi_major_axis.unwrap());
            if e < 0.0 || e == 1.0 {
                return Err(format!(
                    "anchor {id}: `eccentricity` must be 0 or more and not 1"
                ));
            }
            if (e < 1.0 && sma <= 0.0) || (e > 1.0 && sma >= 0.0) {
                return Err(format!(
                    "anchor {id}: `semiMajorAxis` must be positive for an ellipse and negative for a hyperbola"
                ));
            }
        }
        AnchorKinematicsKind::StateVector => {
            if !given(k.ut) || !vec3(&k.position) || !vec3(&k.velocity) {
                return Err(format!(
                    "anchor {id}: a state vector needs a finite `ut`, `position` and `velocity`"
                ));
            }
            if k.position.as_ref().unwrap().iter().all(|x| *x == 0.0) {
                return Err(format!(
                    "anchor {id}: `position` must not be the body's centre"
                ));
            }
        }
        AnchorKinematicsKind::Surface => {
            if !k
                .latitude
                .is_some_and(|l| finite(l) && (-90.0..=90.0).contains(&l))
            {
                return Err(format!(
                    "anchor {id}: a surface point needs `latitude` within -90..90"
                ));
            }
            if !given(k.longitude) || !k.altitude.is_none_or(finite) {
                return Err(format!(
                    "anchor {id}: a surface point needs a finite `longitude` and `altitude`"
                ));
            }
        }
    }
    Ok(())
}

/// A whole replacement set for one client and camera: at most
/// `MAX_ANCHORS_PER_CAMERA`, ids unique, each valid.
pub fn validate_anchor_set(anchors: &[Anchor]) -> Result<(), String> {
    if anchors.len() > MAX_ANCHORS_PER_CAMERA {
        return Err(format!(
            "at most {MAX_ANCHORS_PER_CAMERA} anchors per camera, got {}",
            anchors.len()
        ));
    }
    let mut ids = HashSet::new();
    for a in anchors {
        validate_anchor(a)?;
        if !ids.insert(a.id.as_str()) {
            return Err(format!("anchor id {} is used twice", a.id));
        }
    }
    Ok(())
}

/// One client's anchors on one camera, as the plugin reads them.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AnchorSet {
    pub owner: String,
    pub flight_id: u32,
    pub anchors: Vec<Anchor>,
}

/// Every browser client's anchors, keyed by peer then camera.
#[derive(Debug, Default)]
pub struct AnchorTable {
    sets: BTreeMap<(u32, u32), Vec<Anchor>>,
}

impl AnchorTable {
    /// Replace (or with `None` or an empty list, clear) one client's anchors
    /// on one camera. True when the table changed.
    pub fn set(&mut self, peer_id: u32, flight_id: u32, anchors: Option<Vec<Anchor>>) -> bool {
        match anchors.filter(|a| !a.is_empty()) {
            Some(a) => self.sets.insert((peer_id, flight_id), a.clone()) != Some(a),
            None => self.sets.remove(&(peer_id, flight_id)).is_some(),
        }
    }

    /// Forget a client's anchors on every camera. True when it held any.
    pub fn forget_peer(&mut self, peer_id: u32) -> bool {
        let before = self.sets.len();
        self.sets.retain(|(p, _), _| *p != peer_id);
        self.sets.len() != before
    }

    pub fn is_empty(&self) -> bool {
        self.sets.is_empty()
    }

    /// The table as the plugin reads it.
    pub fn to_sets(&self, boot: u32) -> Vec<AnchorSet> {
        self.sets
            .iter()
            .map(|((peer, flight), anchors)| AnchorSet {
                owner: peer_owner(boot, *peer),
                flight_id: *flight,
                anchors: anchors.clone(),
            })
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::protocol::AnchorDirection;

    fn anchor(id: &str, kind: AnchorKind) -> Anchor {
        Anchor {
            id: id.into(),
            kind,
            vessel: None,
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
            margin_m: None,
            kinematics: None,
        }
    }

    fn result(id: &str, owner: Option<&str>) -> FrameAnchor {
        FrameAnchor {
            id: id.into(),
            owner: owner.map(str::to_owned),
            observed: false,
            x: Some(1.0),
            y: Some(2.0),
            in_frame: true,
            behind: Some(false),
            distance_m: Some(10.0),
            apparent_radius_px: Some(4.0),
            angle_off_axis: Some(1.5),
            forward: None,
            up: None,
            occluded: None,
        }
    }

    #[test]
    fn each_client_gets_its_own_anchors_and_the_games_but_no_other_clients() {
        let me = peer_owner(9, 1);
        let other = peer_owner(9, 2);
        let all = vec![
            result("mine", Some(&me)),
            result("theirs", Some(&other)),
            result("subject", Some("kerbcast")),
            result("reticle", Some("mission-control")),
        ];
        let got = anchors_for(&me, &all);
        let ids: Vec<_> = got
            .iter()
            .map(|a| (a.id.as_str(), a.owner.as_deref()))
            .collect();
        assert_eq!(
            ids,
            vec![
                ("mine", None),
                ("subject", Some("kerbcast")),
                ("reticle", Some("mission-control"))
            ]
        );
    }

    /// A restarted sidecar numbers its peers from 1 again; the boot nonce in
    /// the owner keeps a new peer 1 from receiving a dead peer 1's anchors.
    #[test]
    fn a_previous_sidecars_peer_is_another_client() {
        let all = vec![result("stale", Some(&peer_owner(1, 1)))];
        assert!(anchors_for(&peer_owner(2, 1), &all).is_empty());
    }

    #[test]
    fn validation_covers_every_kind() {
        let mut part = anchor("port", AnchorKind::Part);
        assert!(validate_anchor(&part).is_err(), "part needs a part id");
        part.part = Some(42);
        assert!(validate_anchor(&part).is_ok());
        part.offset = Some(vec![0.0, f64::NAN, 0.0]);
        assert!(validate_anchor(&part).is_err(), "offset must be finite");

        let mut vessel = anchor("ship", AnchorKind::Vessel);
        assert!(validate_anchor(&vessel).is_err());
        vessel.vessel = Some(7);
        assert!(validate_anchor(&vessel).is_ok());

        let mut site = anchor("site", AnchorKind::Surface);
        site.body = Some("Kerbin".into());
        site.latitude = Some(91.0);
        site.longitude = Some(10.0);
        assert!(validate_anchor(&site).is_err(), "latitude in range");
        site.latitude = Some(-0.1);
        assert!(validate_anchor(&site).is_ok());

        let mut dir = anchor("pro", AnchorKind::Direction);
        assert!(
            validate_anchor(&dir).is_err(),
            "needs a direction or vector"
        );
        dir.direction = Some(AnchorDirection::Prograde);
        assert!(validate_anchor(&dir).is_ok());
        dir.vector = Some(vec![1.0, 0.0, 0.0]);
        assert!(validate_anchor(&dir).is_err(), "not both");
        dir.direction = None;
        assert!(validate_anchor(&dir).is_ok());
        dir.vector = Some(vec![0.0, 0.0, 0.0]);
        assert!(
            validate_anchor(&dir).is_err(),
            "a zero vector has no direction"
        );

        let mut ghost = anchor("ghost", AnchorKind::Kinematic);
        assert!(validate_anchor(&ghost).is_err(), "needs kinematics");
        let state = AnchorKinematics {
            kind: AnchorKinematicsKind::StateVector,
            body: "Kerbin".into(),
            inclination: None,
            eccentricity: None,
            semi_major_axis: None,
            lan: None,
            argument_of_periapsis: None,
            mean_anomaly_at_epoch: None,
            epoch: None,
            ut: Some(100.0),
            position: Some(vec![700_000.0, 0.0, 0.0]),
            velocity: Some(vec![0.0, 2200.0, 0.0]),
            latitude: None,
            longitude: None,
            altitude: None,
        };
        ghost.kinematics = Some(state.clone());
        assert!(validate_anchor(&ghost).is_ok());
        ghost.kinematics = Some(AnchorKinematics {
            position: Some(vec![0.0, 0.0, 0.0]),
            ..state.clone()
        });
        assert!(validate_anchor(&ghost).is_err(), "not the body's centre");
        let conic = AnchorKinematics {
            kind: AnchorKinematicsKind::Conic,
            inclination: Some(5.0),
            eccentricity: Some(1.4),
            semi_major_axis: Some(-2e6),
            lan: Some(0.0),
            argument_of_periapsis: Some(0.0),
            mean_anomaly_at_epoch: Some(0.0),
            epoch: Some(0.0),
            ..state.clone()
        };
        ghost.kinematics = Some(conic.clone());
        assert!(validate_anchor(&ghost).is_ok(), "a hyperbola, sma negative");
        ghost.kinematics = Some(AnchorKinematics {
            semi_major_axis: Some(2e6),
            ..conic.clone()
        });
        assert!(
            validate_anchor(&ghost).is_err(),
            "a hyperbola with sma positive"
        );
        ghost.kinematics = Some(AnchorKinematics {
            eccentricity: Some(1.0),
            ..conic
        });
        assert!(validate_anchor(&ghost).is_err(), "a parabola");
        ghost.kinematics = Some(AnchorKinematics {
            kind: AnchorKinematicsKind::Surface,
            latitude: Some(95.0),
            longitude: Some(1.0),
            ..state
        });
        assert!(validate_anchor(&ghost).is_err(), "latitude in range");

        let stray = Anchor {
            body: Some("Kerbin".into()),
            latitude: Some(0.0),
            longitude: Some(0.0),
            kinematics: ghost.kinematics.clone(),
            ..anchor("pad", AnchorKind::Surface)
        };
        assert!(
            validate_anchor(&stray).is_err(),
            "kinematics on another kind is refused, as the plugin refuses it"
        );

        let mut long = anchor(&"x".repeat(65), AnchorKind::Vessel);
        long.vessel = Some(1);
        assert!(validate_anchor(&long).is_err());
    }

    #[test]
    fn a_set_is_capped_and_ids_are_unique() {
        let ship = |id: &str| Anchor {
            vessel: Some(1),
            ..anchor(id, AnchorKind::Vessel)
        };
        let sixteen: Vec<_> = (0..16).map(|i| ship(&format!("a{i}"))).collect();
        assert!(validate_anchor_set(&sixteen).is_ok());
        let mut seventeen = sixteen.clone();
        seventeen.push(ship("a16"));
        assert!(validate_anchor_set(&seventeen).is_err());
        assert!(validate_anchor_set(&[ship("a"), ship("a")]).is_err());
    }

    #[test]
    fn table_replaces_clears_and_forgets_by_peer() {
        let ship = Anchor {
            vessel: Some(1),
            ..anchor("a", AnchorKind::Vessel)
        };
        let mut t = AnchorTable::default();
        assert!(t.set(1, 10, Some(vec![ship.clone()])));
        assert!(
            !t.set(1, 10, Some(vec![ship.clone()])),
            "same set is no change"
        );
        assert!(t.set(1, 11, Some(vec![ship.clone()])));
        assert!(t.set(2, 10, Some(vec![ship.clone()])));
        assert_eq!(t.to_sets(5).len(), 3);
        assert!(t.set(2, 10, Some(vec![])), "an empty list clears");
        assert!(!t.set(2, 10, None));
        /* Peer-scoped: every camera the peer set anchors on, bound or not. */
        assert!(t.forget_peer(1));
        assert!(!t.forget_peer(1));
        assert!(t.is_empty());
    }

    #[test]
    fn table_serialises_for_the_plugin() {
        let mut t = AnchorTable::default();
        t.set(
            3,
            10,
            Some(vec![Anchor {
                part: Some(99),
                orientation: Some(true),
                ..anchor("port", AnchorKind::Part)
            }]),
        );
        let json = serde_json::to_string(&t.to_sets(7)).unwrap();
        assert_eq!(
            json,
            r#"[{"owner":"peer:7:3","flightId":10,"anchors":[{"id":"port","kind":"part","part":99,"orientation":true}]}]"#
        );
    }
}
