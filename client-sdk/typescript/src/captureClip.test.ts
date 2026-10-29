// @vitest-environment node
import { execFileSync } from "child_process";
import fs from "fs";
import os from "os";
import path from "path";
import { describe, expect, it } from "vitest";
import { accessUnits, TWO_SIZES_H264 } from "../fixtures/h264";
import {
  CaptureClip,
  RtpTimeline,
  UT_RETRY_FRAMES,
  missesReference,
  type EncodedFrameCopy,
} from "./captureClip";
import {
  NAL_PPS,
  NAL_SPS,
  avcCodecString,
  nalType,
  parseSps,
  sliceFrameNum,
  splitAnnexB,
  toAvccSample,
  Mp4Writer,
  MP4_PART_BYTES,
} from "./mp4";

function frame(au: Uint8Array, rtpTimestamp: number, receivedAt: number, ssrc = 1): EncodedFrameCopy {
  return { data: au.slice().buffer, rtpTimestamp, ssrc, receivedAt };
}

function hasTool(tool: string): boolean {
  try {
    execFileSync(tool, ["-version"], { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

describe("Annex B", () => {
  it("splits on 3 and 4 byte start codes and strips trailing zeros", () => {
    const data = new Uint8Array([0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 4, 5]);
    const nals = splitAnnexB(data);
    expect(nals.map((n) => Array.from(n))).toEqual([[0x67, 1, 2], [0x68, 3], [0x65, 4, 5]]);
  });

  it("length-prefixes NAL units, dropping delimiters and parameter sets", () => {
    const sample = toAvccSample([
      new Uint8Array([0x09, 0xf0]),
      new Uint8Array([0x67, 1]),
      new Uint8Array([0x68, 4]),
      new Uint8Array([0x06, 5]),
      new Uint8Array([0x65, 2, 3]),
    ]);
    expect(Array.from(sample)).toEqual([0, 0, 0, 2, 0x06, 5, 0, 0, 0, 3, 0x65, 2, 3]);
  });

  it("finds every frame of the fixture", () => {
    expect(accessUnits()).toHaveLength(24);
  });
});

describe("parseSps", () => {
  const spss = splitAnnexB(new Uint8Array(TWO_SIZES_H264)).filter((n) => nalType(n) === NAL_SPS);

  it("reads a cropped baseline size", () => {
    const info = parseSps(spss[0]);
    expect(info).toMatchObject({ profileIdc: 66, width: 66, height: 50 });
    expect(avcCodecString(info)).toMatch(/^avc1\.42/);
  });

  it("reads a high profile size past its chroma and scaling fields", () => {
    const info = parseSps(spss[spss.length - 1]);
    expect(info).toMatchObject({ profileIdc: 100, width: 96, height: 64 });
  });
});

describe("Mp4Writer", () => {
  it("rolls sample bytes into Blob parts instead of holding the buffers", async () => {
    const spsNal = splitAnnexB(new Uint8Array(TWO_SIZES_H264)).filter((n) => nalType(n) === NAL_SPS)[0];
    const writer = new Mp4Writer();
    const held = writer as unknown as { pending: Uint8Array[]; parts: Blob[] };
    const sample = (): Uint8Array<ArrayBuffer> => new Uint8Array(MP4_PART_BYTES / 2 + 1);
    for (let i = 0; i < 5; i++) writer.add(sample(), i * 3000, i === 0, spsNal, new Uint8Array([0x68, 1]));
    expect(held.parts).toHaveLength(2);
    expect(held.pending).toHaveLength(1);
    const blob = writer.finish();
    expect(held.pending).toHaveLength(0);
    expect(blob.size).toBeGreaterThan(5 * (MP4_PART_BYTES / 2 + 1));
  });

  it("finishes an hour of 60 fps frames", () => {
    const spsNal = splitAnnexB(new Uint8Array(TWO_SIZES_H264)).filter((n) => nalType(n) === NAL_SPS)[0];
    const writer = new Mp4Writer();
    const sample = new Uint8Array(4);
    const frames = 60 * 60 * 60;
    for (let i = 0; i < frames; i++) writer.add(sample, i * 1500, i % 120 === 0, spsNal, new Uint8Array([0x68, 1]));
    expect(writer.finish().size).toBeGreaterThan(frames * 4 * 2);
  });

  it("gives the last sample the median recent duration, not a dropout before it", () => {
    const writer = new Mp4Writer();
    const au = splitAnnexB(accessUnits()[0]);
    const sps = au.find((n) => nalType(n) === NAL_SPS)!;
    const pps = au.find((n) => nalType(n) === NAL_PPS)!;
    const sample = toAvccSample(au);
    for (const dts of [0, 3000, 6000, 9000, 12_000, 12_000 + 450_000]) writer.add(sample, dts, true, sps, pps);
    expect(writer.durations()).toEqual([3000, 3000, 3000, 3000, 450_000, 3000]);

    /* With only two gaps to choose from, the lower one: the dropout still does not set the tail. */
    const short = new Mp4Writer();
    for (const dts of [0, 3000, 453_000]) short.add(sample, dts, true, sps, pps);
    expect(short.durations()).toEqual([3000, 450_000, 3000]);
  });

  it.skipIf(!hasTool("ffprobe"))("writes a 64-bit media duration once it outgrows 32 bits", async () => {
    const aus = accessUnits().slice(0, 6);
    const writer = new Mp4Writer();
    const step = 0xc000_0000;
    aus.forEach((au, i) => {
      const nals = splitAnnexB(au);
      const sps = splitAnnexB(aus[0]).find((n) => nalType(n) === NAL_SPS)!;
      const pps = splitAnnexB(aus[0]).find((n) => nalType(n) === NAL_PPS)!;
      writer.add(toAvccSample(nals), i * step, i === 0, sps, pps);
    });
    const file = new Uint8Array(await writer.finish().arrayBuffer());
    const mdhd = findBox(file, "mdhd");
    expect(file[mdhd + 8]).toBe(1);
    const total = 6 * step;
    /* Version 1: 64-bit creation and modification times, the timescale, then a 64-bit duration. */
    expect(u32(file, mdhd + 28)).toBe(90_000);
    expect(u32(file, mdhd + 32) * 2 ** 32 + u32(file, mdhd + 36)).toBe(total);
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "kerbcast-mdhd-"));
    const out = path.join(dir, "long.mp4");
    fs.writeFileSync(out, file);
    try {
      const durationTs = execFileSync("ffprobe", [
        "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration_ts", "-of", "csv=p=0", out,
      ]).toString().trim();
      expect(Number(durationTs)).toBe(total);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });
});

describe("RtpTimeline", () => {
  it("starts at zero and follows RTP deltas, not arrival", () => {
    const t = new RtpTimeline();
    expect(t.next(1000, 1, 0)).toBe(0);
    expect(t.next(4000, 1, 900)).toBe(3000);
    expect(t.next(7000, 1, 901)).toBe(6000);
  });

  it("steps across the 32-bit wrap", () => {
    const t = new RtpTimeline();
    expect(t.next(0xffffffff - 1499, 1, 0)).toBe(0);
    expect(t.next(1500, 1, 33)).toBe(3000);
    expect(t.next(4500, 1, 66)).toBe(6000);
  });

  it("times a backward step, a huge jump or a new source by arrival", () => {
    const t = new RtpTimeline();
    t.next(100_000, 1, 0);
    expect(t.breaks(90_000, 1)).toBe(true);
    expect(t.next(90_000, 1, 100)).toBe(9000);
    expect(t.next(90_000 + 0x7000_0000, 1, 200)).toBe(18_000);
    expect(t.next(5, 2, 300)).toBe(27_000);
    expect(t.breaks(3005, 2)).toBe(false);
    expect(t.next(3005, 2, 400)).toBe(30_000);
  });

  it("continues through a repeated stamp, a tick later", () => {
    const t = new RtpTimeline();
    t.next(9000, 1, 0);
    expect(t.breaks(9000, 1)).toBe(false);
    expect(t.next(9000, 1, 500)).toBe(1);
    expect(t.next(12_000, 1, 533)).toBe(3001);
  });

  it("times the frame after a marked break by arrival, once", () => {
    const t = new RtpTimeline();
    t.next(9000, 1, 0);
    t.markBreak();
    expect(t.breaks(12_000, 1)).toBe(true);
    expect(t.next(12_000, 1, 2000)).toBe(180_000);
    expect(t.next(15_000, 1, 2001)).toBe(183_000);
  });
});

describe("CaptureClip", () => {
  it("drops frames until a keyframe, then keeps every frame", () => {
    const aus = accessUnits();
    const clip = new CaptureClip();
    /* Frames 3..5 are P-frames ahead of the IDR at 6. */
    for (let i = 3; i < 12; i++) clip.push(frame(aus[i], i * 3000, i * 33));
    expect(clip.started).toBe(true);
    const result = clip.finish();
    expect(result.frameCount).toBe(6);
    expect(result.firstFrameAt).toBe(6 * 33);
    expect(result.mimeType).toMatch(/^video\/mp4;codecs=avc1\./);
  });

  it("returns an empty clip when no keyframe ever arrives", () => {
    const aus = accessUnits();
    const clip = new CaptureClip();
    clip.push(frame(aus[1], 3000, 0));
    expect(clip.started).toBe(false);
    const result = clip.finish();
    expect(result.blob.size).toBe(0);
    expect(result.frameCount).toBe(0);
  });

  it("waits for a keyframe again after the source changes", () => {
    const aus = accessUnits();
    const clip = new CaptureClip();
    for (let i = 0; i < 4; i++) clip.push(frame(aus[i], i * 3000, i * 33, 1));
    clip.push(frame(aus[4], 77, 200, 2));
    expect(clip.waitingForKeyframe).toBe(true);
    clip.push(frame(aus[6], 77 + 6000, 260, 2));
    expect(clip.waitingForKeyframe).toBe(false);
    expect(clip.finish().frameCount).toBe(5);
  });

  it("waits for a keyframe after a same-source jump or step back, timing it by arrival", () => {
    const aus = accessUnits();
    const clip = new CaptureClip();
    for (let i = 0; i < 4; i++) clip.push(frame(aus[i], i * 3000, i * 33));
    /* A step back: the P-frames after it refer to pictures the clip never had. */
    clip.push(frame(aus[4], 5000, 1000));
    expect(clip.waitingForKeyframe).toBe(true);
    clip.push(frame(aus[5], 8000, 1500));
    /* Continuous with the last kept frame by stamp alone, but it follows the break. */
    clip.push(frame(aus[6], 11_000, 2000));
    expect(clip.waitingForKeyframe).toBe(false);
    clip.push(frame(aus[7], 14_000, 2033));
    const writer = (clip as unknown as { writer: Mp4Writer }).writer;
    expect(writer.times()).toEqual([0, 3000, 6000, 9000, 9000 + (2000 - 99) * 90, 9000 + (2000 - 99) * 90 + 3000]);
  });

  it("waits for a keyframe after a reference frame goes missing on a continuous timeline", () => {
    const aus = accessUnits();
    const clip = new CaptureClip();
    /* Frame 2 never arrives, but the stamps run on unbroken: 3 to 5 predict from a picture the clip lacks. */
    for (const i of [0, 1, 3]) clip.push(frame(aus[i], i * 3000, i * 33));
    expect(clip.waitingForKeyframe).toBe(true);
    for (const i of [4, 5, 6, 7]) clip.push(frame(aus[i], i * 3000, i * 33));
    expect(clip.waitingForKeyframe).toBe(false);
    const writer = (clip as unknown as { writer: Mp4Writer }).writer;
    /* Timed by the stamps, not by arrival: the wait holds the last good picture. */
    expect(writer.times()).toEqual([0, 3000, 18_000, 21_000]);
  });

  it("reads a missing reference picture from frame_num, allowing non-reference pictures and the wrap", () => {
    expect(missesReference(4, 5, 4)).toBe(false);
    /* A non-reference picture, or one following it, carries the next number without stepping it. */
    expect(missesReference(4, 4, 4)).toBe(false);
    expect(missesReference(15, 0, 4)).toBe(false);
    expect(missesReference(4, 6, 4)).toBe(true);
    expect(missesReference(15, 1, 4)).toBe(true);
    expect(missesReference(4, 3, 4)).toBe(true);
  });

  it("keeps going across a frame_num gap when the SPS allows gaps", () => {
    const aus = accessUnits();
    /* Baseline, log2_max_frame_num 4 like the fixture, gaps_in_frame_num_value_allowed_flag set. */
    const gapsAllowed = rbsp(0x67, "01000010 11000000 00001010 1 1 011 010 1 00101 00100 1 1 0 0");
    expect(parseSps(gapsAllowed)).toMatchObject({ log2MaxFrameNum: 4, frameNumGapsAllowed: true });
    const first = splitAnnexB(aus[0]).map((n) => (nalType(n) === NAL_SPS ? gapsAllowed : n));
    const clip = new CaptureClip();
    clip.push(frame(annexB(first), 0, 0));
    for (const i of [1, 3]) clip.push(frame(aus[i], i * 3000, i * 33));
    expect(clip.waitingForKeyframe).toBe(false);
    expect(clip.finish().frameCount).toBe(3);
  });

  it("reads frame_num past a colour plane id", () => {
    /* High 4:4:4 with separate colour planes, log2_max_frame_num 6. */
    const sps = parseSps(
      rbsp(0x67, "11110100 00000000 00001010 1 00100 1 1 1 0 0 011 011 010 0 00100 00100 1 1 0 0"),
    );
    expect(sps).toMatchObject({ separateColourPlane: true, log2MaxFrameNum: 6 });
    /* first_mb 0, slice_type 5, pps 0, colour_plane_id 2, frame_num 37. */
    expect(sliceFrameNum(rbsp(0x41, "1 00110 1 10 100101"), sps)).toBe(37);
  });

  it("waits for a parameter set it can read instead of opening on one it cannot", () => {
    const aus = accessUnits();
    const nals = splitAnnexB(aus[0]);
    /* An SPS cut off after its level byte. */
    const broken = nals.map((n) => (nalType(n) === NAL_SPS ? n.subarray(0, 4) : n));
    const clip = new CaptureClip();
    expect(() => clip.push(frame(annexB(broken), 0, 0))).not.toThrow();
    clip.push(frame(aus[1], 3000, 33));
    expect(clip.started).toBe(false);
    expect(clip.waitingForKeyframe).toBe(true);
    for (let i = 6; i < 8; i++) clip.push(frame(aus[i], i * 3000, i * 33));
    expect(clip.finish().frameCount).toBe(2);
  });

  it("retries a missed UT lookup on later pushes, while its stamp may still arrive", () => {
    const aus = accessUnits();
    const stamps = new Map<number, number>();
    const clip = new CaptureClip((rtp) => (stamps.has(rtp) ? { ut: stamps.get(rtp)! } : null));
    clip.push(frame(aus[0], 0, 0));
    /* The data channel trails: frame 0's stamp lands after the frame, then leaves the store's window. */
    stamps.set(0, 100);
    stamps.set(3000, 100.05);
    clip.push(frame(aus[1], 3000, 33));
    stamps.delete(0);
    expect(clip.finish().frameUtSamples).toEqual([
      { t: 0, ut: 100 },
      { t: 100 / 3, ut: 100.05 },
    ]);
  });

  it("bounds the UT retries per push", () => {
    const aus = accessUnits();
    let lookups = 0;
    const clip = new CaptureClip(() => {
      lookups++;
      return null;
    });
    const frames = UT_RETRY_FRAMES * 3;
    for (let i = 0; i < frames; i++) clip.push(frame(aus[i % aus.length], i * 3000, i));
    lookups = 0;
    clip.push(frame(aus[frames % aus.length], frames * 3000, frames));
    expect(lookups).toBeLessThanOrEqual(UT_RETRY_FRAMES + 1);
  });

  it("cuts the clip before the first frame stamped from a later epoch", () => {
    const aus = accessUnits();
    const clip = new CaptureClip((rtp) => (rtp >= 12_000 ? { laterEpoch: true } : { ut: 100 + rtp / 90_000 }));
    for (let i = 0; i < 6; i++) clip.push(frame(aus[i], i * 3000, i * 33));
    const result = clip.finish();
    expect(result.frameCount).toBe(4);
    expect(result.frameUtSamples).toHaveLength(4);
    /* The new last frame takes the median of the gaps before it, not the gap to the dropped frame. */
    expect(result.mediaDurationMs).toBeCloseTo((4 * 3000 * 1000) / 90_000);
    expect(clip.cut(0, 1000)?.mediaDurationMs).toBeCloseTo((4 * 3000 * 1000) / 90_000);
  });

  it("cuts at a later epoch whose stamp arrives after the frame", () => {
    const aus = accessUnits();
    const stamps = new Map<number, { ut: number } | { laterEpoch: true }>();
    const clip = new CaptureClip((rtp) => stamps.get(rtp) ?? null);
    for (let i = 0; i < 4; i++) clip.push(frame(aus[i], i * 3000, i * 33));
    for (let i = 0; i < 4; i++) stamps.set(i * 3000, i < 2 ? { ut: 100 + i } : { laterEpoch: true });
    const result = clip.finish();
    expect(result.frameCount).toBe(2);
    expect(result.frameUtSamples.map((s) => s.ut)).toEqual([100, 101]);
  });

  it("keeps frames whose stamps arrive late in the same epoch", () => {
    const aus = accessUnits();
    const stamps = new Map<number, { ut: number }>();
    const clip = new CaptureClip((rtp) => stamps.get(rtp) ?? null);
    for (let i = 0; i < 4; i++) clip.push(frame(aus[i], i * 3000, i * 33));
    stamps.set(0, { ut: 100 });
    stamps.set(3000, { ut: 101 });
    const result = clip.finish();
    expect(result.frameCount).toBe(4);
    expect(result.frameUtSamples.map((s) => s.ut)).toEqual([100, 101]);
  });

  it("leaves a clip with no epoch information as it was", () => {
    const aus = accessUnits();
    const clip = new CaptureClip((rtp) => ({ ut: 100 + rtp / 90_000 }));
    for (let i = 0; i < 4; i++) clip.push(frame(aus[i], i * 3000, i * 33));
    const plain = new CaptureClip();
    for (let i = 0; i < 4; i++) plain.push(frame(aus[i], i * 3000, i * 33));
    const result = clip.finish();
    expect(result.frameCount).toBe(4);
    expect(result.mediaDurationMs).toBe(plain.finish().mediaDurationMs);
  });

  it("collects a per-frame UT track from capture stamps", () => {
    const aus = accessUnits();
    const clip = new CaptureClip((rtp) => (rtp === 6000 ? null : { ut: 100 + rtp / 90_000 }));
    for (let i = 0; i < 4; i++) clip.push(frame(aus[i], i * 3000, i * 33));
    const { frameUtSamples } = clip.finish();
    expect(frameUtSamples.map((s) => s.t)).toEqual([0, 100 / 3, 100]);
    expect(frameUtSamples[2].ut).toBeCloseTo(100 + 9000 / 90_000);
  });

  it("opens a sample entry per parameter set change, one chunk each, sized to the largest", async () => {
    const clip = new CaptureClip();
    accessUnits().forEach((au, i) => clip.push(frame(au, i * 3000, i)));
    const result = clip.finish();
    expect(result.mediaDurationMs).toBeCloseTo(800);
    expect(result.mimeType).toBe("video/mp4;codecs=avc1.64000a");
    const file = new Uint8Array(await result.blob.arrayBuffer());
    const stsd = findBox(file, "stsd");
    expect(u32(file, stsd + 12)).toBe(2);
    const stsc = findBox(file, "stsc");
    /* entry_count, then (first_chunk, samples_per_chunk, sample_description_index) per run. */
    const stscWords = Array.from({ length: 7 }, (_, i) => u32(file, stsc + 12 + i * 4));
    expect(stscWords).toEqual([2, 1, 12, 1, 2, 12, 2]);
    expect(u32(file, findBox(file, "stco") + 12)).toBe(2);
    const tkhd = findBox(file, "tkhd");
    expect([u32(file, tkhd + 84) / 0x10000, u32(file, tkhd + 88) / 0x10000]).toEqual([96, 64]);
    /* High profile avcC carries the chroma and bit depth extension. */
    const [baseline, high] = findAll(file, "avcC");
    expect(u32(file, baseline)).toBe(8 + 6 + 2 + spsLen(file, baseline) + 1 + 2 + ppsLen(file, baseline));
    const highSize = u32(file, high);
    expect(Array.from(file.subarray(high + highSize - 4, high + highSize))).toEqual([0xfd, 0xf8, 0xf8, 0]);
  });

  it("writes a one-size clip byte for byte as the writer before size changes were handled", async () => {
    const clip = new CaptureClip();
    accessUnits()
      .slice(0, 12)
      .forEach((au, i) => clip.push(frame(au, 1000 + i * 3000 + (i % 3) * 7, i)));
    const file = new Uint8Array(await clip.finish().blob.arrayBuffer());
    const golden = fs.readFileSync(path.resolve(__dirname, "../fixtures/one-size-231f794.mp4"));
    expect(Buffer.from(file).equals(golden)).toBe(true);
  });

  it.skipIf(!hasTool("ffprobe"))(
    "switches to 64-bit chunk offsets and mdat size when the file outgrows 32 bits",
    async () => {
      const clip = new CaptureClip();
      accessUnits().forEach((au, i) => clip.push(frame(au, i * 3000, i)));
      const writer = (clip as unknown as { writer: { offsetLimit: number } }).writer;
      writer.offsetLimit = 1000;
      const file = new Uint8Array(await clip.finish().blob.arrayBuffer());
      expect(findAll(file, "stco")).toEqual([]);
      expect(findAll(file, "co64")).toHaveLength(1);
      const mdat = findBox(file, "mdat");
      /* size 1 means the real size follows as 64 bits */
      expect(u32(file, mdat)).toBe(1);
      expect(u32(file, mdat + 8) * 2 ** 32 + u32(file, mdat + 12)).toBe(file.length - mdat);
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), "kerbcast-co64-"));
      const out = path.join(dir, "co64.mp4");
      fs.writeFileSync(out, file);
      try {
        const frames = execFileSync("ffprobe", [
          "-v", "error", "-show_entries", "frame=width,height", "-of", "csv=p=0", out,
        ]).toString().trim().split("\n").map((l) => l.replace(/,$/, ""));
        expect(frames).toEqual([...Array(12).fill("66,50"), ...Array(12).fill("96,64")]);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    },
  );

  it("repeats each size's parameter sets in-band only when the clip changes size", async () => {
    const aus = accessUnits();
    const oneSize = new CaptureClip();
    aus.slice(0, 12).forEach((au, i) => oneSize.push(frame(au, i * 3000, i)));
    const plain = new Uint8Array(await oneSize.finish().blob.arrayBuffer());
    expect(firstSampleNalTypes(plain)).not.toContain(NAL_SPS);

    const twoSizes = new CaptureClip();
    aus.forEach((au, i) => twoSizes.push(frame(au, i * 3000, i)));
    const file = new Uint8Array(await twoSizes.finish().blob.arrayBuffer());
    const stco = findBox(file, "stco");
    const chunks = [u32(file, stco + 16), u32(file, stco + 20)];
    expect(chunks.map((at) => nalTypesAt(file, at).slice(0, 2))).toEqual([
      [NAL_SPS, 8],
      [NAL_SPS, 8],
    ]);
  });

  it("cuts a stretch from the keyframe before its start, keeping every size it crosses", async () => {
    const aus = accessUnits();
    const clip = new CaptureClip((rtp) => ({ ut: 100 + rtp / 90_000 }));
    aus.forEach((au, i) => clip.push(frame(au, i * 3000, i)));
    clip.finish();
    /* 300 ms is frame 9, a P-frame: the cut opens on the IDR at frame 6 (200 ms). */
    const cut = clip.cut(300, 600);
    expect(cut).not.toBeNull();
    expect(cut!.startMs).toBeCloseTo(200);
    expect(cut!.mediaDurationMs).toBeCloseTo(13 * (100 / 3));
    expect(cut!.frameUtSamples[0]).toEqual({ t: 0, ut: 100 + 18_000 / 90_000 });
    expect(cut!.frameUtSamples).toHaveLength(13);
    const file = new Uint8Array(await cut!.blob.arrayBuffer());
    expect(u32(file, findBox(file, "stsd") + 12)).toBe(2);
    expect(u32(file, findBox(file, "stsz") + 16)).toBe(13);
    expect(clip.cut(5000, 6000)).toBeNull();
  });

  it.skipIf(!hasTool("ffprobe"))("cuts a stretch across a resize that decodes at both sizes", async () => {
    const aus = accessUnits();
    const clip = new CaptureClip();
    aus.forEach((au, i) => clip.push(frame(au, i * 3000, i)));
    clip.finish();
    const cut = clip.cut(300, 600)!;
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "kerbcast-cut-"));
    const file = path.join(dir, "cut.mp4");
    fs.writeFileSync(file, new Uint8Array(await cut.blob.arrayBuffer()));
    try {
      const frames = execFileSync("ffprobe", [
        "-v", "error", "-show_entries", "frame=width,height,pts", "-of", "csv=p=0", file,
      ]).toString().trim().split("\n").map((l) => l.replace(/,$/, ""));
      expect(frames).toEqual([
        ...Array.from({ length: 6 }, (_, i) => `${i * 3000},66,50`),
        ...Array.from({ length: 7 }, (_, i) => `${(i + 6) * 3000},96,64`),
      ]);
    } finally {
      fs.rmSync(dir, { recursive: true, force: true });
    }
  });

  it.skipIf(!hasTool("ffprobe"))(
    "writes an MP4 whose packet times follow RTP, across a wrap and a resolution change",
    async () => {
      const aus = accessUnits();
      const clip = new CaptureClip();
      /* Uneven capture spacing, a wrap near the start, and a 500 ms arrival
         stall in the middle that must not show in the file. */
      const steps = [3000, 3000, 1500, 4500, 3000, 6000];
      let rtp = 0xffffffff - 4000;
      let at = 0;
      const expectedTicks: number[] = [];
      let ticks = 0;
      aus.forEach((au, i) => {
        if (i > 0) {
          const step = steps[i % steps.length];
          rtp = (rtp + step) >>> 0;
          ticks += step;
          at += i === 10 ? 500 : 2;
        }
        expectedTicks.push(ticks);
        clip.push(frame(au, rtp, at));
      });
      const { blob } = clip.finish();
      const dir = fs.mkdtempSync(path.join(os.tmpdir(), "kerbcast-mp4-"));
      const file = path.join(dir, "clip.mp4");
      fs.writeFileSync(file, new Uint8Array(await blob.arrayBuffer()));
      try {
        const probe = JSON.parse(
          execFileSync("ffprobe", [
            "-v", "error", "-select_streams", "v:0", "-show_entries",
            "packet=pts,flags:packet_side_data=side_data_type:stream=time_base,codec_name,width,height",
            "-of", "json", file,
          ]).toString(),
        ) as {
          packets: { pts: number; flags: string; side_data_list?: { side_data_type: string }[] }[];
          streams: { time_base: string; codec_name: string; width: number; height: number }[];
        };
        expect(probe.streams[0]).toMatchObject({
          time_base: "1/90000",
          codec_name: "h264",
          width: 66,
          height: 50,
        });
        expect(probe.packets.map((p) => p.pts)).toEqual(expectedTicks);
        expect(probe.packets[0].flags).toMatch(/^K/);
        /* The resize opens the second sample entry, which the demuxer reports as new extradata. */
        const switches = probe.packets.flatMap((p, i) =>
          p.side_data_list?.some((d) => d.side_data_type === "New Extradata") ? [i] : [],
        );
        expect(switches).toEqual([12]);

        /* Every frame decodes, at the size of its own sample entry. */
        const frames = execFileSync("ffprobe", [
          "-v", "error", "-show_entries", "frame=width,height", "-of", "csv=p=0", file,
        ]).toString().trim().split("\n").map((l) => l.replace(/,$/, ""));
        expect(frames).toEqual([...Array(12).fill("66,50"), ...Array(12).fill("96,64")]);
      } finally {
        fs.rmSync(dir, { recursive: true, force: true });
      }
    },
  );
});

