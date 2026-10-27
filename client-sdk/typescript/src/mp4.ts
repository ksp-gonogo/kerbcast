/*
 * A small H.264 MP4 muxer for capture-timed recordings: Annex B access units
 * in, one plain (non-fragmented, moov first) MP4 out. No re-encode. Sample
 * bytes are rolled into Blobs as they arrive, so a long clip is not held as
 * one heap of buffers. Kept
 * in-house rather than a library: the candidates are either MPL-2.0
 * (Mediabunny) or deprecated in its favour (mp4-muxer), and a single video
 * track with no B-frames needs only a few boxes.
 */

/** NAL unit types the muxer cares about. */
export const NAL_IDR = 5;
export const NAL_SPS = 7;
export const NAL_PPS = 8;
export const NAL_AUD = 9;
export const NAL_FILLER = 12;

/** The RTP video clock, and so the timescale of every recorded track. */
export const RTP_VIDEO_CLOCK_HZ = 90_000;

/** NAL unit type of a NAL unit (header byte included). */
export function nalType(nal: Uint8Array): number {
  return nal[0] & 0x1f;
}

/**
 * Splits an Annex B buffer (start-code delimited, as WebRTC hands H.264
 * frames to an encoded transform) into its NAL units. Each unit is a view
 * into `data`. Trailing zero bytes before a 4-byte start code are dropped.
 */
export function splitAnnexB(data: Uint8Array): Uint8Array[] {
  const nals: Uint8Array[] = [];
  let start = -1;
  let i = 0;
  const n = data.length;
  while (i + 2 < n) {
    if (data[i] === 0 && data[i + 1] === 0 && data[i + 2] === 1) {
      if (start >= 0) {
        let end = i;
        while (end > start && data[end - 1] === 0) end--;
        if (end > start) nals.push(data.subarray(start, end));
      }
      i += 3;
      start = i;
    } else {
      i++;
    }
  }
  if (start >= 0 && start < n) nals.push(data.subarray(start, n));
  return nals;
}

/**
 * Packs NAL units as one MP4 sample: each unit prefixed with its 4-byte
 * length (AVCC). Access unit delimiters and filler are dropped, and so are
 * parameter sets: an `avc1` track carries them in its sample entries.
 */
export function toAvccSample(nals: Uint8Array[]): Uint8Array<ArrayBuffer> {
  const kept = nals.filter((nal) => {
    const type = nalType(nal);
    return type !== NAL_AUD && type !== NAL_FILLER && type !== NAL_SPS && type !== NAL_PPS;
  });
  let size = 0;
  for (const nal of kept) size += 4 + nal.length;
  const out = new Uint8Array(size);
  const view = new DataView(out.buffer);
  let at = 0;
  for (const nal of kept) {
    view.setUint32(at, nal.length);
    out.set(nal, at + 4);
    at += 4 + nal.length;
  }
  return out;
}

/** What a sequence parameter set says about the stream. */
export interface SpsInfo {
  profileIdc: number;
  constraintFlags: number;
  levelIdc: number;
  chromaFormatIdc: number;
  bitDepthLumaMinus8: number;
  bitDepthChromaMinus8: number;
  width: number;
  height: number;
}

/** Exp-Golomb bit reader over an RBSP (emulation prevention already removed). */
class BitReader {
  private bit = 0;
  constructor(private readonly bytes: Uint8Array) {}

  u(count: number): number {
    let v = 0;
    for (let i = 0; i < count; i++) {
      const byte = this.bytes[this.bit >> 3];
      if (byte === undefined) throw new Error("SPS ended early");
      v = v * 2 + ((byte >> (7 - (this.bit & 7))) & 1);
      this.bit++;
    }
    return v;
  }

  ue(): number {
    let zeros = 0;
    while (this.u(1) === 0) {
      if (++zeros > 31) throw new Error("bad exp-Golomb code in SPS");
    }
    return 2 ** zeros - 1 + this.u(zeros);
  }

  se(): number {
    const k = this.ue();
    return k % 2 === 1 ? (k + 1) / 2 : -k / 2;
  }
}

/** Strips emulation prevention bytes (00 00 03 -> 00 00). */
function unescapeRbsp(nal: Uint8Array): Uint8Array {
  const out = new Uint8Array(nal.length);
  let len = 0;
  let zeros = 0;
  for (const byte of nal) {
    if (zeros >= 2 && byte === 3) {
      zeros = 0;
      continue;
    }
    zeros = byte === 0 ? zeros + 1 : 0;
    out[len++] = byte;
  }
  return out.subarray(0, len);
}

