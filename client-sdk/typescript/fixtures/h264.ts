/* Test-only access to the H.264 fixture, outside src so it never ships. */
import fs from "fs";
import path from "path";
import { NAL_AUD, NAL_IDR, NAL_SPS, nalType, splitAnnexB } from "../src/mp4";

/** 12 frames of 66x50 baseline (no AUDs) then 12 of 96x64 high (with AUDs), IDR every 6. */
export const TWO_SIZES_H264 = fs.readFileSync(path.resolve(__dirname, "two-sizes.h264"));

/** The fixture's access units, one per frame, each re-joined as Annex B. */
export function accessUnits(): Uint8Array[] {
  const units: Uint8Array[][] = [];
  let current: Uint8Array[] = [];
  let hasSlice = false;
  for (const nal of splitAnnexB(new Uint8Array(TWO_SIZES_H264))) {
    const type = nalType(nal);
    const isSlice = type === 1 || type === NAL_IDR;
    if (hasSlice && (isSlice || type === NAL_AUD || type === NAL_SPS || type === 6)) {
      units.push(current);
      current = [];
      hasSlice = false;
    }
    current.push(nal);
    if (isSlice) hasSlice = true;
  }
  units.push(current);
  return units.map((nals) => {
    const parts = nals.flatMap((n) => [new Uint8Array([0, 0, 0, 1]), n]);
    const out = new Uint8Array(parts.reduce((a, p) => a + p.length, 0));
    let at = 0;
    for (const p of parts) {
      out.set(p, at);
      at += p.length;
    }
    return out;
  });
}