/** A NAL unit from its header byte and its fields as a bit string, with the stop bit and padding added. */
function rbsp(header: number, fields: string): Uint8Array {
  const bits = fields.replace(/ /g, "") + "1";
  const out = new Uint8Array(1 + Math.ceil(bits.length / 8));
  out[0] = header;
  for (let i = 0; i < bits.length; i++) if (bits[i] === "1") out[1 + (i >> 3)] |= 0x80 >> (i & 7);
  return out;
}

/** NAL units joined back into one Annex B access unit. */
function annexB(nals: Uint8Array[]): Uint8Array {
  const parts = nals.flatMap((n) => [new Uint8Array([0, 0, 0, 1]), n]);
  const out = new Uint8Array(parts.reduce((a, p) => a + p.length, 0));
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

/** NAL unit types of the length-prefixed units of a sample starting at `at`, up to the first slice. */
function nalTypesAt(file: Uint8Array, at: number): number[] {
  const types: number[] = [];
  while (at + 4 < file.length) {
    const type = file[at + 4] & 0x1f;
    types.push(type);
    if (type === 1 || type === 5) break;
    at += 4 + u32(file, at);
  }
  return types;
}

function firstSampleNalTypes(file: Uint8Array): number[] {
  return nalTypesAt(file, u32(file, findBox(file, "stco") + 16));
}

function u32(file: Uint8Array, at: number): number {
  return new DataView(file.buffer, file.byteOffset).getUint32(at);
}

/** Offsets of every box of `type`, found by scanning for its fourcc. */
function findAll(file: Uint8Array, type: string): number[] {
  const code = Array.from(type, (c) => c.charCodeAt(0));
  const out: number[] = [];
  for (let i = 4; i + 4 <= file.length; i++) {
    if (code.every((c, j) => file[i + j] === c)) out.push(i - 4);
  }
  return out;
}

function findBox(file: Uint8Array, type: string): number {
  return findAll(file, type)[0];
}

function spsLen(file: Uint8Array, avcC: number): number {
  return (file[avcC + 14] << 8) | file[avcC + 15];
}

function ppsLen(file: Uint8Array, avcC: number): number {
  const at = avcC + 16 + spsLen(file, avcC) + 1;
  return (file[at] << 8) | file[at + 1];
}