/** Profiles whose SPS carries chroma format, bit depth and scaling lists. */
const HIGH_PROFILES = new Set([100, 110, 122, 244, 44, 83, 86, 118, 128, 138, 139, 134, 135]);

function skipScalingList(r: BitReader, size: number): void {
  let last = 8;
  let next = 8;
  for (let j = 0; j < size; j++) {
    if (next !== 0) next = (last + r.se() + 256) % 256;
    last = next === 0 ? last : next;
  }
}

/** Reads profile, level and the cropped picture size from an SPS NAL unit. */
export function parseSps(nal: Uint8Array): SpsInfo {
  const r = new BitReader(unescapeRbsp(nal.subarray(1)));
  const profileIdc = r.u(8);
  const constraintFlags = r.u(8);
  const levelIdc = r.u(8);
  r.ue();
  let chromaFormatIdc = 1;
  let separateColourPlane = 0;
  let bitDepthLumaMinus8 = 0;
  let bitDepthChromaMinus8 = 0;
  if (HIGH_PROFILES.has(profileIdc)) {
    chromaFormatIdc = r.ue();
    if (chromaFormatIdc === 3) separateColourPlane = r.u(1);
    bitDepthLumaMinus8 = r.ue();
    bitDepthChromaMinus8 = r.ue();
    r.u(1);
    if (r.u(1)) {
      const lists = chromaFormatIdc === 3 ? 12 : 8;
      for (let i = 0; i < lists; i++) {
        if (r.u(1)) skipScalingList(r, i < 6 ? 16 : 64);
      }
    }
  }
  r.ue();
  const pocType = r.ue();
  if (pocType === 0) {
    r.ue();
  } else if (pocType === 1) {
    r.u(1);
    r.se();
    r.se();
    const cycle = r.ue();
    for (let i = 0; i < cycle; i++) r.se();
  }
  r.ue();
  r.u(1);
  const widthMbs = r.ue() + 1;
  const heightMapUnits = r.ue() + 1;
  const frameMbsOnly = r.u(1);
  if (!frameMbsOnly) r.u(1);
  r.u(1);
  let crop = [0, 0, 0, 0];
  if (r.u(1)) crop = [r.ue(), r.ue(), r.ue(), r.ue()];
  const chromaArray = separateColourPlane ? 0 : chromaFormatIdc;
  const cropUnitX = chromaArray === 0 || chromaArray === 3 ? 1 : 2;
  const cropUnitY = (chromaArray === 1 ? 2 : 1) * (2 - frameMbsOnly);
  return {
    profileIdc,
    constraintFlags,
    levelIdc,
    chromaFormatIdc,
    bitDepthLumaMinus8,
    bitDepthChromaMinus8,
    width: widthMbs * 16 - cropUnitX * (crop[0] + crop[1]),
    height: (2 - frameMbsOnly) * heightMapUnits * 16 - cropUnitY * (crop[2] + crop[3]),
  };
}

/** RFC 6381 codec string for an SPS, e.g. `avc1.42e01f`. */
export function avcCodecString(sps: SpsInfo): string {
  const hex = (v: number): string => v.toString(16).padStart(2, "0");
  return `avc1.${hex(sps.profileIdc)}${hex(sps.constraintFlags)}${hex(sps.levelIdc)}`;
}

function u16(v: number): Uint8Array {
  return new Uint8Array([(v >>> 8) & 0xff, v & 0xff]);
}

function u32(v: number): Uint8Array {
  const out = new Uint8Array(4);
  new DataView(out.buffer).setUint32(0, v >>> 0);
  return out;
}

function u64(v: number): Uint8Array {
  const out = new Uint8Array(8);
  const view = new DataView(out.buffer);
  view.setUint32(0, Math.floor(v / 2 ** 32));
  view.setUint32(4, v >>> 0);
  return out;
}

function ascii(s: string): Uint8Array {
  return Uint8Array.from(s, (c) => c.charCodeAt(0));
}

