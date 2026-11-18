#!/usr/bin/env node
// Fails the frontend job on npm advisories of moderate severity or higher.
//
// Two audits run against the public registry:
// - the production tree (`--omit=dev`) must have no such advisory at all;
// - the full tree may additionally carry advisories listed in
//   npm-audit-allowlist.json, each with a reason and a review date. An entry
//   past its review date fails the run, so every exception is re-examined.
//
// Run from the frontend directory: node ../.github/scripts/check-npm-audit.mjs

import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";

const registry = "https://registry.npmjs.org/";
const severityRank = { info: 0, low: 1, moderate: 2, high: 3, critical: 4 };
const threshold = severityRank.moderate;

let failed = false;
const error = (message) => {
  console.log(`::error::${message}`);
  failed = true;
};

function audit(extraArgs) {
  const label = ["npm audit", ...extraArgs].join(" ");
  const result = spawnSync("npm", ["audit", "--json", `--registry=${registry}`, ...extraArgs], {
    encoding: "utf8",
    maxBuffer: 64 * 1024 * 1024,
  });
  let report;
  try {
    report = JSON.parse(result.stdout);
  } catch {
    console.log(`::error::${label} returned no JSON report (exit ${result.status}).`);
    process.exit(1);
  }
  if (report.error) {
    console.log(`::error::${label} failed: ${report.error.summary ?? report.error.code}`);
    process.exit(1);
  }
  return report;
}

// Advisories at or above the threshold, keyed by advisory id. Entries in `via`
// that are strings only point at another vulnerable package, whose own entry
// carries the advisory.
function advisories(report) {
  const found = new Map();
  for (const [dependent, vulnerability] of Object.entries(report.vulnerabilities ?? {})) {
    for (const via of vulnerability.via ?? []) {
      if (typeof via !== "object" || (severityRank[via.severity] ?? 0) < threshold) continue;
      const id = String(via.url ?? via.source).split("/").pop();
      const entry = found.get(id) ?? { id, name: via.name, title: via.title, severity: via.severity, url: via.url, dependents: new Set() };
      entry.dependents.add(dependent);
      found.set(id, entry);
    }
  }
  return found;
}

const describe = (a) => `${a.id} (${a.severity}) in ${a.name}: ${a.title} ${a.url ?? ""}`.trim();

const production = advisories(audit(["--omit=dev"]));
for (const advisory of production.values()) {
  error(`Production dependency advisory: ${describe(advisory)}`);
}

const allowlist = JSON.parse(readFileSync(new URL("./npm-audit-allowlist.json", import.meta.url), "utf8"));
const allowed = new Map(allowlist.map((entry) => [entry.id, entry]));
const today = new Date().toISOString().slice(0, 10);

const full = advisories(audit([]));
for (const advisory of full.values()) {
  const entry = allowed.get(advisory.id);
  if (!entry) {
    error(`Dependency advisory: ${describe(advisory)}`);
  } else if (entry.package !== advisory.name) {
    error(`Allowlist entry ${entry.id} names ${entry.package}, but the advisory affects ${advisory.name}.`);
  } else if (entry.reviewBy < today) {
    error(`Allowlist entry ${entry.id} passed its review date ${entry.reviewBy}; re-check the advisory and update or remove the entry.`);
  } else {
    console.log(`::warning::Allowed until ${entry.reviewBy}: ${describe(advisory)}`);
  }
}

for (const entry of allowlist) {
  if (!full.has(entry.id)) {
    console.log(`::notice::Allowlist entry ${entry.id} no longer matches any advisory; remove it.`);
  }
}

if (failed) process.exit(1);
console.log(`npm audit passed: ${production.size} production and ${full.size} total advisories at moderate or higher, all allowed.`);
