//! Fake KSP-plugin writer for local end-to-end testing without KSP.
//!
//! Creates `<shm_dir>/<flight_id>.info.json` + `<flight_id>.ring` and keeps
//! producing an animated RGBA test pattern, exactly the way the plugin's
//! writer side does. Point a normally-launched sidecar at the same
//! `--shm-dir` and any browser client (the bundled web page, gonogo) sees a
//! live camera.
//!
//! Usage:
//!   cargo run --example fake_camera -- /tmp/kerbcast-rings 101 "NavCam"
//!
//! Ring config must match the sidecar's CLI defaults (slot_count 4,
//! max 1024x576); pass a different shm dir per camera id to run several.

use std::env;
use std::fs;
use std::path::PathBuf;
use std::time::{Duration, Instant};

use kerbcast_sidecar::shared_mem::{MmapFrameRing, MmapRingConfig};

const WIDTH: u32 = 640;
const HEIGHT: u32 = 360;
const FPS: u64 = 30;

fn main() {
    let mut args = env::args().skip(1);
    let shm_dir = PathBuf::from(args.next().unwrap_or_else(|| "/tmp/kerbcast-rings".into()));
    let flight_id: u32 = args
        .next()
        .unwrap_or_else(|| "101".into())
        .parse()
        .expect("flight id must be a u32");
    let name = args.next().unwrap_or_else(|| "FakeCam".into());

    fs::create_dir_all(&shm_dir).expect("create shm dir");

    let info = format!(
        concat!(
            "{{\"flight_id\":{id},\"lifecycle\":\"active\",",
            "\"part_name\":\"mumech.MuMechModuleHullCamera\",",
            "\"part_title\":\"{name}\",\"camera_name\":\"{name}\",",
            "\"vessel_name\":\"Fake Vessel\",",
            "\"supports_zoom\":true,\"fov\":60.0,\"fov_min\":15.0,\"fov_max\":90.0,",
            "\"supports_pan\":false,\"pan_yaw_min\":0.0,\"pan_yaw_max\":0.0,",
            "\"pan_pitch_min\":0.0,\"pan_pitch_max\":0.0}}"
        ),
        id = flight_id,
        name = name,
    );
    fs::write(shm_dir.join(format!("{flight_id}.info.json")), info).expect("write info.json");

    // Must match the sidecar's CLI defaults or its open() rejects the layout.
    let cfg = MmapRingConfig {
        slot_count: 4,
        max_width: 1024,
        max_height: 576,
    };
    let ring_path = shm_dir.join(format!("{flight_id}.ring"));
    let mut ring = MmapFrameRing::create(&ring_path, cfg).expect("create ring");

    println!(
        "fake camera {flight_id} ({name}) -> {}",
        ring_path.display()
    );

    let start = Instant::now();
    let mut pixels = vec![0u8; (WIDTH * HEIGHT * 4) as usize];
    let mut n: u64 = 0;
    loop {
        let t = start.elapsed().as_secs_f64();
        render(&mut pixels, flight_id, t);
        ring.produce(WIDTH, HEIGHT, t * 1000.0, &pixels)
            .expect("ring produce");
        n += 1;
        if n.is_multiple_of(FPS * 5) {
            println!("{n} frames");
        }
        std::thread::sleep(Duration::from_millis(1000 / FPS));
    }
}

/// Animated gradient + sweeping bar, hue varied by flight id so multiple
/// fake cameras are tellable apart.
fn render(pixels: &mut [u8], flight_id: u32, t: f64) {
    let bar_y = ((t * 60.0) as u32) % HEIGHT;
    let base_r = (flight_id * 53) % 200;
    let base_b = (flight_id * 131) % 200;
    for y in 0..HEIGHT {
        let on_bar = y >= bar_y.saturating_sub(4) && y <= bar_y + 4;
        for x in 0..WIDTH {
            let i = ((y * WIDTH + x) * 4) as usize;
            if on_bar {
                pixels[i] = 255;
                pixels[i + 1] = 255;
                pixels[i + 2] = 255;
            } else {
                pixels[i] = (base_r + x * 55 / WIDTH) as u8;
                pixels[i + 1] = (y * 200 / HEIGHT) as u8;
                pixels[i + 2] = (base_b + ((t * 20.0) as u32 % 55)) as u8;
            }
            pixels[i + 3] = 0xFF;
        }
    }
}
