#!/usr/bin/env node
// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

import { createHash } from "node:crypto";
import {
  existsSync,
  lstatSync,
  readFileSync,
  readlinkSync,
  readdirSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));
export const DEFAULT_RADE_SOURCE_SPEC = path.join(moduleDirectory, "SOURCE-SLICES.json");
export const RADE_BINDING_PATH = "native/radae/vendor/BINARY-SOURCE-BINDING.json";
const REQUIRED_SLICES = new Set(["radae_c", "opus_dnn", "freedv_text"]);
const REQUIRED_BINARY_RIDS = new Set(["linux-x64", "linux-arm64", "win-x64", "osx-arm64"]);

function normalizedPath(filePath) {
  return filePath.split(path.sep).join("/");
}

function sha256Bytes(bytes) {
  return createHash("sha256").update(bytes).digest("hex");
}

export function sha256File(filePath) {
  return sha256Bytes(readFileSync(filePath));
}

export function loadRadeSourceSpec(specPath = DEFAULT_RADE_SOURCE_SPEC) {
  const spec = JSON.parse(readFileSync(specPath, "utf8"));
  if (spec.schemaVersion !== 1) throw new Error("RADE source-slice spec schemaVersion must be 1");
  if (!/^[0-9a-f]{40}$/.test(spec.upstream?.commit ?? "")) {
    throw new Error("RADE source-slice spec has an invalid Thetis-RADE commit");
  }
  if (!/^[0-9a-f]{40}$/.test(spec.opusCommit ?? "")) {
    throw new Error("RADE source-slice spec has an invalid Opus commit");
  }
  if (!Array.isArray(spec.slices) || spec.slices.length === 0) {
    throw new Error("RADE source-slice spec must declare at least one slice");
  }
  if (!Array.isArray(spec.binaries) || spec.binaries.length === 0) {
    throw new Error("RADE source-slice spec must declare at least one binary");
  }
  if (
    typeof spec.upstream.repository !== "string"
    || !spec.upstream.repository.startsWith("https://")
    || typeof spec.upstream.sparsePath !== "string"
    || spec.upstream.sparsePath.length === 0
    || path.isAbsolute(spec.upstream.sparsePath)
    || normalizedPath(spec.upstream.sparsePath).split("/").includes("..")
  ) {
    throw new Error("RADE source-slice spec has an unsafe upstream repository or sparse path");
  }

  const sliceNames = new Set();
  for (const slice of spec.slices) {
    if (
      typeof slice.name !== "string"
      || !/^[A-Za-z0-9_-]+$/.test(slice.name)
      || typeof slice.upstreamPath !== "string"
      || !slice.upstreamPath.startsWith(`${spec.upstream.sparsePath}/`)
      || normalizedPath(slice.upstreamPath).split("/").includes("..")
      || !/^[0-9a-f]{40}$/.test(slice.gitTree ?? "")
      || !Number.isSafeInteger(slice.fileCount)
      || slice.fileCount <= 0
      || !/^[0-9a-f]{64}$/.test(slice.contentSha256 ?? "")
    ) {
      throw new Error("RADE source-slice spec contains an invalid slice record");
    }
    if (sliceNames.has(slice.name)) throw new Error(`RADE source-slice spec repeats ${slice.name}`);
    sliceNames.add(slice.name);
  }
  if (sliceNames.size !== REQUIRED_SLICES.size || [...REQUIRED_SLICES].some((name) => !sliceNames.has(name))) {
    throw new Error("RADE source-slice spec must bind exactly radae_c, opus_dnn, and freedv_text");
  }

  const binaryRids = new Set();
  for (const binary of spec.binaries) {
    if (
      typeof binary.rid !== "string"
      || !/^[A-Za-z0-9._-]+$/.test(binary.rid)
      || typeof binary.path !== "string"
      || binary.path.length === 0
      || path.isAbsolute(binary.path)
      || normalizedPath(binary.path).split("/").includes("..")
      || !/^[0-9a-f]{64}$/.test(binary.sha256 ?? "")
      || typeof binary.toolchain !== "string"
      || binary.toolchain.length === 0
    ) {
      throw new Error("RADE source-slice spec contains an invalid binary record");
    }
    if (binaryRids.has(binary.rid)) throw new Error(`RADE source-slice spec repeats RID ${binary.rid}`);
    binaryRids.add(binary.rid);
  }
  if (
    binaryRids.size !== REQUIRED_BINARY_RIDS.size
    || [...REQUIRED_BINARY_RIDS].some((rid) => !binaryRids.has(rid))
  ) {
    throw new Error("RADE source-slice spec must bind exactly linux-x64, linux-arm64, win-x64, and osx-arm64");
  }

  if (!Array.isArray(spec.buildInputs) || spec.buildInputs.length === 0) {
    throw new Error("RADE source-slice spec must declare build inputs");
  }
  for (const input of spec.buildInputs) {
    if (
      typeof input !== "string"
      || input.length === 0
      || path.isAbsolute(input)
      || normalizedPath(input).split("/").includes("..")
    ) {
      throw new Error("RADE source-slice spec contains an unsafe build input path");
    }
  }
  if (!Array.isArray(spec.cmakeArguments) || spec.cmakeArguments.length === 0) {
    throw new Error("RADE source-slice spec must declare the CMake configuration");
  }
  return spec;
}