function concat(parts: Uint8Array[]): Uint8Array<ArrayBuffer> {
  let size = 0;
  for (const p of parts) size += p.length;
  const out = new Uint8Array(size);
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

function box(type: string, ...parts: Uint8Array[]): Uint8Array {
  const body = concat(parts);
  return concat([u32(body.length + 8), ascii(type), body]);
}

function fullBox(type: string, version: number, flags: number, ...parts: Uint8Array[]): Uint8Array {
  return box(type, u32(((version & 0xff) << 24) | (flags & 0xffffff)), ...parts);
}

const UNITY_MATRIX = concat(
  [0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x40000000].map((v) => u32(v)),
);

/** Profiles whose `avcC` carries the chroma and bit depth extension (ISO/IEC 14496-15). */
const AVCC_EXT_PROFILES = new Set([100, 110, 122, 144]);

function avcC(sps: Uint8Array, pps: Uint8Array, info: SpsInfo): Uint8Array {
  const ext = AVCC_EXT_PROFILES.has(info.profileIdc)
    ? [
        new Uint8Array([
          0xfc | info.chromaFormatIdc,
          0xf8 | info.bitDepthLumaMinus8,
          0xf8 | info.bitDepthChromaMinus8,
          0,
        ]),
      ]
    : [];
  return box(
    "avcC",
    new Uint8Array([1, sps[1], sps[2], sps[3], 0xff, 0xe1]),
    u16(sps.length),
    sps,
    new Uint8Array([1]),
    u16(pps.length),
    pps,
    ...ext,
  );
}

/** One `avc1` sample entry: the parameter sets a run of samples decodes with. */
export interface SampleEntry {
  sps: Uint8Array;
  pps: Uint8Array;
  info: SpsInfo;
}

function avc1(entry: SampleEntry): Uint8Array {
  return box(
    "avc1",
    new Uint8Array(6),
    u16(1),
    new Uint8Array(16),
    u16(entry.info.width),
    u16(entry.info.height),
    u32(0x00480000),
    u32(0x00480000),
    u32(0),
    u16(1),
    new Uint8Array(32),
    u16(0x18),
    u16(0xffff),
    avcC(entry.sps, entry.pps, entry.info),
  );
}
/**
 * A table box body: `count` rows of 32-bit words, written straight into one
 * buffer. Sample tables run to one row per frame (over 100k in an hour at
 * 30 fps), far past what can be spread into a call's arguments.
 */
function table(count: number, words: number, row: (i: number, put: (v: number) => void) => void): Uint8Array {
  const out = new Uint8Array(4 + count * words * 4);
  const view = new DataView(out.buffer);
  view.setUint32(0, count);
  let at = 4;
  const put = (v: number): void => {
    view.setUint32(at, v >>> 0);
    at += 4;
  };
  for (let i = 0; i < count; i++) row(i, put);
  return out;
}

function stts(durations: number[]): Uint8Array {
  const runs: [number, number][] = [];
  for (const d of durations) {
    const last = runs[runs.length - 1];
    if (last && last[1] === d) last[0]++;
    else runs.push([1, d]);
  }
  return fullBox(
    "stts",
    0,
    0,
    table(runs.length, 2, (i, put) => {
      put(runs[i][0]);
      put(runs[i][1]);
    }),
  );
}

function sameBytes(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
  return true;
}

/** The parameter sets of a sample entry as length-prefixed NAL units, for an in-band copy. */
function inBandParameterSets(entry: SampleEntry): Uint8Array<ArrayBuffer> {
  return concat([u32(entry.sps.length), entry.sps, u32(entry.pps.length), entry.pps]);
}

/** Sample bytes held in memory before they are rolled into a Blob part. */
export const MP4_PART_BYTES = 4 * 1024 * 1024;

/** One run of consecutive samples sharing a sample entry: one chunk in the file. */
interface Chunk {
  /** Index into the file's own sample entries. */
  entry: number;
  first: number;
  samples: number;
  /** Bytes put ahead of the chunk's first sample (its in-band parameter sets, if any). */
  prefix: Uint8Array<ArrayBuffer>;
}

/**
 * Builds a plain MP4 (moov before mdat, so it plays progressively) with one
 * H.264 video track on the 90 kHz RTP clock. Timestamps are kept exactly as
 * given, so the file plays at the pace the samples were stamped with, not
 * the pace they arrived at. Only the sample tables stay in memory; the
 * sample bytes are rolled into Blob parts every {@link MP4_PART_BYTES}.
 *
 * Each change of SPS or PPS (the feed changing size) opens a new `avc1`
 * sample entry and a new chunk, so every sample decodes with the parameter
 * sets in force when it was encoded. A file with more than one entry also
 * carries each entry's parameter sets in-band at the start of its chunk:
 * Safari decodes by sample entry, but Chrome reads only the first entry's
 * and needs the new SPS in the stream to reconfigure. `avc3` (in-band only)
 * would cover Chrome alone; Safari and QuickTime refuse it.
 */
export class Mp4Writer {
  private readonly entries: SampleEntry[] = [];
  private readonly entryOf: number[] = [];
  private readonly sizes: number[] = [];
  private readonly offsets: number[] = [];
  private readonly dts: number[] = [];
  private readonly key: boolean[] = [];
  private readonly parts: Blob[] = [];
  private pending: Uint8Array<ArrayBuffer>[] = [];
  private pendingBytes = 0;
  private payload = 0;
  /** The largest file offset 32-bit chunk offsets can carry; a test lowers it to reach co64. */
  private readonly offsetLimit: number = 0xffffffff;

  get sampleCount(): number {
    return this.sizes.length;
  }

  /**
   * Appends one sample. `sps` and `pps` are the parameter sets it decodes
   * with; `dts` is in {@link RTP_VIDEO_CLOCK_HZ} ticks and must rise. The
   * first sample must be a keyframe.
   */
  add(data: Uint8Array<ArrayBuffer>, dts: number, key: boolean, sps: Uint8Array, pps: Uint8Array): void {
    const current = this.entries[this.entries.length - 1];
    if (!current || !sameBytes(current.sps, sps) || !sameBytes(current.pps, pps)) {
      this.entries.push({ sps: sps.slice(), pps: pps.slice(), info: parseSps(sps) });
    }
    this.entryOf.push(this.entries.length - 1);
    this.sizes.push(data.length);
    this.offsets.push(this.payload);
    this.dts.push(dts);
    this.key.push(key);
    this.payload += data.length;
    this.pending.push(data);
    this.pendingBytes += data.length;
    if (this.pendingBytes >= MP4_PART_BYTES) this.roll();
  }

  /** Each sample's duration: the gap to the next, the last repeating the one before it. */
  durations(fallback = RTP_VIDEO_CLOCK_HZ / 30): number[] {
    const out: number[] = [];
    for (let i = 0; i < this.dts.length - 1; i++) out.push(this.dts[i + 1] - this.dts[i]);
    if (this.dts.length > 0) out.push(out.length > 0 ? out[out.length - 1] : fallback);
    return out;
  }

  /** Each sample's time in ticks, as given to {@link add}. */
  times(): readonly number[] {
    return this.dts;
  }

  /** The whole clip as one file. */
  finish(): Blob {
    return this.write(0, this.sizes.length);
  }

  /**
   * Samples `from` (inclusive) to `to` (exclusive) as a file of their own,
   * timed from the first of them. The range must open on a keyframe; see
   * {@link keyframeAtOrBefore}. Sample bytes are shared with the full clip,
   * not copied.
   */
  slice(from: number, to: number): Blob {
    return this.write(from, to);
  }

  /** The last keyframe at or before sample `index`, or 0 when there is none. */
  keyframeAtOrBefore(index: number): number {
    for (let i = Math.min(index, this.key.length - 1); i > 0; i--) if (this.key[i]) return i;
    return 0;
  }

  /**
   * `tkhd` and the codec string describe the largest picture in the file, so
   * a player sizes its view for the whole of it.
   */
  private write(from: number, to: number): Blob {
    this.roll();
    if (to <= from) return new Blob([], { type: "video/mp4" });

    const used: number[] = [];
    const chunks: Chunk[] = [];
    for (let i = from; i < to; i++) {
      const source = this.entryOf[i];
      const last = chunks[chunks.length - 1];
      if (last && used[last.entry] === source) {
        last.samples++;
        continue;
      }
      let entry = used.indexOf(source);
      if (entry < 0) entry = used.push(source) - 1;
      chunks.push({ entry, first: i, samples: 1, prefix: new Uint8Array(0) });
    }
    const entries = used.map((source) => this.entries[source]);
    if (entries.length > 1) {
      for (const c of chunks) c.prefix = inBandParameterSets(entries[c.entry]);
    }

    const all = this.durations();
    const durations = all.slice(from, to);
    const sizes = this.sizes.slice(from, to);
    for (const c of chunks) sizes[c.first - from] += c.prefix.length;
    const chunkOffsets: number[] = [];
    let mdatBytes = 0;
    for (const c of chunks) {
      chunkOffsets.push(mdatBytes);
      const end = c.first + c.samples;
      mdatBytes += c.prefix.length + this.offsets[end - 1] + this.sizes[end - 1] - this.offsets[c.first];
    }
    const keys: number[] = [];
    for (let i = from; i < to; i++) if (this.key[i]) keys.push(i - from + 1);

    const largest = entries.reduce((a, b) =>
      b.info.width * b.info.height > a.info.width * a.info.height ? b : a,
    );
    let totalTicks = 0;
    for (const d of durations) totalTicks += d;
    const movieMs = Math.round((totalTicks * 1000) / RTP_VIDEO_CLOCK_HZ);
    /* Set below, once the file's size is known; the boxes read it when built. */
    let large = false;

    const stbl = (base: number): Uint8Array =>
      box(
        "stbl",
        fullBox("stsd", 0, 0, u32(entries.length), ...entries.map(avc1)),
        stts(durations),
        fullBox("stss", 0, 0, table(keys.length, 1, (i, put) => put(keys[i]))),
        fullBox(
          "stsc",
          0,
          0,
          table(chunks.length, 3, (i, put) => {
            put(i + 1);
            put(chunks[i].samples);
            put(chunks[i].entry + 1);
          }),
        ),
        fullBox("stsz", 0, 0, u32(0), table(sizes.length, 1, (i, put) => put(sizes[i]))),
        large
          ? fullBox(
              "co64",
              0,
              0,
              table(chunkOffsets.length, 2, (i, put) => {
                const at = base + chunkOffsets[i];
                put(Math.floor(at / 2 ** 32));
                put(at);
              }),
            )
          : fullBox("stco", 0, 0, table(chunkOffsets.length, 1, (i, put) => put(base + chunkOffsets[i]))),
      );
    const moov = (base: number): Uint8Array =>
      box(
        "moov",
        fullBox(
          "mvhd",
          0,
          0,
          u32(0),
          u32(0),
          u32(1000),
          u32(movieMs),
          u32(0x00010000),
          u16(0x0100),
          new Uint8Array(10),
          UNITY_MATRIX,
          new Uint8Array(24),
          u32(2),
        ),
        box(
          "trak",
          fullBox(
            "tkhd",
            0,
            3,
            u32(0),
            u32(0),
            u32(1),
            u32(0),
            u32(movieMs),
            new Uint8Array(8),
            u16(0),
            u16(0),
            u16(0),
            u16(0),
            UNITY_MATRIX,
            u32(largest.info.width * 0x10000),
            u32(largest.info.height * 0x10000),
          ),
          box(
            "mdia",
            fullBox(
              "mdhd",
              0,
              0,
              u32(0),
              u32(0),
              u32(RTP_VIDEO_CLOCK_HZ),
              u32(totalTicks),
              u16(0x55c4),
              u16(0),
            ),
            fullBox("hdlr", 0, 0, u32(0), ascii("vide"), new Uint8Array(12), ascii("VideoHandler\0")),
            box(
              "minf",
              fullBox("vmhd", 0, 1, new Uint8Array(8)),
              box("dinf", fullBox("dref", 0, 0, u32(1), fullBox("url ", 0, 1))),
              stbl(base),
            ),
          ),
        ),
      );

    const ftyp = box("ftyp", ascii("isom"), u32(0x200), ascii("isomiso2avc1mp41"));
    /* The last chunk offset, and the mdat size, must fit 32 bits for stco
       and a plain mdat header; measured with those, the smaller layout. */
    large = ftyp.length + moov(0).length + 8 + mdatBytes > this.offsetLimit;
    const mdatHeader = large
      ? concat([u32(1), ascii("mdat"), u64(mdatBytes + 16)])
      : concat([u32(mdatBytes + 8), ascii("mdat")]);
    /* Chunk offsets are fixed-width fields, so the moov size does not depend on their values. */
    const moovSize = moov(0).length;
    const header = concat([ftyp, moov(ftyp.length + moovSize + mdatHeader.length), mdatHeader]);
    const payload = new Blob(this.parts);
    const body: BlobPart[] = [];
    for (const c of chunks) {
      const end = c.first + c.samples;
      if (c.prefix.length > 0) body.push(c.prefix);
      body.push(payload.slice(this.offsets[c.first], this.offsets[end - 1] + this.sizes[end - 1]));
    }
    return new Blob([header, ...body], {
      type: `video/mp4;codecs=${avcCodecString(largest.info)}`,
    });
  }

  /** Moves the pending sample bytes into a Blob part, letting their buffers go. */
  private roll(): void {
    if (this.pending.length === 0) return;
    this.parts.push(new Blob(this.pending));
    this.pending = [];
    this.pendingBytes = 0;
  }
}
