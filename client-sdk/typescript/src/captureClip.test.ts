// @vitest-environment node
import { execFileSync } from "child_process";
import fs from "fs";
import os from "os";
import path from "path";
import { describe, expect, it } from "vitest";
import { accessUnits, TWO_SIZES_H264 } from "../fixtures/h264";
import { CaptureClip, RtpTimeline, type EncodedFrameCopy } from "./captureClip";
import {
  NAL_SPS,
  avcCodecString,
  nalType,
  parseSps,
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