function walkFiles(root) {
  const files = [];
  const visit = (directory) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const absolutePath = path.join(directory, entry.name);
      if (entry.isDirectory()) {
        visit(absolutePath);
      } else if (entry.isFile() || entry.isSymbolicLink()) {
        files.push(normalizedPath(path.relative(root, absolutePath)));
      } else {
        throw new Error(`${absolutePath}: unsupported source entry type`);
      }
    }
  };
  visit(root);
  return files.sort();
}

// Deterministic across macOS and Linux: hash each sorted relative path, entry
// kind, bytes (or symlink target), and NUL delimiters. Git tree IDs separately
// bind modes and tree structure at the pinned upstream commit.
export function digestSourceSlice(sliceRoot) {
  const files = walkFiles(sliceRoot);
  const digest = createHash("sha256");
  for (const relativePath of files) {
    const absolutePath = path.join(sliceRoot, ...relativePath.split("/"));
    const stat = lstatSync(absolutePath);
    digest.update(relativePath);
    digest.update("\0");
    if (stat.isSymbolicLink()) {
      digest.update("symlink\0");
      digest.update(readlinkSync(absolutePath));
    } else {
      digest.update("file\0");
      digest.update(readFileSync(absolutePath));
    }
    digest.update("\0");
  }
  return { fileCount: files.length, contentSha256: digest.digest("hex") };
}

export function auditMaterializedRadeSlices(vendorRoot, { spec = loadRadeSourceSpec() } = {}) {
  const violations = [];
  for (const slice of spec.slices) {
    const sliceRoot = path.join(vendorRoot, slice.name);
    if (!existsSync(sliceRoot) || !lstatSync(sliceRoot).isDirectory()) {
      violations.push(`native/radae/vendor/${slice.name}: exact pinned source slice is missing`);
      continue;
    }
    let actual;
    try {
      actual = digestSourceSlice(sliceRoot);
    } catch (error) {
      violations.push(`native/radae/vendor/${slice.name}: could not hash source slice: ${error.message}`);
      continue;
    }
    if (actual.fileCount !== slice.fileCount) {
      violations.push(
        `native/radae/vendor/${slice.name}: file count ${actual.fileCount} does not match pinned count ${slice.fileCount}`,
      );
    }
    if (actual.contentSha256 !== slice.contentSha256) {
      violations.push(
        `native/radae/vendor/${slice.name}: content SHA-256 ${actual.contentSha256} does not match pin ${slice.contentSha256}`,
      );
    }
  }

  const opusPinPath = path.join(vendorRoot, "opus_dnn", "commit_pin.txt");
  if (existsSync(opusPinPath) && lstatSync(opusPinPath).isFile()) {
    const opusPin = readFileSync(opusPinPath, "utf8");
    if (!opusPin.includes(spec.opusCommit)) {
      violations.push(`native/radae/vendor/opus_dnn/commit_pin.txt does not record Opus ${spec.opusCommit}`);
    }
  }
  return violations;
}

function expectedHashedFiles(sourceRoot, entries, label, violations) {
  const result = [];
  for (const entry of entries) {
    const relativePath = typeof entry === "string" ? entry : entry.path;
    const absolutePath = path.join(sourceRoot, ...relativePath.split("/"));
    if (!existsSync(absolutePath) || !lstatSync(absolutePath).isFile()) {
      violations.push(`${relativePath}: required RADE ${label} is missing`);
      continue;
    }
    const sha256 = sha256File(absolutePath);
    if (typeof entry !== "string" && sha256 !== entry.sha256) {
      violations.push(`${relativePath}: SHA-256 ${sha256} does not match recorded ${entry.sha256}`);
    }
    result.push(typeof entry === "string" ? { path: relativePath, sha256 } : { ...entry, sha256 });
  }
  return result;
}

