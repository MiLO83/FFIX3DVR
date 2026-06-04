#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const EOF_OFFSET = 0x454f46;

function argValue(name, fallback) {
  const index = process.argv.indexOf(name);
  if (index === -1 || index + 1 >= process.argv.length) return fallback;
  return process.argv[index + 1];
}

function sha256(bytes) {
  return crypto.createHash("sha256").update(bytes).digest("hex").toUpperCase();
}

function writeU24BE(value) {
  if (value < 0 || value > 0xffffff) {
    throw new Error(`IPS offset is out of range: ${value}`);
  }
  return Buffer.from([(value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff]);
}

function writeU16BE(value) {
  if (value < 0 || value > 0xffff) {
    throw new Error(`IPS length is out of range: ${value}`);
  }
  return Buffer.from([(value >>> 8) & 0xff, value & 0xff]);
}

function addRecord(parts, offset, data) {
  if (data.length === 0) return;

  // Standard IPS reserves offset 0x454F46 as the EOF marker. If a chunk would
  // start there, overlap the previous byte and write that target byte again.
  if (offset === EOF_OFFSET) {
    if (offset === 0) throw new Error("Cannot move EOF-offset record backward");
    offset -= 1;
    data = Buffer.concat([target.subarray(offset, offset + 1), data]);
  }

  let cursor = 0;
  while (cursor < data.length) {
    let chunkOffset = offset + cursor;
    let chunkLength = Math.min(0xffff, data.length - cursor);
    if (chunkOffset === EOF_OFFSET) {
      if (chunkOffset === 0) throw new Error("Cannot move split EOF-offset record backward");
      chunkOffset -= 1;
      cursor -= 1;
      chunkLength = Math.min(0xffff, data.length - cursor);
    }
    parts.push(writeU24BE(chunkOffset));
    parts.push(writeU16BE(chunkLength));
    parts.push(data.subarray(cursor, cursor + chunkLength));
    cursor += chunkLength;
  }
}

function makeIps(base, target, gapJoinBytes) {
  if (target.length > 0xffffff) {
    throw new Error(`Target DLL is too large for IPS: ${target.length} bytes`);
  }

  const parts = [Buffer.from("PATCH", "ascii")];
  let recordCount = 0;
  let dataBytes = 0;
  let i = 0;

  while (i < target.length) {
    const baseByte = i < base.length ? base[i] : -1;
    if (baseByte === target[i]) {
      i += 1;
      continue;
    }

    const start = i;
    let lastDiff = i;
    i += 1;
    while (i < target.length) {
      const nextBaseByte = i < base.length ? base[i] : -1;
      if (nextBaseByte !== target[i]) {
        lastDiff = i;
        i += 1;
      } else if (i - lastDiff <= gapJoinBytes) {
        i += 1;
      } else {
        break;
      }
    }

    const data = target.subarray(start, lastDiff + 1);
    const before = parts.length;
    addRecord(parts, start, data);
    recordCount += parts.length - before > 0 ? Math.ceil(data.length / 0xffff) : 0;
    dataBytes += data.length;
  }

  parts.push(Buffer.from("EOF", "ascii"));
  return { bytes: Buffer.concat(parts), recordCount, dataBytes };
}

const basePath = path.resolve(repoRoot, argValue("--base", ".memoria-src/Memoria.Patcher/StreamingAssets/Scripts/Project/References/Assembly-CSharp.dll"));
const targetPath = path.resolve(repoRoot, argValue("--target", ".memoria-src/Output/Assembly-CSharp.dll"));
const outDir = path.resolve(repoRoot, argValue("--out", "runtime-patch"));
const label = argValue("--label", "Memoria reference");
const patchName = argValue("--patch-name", "Assembly-CSharp.memoria-reference-to-ff9depthvr.ips");
const version = argValue("--version", "0.2.0-open-beta.5");
const gapJoinBytes = Number.parseInt(argValue("--gap", "8"), 10);

const base = fs.readFileSync(basePath);
const target = fs.readFileSync(targetPath);
const patch = makeIps(base, target, gapJoinBytes);

fs.mkdirSync(outDir, { recursive: true });
const patchPath = path.join(outDir, patchName);
fs.writeFileSync(patchPath, patch.bytes);

const metadataPath = path.join(outDir, "patches.json");
let metadata = {
  format: "FF9DepthVRRuntimePatchV1",
  version,
  dllRelativePath: "x64/FF9_Data/Managed/Assembly-CSharp.dll",
  target: {
    sha256: sha256(target),
    length: target.length,
  },
  patches: [],
};

if (fs.existsSync(metadataPath)) {
  metadata = JSON.parse(fs.readFileSync(metadataPath, "utf8"));
  metadata.version = version;
  metadata.target = { sha256: sha256(target), length: target.length };
}

const baseHash = sha256(base);
metadata.patches = metadata.patches.filter((entry) => entry.baseSha256 !== baseHash);
metadata.patches.push({
  label,
  baseSha256: baseHash,
  baseLength: base.length,
  ipsFile: patchName,
  ipsSha256: sha256(patch.bytes),
  ipsLength: patch.bytes.length,
  targetSha256: sha256(target),
  targetLength: target.length,
  gapJoinBytes,
  records: patch.recordCount,
  encodedDataBytes: patch.dataBytes,
  createdUtc: new Date().toISOString(),
});
metadata.patches.sort((a, b) => a.label.localeCompare(b.label));
fs.writeFileSync(metadataPath, `${JSON.stringify(metadata, null, 2)}\n`);

console.log(`Created ${path.relative(repoRoot, patchPath)} (${patch.bytes.length} bytes)`);
console.log(`Base ${baseHash} (${base.length} bytes)`);
console.log(`Target ${metadata.target.sha256} (${target.length} bytes)`);
