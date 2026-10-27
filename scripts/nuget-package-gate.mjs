#!/usr/bin/env node
/**
 * Gate the packed KspGonogo.Kerbcast.API and KspGonogo.Kerbcast.API.Testing
 * packages before anything is pushed.
 *
 * Everything that can go wrong with them goes wrong in another mod's restore
 * or in a player's GameData, long after a green build here, and all of it packs
 * happily:
 *
 *   - A KSP OR UNITY REFERENCE. The point of the packages is that a mod
 *     compiles against them without a KSP install. Every packed assembly may
 *     reference only the base class library (and Testing, Kerbcast.API), read
 *     from its metadata rather than trusted from the csproj
 *   - THE WRONG FILES IN A LIB GROUP. net48 is what KSP loads and
 *     netstandard2.0 is what a modern test project resolves; each must carry
 *     exactly its own assembly and docs, and nothing may sit outside lib/ that
 *     NuGet would hand a consumer (build/, content/, tools/)
 *   - A DEPENDENCY THAT SHOULD NOT BE THERE. The API depends on nothing.
 *     Testing depends on exactly its own version of the API, because it uses
 *     the API's internals
 *   - THE WRONG VERSIONS. The package version is KerbcastApiVersion (or the
 *     RC this run asked for), and AssemblyVersion stays <major>.0.0.0 so a mod
 *     built against any 1.x binds to any other
 *
 * Plus the metadata nuget.org shows: id, readme, licence, project URL,
 * repository, and the symbols package beside each one.
 *
 * SELF-CHECK. The audit is a pure function over a description of the packages,
 * so the script plants one violation of every family into a copy and requires
 * each to be caught, on every run, before it reports. A check that cannot see
 * its own failure reports nothing, and nothing reads as success.
 *
 * Usage:
 *   node scripts/nuget-package-gate.mjs <api.nupkg> <testing.nupkg>
 *   node scripts/nuget-package-gate.mjs <api.nupkg> <testing.nupkg> --rc <version>
 *
 * Without --rc the packages must carry KerbcastApiVersion from
 * Plugin/Versions.props. With it they must carry exactly that version, which
 * must be KerbcastApiVersion-rc.<n>.
 */

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { readAssembly } from "./nuget-metadata.mjs";

const REPO_ROOT = join(dirname(fileURLToPath(import.meta.url)), "..");
const VERSIONS_PROPS = join(REPO_ROOT, "Plugin", "Versions.props");

const API_ID = "KspGonogo.Kerbcast.API";
const TESTING_ID = "KspGonogo.Kerbcast.API.Testing";

const TFMS = ["net48", "netstandard2.0"];
const NUSPEC_TFMS = { ".NETFramework4.8": "net48", ".NETStandard2.0": "netstandard2.0" };

/** The base class library each framework group may reference, and nothing more. */
const BCL = {
  net48: ["mscorlib", "System", "System.Core"],
  "netstandard2.0": ["netstandard"],
};

/** What each package is, spelled out rather than derived: the asymmetry is the design. */
const EXPECTED = {
  [API_ID]: { assembly: "Kerbcast.API", references: [], dependsOnApi: false },
  [TESTING_ID]: { assembly: "Kerbcast.API.Testing", references: ["Kerbcast.API"], dependsOnApi: true },
};

/** Archive members NuGet itself writes, which carry nothing a consumer binds to. */
const PACKAGING_MEMBER = /^(_rels\/|package\/services\/|\[Content_Types\]\.xml$|[^/]+\.nuspec$)/;

const unzipText = (nupkg, member) =>
  execFileSync("unzip", ["-p", nupkg, member], { encoding: "utf8", maxBuffer: 64 * 1024 * 1024 });
const unzipBinary = (nupkg, member) =>
  execFileSync("unzip", ["-p", nupkg, member], { encoding: "buffer", maxBuffer: 64 * 1024 * 1024 });

