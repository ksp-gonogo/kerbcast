//! Integration test: synthetic frame source → shared-mem ring → encoder
//! backend → NAL output. Mirrors the rebuild doc §10.2 "synthetic-frame
//! harness" but in-process for now (no subprocess, no WebRTC). Real
//! end-to-end harness lands when WebRTC is wired up.

use kerbcast_sidecar::encoder::{EncodeConfig, EncoderBackend, RawFrame, Software};
use kerbcast_sidecar::shared_mem::FrameRing;

fn synthetic_rgba(width: u32, height: u32, frame_n: u32) -> Vec<u8> {
    // Simple gradient that varies per frame so each one is distinct — useful
    // for testing dropped/duplicated-frame detection in later spikes.
    let mut data = Vec::with_capacity((width * height * 4) as usize);
    let frame_seed = (frame_n & 0xFF) as u8;
    for y in 0..height {
        for x in 0..width {
            data.push(((x * 255 / width) & 0xFF) as u8); // R: x-gradient
            data.push(((y * 255 / height) & 0xFF) as u8); // G: y-gradient
            data.push(frame_seed); // B: frame counter
            data.push(0xFF); // A
        }
    }
    data
}

#[test]
fn end_to_end_synthetic_frames_through_ring_into_encoder() {
    let ring = FrameRing::new();
    let mut encoder = Software::new();
    encoder
        .init(EncodeConfig {
            width: 16,
            height: 16,
            fps: 30,
            bitrate_bps: 500_000,
        })
        .expect("encoder init");

    // Produce 10 frames, pull each, run through encoder.
    for n in 0..10 {
        let pixels = synthetic_rgba(16, 16, n);
        ring.produce(16, 16, n as f64 * 33.3, &pixels)
            .expect("ring produce");

        let slot = ring.latest().expect("latest slot");
        assert_eq!(slot.sequence, (n + 1) as u64);
        assert_eq!(slot.width, 16);
        assert_eq!(slot.height, 16);

        let nals = encoder
            .encode(&RawFrame {
                width: slot.width,
                height: slot.height,
                data: &slot.pixels,
                capture_ts_ms: slot.capture_ts_ms,
            })
            .expect("encode");

        // First frame is the IDR (keyframe). OpenH264 emits SPS + PPS +
        // IDR NALs on frame 0 — always >= 1 NAL. Subsequent P-frames may
        // be empty if the bitrate controller decides to drop. We assert
        // each call returns ok (no error), not a specific NAL count.
        if n == 0 {
            assert!(
                !nals.is_empty(),
                "first frame should be a keyframe with at least one NAL"
            );
        }
    }

    encoder.close();
}

#[test]
fn ring_dropped_frames_are_observable_via_sequence_gap() {
    let ring = FrameRing::new();
    let pixels = vec![0u8; 4 * 4 * 4];

    // Producer writes 5 frames before consumer reads any.
    for _ in 0..5 {
        ring.produce(4, 4, 0.0, &pixels).unwrap();
    }

    // Consumer sees the latest slot, sequence=5. Earlier frames are lost
    // (4-slot ring + 5 produces = 1 drop minimum). Sequence jumps from
    // implicit 0 to 5, signalling 4 dropped frames — the real consumer
    // would surface this via a metric.
    let slot = ring.latest().expect("latest");
    assert_eq!(slot.sequence, 5);
}
