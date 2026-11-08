#!/usr/bin/env node
/**
 * Is a version nuget.org already carries a fossil of what this commit would
 * publish?
 *
 * The Kerbcast.API packages are versioned by KerbcastApiVersion, the API's own
 * line, so most releases pack a version nuget.org already has, and the release
 * skips the push. That skip is right only when nothing in the package changed.
 * A changed assembly or doc file under an unchanged version would never reach
 * anyone while the release reads green, so this compares the fresh pack with
 * the published copy and fails on any real difference: the fix is to raise
 * KerbcastApiVersion.
 *
 * Two builds of identical source at different commits are not byte-identical
 * even with ContinuousIntegrationBuild set: SourceLink makes the PE timestamp,
 * debug-directory timestamps, PDB id, PDB checksum and module MVID depend on the
 * commit. Those regions are zeroed before the assemblies are compared; every
 * other byte must agree. XML docs are compared as they are.
 *
 * A compiler that emits different IL for the same source also reads as a
 * difference. release.yml pins the SDK for that reason; if the pin moves, the
 * next release has to raise the API version, and that stays loud on purpose.
 *
 * Usage:
 *   node scripts/nuget-fossil-check.mjs <fresh.nupkg> <published.nupkg>
 *
 * Exits 0 when everything agrees, 1 on a difference or on a package it cannot
 * read (a failed read is "could not check", never "no difference").
 */

import { execFileSync } from "node:child_process";
import { existsSync } from "node:fs";
import { identityRegions } from "./nuget-metadata.mjs";

const listMembers = (nupkg) =>
  execFileSync("unzip", ["-Z1", nupkg], { encoding: "utf8" }).split("\n").filter(Boolean);

const unzipBinary = (nupkg, member) =>
  execFileSync("unzip", ["-p", nupkg, member], { encoding: "buffer", maxBuffer: 64 * 1024 * 1024 });

/** Member path to bytes, for every assembly and doc file a consumer compiles against. */
function readLib(nupkg) {
  const files = new Map();
  for (const member of listMembers(nupkg)) {
    if (/^lib\/[^/]+\/.+\.(dll|xml)$/.test(member)) files.set(member, unzipBinary(nupkg, member));
  }
  return files;
}

function zeroed(buf, regions) {
  const copy = Buffer.from(buf);
  for (const { offset, length } of regions) copy.fill(0, offset, offset + length);
  return copy;
}

function compare(fresh, published) {
  const problems = [];
  for (const member of [...new Set([...fresh.keys(), ...published.keys()])].sort()) {
    const a = fresh.get(member);
    const b = published.get(member);
    if (!a) {
      problems.push(`${member}: the published copy carries this, the fresh pack does not`);
      continue;
    }
    if (!b) {
      problems.push(`${member}: the fresh pack carries this, the published copy does not`);
      continue;
    }
    if (member.endsWith(".xml")) {
      if (!a.equals(b)) problems.push(`${member}: the doc comments differ`);
      continue;
    }
    let na, nb;
    try {
      na = zeroed(a, identityRegions(a));
      nb = zeroed(b, identityRegions(b));
    } catch (err) {
      problems.push(`${member}: could not read its PE structure (${err.message})`);
      continue;
    }
    if (!na.equals(nb)) {
      let n = 0;
      if (na.length === nb.length) for (let i = 0; i < na.length; i++) n += na[i] !== nb[i];
      problems.push(
        `${member}: the assembly differs once build identity is normalised (${na.length === nb.length ? `${n} bytes` : "different lengths"})`,
      );
    }
  }
  return problems;
}

const [freshPath, publishedPath] = process.argv.slice(2);
if (!freshPath || !publishedPath) {
  console.error("usage: node scripts/nuget-fossil-check.mjs <fresh.nupkg> <published.nupkg>");
  process.exit(2);
}
for (const p of [freshPath, publishedPath]) {
  if (!existsSync(p)) {
    console.error(`no such .nupkg: ${p}`);
    process.exit(2);
  }
}

const fresh = readLib(freshPath);
const problems = compare(fresh, readLib(publishedPath));
if (problems.length > 0) {
  console.error(
    `\nThe version already on nuget.org is a FOSSIL of ${freshPath}:\n` +
      problems.map((p) => `  x ${p}`).join("\n") +
      "\n\nThe package changed but its version did not, so this release cannot publish the change." +
      "\nRaise KerbcastApiVersion in Plugin/Versions.props (the patch for a fix or a doc change," +
      "\nthe minor for new members) and release again.\n",
  );
  process.exit(1);
}
console.log(
  `${fresh.size} lib files agree with the published copy once build identity is normalised out: nothing shipped differs.`,
);