/** A .nupkg as a plain object the audit reasons about and the self-check mutates. */
function describePackage(nupkg) {
  const members = execFileSync("unzip", ["-Z1", nupkg], { encoding: "utf8" }).split("\n").filter(Boolean);
  const nuspecName = members.find((m) => /^[^/]+\.nuspec$/.test(m));
  if (!nuspecName) throw new Error(`${nupkg} carries no .nuspec: it is not a NuGet package`);
  const nuspec = unzipText(nupkg, nuspecName);
  const pick = (tag) => new RegExp(`<${tag}[^>]*>([\\s\\S]*?)</${tag}>`).exec(nuspec)?.[1]?.trim();

  const libFiles = {};
  for (const member of members) {
    const m = /^lib\/([^/]+)\/(.+)$/.exec(member);
    if (m) (libFiles[m[1]] ??= []).push(m[2]);
  }

  const dependencyGroups = {};
  const block = /<dependencies>([\s\S]*?)<\/dependencies>/.exec(nuspec)?.[1] ?? "";
  for (const g of block.matchAll(/<group targetFramework="([^"]+)"\s*(\/>|>([\s\S]*?)<\/group>)/g)) {
    dependencyGroups[NUSPEC_TFMS[g[1]] ?? g[1]] = [
      ...(g[3] ?? "").matchAll(/<dependency id="([^"]+)" version="([^"]+)"/g),
    ].map((d) => ({ id: d[1], version: d[2] }));
  }

  const assemblies = {};
  for (const member of members.filter((m) => /^lib\/.+\.dll$/.test(m))) {
    assemblies[member] = readAssembly(unzipBinary(nupkg, member));
  }

  return {
    id: pick("id"),
    version: pick("version"),
    readme: pick("readme"),
    license: pick("license"),
    projectUrl: pick("projectUrl"),
    hasRepository: /<repository\b/.test(nuspec),
    otherMembers: members.filter(
      (m) => !m.startsWith("lib/") && !PACKAGING_MEMBER.test(m) && !["README.md", "LICENSE"].includes(m),
    ),
    members,
    libFiles,
    dependencyGroups,
    assemblies,
  };
}

/** Pure: package descriptions in, human-readable failures out. */
export function audit(packages, expected) {
  const failures = [];
  const ids = packages.map((p) => p.id).sort();
  if (ids.join(",") !== [API_ID, TESTING_ID].sort().join(",")) {
    failures.push(
      `the packages are [${ids.join(", ")}], expected [${API_ID}, ${TESTING_ID}]. ` +
        `nuget.org ids are permanent, and a typo publishes a second package rather than failing.`,
    );
  }

  for (const pkg of packages) {
    const want = EXPECTED[pkg.id];
    if (!want) continue;
    const name = pkg.id;

    if (pkg.version !== expected.version) {
      failures.push(`${name} is version ${pkg.version}, but this run ships ${expected.version}`);
    }
    if (!pkg.readme || !pkg.members.includes(pkg.readme)) failures.push(`${name} has no packed readme`);
    if (pkg.license !== "MIT") {
      failures.push(`${name}'s licence is ${pkg.license ?? "missing"}, expected the MIT expression its LICENSE grants`);
    }
    if (!pkg.members.includes("LICENSE")) failures.push(`${name} does not carry its LICENSE file`);
    if (!pkg.projectUrl) failures.push(`${name} has no <projectUrl>`);
    if (!pkg.hasRepository) failures.push(`${name} has no <repository>, so a debugger cannot find its source`);
    if (pkg.otherMembers.length > 0) {
      failures.push(
        `${name} carries [${pkg.otherMembers.join(", ")}] outside lib/. NuGet hands build/, content/ and ` +
          `tools/ to every consumer; this package ships an assembly and its docs only.`,
      );
    }

    const tfms = Object.keys(pkg.libFiles).sort();
    if (tfms.join(",") !== TFMS.join(",")) {
      failures.push(`${name}'s lib/ holds [${tfms.join(", ")}], expected [${TFMS.join(", ")}]`);
    }

    for (const tfm of TFMS) {
      const files = (pkg.libFiles[tfm] ?? []).slice().sort();
      const wantFiles = [`${want.assembly}.dll`, `${want.assembly}.xml`];
      if (files.join(",") !== wantFiles.join(",")) {
        failures.push(
          `${name}'s lib/${tfm} holds [${files.join(", ") || "nothing"}], expected [${wantFiles.join(", ")}]. ` +
            `Anything else lands in a mod's build output, and from there in GameData.`,
        );
      }

      const deps = (pkg.dependencyGroups[tfm] ?? []).map((d) => `${d.id} ${d.version}`).sort();
      const wantDeps = want.dependsOnApi ? [`${API_ID} [${expected.version}]`] : [];
      if (deps.join(",") !== wantDeps.join(",")) {
        failures.push(
          `${name}'s ${tfm} dependency group is [${deps.join(", ") || "empty"}], expected ` +
            `[${wantDeps.join(", ") || "empty"}]. Testing uses the API's internals, so it pins ` +
            `exactly its own version of it; the API depends on nothing.`,
        );
      }

      const asm = pkg.assemblies[`lib/${tfm}/${want.assembly}.dll`];
      if (!asm) continue;
      if (asm.assembly?.name !== want.assembly) {
        failures.push(`lib/${tfm}/${want.assembly}.dll names itself ${asm.assembly?.name ?? "nothing"}`);
      }
      if (asm.assembly?.version !== expected.assemblyVersion) {
        failures.push(
          `${want.assembly} (${tfm}) has AssemblyVersion ${asm.assembly?.version}, expected ` +
            `${expected.assemblyVersion}. It stays fixed for the whole major so a mod built against ` +
            `any minor binds to any other.`,
        );
      }
      const allowed = new Set([...BCL[tfm], ...want.references]);
      const stray = asm.assemblyRefs.map((r) => r.name).filter((n) => !allowed.has(n));
      if (stray.length > 0) {
        failures.push(
          `${want.assembly} (${tfm}) references [${stray.join(", ")}]. A mod must compile against this ` +
            `package without KSP or Unity, so it may reference only [${[...allowed].join(", ")}].`,
        );
      }
      for (const ref of asm.assemblyRefs.filter((r) => r.name === "Kerbcast.API")) {
        if (ref.version !== expected.assemblyVersion) {
          failures.push(`${want.assembly} (${tfm}) binds Kerbcast.API ${ref.version}, expected ${expected.assemblyVersion}`);
        }
      }
    }
  }
  return failures;
}