export function createRadeBinding(sourceRoot, { spec = loadRadeSourceSpec() } = {}) {
  const violations = [];
  const binaries = expectedHashedFiles(sourceRoot, spec.binaries, "runtime binary", violations);
  const buildInputs = expectedHashedFiles(sourceRoot, spec.buildInputs, "build input", violations);
  violations.push(...auditMaterializedRadeSlices(path.join(sourceRoot, "native/radae/vendor"), { spec }));
  if (violations.length > 0) throw new Error(violations.join("\n"));

  return {
    schemaVersion: 1,
    upstream: { ...spec.upstream },
    opusCommit: spec.opusCommit,
    sourceSlices: spec.slices.map((slice) => ({
      name: slice.name,
      path: `native/radae/vendor/${slice.name}`,
      upstreamPath: slice.upstreamPath,
      gitTree: slice.gitTree,
      fileCount: slice.fileCount,
      contentSha256: slice.contentSha256,
    })),
    binaries,
    buildInputs,
    buildConfiguration: {
      cmakeArguments: [...spec.cmakeArguments],
      localChanges: "Zeus CMake composition and shim are identified by buildInputs; fetched upstream slices are unmodified.",
    },
  };
}

export function auditRadeRepository(sourceRoot, { spec = loadRadeSourceSpec() } = {}) {
  const violations = [];
  expectedHashedFiles(sourceRoot, spec.binaries, "runtime binary", violations);
  expectedHashedFiles(sourceRoot, spec.buildInputs, "build input", violations);
  return violations;
}

export function auditStagedRadeCompliance(sourceRoot, { spec = loadRadeSourceSpec() } = {}) {
  const violations = auditRadeRepository(sourceRoot, { spec });
  violations.push(...auditMaterializedRadeSlices(path.join(sourceRoot, "native/radae/vendor"), { spec }));
  const bindingPath = path.join(sourceRoot, ...RADE_BINDING_PATH.split("/"));
  if (!existsSync(bindingPath) || !lstatSync(bindingPath).isFile()) {
    violations.push(`${RADE_BINDING_PATH}: binary/source binding record is missing`);
    return violations;
  }
  let actualBinding;
  let expectedBinding;
  try {
    actualBinding = JSON.parse(readFileSync(bindingPath, "utf8"));
  } catch (error) {
    violations.push(`${RADE_BINDING_PATH}: binding record is invalid JSON: ${error.message}`);
    return violations;
  }
  try {
    expectedBinding = createRadeBinding(sourceRoot, { spec });
  } catch (error) {
    violations.push(...error.message.split("\n"));
    return violations;
  }
  if (JSON.stringify(actualBinding) !== JSON.stringify(expectedBinding)) {
    violations.push(`${RADE_BINDING_PATH}: binding record does not match the staged binaries, slices, pin, shim, and CMake inputs`);
  }
  return violations;
}

function reportViolations(violations) {
  if (violations.length === 0) return;
  for (const violation of violations) console.error(`RADE source compliance: ${violation}`);
  process.exitCode = 1;
}

function usage() {
  console.error("usage: source-slices.mjs fetch-config|slice-paths|verify <vendor-dir>|write-binding <source-root> <output.json>");
}

function isMainModule() {
  return process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
}

if (isMainModule()) {
  const [command, ...args] = process.argv.slice(2);
  const spec = loadRadeSourceSpec();
  switch (command) {
    case "fetch-config":
      if (args.length !== 0) { usage(); process.exit(64); }
      process.stdout.write(`${spec.upstream.repository}\t${spec.upstream.commit}\t${spec.upstream.sparsePath}\n`);
      break;
    case "slice-paths":
      if (args.length !== 0) { usage(); process.exit(64); }
      process.stdout.write(spec.slices.map(({ name, upstreamPath, gitTree }) => `${name}\t${upstreamPath}\t${gitTree}`).join("\n") + "\n");
      break;
    case "verify":
      if (args.length !== 1) { usage(); process.exit(64); }
      reportViolations(auditMaterializedRadeSlices(path.resolve(args[0]), { spec }));
      break;
    case "write-binding": {
      if (args.length !== 2) { usage(); process.exit(64); }
      try {
        const binding = createRadeBinding(path.resolve(args[0]), { spec });
        writeFileSync(path.resolve(args[1]), `${JSON.stringify(binding, null, 2)}\n`);
      } catch (error) {
        reportViolations(error.message.split("\n"));
      }
      break;
    }
    default:
      usage();
      process.exit(64);
  }
}
