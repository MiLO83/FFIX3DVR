import { copyFile, mkdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const viewerRoot = path.resolve(__dirname, "..");
const repoRoot = path.resolve(viewerRoot, "..");
const queuePath = path.join(repoRoot, "artifacts", "all-fields", "erp_queue.json");
const publicAssetsRoot = path.join(viewerRoot, "public", "assets");
const mapsRoot = path.join(publicAssetsRoot, "maps");
const originalsRoot = path.join(publicAssetsRoot, "originals");

async function readJsonIfExists(filePath, fallback) {
  try {
    return JSON.parse(await readFile(filePath, "utf8"));
  } catch {
    return fallback;
  }
}

const queue = JSON.parse(await readFile(queuePath, "utf8"));
const corrections = await readJsonIfExists(path.join(repoRoot, "artifacts", "all-fields", "color_corrections.json"), {});
const completeJobs = queue.jobs.filter((job) => job.status === "complete");

await rm(mapsRoot, { recursive: true, force: true });
await rm(originalsRoot, { recursive: true, force: true });
await mkdir(mapsRoot, { recursive: true });
await mkdir(originalsRoot, { recursive: true });

const assets = [];
const generatedIds = new Set(completeJobs.map((job) => job.mapName.toLowerCase()));
const originalBackgrounds = [];

for (const job of queue.jobs) {
  const id = job.mapName.toLowerCase();
  const sourcePlate = path.join(repoRoot, job.sourcePlate);
  let sourceStat;
  try {
    sourceStat = await stat(sourcePlate);
  } catch {
    continue;
  }

  const outDir = path.join(originalsRoot, id);
  await mkdir(outDir, { recursive: true });
  await copyFile(sourcePlate, path.join(outDir, "source_plate.png"));
  const sourceDir = path.dirname(sourcePlate);
  const walkmeshPath = path.join(sourceDir, "walkmesh.json");
  const camerasPath = path.join(sourceDir, "cameras.json");
  const walkmesh = await readJsonIfExists(walkmeshPath, null);
  const cameras = await readJsonIfExists(camerasPath, null);
  if (walkmesh?.ok) await copyFile(walkmeshPath, path.join(outDir, "walkmesh.json"));
  if (cameras?.cameras?.length) await copyFile(camerasPath, path.join(outDir, "cameras.json"));
  originalBackgrounds.push({
    id,
    index: job.index,
    mapName: job.mapName,
    bundle: job.bundle,
    status: job.status,
    thumbnail: `/assets/originals/${id}/source_plate.png`,
    sourcePlate: {
      url: `/assets/originals/${id}/source_plate.png`,
      bytes: sourceStat.size,
    },
    walkmesh: walkmesh?.ok
      ? {
          url: `/assets/originals/${id}/walkmesh.json`,
          triangles: Number(walkmesh.counts?.triangles ?? walkmesh.triangles?.length ?? 0),
          vertices: Number(walkmesh.counts?.vertices ?? walkmesh.vertices?.length ?? 0),
        }
      : undefined,
    cameraMetadata: cameras?.cameras?.length
      ? {
          url: `/assets/originals/${id}/cameras.json`,
        }
      : undefined,
    generatedAssetId: generatedIds.has(id) ? id : undefined,
  });
}

for (const job of completeJobs) {
  const id = job.mapName.toLowerCase();
  const outDir = path.join(mapsRoot, id);
  await mkdir(outDir, { recursive: true });

  const sourcePlate = path.join(repoRoot, job.sourcePlate);
  const erpRel =
    job.runtimeAssets?.lowPower?.path ||
    job.ffixInfill?.colorMatchedErpOutput ||
    job.colorHarmonized?.output ||
    job.actualErpOutput ||
    job.erpOutput;
  const erp = path.join(repoRoot, erpRel);
  const splat = path.join(repoRoot, job.splatOutput);
  const triposplatRel = job.runtimeAssets?.highPowerTripoSplat?.path || job.triposplat?.ply;
  const triposplatPreparedRel = job.triposplat?.prepared;
  const triposplat = triposplatRel ? path.join(repoRoot, triposplatRel) : "";
  const triposplatPrepared = triposplatPreparedRel ? path.join(repoRoot, triposplatPreparedRel) : "";

  await copyFile(sourcePlate, path.join(outDir, "source_plate.png"));
  await copyFile(erp, path.join(outDir, "field_erp.png"));
  await copyFile(splat, path.join(outDir, "field_spherical_env.ply"));
  let triposplatStat = null;
  let triposplatPreparedUrl = undefined;
  if (triposplat) {
    try {
      await copyFile(triposplat, path.join(outDir, "field_triposplat.ply"));
      triposplatStat = await stat(triposplat);
      if (triposplatPrepared) {
        await copyFile(triposplatPrepared, path.join(outDir, "field_triposplat_preprocessed.webp"));
        triposplatPreparedUrl = `/assets/maps/${id}/field_triposplat_preprocessed.webp`;
      }
    } catch {
      triposplatStat = null;
      triposplatPreparedUrl = undefined;
    }
  }

  const erpStat = await stat(erp);
  const splatStat = await stat(splat);
  const sourceStat = await stat(sourcePlate);
  const correction = corrections[id];
  if (correction?.artifactPath) {
    try {
      await copyFile(path.join(repoRoot, correction.artifactPath), path.join(outDir, "field_erp_tuned.png"));
    } catch {
      // A stale correction entry should not block publishing the base map.
    }
  }
  const overlay = job.exactSourcePlateOverlay;
  const sticker = job.sticker ?? {};
  const hFovDeg = Number(overlay?.hFOV_deg ?? sticker.hFOV_deg ?? 104);
  const vFovDeg = Number(overlay?.vFOV_deg ?? 80.94788284659688);

  assets.push({
    id,
    index: job.index,
    mapName: job.mapName,
    bundle: job.bundle,
    status: job.status,
    thumbnail: `/assets/maps/${id}/source_plate.png`,
    erp360: {
      url: `/assets/maps/${id}/field_erp.png`,
      bytes: erpStat.size,
      projection: "equirectangular",
    },
    splat: {
      url: `/assets/maps/${id}/field_spherical_env.ply`,
      bytes: splatStat.size,
      type: "gaussian_splat_ply",
      variant: "spherical_env",
      vertices: job.runtimeAssets?.highPower?.vertices ?? 131072,
    },
    triposplat: triposplatStat
      ? {
          url: `/assets/maps/${id}/field_triposplat.ply`,
          bytes: triposplatStat.size,
          type: "gaussian_splat_ply",
          variant: job.runtimeAssets?.highPowerTripoSplat?.variant ?? "triposplat_scene_plate",
          vertices: job.runtimeAssets?.highPowerTripoSplat?.vertices ?? job.triposplat?.numGaussians ?? 131072,
          preparedUrl: triposplatPreparedUrl,
        }
      : undefined,
    sourcePlate: {
      url: `/assets/maps/${id}/source_plate.png`,
      bytes: sourceStat.size,
    },
    sourceProjection: {
      yawDeg: Number(overlay?.yaw_deg ?? sticker.yaw_deg ?? 0),
      pitchDeg: Number(overlay?.pitch_deg ?? sticker.pitch_deg ?? -4),
      hFovDeg,
      vFovDeg,
    },
    colorCorrection: correction
      ? {
          contrast: correction.contrast,
          brightness: correction.brightness,
          saturation: correction.saturation,
          match: correction.match,
          url: `/assets/maps/${id}/field_erp_tuned.png`,
          savedAt: correction.savedAt,
        }
      : undefined,
  });
}

const manifest = {
  generatedAt: new Date().toISOString(),
  sourceQueue: "artifacts/all-fields/erp_queue.json",
  totalGenerated: assets.length,
  totalKnownMaps: queue.totalJobs,
  assets,
  originalBackgrounds,
};

await writeFile(path.join(publicAssetsRoot, "index.json"), `${JSON.stringify(manifest, null, 2)}\n`);
console.log(
  `Published ${assets.length} complete maps and ${originalBackgrounds.length} original backgrounds to ${path.relative(
    repoRoot,
    publicAssetsRoot,
  )}`,
);