const lib = (p, tfm, f) => p.libFiles[tfm].push(f);
const find = (pkgs, id) => pkgs.find((p) => p.id === id);
const apiAsm = (pkgs, tfm) => find(pkgs, API_ID).assemblies[`lib/${tfm}/Kerbcast.API.dll`];
const testingAsm = (pkgs, tfm) => find(pkgs, TESTING_ID).assemblies[`lib/${tfm}/Kerbcast.API.Testing.dll`];

/** One mutation per failure family. Each must be caught, or the gate is blind. */
const PLANTS = [
  { what: "a UnityEngine reference in the net48 API", mutate: (p) => apiAsm(p, "net48").assemblyRefs.push({ name: "UnityEngine.CoreModule", version: "0.0.0.0" }) },
  { what: "an Assembly-CSharp reference in the netstandard2.0 Testing", mutate: (p) => testingAsm(p, "netstandard2.0").assemblyRefs.push({ name: "Assembly-CSharp", version: "0.0.0.0" }) },
  { what: "Kerbcast.API.dll copied into Testing's net48 group", mutate: (p) => lib(find(p, TESTING_ID), "net48", "Kerbcast.API.dll") },
  { what: "a pdb inside the API's lib group", mutate: (p) => lib(find(p, API_ID), "netstandard2.0", "Kerbcast.API.pdb") },
  { what: "the API missing from lib/net48", mutate: (p) => { const a = find(p, API_ID); a.libFiles.net48 = a.libFiles.net48.filter((f) => f !== "Kerbcast.API.dll"); } },
  { what: "an extra net8.0 lib group", mutate: (p) => { find(p, API_ID).libFiles["net8.0"] = ["Kerbcast.API.dll"]; } },
  { what: "a build/ props file a consumer would import", mutate: (p) => find(p, API_ID).otherMembers.push("build/KspGonogo.Kerbcast.API.props") },
  { what: "the API depending on a package", mutate: (p) => { find(p, API_ID).dependencyGroups.net48 = [{ id: "Krafs.Publicizer", version: "2.2.1" }]; } },
  { what: "Testing depending on the API as a floating range", mutate: (p) => { find(p, TESTING_ID).dependencyGroups["netstandard2.0"] = [{ id: API_ID, version: "1.0.0" }]; } },
  { what: "Testing depending on something besides the API", mutate: (p) => find(p, TESTING_ID).dependencyGroups.net48.push({ id: "xunit.assert", version: "2.9.2" }) },
  { what: "an AssemblyVersion that follows the package version", mutate: (p) => { apiAsm(p, "net48").assembly.version = "1.3.0.0"; } },
  { what: "Testing binding a different Kerbcast.API", mutate: (p) => { testingAsm(p, "net48").assemblyRefs.find((r) => r.name === "Kerbcast.API").version = "2.0.0.0"; } },
  { what: "a package version this run does not ship", mutate: (p) => { find(p, TESTING_ID).version = "999.0.0"; } },
  { what: "a misspelt package id", mutate: (p) => { find(p, API_ID).id = "Kerbcast.API"; } },
  { what: "a licence other than MIT", mutate: (p) => { find(p, API_ID).license = "CC-BY-NC-SA-4.0"; } },
  { what: "a missing readme", mutate: (p) => { find(p, TESTING_ID).readme = undefined; } },
  { what: "a missing repository", mutate: (p) => { find(p, API_ID).hasRepository = false; } },
];

