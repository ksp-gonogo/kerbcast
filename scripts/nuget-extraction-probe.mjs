#!/usr/bin/env node
/**
 * Can another mod build against the packed Kerbcast.API without KSP?
 *
 * Inside this repo every project reaches Kerbcast.API by ProjectReference, so
 * nothing here proves the packages work for an outside mod. The probe copies
 * scripts/nuget-probe/ (ProbeMod, a stand-in mod, and its tests) to a directory
 * outside the repo, points NuGet at a feed holding only the two packed files
 * (plus nuget.org for the .NET reference packs), and there:
 *
 *   - builds ProbeMod for net48 and netstandard2.0 with no KSP or Unity
 *     assembly anywhere, referencing the package the way the README says
 *   - checks that reference kept Kerbcast.API.dll out of the mod's output,
 *     since a copy shipped in a mod's GameData folder shadows Kerbcast's own
 *   - runs ProbeMod.Tests, which reaches Kerbcast only through the Testing
 *     package, so the API must arrive as Testing's dependency
 *
 * IT MUST BE ABLE TO FAIL. `--plant` adds a source file to ProbeMod naming a
 * type the package does not carry. That build must fail on that type; if it
 * builds, or fails for another reason, the probe could not have seen a real gap
 * either and it exits BLIND. CI runs both.
 *
 * Every run restores into a package cache of its own: a version never changes
 * between two packs of one tree, so a shared cache would hand back whichever
 * copy it saw first. The work directory is removed on exit;
 * KERBCAST_KEEP_TMP=1 keeps it.
 *
 * Usage:
 *   node scripts/nuget-extraction-probe.mjs <api.nupkg> <testing.nupkg> [--plant]
 */

import { spawnSync } from "node:child_process";
import { copyFileSync, cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const PROBE_SOURCE = join(dirname(fileURLToPath(import.meta.url)), "nuget-probe");
const API_ID = "KspGonogo.Kerbcast.API";
const TESTING_ID = "KspGonogo.Kerbcast.API.Testing";
const PLANTED = "NugetProbePlantedAbsentType";

const args = process.argv.slice(2);
const plant = args.includes("--plant");
const [apiNupkg, testingNupkg] = args.filter((a) => a !== "--plant").map((a) => resolve(a));

const versionOf = (file, id) => {
  const name = basename(file ?? "");
  const m = new RegExp(`^${id.replace(/\./g, "\\.")}\\.(\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.]+)?)\\.nupkg$`).exec(name);
  return m?.[1];
};
const version = versionOf(apiNupkg, API_ID);
if (!version || !existsSync(apiNupkg) || !existsSync(testingNupkg) || versionOf(testingNupkg, TESTING_ID) !== version) {
  console.error(`usage: node scripts/nuget-extraction-probe.mjs <${API_ID}.X.nupkg> <${TESTING_ID}.X.nupkg> [--plant], one version`);
  process.exit(2);
}

const work = mkdtempSync(join(tmpdir(), "kerbcast-nuget-probe-"));
const cleanup = () => {
  if (process.env.KERBCAST_KEEP_TMP === "1") console.log(`kept ${work}`);
  else rmSync(work, { recursive: true, force: true });
};

const feed = join(work, "feed");
mkdirSync(feed);
for (const f of [apiNupkg, testingNupkg]) copyFileSync(f, join(feed, basename(f)));
const probe = join(work, "probe");
cpSync(PROBE_SOURCE, probe, { recursive: true, filter: (src) => !/[\\/](bin|obj)$/.test(src) });
writeFileSync(
  join(probe, "nuget.config"),
  `<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="probe" value="${feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="probe">
      <package pattern="KspGonogo.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
`,
);

function dotnet(...argv) {
  const r = spawnSync("dotnet", [...argv, "--nologo", `-p:KerbcastApiPackageVersion=${version}`], {
    cwd: probe,
    encoding: "utf8",
    env: { ...process.env, NUGET_PACKAGES: join(work, "packages"), DOTNET_CLI_TELEMETRY_OPTOUT: "1" },
  });
  return { ok: r.status === 0, out: `${r.stdout ?? ""}\n${r.stderr ?? ""}` };
}

const errorsIn = (out) => [
  ...new Set(out.split("\n").filter((l) => /error (CS|NU|MSB)\d+/.test(l)).map((l) => l.replace(/^.*?error /, "error ").trim())),
];

function finish(ok, message, out) {
  console.log(`${ok ? "ok" : "FAIL"} ${message}`);
  if (!ok && out) for (const e of errorsIn(out).slice(0, 8)) console.log(`    ${e}`);
  cleanup();
  process.exit(ok ? 0 : 1);
}

console.log(`probing ${API_ID} and ${TESTING_ID} ${version} from ${feed}`);

if (plant) {
  writeFileSync(
    join(probe, "ProbeMod", "Plant.cs"),
    `namespace ProbeMod { internal static class Plant { internal static object Use() => typeof(Kerbcast.Api.${PLANTED}); } }\n`,
  );
  const build = dotnet("build", "ProbeMod/ProbeMod.csproj", "-c", "Release");
  if (build.ok) finish(false, "BLIND: a mod naming a type the package lacks built anyway, so this probe cannot see a gap");
  if (!build.out.includes(PLANTED)) finish(false, "BLIND: the planted build failed, but not on the planted type", build.out);
  finish(true, "the planted absent type failed the build, as it must");
}

const build = dotnet("build", "ProbeMod/ProbeMod.csproj", "-c", "Release");
if (!build.ok) finish(false, "ProbeMod does not build against the packages without KSP", build.out);

for (const tfm of ["net48", "netstandard2.0"]) {
  const out = join(probe, "ProbeMod", "bin", "Release", tfm);
  if (!existsSync(join(out, "ProbeMod.dll"))) finish(false, `ProbeMod built no ${tfm} assembly`);
  if (readdirSync(out).some((f) => /^Kerbcast\.API/.test(f))) {
    finish(false, `ProbeMod's ${tfm} output carries a copy of Kerbcast.API: the README's reference would ship it in a mod`);
  }
}

const tests = dotnet("run", "--project", "ProbeMod.Tests/ProbeMod.Tests.csproj", "-c", "Release");
if (!tests.ok || !tests.out.includes("ALL PASS")) {
  finish(false, `ProbeMod.Tests does not pass against the Testing package:\n${tests.out.trim().split("\n").slice(-6).join("\n")}`, tests.out);
}
finish(true, "ProbeMod builds for net48 and netstandard2.0 without KSP, ships no Kerbcast.API copy, and its tests pass on the Testing package alone");
