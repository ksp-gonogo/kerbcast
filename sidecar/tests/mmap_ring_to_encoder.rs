//! End-to-end: producer writes synthetic RGBA frames into a file-backed
//! `MmapFrameRing`; consumer reads them and feeds the OpenH264 software
//! encoder. Validates the cross-process *layout contract* (two separate
//! `MmapFrameRing` handles against the same file behave like two processes
//! sharing the ring) plus the encoder integration.

use std::env::temp_dir;
use std::fs;

use kerbcast_sidecar::encoder::{EncodeConfig, EncoderBackend, RawFrame, Software};
use kerbcast_sidecar::shared_mem::{MmapFrameRing, MmapRingConfig};

fn tmp_path(suffix: &str) -> std::path::PathBuf {
    let pid = std::process::id();
    let nanos = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_nanos();
    temp_dir().join(format!("kerbcast-e2e-{pid}-{nanos}-{suffix}"))
}

fn synthetic_rgba(width: u32, height: u32, frame_n: u32) -> Vec<u8> {
    let mut data = Vec::with_capacity((width * height * 4) as usize);
    let seed = (frame_n & 0xFF) as u8;
    for y in 0..height {
        for x in 0..width {
            data.push(((x * 255 / width) & 0xFF) as u8);
            data.push(((y * 255 / height) & 0xFF) as u8);
            data.push(seed);
            data.push(0xFF);
        }
    }
    data
}

#[test]
fn producer_mmap_then_consumer_mmap_then_encoder_yields_real_nals() {
    let path = tmp_path("e2e-mmap-encoder");
    let ring_cfg = MmapRingConfig {
        slot_count: 4,
        max_width: 64,
        max_height: 64,
    };

    // Producer side — analogue of the KSP plugin writer.
    let mut producer = MmapFrameRing::create(&path, ring_cfg).unwrap();

    // Consumer side — analogue of the sidecar reader. Separate handle on
    // the same file, the way it'd look across process boundaries.
    let consumer = MmapFrameRing::open(&path, ring_cfg).unwrap();

    let mut encoder = Software::new();
    encoder
        .init(EncodeConfig {
            width: 64,
            height: 64,
            fps: 30,
            bitrate_bps: 500_000,
        })
        .expect("encoder init");

    let mut first_frame_nal_seen = false;
    for n in 0..6 {
        let pixels = synthetic_rgba(64, 64, n);
        producer
            .produce(64, 64, n as f64 * 33.3, &pixels)
            .expect("ring produce");

        let slot = consumer
            .latest()
            .expect("latest read")
            .expect("at least one frame present after produce");
        assert_eq!(slot.width, 64);
        assert_eq!(slot.height, 64);
        assert_eq!(slot.sequence, (n + 1) as u64);
        assert_eq!(slot.pixels.len(), 64 * 64 * 4);

        let nals = encoder
            .encode(&RawFrame {
                width: slot.width,
                height: slot.height,
                data: &slot.pixels,
                capture_ts_ms: slot.capture_ts_ms,
            })
            .expect("encode");
        if !nals.is_empty() {
            first_frame_nal_seen = true;
            // SPS/PPS/IDR NALs each begin with the H.264 start code 0x00 0x00 0x00 0x01.
            let bytes = &nals[0].0;
            assert!(
                bytes.len() > 4,
                "NAL too short to contain start code + payload"
            );
        }
    }
    assert!(
        first_frame_nal_seen,
        "expected encoder to emit at least one NAL over 6 frames"
    );

    drop(consumer);
    drop(producer);
    fs::remove_file(&path).ok();
}

#[test]
fn dropped_frames_in_mmap_ring_show_up_as_sequence_gaps() {
    let path = tmp_path("e2e-dropframes");
    let ring_cfg = MmapRingConfig {
        slot_count: 4,
        max_width: 16,
        max_height: 16,
    };

    let mut producer = MmapFrameRing::create(&path, ring_cfg).unwrap();
    let consumer = MmapFrameRing::open(&path, ring_cfg).unwrap();

    let buf = vec![0xCDu8; 16 * 16 * 4];
    for _ in 0..10 {
        producer.produce(16, 16, 0.0, &buf).unwrap();
    }

    // Consumer only sees `latest`; sequences 1..6 are gone (ring is 4 slots
    // wide, 10 produces means we lapped). Sequence = 10 = last produce.
    let slot = consumer.latest().unwrap().unwrap();
    assert_eq!(slot.sequence, 10);

    drop(consumer);
    drop(producer);
    fs::remove_file(&path).ok();
}