function blindSpots(packages, expected) {
  return PLANTS.filter((plant) => {
    const copy = structuredClone(packages);
    plant.mutate(copy);
    return audit(copy, expected).length === 0;
  }).map((plant) => plant.what);
}

function apiVersion() {
  const m = /<KerbcastApiVersion>([^<]+)<\/KerbcastApiVersion>/.exec(readFileSync(VERSIONS_PROPS, "utf8"));
  if (!m || !/^\d+\.\d+\.\d+$/.test(m[1])) throw new Error(`no X.Y.Z KerbcastApiVersion in ${VERSIONS_PROPS}`);
  return m[1];
}

const args = process.argv.slice(2);
const rcAt = args.indexOf("--rc");
const rc = rcAt >= 0 ? args[rcAt + 1] : null;
const files = args.filter((_, i) => rcAt < 0 || (i !== rcAt && i !== rcAt + 1));
if (files.length !== 2 || files.some((f) => !existsSync(f))) {
  console.error("usage: node scripts/nuget-package-gate.mjs <api.nupkg> <testing.nupkg> [--rc <version>]");
  process.exit(2);
}

const api = apiVersion();
if (rc !== null && !new RegExp(`^${api.replace(/\./g, "\\.")}-rc\\.\\d+$`).test(rc)) {
  console.error(`--rc ${rc} is not ${api}-rc.<n>`);
  process.exit(2);
}
const expected = { version: rc ?? api, assemblyVersion: `${api.split(".")[0]}.0.0.0` };

const packages = files.map(describePackage);
const blind = blindSpots(packages, expected);
if (blind.length > 0) {
  console.error(
    "\nnuget-package-gate is BLIND: it did not catch a planted\n" +
      blind.map((b) => `  x ${b}`).join("\n") +
      "\nA gate that cannot see its own failure reports nothing, and nothing reads as success.\n",
  );
  process.exit(1);
}

const failures = audit(packages, expected);
for (const f of files) {
  if (!existsSync(f.replace(/\.nupkg$/, ".snupkg"))) failures.push(`${f} has no .snupkg beside it`);
}
if (failures.length > 0) {
  console.error(`\nThe packages are NOT publishable:\n${failures.map((f) => `  x ${f}`).join("\n")}\n`);
  process.exit(1);
}

console.log(
  `${API_ID} and ${TESTING_ID} ${expected.version} are clean: net48 and netstandard2.0 each carry ` +
    `their own assembly and docs alone, referencing only the base class library (and Testing, ` +
    `Kerbcast.API), AssemblyVersion ${expected.assemblyVersion}, the API depends on nothing and ` +
    `Testing on exactly [${expected.version}] of it, MIT, symbols beside both. ` +
    `${PLANTS.length} planted violations were all caught.`,
);
