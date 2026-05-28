import type { PanoTuning, SceneAsset } from "./types";

type Basis = {
  right: [number, number, number];
  up: [number, number, number];
  forward: [number, number, number];
  tanH: number;
  tanV: number;
};

type ChannelStats = {
  count: number;
  r: number;
  g: number;
  b: number;
  luma: number;
  saturation: number;
};

export const DEFAULT_PANO_TUNING: PanoTuning = {
  contrast: 1,
  brightness: 1,
  saturation: 1,
  match: 0,
};

function radians(degrees: number) {
  return (degrees * Math.PI) / 180;
}

function dot(a: [number, number, number], b: [number, number, number]) {
  return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}

export function projectionBasis(asset: SceneAsset): Basis | null {
  const projection = asset.sourceProjection;
  if (!projection) return null;

  const yaw = radians(projection.yawDeg);
  const pitch = radians(projection.pitchDeg);
  const forward: [number, number, number] = [
    Math.cos(pitch) * Math.sin(yaw),
    Math.sin(pitch),
    Math.cos(pitch) * Math.cos(yaw),
  ];
  const right: [number, number, number] = [Math.cos(yaw), 0, -Math.sin(yaw)];
  const up: [number, number, number] = [
    forward[1] * right[2] - forward[2] * right[1],
    forward[2] * right[0] - forward[0] * right[2],
    forward[0] * right[1] - forward[1] * right[0],
  ];

  return {
    right,
    up,
    forward,
    tanH: Math.tan(radians(projection.hFovDeg) / 2),
    tanV: Math.tan(radians(projection.vFovDeg) / 2),
  };
}

function directionFromErp(x: number, y: number, width: number, height: number): [number, number, number] {
  const yaw = ((x + 0.5) / width - 0.5) * 2 * Math.PI;
  const pitch = (0.5 - (y + 0.5) / height) * Math.PI;
  const cp = Math.cos(pitch);
  return [cp * Math.sin(yaw), Math.sin(pitch), cp * Math.cos(yaw)];
}

function inSourceProjection(direction: [number, number, number], basis: Basis | null) {
  if (!basis) return false;
  const z = dot(direction, basis.forward);
  if (z <= 0) return false;
  const ndcX = dot(direction, basis.right) / (z * basis.tanH);
  const ndcY = dot(direction, basis.up) / (z * basis.tanV);
  return ndcX >= -1 && ndcX <= 1 && ndcY >= -1 && ndcY <= 1;
}

function clampByte(value: number) {
  return Math.max(0, Math.min(255, Math.round(value)));
}

function clamp(value: number, min: number, max: number) {
  return Math.max(min, Math.min(max, value));
}

function normalizeTuning(tuning: PanoTuning | number): PanoTuning {
  if (typeof tuning === "number") return { ...DEFAULT_PANO_TUNING, contrast: tuning };
  return {
    contrast: Number.isFinite(tuning.contrast) ? tuning.contrast : DEFAULT_PANO_TUNING.contrast,
    brightness: Number.isFinite(tuning.brightness) ? tuning.brightness : DEFAULT_PANO_TUNING.brightness,
    saturation: Number.isFinite(tuning.saturation) ? tuning.saturation : DEFAULT_PANO_TUNING.saturation,
    match: Number.isFinite(tuning.match) ? tuning.match : DEFAULT_PANO_TUNING.match,
  };
}

function emptyStats(): ChannelStats {
  return { count: 0, r: 0, g: 0, b: 0, luma: 0, saturation: 0 };
}

function addStats(stats: ChannelStats, r: number, g: number, b: number) {
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  stats.count += 1;
  stats.r += r;
  stats.g += g;
  stats.b += b;
  stats.luma += 0.2126 * r + 0.7152 * g + 0.0722 * b;
  stats.saturation += max - min;
}

function finalizeStats(stats: ChannelStats) {
  const count = Math.max(1, stats.count);
  return {
    r: stats.r / count,
    g: stats.g / count,
    b: stats.b / count,
    luma: stats.luma / count,
    saturation: stats.saturation / count,
  };
}

function loadImage(src: string) {
  return new Promise<HTMLImageElement>((resolve, reject) => {
    const image = new Image();
    image.crossOrigin = "anonymous";
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error(`Unable to load ${src}`));
    image.src = src;
  });
}

export async function renderTunedPano(asset: SceneAsset, tuningInput: PanoTuning | number) {
  const tuning = normalizeTuning(tuningInput);
  const image = await loadImage(asset.erp360.url);
  const canvas = document.createElement("canvas");
  canvas.width = image.naturalWidth;
  canvas.height = image.naturalHeight;

  const context = canvas.getContext("2d", { willReadFrequently: true });
  if (!context) throw new Error("Canvas rendering is unavailable");

  context.drawImage(image, 0, 0);
  const frame = context.getImageData(0, 0, canvas.width, canvas.height);
  const pixels = frame.data;
  const basis = projectionBasis(asset);
  const sourceStats = emptyStats();
  const generatedStats = emptyStats();
  const sourceMask = new Uint8Array(canvas.width * canvas.height);

  for (let y = 0; y < canvas.height; y += 1) {
    for (let x = 0; x < canvas.width; x += 1) {
      const direction = directionFromErp(x, y, canvas.width, canvas.height);
      const offset = (y * canvas.width + x) * 4;
      const insideSource = inSourceProjection(direction, basis);
      sourceMask[y * canvas.width + x] = insideSource ? 1 : 0;
      addStats(insideSource ? sourceStats : generatedStats, pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }
  }

  const source = finalizeStats(sourceStats);
  const generated = finalizeStats(generatedStats);
  const match = clamp(tuning.match, 0, 1);
  const redMatch = 1 + (clamp(source.r / Math.max(1, generated.r), 0.7, 1.35) - 1) * match;
  const greenMatch = 1 + (clamp(source.g / Math.max(1, generated.g), 0.7, 1.35) - 1) * match;
  const blueMatch = 1 + (clamp(source.b / Math.max(1, generated.b), 0.7, 1.35) - 1) * match;
  const lumaMatch = 1 + (clamp(source.luma / Math.max(1, generated.luma), 0.65, 1.45) - 1) * match;
  const saturationMatch = 1 + (clamp(source.saturation / Math.max(1, generated.saturation), 0.55, 1.65) - 1) * match;

  for (let y = 0; y < canvas.height; y += 1) {
    for (let x = 0; x < canvas.width; x += 1) {
      if (sourceMask[y * canvas.width + x]) continue;

      const offset = (y * canvas.width + x) * 4;
      let r = pixels[offset] * redMatch * lumaMatch * tuning.brightness;
      let g = pixels[offset + 1] * greenMatch * lumaMatch * tuning.brightness;
      let b = pixels[offset + 2] * blueMatch * lumaMatch * tuning.brightness;

      r = 128 + (r - 128) * tuning.contrast;
      g = 128 + (g - 128) * tuning.contrast;
      b = 128 + (b - 128) * tuning.contrast;

      const luma = 0.2126 * r + 0.7152 * g + 0.0722 * b;
      const saturation = tuning.saturation * saturationMatch;
      pixels[offset] = clampByte(luma + (r - luma) * saturation);
      pixels[offset + 1] = clampByte(luma + (g - luma) * saturation);
      pixels[offset + 2] = clampByte(luma + (b - luma) * saturation);
    }
  }

  context.putImageData(frame, 0, 0);
  return new Promise<Blob>((resolve, reject) => {
    canvas.toBlob((blob) => {
      if (blob) resolve(blob);
      else reject(new Error("Unable to encode tuned panorama"));
    }, "image/png");
  });
}
