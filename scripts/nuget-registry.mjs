#!/usr/bin/env node
/**
 * What nuget.org already has of the Kerbcast.API packages, for release.yml.
 *
 *   plan <api.nupkg> <testing.nupkg>
 *     Decides, per package, whether this run publishes it or skips it, and
 *     says which in the log. A version nuget.org already carries is skipped
 *     only when the published copy matches the fresh pack (nuget-fossil-check);
 *     any difference fails, because nuget.org never replaces a version. An RC
 *     whose API version is already out stable is checked against that stable
 *     copy and skipped: the API has not moved, so there is nothing to try out.
 *     Writes `publish=<file names>` to $GITHUB_OUTPUT.
 *
 *   listed <id>@<version>... [--wait-minutes N]
 *     Waits until nuget.org's restore index lists every version. A push is
 *     accepted at once but listed only after validation, sometimes twenty
 *     minutes later, and a restore in between fails with NU1102.
 *
 *   matches <file.nupkg>
 *     Downloads the version nuget.org serves under that file's id and version
 *     and fails unless it matches the file (nuget-fossil-check). A push skipped
 *     as a duplicate, or another run racing on the same version, would
 *     otherwise leave different bytes behind a green release.
 *
 * Only a 404 from the index means "never published". Any other answer is an
 * error: reading an outage as absent would attempt a push nuget.org refuses,
 * and reading it as present would skip one.
 */

import { spawnSync } from "node:child_process";
import { appendFileSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const SCRIPTS = dirname(fileURLToPath(import.meta.url));
const VERSIONS_PROPS = join(SCRIPTS, "..", "Plugin", "Versions.props");
/* Overridable so the skip and fossil paths can be tried against a local stand-in. */
const FLAT = process.env.NUGET_FLAT_CONTAINER ?? "https://api.nuget.org/v3-flatcontainer";

async function publishedVersions(id) {
  const res = await fetch(`${FLAT}/${id.toLowerCase()}/index.json`, { cache: "no-store" });
  if (res.status === 404) return [];
  if (res.status !== 200) throw new Error(`nuget.org answered ${res.status} for ${id}'s version list`);
  return (await res.json()).versions;
}

async function download(id, version, dir) {
  const lid = id.toLowerCase();
  const lv = version.toLowerCase();
  const res = await fetch(`${FLAT}/${lid}/${lv}/${lid}.${lv}.nupkg`);
  if (res.status !== 200) throw new Error(`nuget.org answered ${res.status} for ${id} ${version}'s package`);
  const file = join(dir, `${id}.${version}.published.nupkg`);
  writeFileSync(file, Buffer.from(await res.arrayBuffer()));
  return file;
}

/** The id and version a packed file carries, read from its name as dotnet pack writes it. */
function identify(nupkg) {
  const m = /^(KspGonogo\.Kerbcast\.API(?:\.Testing)?)\.(\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)\.nupkg$/.exec(basename(nupkg));
  if (!m) throw new Error(`${nupkg} is not a KspGonogo.Kerbcast.API package`);
  return { id: m[1], version: m[2] };
}

function stableApiVersion() {
  const m = /<KerbcastApiVersion>([^<]+)<\/KerbcastApiVersion>/.exec(readFileSync(VERSIONS_PROPS, "utf8"));
  if (!m) throw new Error(`no KerbcastApiVersion in ${VERSIONS_PROPS}`);
  return m[1];
}

const summary = (line) => {
  console.log(line);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${line}\n`);
};

function fossilCheck(fresh, published) {
  return spawnSync(process.execPath, [join(SCRIPTS, "nuget-fossil-check.mjs"), fresh, published], { stdio: "inherit" }).status === 0;
}

async function matches(nupkg) {
  const { id, version } = identify(nupkg);
  const published = await download(id, version, mkdtempSync(join(tmpdir(), "kerbcast-nuget-matches-")));
  if (!fossilCheck(nupkg, published)) {
    summary(`FAIL ${id} ${version}: what nuget.org serves is not this run's artifact`);
    process.exit(1);
  }
  summary(`OK ${id} ${version}: nuget.org serves this run's artifact`);
}

async function plan(files) {
  const stable = stableApiVersion();
  const work = mkdtempSync(join(tmpdir(), "kerbcast-nuget-plan-"));
  const publish = [];
  for (const nupkg of files) {
    const { id, version } = identify(nupkg);
    const versions = await publishedVersions(id);
    const compareWith = versions.includes(version.toLowerCase())
      ? version
      : version !== stable && versions.includes(stable)
        ? stable
        : null;
    if (compareWith === null) {
      summary(`PUBLISH ${id} ${version}: nuget.org does not have it yet`);
      publish.push(basename(nupkg));
      continue;
    }
    const published = await download(id, compareWith, work);
    if (!fossilCheck(nupkg, published)) {
      summary(`FAIL ${id} ${version}: nuget.org's ${compareWith} differs from this pack, and its version has not moved`);
      process.exit(1);
    }
    summary(
      compareWith === version
        ? `SKIP ${id} ${version}: already on nuget.org, and the published copy matches this pack`
        : `SKIP ${id} ${version}: the API is unchanged since ${compareWith}, which is already on nuget.org and matches this pack`,
    );
  }
  if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, `publish=${publish.join(" ")}\n`);
}

async function listed(specs, waitMinutes) {
  const deadline = Date.now() + waitMinutes * 60_000;
  const pending = new Set(specs);
  for (;;) {
    for (const spec of [...pending]) {
      const [id, version] = spec.split("@");
      if ((await publishedVersions(id)).includes(version.toLowerCase())) {
        console.log(`${id} ${version} is listed on nuget.org`);
        pending.delete(spec);
      }
    }
    if (pending.size === 0) return;
    if (Date.now() > deadline) {
      console.error(`::error::not listed after ${waitMinutes} minutes: ${[...pending].join(", ")}`);
      process.exit(1);
    }
    console.log(`waiting for nuget.org to list ${[...pending].join(", ")}`);
    await new Promise((r) => setTimeout(r, 30_000));
  }
}

const [command, ...rest] = process.argv.slice(2);
try {
  if (command === "plan" && rest.length === 2) {
    await plan(rest);
  } else if (command === "matches" && rest.length === 1) {
    await matches(rest[0]);
  } else if (command === "listed" && rest.length > 0) {
    const at = rest.indexOf("--wait-minutes");
    const minutes = at >= 0 ? Number(rest[at + 1]) : 45;
    await listed(rest.filter((_, i) => at < 0 || (i !== at && i !== at + 1)), minutes);
  } else {
    console.error(
      "usage: node scripts/nuget-registry.mjs plan <api.nupkg> <testing.nupkg>\n" +
        "       node scripts/nuget-registry.mjs listed <id>@<version>... [--wait-minutes N]\n" +
        "       node scripts/nuget-registry.mjs matches <file.nupkg>",
    );
    process.exit(2);
  }
} catch (err) {
  /* Exit 2, never 1: 1 is a check that ran and failed, 2 is one that could not run. */
  console.error(`::error::${err.message}`);
  process.exit(2);
}
