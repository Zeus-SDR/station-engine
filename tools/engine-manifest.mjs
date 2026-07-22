// SPDX-License-Identifier: GPL-2.0-or-later
//
// Generates the engine channel manifest (latest.json) consumed by the Zeus Link
// launcher's ReleaseEngineProvider. The launcher deserializes this with
// `#[serde(deny_unknown_fields)]`, so the output must contain EXACTLY these
// fields and nothing else:
//
//   {
//     "schema_version": 1,
//     "version": "<string>",
//     "minEngineVersion": "<string>",   // optional; omitted when not supplied
//     "artifacts": [
//       { "target": "<rust-triple>", "url": "<https url>",
//         "sha256": "<lowercase hex>", "archive": "raw" | "zip",
//         "executable": "<path inside the archive>" }
//     ]
//   }
//
// `url` is base-url + "/" + the artifact file's basename, so the R2 object key
// must be uploaded under that same basename. sha256 is computed here from the
// file on disk, so the manifest can never advertise a hash the bytes don't match.
//
// Usage:
//   node tools/engine-manifest.mjs \
//     --version 0.15.1-dev.20260722.abc1234 \
//     --base-url https://downloads.zeussdr.com/engine-dev \
//     --out latest.json \
//     [--min-engine-version 0.15.0] \
//     --artifact target=x86_64-pc-windows-msvc,file=upload/StationEngine-<v>-x86_64-pc-windows-msvc.zip,executable=StationEngine.exe,archive=zip \
//     --artifact target=aarch64-apple-darwin,file=...,executable=StationEngine,archive=zip

import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { basename } from 'node:path';

const VALID_TARGETS = new Set([
  'x86_64-pc-windows-msvc',
  'aarch64-apple-darwin',
  'x86_64-apple-darwin',
  'x86_64-unknown-linux-gnu',
  'aarch64-unknown-linux-gnu',
]);
const VALID_ARCHIVES = new Set(['raw', 'zip']);

function fail(message) {
  console.error(`engine-manifest: ${message}`);
  process.exit(1);
}

// Minimal --flag value / repeated --artifact parser.
function parseArgs(argv) {
  const opts = { artifact: [] };
  for (let i = 0; i < argv.length; i += 1) {
    const key = argv[i];
    if (!key.startsWith('--')) fail(`unexpected argument: ${key}`);
    const value = argv[i + 1];
    if (value === undefined || value.startsWith('--')) fail(`missing value for ${key}`);
    i += 1;
    const name = key.slice(2);
    if (name === 'artifact') opts.artifact.push(value);
    else opts[name] = value;
  }
  return opts;
}

// "target=..,file=..,executable=..,archive=.." -> object
function parseArtifactSpec(spec) {
  const fields = {};
  for (const pair of spec.split(',')) {
    const idx = pair.indexOf('=');
    if (idx === -1) fail(`malformed artifact field (expected key=value): ${pair}`);
    fields[pair.slice(0, idx)] = pair.slice(idx + 1);
  }
  for (const required of ['target', 'file', 'executable', 'archive']) {
    if (!fields[required]) fail(`artifact spec missing ${required}: ${spec}`);
  }
  if (!VALID_TARGETS.has(fields.target)) fail(`unknown target '${fields.target}'`);
  if (!VALID_ARCHIVES.has(fields.archive)) fail(`archive must be raw|zip, got '${fields.archive}'`);
  return fields;
}

function sha256(path) {
  return createHash('sha256').update(readFileSync(path)).digest('hex');
}

const opts = parseArgs(process.argv.slice(2));
for (const required of ['version', 'base-url', 'out']) {
  if (!opts[required]) fail(`missing required --${required}`);
}
if (opts.artifact.length === 0) fail('at least one --artifact is required');

const baseUrl = opts['base-url'].replace(/\/+$/, '');
const seenTargets = new Set();

const artifacts = opts.artifact.map((spec) => {
  const a = parseArtifactSpec(spec);
  if (seenTargets.has(a.target)) fail(`duplicate target '${a.target}'`);
  seenTargets.add(a.target);
  return {
    target: a.target,
    url: `${baseUrl}/${basename(a.file)}`,
    sha256: sha256(a.file),
    archive: a.archive,
    executable: a.executable,
  };
});

const manifest = { schema_version: 1, version: opts.version };
if (opts['min-engine-version']) manifest.minEngineVersion = opts['min-engine-version'];
manifest.artifacts = artifacts;

writeFileSync(opts.out, `${JSON.stringify(manifest, null, 2)}\n`);
console.log(`Wrote ${opts.out} (${artifacts.length} artifact(s), version ${opts.version}):`);
console.log(JSON.stringify(manifest, null, 2));
