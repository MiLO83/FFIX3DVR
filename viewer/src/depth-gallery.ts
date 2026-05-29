import * as THREE from "three";
import { isImmersiveVrSupported, startImmersiveVr } from "./webxr";
import type { AssetManifest, OriginalBackground } from "./types";

type GalleryBackground = OriginalBackground & {
  depthUrl: string;
  hasGeneratedDepth: boolean;
};

type SceneState = {
  renderer: THREE.WebGLRenderer;
  scene: THREE.Scene;
  camera: THREE.PerspectiveCamera;
  mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial> | null;
};

type WalkmeshData = {
  ok?: boolean;
  charPos?: number[];
  activeFloor?: number;
  counts?: { triangles?: number; vertices?: number };
  triangles?: Array<{
    triIndex?: number;
    floorIndex?: number;
    worldVertices?: number[][];
  }>;
};

type CameraData = {
  cameras?: Array<{
    index: number;
    proj: number;
    r: number[][];
    t: number[];
    center_offset?: number[];
    w: number;
    h: number;
  }>;
  activeOverlayCameras?: number[];
};

type WalkmeshMode = "depth";
type ViewMode = "depth3d" | "flat2d";
type WalkmeshFloorMode = "active" | "all" | number;
type DepthSurfaceMode = "ground" | "nearest" | "farthest";

type ProjectedWalkPoint = {
  uv: THREE.Vector2;
  cameraDepth: number;
};

type WalkmeshPositionMap = {
  width: number;
  height: number;
  data: Float32Array;
  texture: THREE.DataTexture;
};

const BASE_CAMERA_Z = 8.2;
const PLATE_VIEWPORT_FILL = 0.9;
const LOCKED_WIGGLE = 1.2;
const LOCKED_DEPTH = 2.5;
const LOCKED_DOF = 1;
const TILT_MULTIPLIER = 2;
const VIEW_ANGLE_MULTIPLIER = 1;

const root = document.querySelector<HTMLDivElement>("#depth-gallery");
if (!root) throw new Error("Depth gallery root is missing");

root.innerHTML = `
  <main class="depth-page">
    <section class="stage-shell">
      <canvas class="depth-canvas"></canvas>
      <div class="topbar">
        <div>
          <p>FFIX Depth Gallery</p>
          <h1 id="scene-title">Loading backgrounds</h1>
        </div>
        <div class="top-actions">
          <button id="prev-btn" type="button" title="Previous background">Prev</button>
          <button id="next-btn" type="button" title="Next background">Next</button>
          <button id="xr-btn" type="button" title="Enter VR">VR</button>
        </div>
      </div>
      <div class="focus-dot" id="focus-dot"></div>
      <aside class="control-panel">
        <div class="control-buttons">
          <label class="readout-row">
            <span>Mouse Z</span>
            <output id="focus-value">0.50</output>
          </label>
          <button id="reset-btn" type="button">Reset View</button>
          <button id="view-mode-btn" type="button">View 2D</button>
          <button id="floor-mode-btn" type="button">Floor Active</button>
          <button id="depth-surface-btn" type="button">Cast Ground</button>
          <button id="walkmesh-visible-btn" type="button" aria-pressed="false">Show Walkmesh</button>
        </div>
        <div class="status-row">
          <small id="depth-status">Depth: checking</small>
          <small id="walkmesh-status">Walkmesh: hidden</small>
        </div>
      </aside>
    </section>
    <nav class="thumb-rail" id="thumb-rail" aria-label="Final Fantasy IX backgrounds"></nav>
  </main>
`;

const style = document.createElement("style");
style.textContent = `
  :root {
    color-scheme: dark;
    font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    background: #070809;
    color: #f6efe4;
  }

  * { box-sizing: border-box; }
  html, body, #depth-gallery { width: 100%; height: 100%; margin: 0; overflow: hidden; }
  button, input { font: inherit; }
  button { color: inherit; }

  .depth-page {
    display: grid;
    grid-template-rows: minmax(0, 1fr) 112px;
    width: 100vw;
    height: 100vh;
    background: #050506;
  }

  .stage-shell {
    position: relative;
    min-height: 0;
    overflow: hidden;
    background:
      radial-gradient(circle at 62% 35%, rgba(189, 118, 70, 0.16), transparent 28%),
      radial-gradient(circle at 28% 70%, rgba(38, 93, 97, 0.16), transparent 32%),
      #050506;
  }

  .depth-canvas {
    display: block;
    width: 100%;
    height: 100%;
  }

  .topbar {
    position: absolute;
    z-index: 4;
    top: 0;
    left: 0;
    right: 0;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 18px;
    padding: 18px 20px;
    background: linear-gradient(180deg, rgba(0, 0, 0, 0.78), transparent);
    pointer-events: none;
  }

  .topbar p {
    margin: 0 0 4px;
    color: #d8b56d;
    font-size: 0.72rem;
    font-weight: 900;
    letter-spacing: 0;
    text-transform: uppercase;
  }

  .topbar h1 {
    max-width: 72vw;
    margin: 0;
    overflow-wrap: anywhere;
    font-size: clamp(1.1rem, 2.8vw, 2rem);
    line-height: 1.05;
  }

  .top-actions {
    display: flex;
    gap: 8px;
    pointer-events: auto;
  }

  .top-actions button,
  .control-panel button {
    min-height: 40px;
    border: 1px solid rgba(255, 255, 255, 0.14);
    border-radius: 8px;
    background: rgba(255, 255, 255, 0.08);
    padding: 0 13px;
    cursor: pointer;
  }

  .top-actions button:hover,
  .control-panel button:hover {
    border-color: rgba(216, 181, 109, 0.58);
    background: rgba(216, 181, 109, 0.16);
  }

  .control-panel button.active {
    border-color: rgba(68, 215, 255, 0.72);
    background: rgba(68, 215, 255, 0.16);
  }

  .top-actions button:disabled {
    color: #6e7774;
    cursor: not-allowed;
  }

  .control-panel {
    position: absolute;
    z-index: 4;
    left: 50%;
    bottom: 16px;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    width: min(980px, calc(100vw - 32px));
    gap: 8px;
    border: 1px solid rgba(255, 255, 255, 0.1);
    border-radius: 8px;
    background: rgba(7, 8, 9, 0.78);
    padding: 10px;
    transform: translateX(-50%);
    backdrop-filter: blur(14px);
  }

  .control-buttons,
  .status-row {
    display: flex;
    flex-wrap: nowrap;
    align-items: center;
    justify-content: center;
    gap: 8px;
    width: 100%;
  }

  .control-buttons > * {
    flex: 0 0 auto;
  }

  .control-panel button {
    white-space: nowrap;
  }

  .status-row {
    gap: 14px;
  }

  .control-panel label {
    display: grid;
    grid-template-columns: minmax(0, 1fr) 52px;
    gap: 9px;
    align-items: center;
    min-width: 132px;
  }

  .control-panel span,
  .control-panel output,
  .control-panel small {
    color: #dce4df;
    font-size: 0.78rem;
    font-weight: 850;
  }

  .control-panel output {
    text-align: right;
  }

  .focus-dot {
    position: absolute;
    z-index: 3;
    width: 18px;
    height: 18px;
    border: 2px solid rgba(216, 181, 109, 0.86);
    border-radius: 50%;
    box-shadow: 0 0 22px rgba(216, 181, 109, 0.36);
    pointer-events: none;
    transform: translate(-50%, -50%);
  }

  .thumb-rail {
    display: flex;
    gap: 10px;
    overflow-x: auto;
    overflow-y: hidden;
    border-top: 1px solid rgba(255, 255, 255, 0.1);
    background: rgba(8, 9, 11, 0.96);
    padding: 12px;
  }

  .thumb {
    display: grid;
    flex: 0 0 152px;
    grid-template-rows: 68px auto;
    gap: 7px;
    border: 1px solid transparent;
    border-radius: 8px;
    background: rgba(255, 255, 255, 0.055);
    padding: 7px;
    text-align: left;
    cursor: pointer;
  }

  .thumb.active {
    border-color: rgba(216, 181, 109, 0.74);
    background: rgba(216, 181, 109, 0.14);
  }

  .thumb img {
    width: 100%;
    height: 68px;
    border-radius: 6px;
    object-fit: cover;
    background: #111;
  }

  .thumb strong {
    overflow: hidden;
    color: #f6efe4;
    font-size: 0.7rem;
    line-height: 1.2;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  @media (max-width: 780px) {
    .depth-page { grid-template-rows: minmax(0, 1fr) 96px; }
    .topbar { align-items: flex-start; flex-direction: column; }
    .control-panel { left: 10px; right: 10px; bottom: 10px; width: auto; transform: none; }
    .control-buttons { flex-wrap: wrap; }
    .status-row { flex-wrap: wrap; }
    .control-panel label { grid-template-columns: minmax(0, 1fr) 48px; }
    .thumb { flex-basis: 128px; }
  }
`;
document.head.appendChild(style);

const canvas = root.querySelector<HTMLCanvasElement>(".depth-canvas")!;
const title = root.querySelector<HTMLHeadingElement>("#scene-title")!;
const rail = root.querySelector<HTMLElement>("#thumb-rail")!;
const focusDot = root.querySelector<HTMLElement>("#focus-dot")!;
const depthStatus = root.querySelector<HTMLElement>("#depth-status")!;
const walkmeshStatus = root.querySelector<HTMLElement>("#walkmesh-status")!;
const xrButton = root.querySelector<HTMLButtonElement>("#xr-btn")!;
const viewModeButton = root.querySelector<HTMLButtonElement>("#view-mode-btn")!;
const walkmeshVisibleButton = root.querySelector<HTMLButtonElement>("#walkmesh-visible-btn")!;
const floorModeButton = root.querySelector<HTMLButtonElement>("#floor-mode-btn")!;
const depthSurfaceButton = root.querySelector<HTMLButtonElement>("#depth-surface-btn")!;
const focusOutput = root.querySelector<HTMLOutputElement>("#focus-value")!;

let backgrounds: GalleryBackground[] = [];
let activeIndex = 0;
let focusUv = new THREE.Vector2(0.5, 0.5);
let pointerUv = new THREE.Vector2(0.5, 0.5);
const pointerNdc = new THREE.Vector2(0, 0);
const raycaster = new THREE.Raycaster();
let targetYaw = 0;
let targetPitch = 0;
let lastFocusDepth = 0.5;
let targetFocusDepth = 0.5;
let autofocusDepth = 0.5;
let autofocusVelocity = 0;
let displayedFocusDepth = 0.5;
let displayedDofAmount = LOCKED_DOF;
let rackTargetDepth = 0.5;
let rackStartDepth = 0.5;
let rackElapsedSeconds = 99;
let walkmeshMode: WalkmeshMode = "depth";
let walkmeshVisible = false;
let viewMode: ViewMode = "depth3d";
let walkmeshFloorMode: WalkmeshFloorMode = "active";
let depthSurfaceMode: DepthSurfaceMode = "farthest";
let availableWalkmeshFloors: number[] = [];
const movementKeys = new Set<string>();

function fitMeshToViewport(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>, camera: THREE.PerspectiveCamera) {
  const baseWidth = Number(mesh.userData.baseWidth) || 1;
  const baseHeight = Number(mesh.userData.baseHeight) || 1;
  const visibleHeight = 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) * BASE_CAMERA_Z;
  const visibleWidth = visibleHeight * camera.aspect;
  const maxWidth = visibleWidth * PLATE_VIEWPORT_FILL;
  const maxHeight = visibleHeight * PLATE_VIEWPORT_FILL;
  const scale = Math.min(maxWidth / baseWidth, maxHeight / baseHeight);
  mesh.scale.set(scale, scale, 1);
}

function toBackgrounds(manifest: AssetManifest): GalleryBackground[] {
  const base = manifest.originalBackgrounds?.length
    ? manifest.originalBackgrounds
    : manifest.assets.map((asset) => ({
        id: asset.id,
        index: asset.index,
        mapName: asset.mapName,
        bundle: asset.bundle,
        status: asset.status,
        thumbnail: asset.thumbnail,
        sourcePlate: asset.sourcePlate,
        generatedAssetId: asset.id,
      }));

  return base.map((background) => ({
    ...background,
    depthUrl: `/assets/depth/${background.id}/depth.png`,
    hasGeneratedDepth: false,
  }));
}

async function imageExists(url: string) {
  try {
    const response = await fetch(url, { method: "HEAD", cache: "no-store" });
    return response.ok && (response.headers.get("content-type")?.startsWith("image/") ?? false);
  } catch {
    return false;
  }
}

function loadImage(url: string) {
  return new Promise<HTMLImageElement>((resolve, reject) => {
    const image = new Image();
    image.crossOrigin = "anonymous";
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error(`Unable to load ${url}`));
    image.src = url;
  });
}

async function loadJson<T>(url: string) {
  const response = await fetch(url, { cache: "no-store" });
  if (!response.ok) throw new Error(`Unable to load ${url}`);
  return response.json() as Promise<T>;
}

function makeFallbackDepth(image: HTMLImageElement) {
  const canvas2d = document.createElement("canvas");
  canvas2d.width = image.naturalWidth;
  canvas2d.height = image.naturalHeight;
  const context = canvas2d.getContext("2d", { willReadFrequently: true });
  if (!context) throw new Error("Depth canvas unavailable");
  context.drawImage(image, 0, 0);
  const frame = context.getImageData(0, 0, canvas2d.width, canvas2d.height);
  const pixels = frame.data;
  for (let index = 0; index < pixels.length; index += 4) {
    const luma = pixels[index] * 0.2126 + pixels[index + 1] * 0.7152 + pixels[index + 2] * 0.0722;
    const depth = Math.max(0, Math.min(255, 255 - luma * 0.86));
    pixels[index] = depth;
    pixels[index + 1] = depth;
    pixels[index + 2] = depth;
  }
  context.putImageData(frame, 0, 0);
  return canvas2d;
}

function makeDepthSampler(source: HTMLImageElement | HTMLCanvasElement) {
  const canvas2d = document.createElement("canvas");
  canvas2d.width = source instanceof HTMLImageElement ? source.naturalWidth : source.width;
  canvas2d.height = source instanceof HTMLImageElement ? source.naturalHeight : source.height;
  const context = canvas2d.getContext("2d", { willReadFrequently: true });
  if (!context) throw new Error("Depth sampling unavailable");
  context.drawImage(source, 0, 0);
  const data = context.getImageData(0, 0, canvas2d.width, canvas2d.height).data;
  return (u: number, v: number) => {
    const x = Math.max(0, Math.min(canvas2d.width - 1, Math.round(u * (canvas2d.width - 1))));
    const y = Math.max(0, Math.min(canvas2d.height - 1, Math.round(v * (canvas2d.height - 1))));
    return data[(y * canvas2d.width + x) * 4] / 255;
  };
}

function sampleShaderDepth(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>, uv: THREE.Vector2) {
  const sampleDepth = mesh.userData.sampleFocusDepth as ((u: number, v: number) => number) | undefined;
  return sampleDepth ? sampleDepth(uv.x, 1 - uv.y) : lastFocusDepth;
}

function plateLocalY(uvY: number, baseHeight: number) {
  const imageY = 1 - uvY;
  return (imageY - 0.5) * baseHeight;
}

function plateUvY(localY: number, baseHeight: number) {
  const imageY = localY / baseHeight + 0.5;
  return 1 - imageY;
}

function depthPlatePoint(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>, uv: THREE.Vector2, lift = 0.018) {
  const baseWidth = Number(mesh.userData.baseWidth) || 1;
  const baseHeight = Number(mesh.userData.baseHeight) || 1;
  const depth = sampleShaderDepth(mesh, uv);
  return new THREE.Vector3((uv.x - 0.5) * baseWidth, plateLocalY(uv.y, baseHeight), (depth - 0.5) * LOCKED_DEPTH + lift);
}

function flatPlatePoint(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>, uv: THREE.Vector2, lift = 0.018) {
  const baseWidth = Number(mesh.userData.baseWidth) || 1;
  const baseHeight = Number(mesh.userData.baseHeight) || 1;
  return new THREE.Vector3((uv.x - 0.5) * baseWidth, plateLocalY(uv.y, baseHeight), lift);
}

function groundDepthPoint(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>, uv: THREE.Vector2, lift: number) {
  const baseWidth = Number(mesh.userData.baseWidth) || 1;
  const baseHeight = Number(mesh.userData.baseHeight) || 1;
  const sourceWidth = Math.max(1, Number(mesh.userData.sourceWidth) || 1);
  const sourceHeight = Math.max(1, Number(mesh.userData.sourceHeight) || 1);
  const center = pixelFromUv(uv, sourceWidth, sourceHeight);
  const samples: number[] = [];

  for (let y = center.y - 8; y <= center.y + 8; y += 2) {
    if (y < 0 || y >= sourceHeight) continue;
    for (let x = center.x - 8; x <= center.x + 8; x += 2) {
      if (x < 0 || x >= sourceWidth) continue;
      const sampleU = x / Math.max(1, sourceWidth - 1);
      const sampleV = y / Math.max(1, sourceHeight - 1);
      samples.push((sampleShaderDepth(mesh, new THREE.Vector2(sampleU, sampleV)) - 0.5) * LOCKED_DEPTH);
    }
  }

  samples.sort((a, b) => a - b);
  const groundZ = samples[Math.max(0, Math.min(samples.length - 1, Math.floor(samples.length * 0.22)))] ?? 0;
  return new THREE.Vector3((uv.x - 0.5) * baseWidth, plateLocalY(uv.y, baseHeight), groundZ + lift);
}

function settleWalkmeshPointToGround(
  mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>,
  projected: ProjectedWalkPoint,
  local: THREE.Vector3 | null,
  lift: number,
) {
  const ground = groundDepthPoint(mesh, projected.uv, lift);
  if (!local) return ground;
  if (local.z > ground.z + 0.08) {
    local.z = ground.z;
  }
  return local;
}

function walkmeshPlatePoint(
  mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>,
  projected: ProjectedWalkPoint,
  lift: number,
  mode: WalkmeshMode,
  positionMap?: WalkmeshPositionMap,
  camera?: THREE.PerspectiveCamera,
) {
  if (mode === "depth" && camera) {
    return (
      castWalkmeshPointToBackground(mesh, projected, camera, lift) ??
      (positionMap ? samplePositionMap(positionMap, projected.uv, lift) : null) ??
      depthPlatePoint(mesh, projected.uv, lift)
    );
  }
  if (mode === "depth" && positionMap) {
    return samplePositionMap(positionMap, projected.uv, lift) ?? depthPlatePoint(mesh, projected.uv, lift);
  }
  return mode === "depth" ? depthPlatePoint(mesh, projected.uv, lift) : flatPlatePoint(mesh, projected.uv, lift);
}

function castWalkmeshPointToBackground(
  mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>,
  projected: ProjectedWalkPoint,
  camera: THREE.PerspectiveCamera,
  lift: number,
) {
  const flat = flatPlatePoint(mesh, projected.uv, 0);
  const screen = mesh.localToWorld(flat.clone()).project(camera);
  if (!Number.isFinite(screen.x) || !Number.isFinite(screen.y)) return null;

  raycaster.setFromCamera(new THREE.Vector2(screen.x, screen.y), camera);
  const hits = raycaster.intersectObject(mesh, false);
  const hit = depthSurfaceMode === "farthest" ? hits[hits.length - 1] : hits[0];
  if (!hit) return null;

  const local = mesh.worldToLocal(hit.point.clone());
  local.z += lift;
  return depthSurfaceMode === "ground" ? settleWalkmeshPointToGround(mesh, projected, local, lift) : local;
}

function uvFromDepthPlatePoint(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>, point: THREE.Vector3) {
  const baseWidth = Number(mesh.userData.baseWidth) || 1;
  const baseHeight = Number(mesh.userData.baseHeight) || 1;
  return new THREE.Vector2(point.x / baseWidth + 0.5, plateUvY(point.y, baseHeight));
}

function cameraProject(camera: NonNullable<CameraData["cameras"]>[number], point: number[]) {
  const r = camera.r;
  const t = camera.t;
  const vx = (r[0][0] * point[0] + r[0][1] * point[1] + r[0][2] * point[2]) / 4096 + t[0];
  const vy = (r[1][0] * point[0] + r[1][1] * point[1] + r[1][2] * point[2]) / 4096 + t[1];
  const vz = (r[2][0] * point[0] + r[2][1] * point[1] + r[2][2] * point[2]) / 4096 + t[2];
  if (Math.abs(vz) < 1e-5) return null;
  const offset = camera.center_offset ?? [0, 0];
  const cx = camera.w * 0.5 + offset[0];
  const cy = camera.h * 0.5 + offset[1];
  const sx = cx + (camera.proj * vx) / vz;
  const sy = cy + (camera.proj * vy) / vz;
  return {
    uv: new THREE.Vector2(sx / Math.max(1, camera.w), sy / Math.max(1, camera.h)),
    cameraDepth: Math.abs(vz),
  };
}

function barycentricInTriangle(point: THREE.Vector2, tri: THREE.Vector2[]) {
  const [a, b, c] = tri;
  const v0 = new THREE.Vector2().subVectors(c, a);
  const v1 = new THREE.Vector2().subVectors(b, a);
  const v2 = new THREE.Vector2().subVectors(point, a);
  const dot00 = v0.dot(v0);
  const dot01 = v0.dot(v1);
  const dot02 = v0.dot(v2);
  const dot11 = v1.dot(v1);
  const dot12 = v1.dot(v2);
  const invDenom = 1 / Math.max(1e-8, dot00 * dot11 - dot01 * dot01);
  const u = (dot11 * dot02 - dot01 * dot12) * invDenom;
  const v = (dot00 * dot12 - dot01 * dot02) * invDenom;
  if (u < -0.001 || v < -0.001 || u + v > 1.001) return null;
  return { a: 1 - u - v, b: v, c: u };
}

function pointInTriangle(point: THREE.Vector2, tri: THREE.Vector2[]) {
  return Boolean(barycentricInTriangle(point, tri));
}

function zOnTriangle(point: THREE.Vector2, tri2d: THREE.Vector2[], tri3d: THREE.Vector3[]) {
  const bary = barycentricInTriangle(point, tri2d);
  if (!bary) return null;
  return tri3d[0].z * bary.a + tri3d[1].z * bary.b + tri3d[2].z * bary.c;
}

function pixelFromUv(uv: THREE.Vector2, width: number, height: number) {
  return {
    x: Math.max(0, Math.min(width - 1, Math.round(uv.x * (width - 1)))),
    y: Math.max(0, Math.min(height - 1, Math.round(uv.y * (height - 1)))),
  };
}

function pointMapIndex(x: number, y: number, width: number) {
  return (y * width + x) * 4;
}

function samplePositionMap(positionMap: WalkmeshPositionMap, uv: THREE.Vector2, lift = 0) {
  const center = pixelFromUv(uv, positionMap.width, positionMap.height);
  for (let radius = 0; radius <= 3; radius += 1) {
    for (let y = center.y - radius; y <= center.y + radius; y += 1) {
      if (y < 0 || y >= positionMap.height) continue;
      for (let x = center.x - radius; x <= center.x + radius; x += 1) {
        if (x < 0 || x >= positionMap.width) continue;
        const offset = pointMapIndex(x, y, positionMap.width);
        if (positionMap.data[offset + 3] <= 0) continue;
        return new THREE.Vector3(positionMap.data[offset], positionMap.data[offset + 1], positionMap.data[offset + 2] + lift);
      }
    }
  }
  return null;
}

function buildWalkmeshPositionMap(
  mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>,
  projectedSource: ProjectedWalkPoint[][],
  camera?: THREE.PerspectiveCamera,
) {
  const width = Number(mesh.userData.sourceWidth) || 1;
  const height = Number(mesh.userData.sourceHeight) || 1;
  const data = new Float32Array(width * height * 4);

  for (const projected of projectedSource) {
    const pixelTriangle = projected.map((point) => {
      const pixel = pixelFromUv(point.uv, width, height);
      return new THREE.Vector2(pixel.x, pixel.y);
    });
    const localTriangle = projected.map((point) => (camera ? castWalkmeshPointToBackground(mesh, point, camera, 0) : null) ?? depthPlatePoint(mesh, point.uv, 0));
    const minX = Math.max(0, Math.floor(Math.min(...pixelTriangle.map((point) => point.x))));
    const maxX = Math.min(width - 1, Math.ceil(Math.max(...pixelTriangle.map((point) => point.x))));
    const minY = Math.max(0, Math.floor(Math.min(...pixelTriangle.map((point) => point.y))));
    const maxY = Math.min(height - 1, Math.ceil(Math.max(...pixelTriangle.map((point) => point.y))));

    for (let y = minY; y <= maxY; y += 1) {
      for (let x = minX; x <= maxX; x += 1) {
        const bary = barycentricInTriangle(new THREE.Vector2(x + 0.5, y + 0.5), pixelTriangle);
        if (!bary) continue;
        const offset = pointMapIndex(x, y, width);
        data[offset] = localTriangle[0].x * bary.a + localTriangle[1].x * bary.b + localTriangle[2].x * bary.c;
        data[offset + 1] = localTriangle[0].y * bary.a + localTriangle[1].y * bary.b + localTriangle[2].y * bary.c;
        data[offset + 2] = localTriangle[0].z * bary.a + localTriangle[1].z * bary.b + localTriangle[2].z * bary.c;
        data[offset + 3] = 1;
      }
    }

    for (let index = 0; index < projected.length; index += 1) {
      const pixel = pixelFromUv(projected[index].uv, width, height);
      const point = localTriangle[index];
      const offset = pointMapIndex(pixel.x, pixel.y, width);
      data[offset] = point.x;
      data[offset + 1] = point.y;
      data[offset + 2] = point.z;
      data[offset + 3] = 1;
    }
  }

  const texture = new THREE.DataTexture(data, width, height, THREE.RGBAFormat, THREE.FloatType);
  texture.needsUpdate = true;
  return { width, height, data, texture };
}

function syncFocusFromPointer(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>) {
  raycaster.setFromCamera(pointerNdc, state.camera);
  const hit = raycaster.intersectObject(mesh, false)[0];
  if (!hit?.uv) return targetFocusDepth;
  focusUv.copy(hit.uv);
  lastFocusDepth = sampleShaderDepth(mesh, focusUv);
  if (Math.abs(lastFocusDepth - rackTargetDepth) > 0.018) {
    const travel = lastFocusDepth - autofocusDepth;
    rackStartDepth = autofocusDepth;
    rackTargetDepth = lastFocusDepth;
    rackElapsedSeconds = 0;
    autofocusVelocity = -Math.sign(travel || 1) * Math.min(0.42, 0.1 + Math.abs(travel) * 1.8);
  }
  targetFocusDepth = lastFocusDepth;
  focusOutput.value = lastFocusDepth.toFixed(2);
  return targetFocusDepth;
}

function updateAutofocus(targetDepth: number, deltaSeconds: number, elapsedSeconds: number) {
  rackElapsedSeconds += deltaSeconds;
  const error = targetDepth - autofocusDepth;
  autofocusVelocity += error * 92 * deltaSeconds;
  autofocusVelocity *= Math.exp(-5.8 * deltaSeconds);
  autofocusDepth = THREE.MathUtils.clamp(autofocusDepth + autofocusVelocity * deltaSeconds, 0, 1);

  const rackTravel = rackTargetDepth - rackStartDepth;
  const rackDirection = Math.sign(rackTravel || 1);
  const rackAmount = Math.min(0.16, 0.035 + Math.abs(rackTravel) * 0.7);
  const rackEnvelope = Math.exp(-rackElapsedSeconds * 3.2);
  const wrongWayPull = -rackDirection * rackAmount * Math.exp(-rackElapsedSeconds * 10);
  const hunt = Math.sin(rackElapsedSeconds * 21 + Math.PI * 0.35) * rackAmount * rackEnvelope;
  const microHunt = Math.sin(elapsedSeconds * 31) * Math.min(0.006, Math.abs(error) * 0.08);
  return THREE.MathUtils.clamp(autofocusDepth + wrongWayPull + hunt + microHunt, 0, 1);
}

function makeMaterial(colorTexture: THREE.Texture, depthTexture: THREE.Texture, colorWidth: number, colorHeight: number) {
  return new THREE.ShaderMaterial({
    uniforms: {
      colorMap: { value: colorTexture },
      depthMap: { value: depthTexture },
      colorTexel: { value: new THREE.Vector2(1 / colorWidth, 1 / colorHeight) },
      focusUv: { value: new THREE.Vector2(0.5, 0.5) },
      focusDepth: { value: 0.5 },
      dofAmount: { value: LOCKED_DOF },
    },
    vertexShader: `
      varying vec2 vUv;
      void main() {
        vUv = uv;
        gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
      }
    `,
    fragmentShader: `
      uniform sampler2D colorMap;
      uniform sampler2D depthMap;
      uniform vec2 colorTexel;
      uniform vec2 focusUv;
      uniform float focusDepth;
      uniform float dofAmount;
      varying vec2 vUv;

      float luma(vec3 color) {
        return dot(color, vec3(0.2126, 0.7152, 0.0722));
      }

      void main() {
        vec4 baseColor = texture2D(colorMap, vUv);
        float depth = texture2D(depthMap, vUv).r;
        float depthDefocus = abs(depth - focusDepth);
        float focusFalloff = smoothstep(0.015, 0.18, distance(vUv, focusUv));
        vec2 edgeStep = colorTexel * 1.5;
        float centerLuma = luma(baseColor.rgb);
        float lumaEdge = 0.0;
        lumaEdge = max(lumaEdge, abs(centerLuma - luma(texture2D(colorMap, vUv + vec2( edgeStep.x, 0.0)).rgb)));
        lumaEdge = max(lumaEdge, abs(centerLuma - luma(texture2D(colorMap, vUv + vec2(-edgeStep.x, 0.0)).rgb)));
        lumaEdge = max(lumaEdge, abs(centerLuma - luma(texture2D(colorMap, vUv + vec2(0.0,  edgeStep.y)).rgb)));
        lumaEdge = max(lumaEdge, abs(centerLuma - luma(texture2D(colorMap, vUv + vec2(0.0, -edgeStep.y)).rgb)));
        lumaEdge = max(lumaEdge, abs(centerLuma - luma(texture2D(colorMap, vUv + vec2( edgeStep.x,  edgeStep.y)).rgb)));
        lumaEdge = max(lumaEdge, abs(centerLuma - luma(texture2D(colorMap, vUv + vec2(-edgeStep.x, -edgeStep.y)).rgb)));

        float depthEdge = 0.0;
        depthEdge = max(depthEdge, abs(depth - texture2D(depthMap, vUv + vec2( edgeStep.x, 0.0)).r));
        depthEdge = max(depthEdge, abs(depth - texture2D(depthMap, vUv + vec2(-edgeStep.x, 0.0)).r));
        depthEdge = max(depthEdge, abs(depth - texture2D(depthMap, vUv + vec2(0.0,  edgeStep.y)).r));
        depthEdge = max(depthEdge, abs(depth - texture2D(depthMap, vUv + vec2(0.0, -edgeStep.y)).r));

        float lumaEdgeFocus = smoothstep(0.055, 0.22, lumaEdge);
        float hardDepthEdge = smoothstep(0.06, 0.18, depthEdge);
        float edgeDefocus = max(lumaEdgeFocus, hardDepthEdge * 0.85) * (0.009 + focusFalloff * 0.005) * dofAmount;
        float blur = clamp(depthDefocus * (0.006 + focusFalloff * 0.013) * dofAmount + edgeDefocus, 0.0, 0.017);
        if (dofAmount < 0.001) {
          gl_FragColor = baseColor;
          return;
        }
        vec2 aspect = vec2(1.0, 1.42);

        vec4 color = baseColor * 0.20;
        color += texture2D(colorMap, vUv + vec2( 0.0000,  0.8500) * blur * aspect) * 0.060;
        color += texture2D(colorMap, vUv + vec2( 0.7361,  0.4250) * blur * aspect) * 0.060;
        color += texture2D(colorMap, vUv + vec2( 0.7361, -0.4250) * blur * aspect) * 0.060;
        color += texture2D(colorMap, vUv + vec2( 0.0000, -0.8500) * blur * aspect) * 0.060;
        color += texture2D(colorMap, vUv + vec2(-0.7361, -0.4250) * blur * aspect) * 0.060;
        color += texture2D(colorMap, vUv + vec2(-0.7361,  0.4250) * blur * aspect) * 0.060;

        color += texture2D(colorMap, vUv + vec2( 0.0000,  1.6500) * blur * aspect) * 0.050;
        color += texture2D(colorMap, vUv + vec2( 1.4289,  0.8250) * blur * aspect) * 0.050;
        color += texture2D(colorMap, vUv + vec2( 1.4289, -0.8250) * blur * aspect) * 0.050;
        color += texture2D(colorMap, vUv + vec2( 0.0000, -1.6500) * blur * aspect) * 0.050;
        color += texture2D(colorMap, vUv + vec2(-1.4289, -0.8250) * blur * aspect) * 0.050;
        color += texture2D(colorMap, vUv + vec2(-1.4289,  0.8250) * blur * aspect) * 0.050;

        color += texture2D(colorMap, vUv + vec2( 0.5667,  0.0000) * blur * aspect) * 0.040;
        color += texture2D(colorMap, vUv + vec2(-0.5667,  0.0000) * blur * aspect) * 0.040;
        color += texture2D(colorMap, vUv + vec2( 0.2834,  0.4908) * blur * aspect) * 0.040;
        color += texture2D(colorMap, vUv + vec2(-0.2834, -0.4908) * blur * aspect) * 0.040;

        gl_FragColor = vec4(color.rgb, 1.0);
      }
    `,
    side: THREE.DoubleSide,
  });
}

async function makeDepthMesh(background: GalleryBackground, state: SceneState, mode: ViewMode) {
  const colorImage = await loadImage(background.sourcePlate.url);
  const depthReady = await imageExists(background.depthUrl);
  const depthImage = depthReady ? await loadImage(background.depthUrl) : makeFallbackDepth(colorImage);
  background.hasGeneratedDepth = depthReady;

  const colorTexture = new THREE.Texture(colorImage);
  colorTexture.colorSpace = THREE.SRGBColorSpace;
  colorTexture.needsUpdate = true;
  const depthTexture = new THREE.Texture(depthImage);
  depthTexture.needsUpdate = true;

  const aspect = colorImage.naturalWidth / colorImage.naturalHeight;
  const baseWidth = aspect;
  const baseHeight = 1;
  const geometry = new THREE.PlaneGeometry(baseWidth, baseHeight, 168, 96);
  const positions = geometry.attributes.position;
  const sampleDepth = makeDepthSampler(depthImage);
  const depthStrength = LOCKED_DEPTH;

  for (let index = 0; index < positions.count; index += 1) {
    const u = geometry.attributes.uv.getX(index);
    const v = geometry.attributes.uv.getY(index);
    const depth = sampleDepth(u, 1 - v);
    const centered = mode === "flat2d" ? 0 : (depth - 0.5) * depthStrength;
    positions.setZ(index, centered);
  }

  positions.needsUpdate = true;
  geometry.computeVertexNormals();
  const material = makeMaterial(colorTexture, depthTexture, colorImage.naturalWidth, colorImage.naturalHeight);
  const mesh = new THREE.Mesh(geometry, material);
  mesh.userData.baseWidth = baseWidth;
  mesh.userData.baseHeight = baseHeight;
  mesh.userData.sourceWidth = colorImage.naturalWidth;
  mesh.userData.sourceHeight = colorImage.naturalHeight;
  const sampleFocusDepth = makeDepthSampler(depthImage);
  mesh.userData.sampleFocusDepth = sampleFocusDepth;
  material.uniforms.focusDepth.value = sampleShaderDepth(mesh, focusUv);
  mesh.frustumCulled = false;
  fitMeshToViewport(mesh, state.camera);
  return mesh;
}

async function makeWalkmeshGroup(
  background: GalleryBackground,
  mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>,
  mode: WalkmeshMode,
  camera: THREE.PerspectiveCamera,
) {
  const walkmeshUrl = background.walkmesh?.url ?? `/assets/originals/${background.id}/walkmesh.json`;
  const camerasUrl = background.cameraMetadata?.url ?? `/assets/originals/${background.id}/cameras.json`;
  const [walkmesh, cameraData] = await Promise.all([loadJson<WalkmeshData>(walkmeshUrl), loadJson<CameraData>(camerasUrl)]);
  const cameraIndex = cameraData.activeOverlayCameras?.[0] ?? 0;
  const fieldCamera = cameraData.cameras?.[cameraIndex] ?? cameraData.cameras?.[0];
  if (!walkmesh.ok || !fieldCamera) throw new Error("Walkmesh unavailable");

  const projectedSource: ProjectedWalkPoint[][] = [];
  const activeFloor = walkmesh.activeFloor ?? 0;
  const floorIndices = Array.from(
    new Set(
      (walkmesh.triangles ?? [])
        .map((triangle) => triangle.floorIndex)
        .filter((floorIndex): floorIndex is number => typeof floorIndex === "number"),
    ),
  ).sort((a, b) => a - b);
  availableWalkmeshFloors = floorIndices.length ? floorIndices : [activeFloor];
  for (const triangle of walkmesh.triangles ?? []) {
    if (walkmeshFloorMode === "active" && triangle.floorIndex !== activeFloor) continue;
    if (typeof walkmeshFloorMode === "number" && triangle.floorIndex !== walkmeshFloorMode) continue;
    const projected = (triangle.worldVertices ?? [])
      .map((vertex) => cameraProject(fieldCamera, vertex))
      .filter((point): point is ProjectedWalkPoint => Boolean(point));
    if (projected.length !== 3) continue;
    if (projected.every((point) => point.uv.x < -0.2 || point.uv.x > 1.2 || point.uv.y < -0.2 || point.uv.y > 1.2)) continue;
    projectedSource.push(projected);
  }
  if (!projectedSource.length) throw new Error("Walkmesh projection empty");

  const projectedTriangles: THREE.Vector2[][] = [];
  const projectedSurfaces: THREE.Vector3[][] = [];
  const fillPositions: number[] = [];
  const linePositions: number[] = [];
  const meshLift = mode === "depth" ? 0.006 : 0.018;
  const lineLift = mode === "depth" ? 0.006 : 0.01;
  mesh.updateMatrixWorld(true);
  camera.updateMatrixWorld(true);
  const positionMap = mode === "depth" ? buildWalkmeshPositionMap(mesh, projectedSource, camera) : undefined;
  for (const projected of projectedSource) {
    const local = projected.map((point) => walkmeshPlatePoint(mesh, point, meshLift, mode, positionMap, camera));
    projectedTriangles.push(local.map((point) => new THREE.Vector2(point.x, point.y)));
    projectedSurfaces.push(local);
    for (const point of local) fillPositions.push(point.x, point.y, point.z);
    for (let edge = 0; edge < 3; edge += 1) {
      const a = local[edge];
      const b = local[(edge + 1) % 3];
      linePositions.push(a.x, a.y, a.z + lineLift, b.x, b.y, b.z + lineLift);
    }
  }
  if (!fillPositions.length) throw new Error("Walkmesh projection empty");

  const group = new THREE.Group();
  group.name = "walkmesh-preview";
  group.userData.mode = mode;
  group.userData.projectedTriangles = projectedTriangles;
  group.userData.projectedSurfaces = projectedSurfaces;
  group.userData.positionMapTexture = positionMap?.texture;
  const fillGeometry = new THREE.BufferGeometry();
  fillGeometry.setAttribute("position", new THREE.Float32BufferAttribute(fillPositions, 3));
  fillGeometry.computeVertexNormals();
  const fill = new THREE.Mesh(
    fillGeometry,
    new THREE.MeshBasicMaterial({
      color: 0x44d7ff,
      transparent: true,
      opacity: 0.2,
      side: THREE.DoubleSide,
      depthWrite: false,
    }),
  );
  fill.renderOrder = 4;
  group.add(fill);

  const lineGeometry = new THREE.BufferGeometry();
  lineGeometry.setAttribute("position", new THREE.Float32BufferAttribute(linePositions, 3));
  const lines = new THREE.LineSegments(
    lineGeometry,
    new THREE.LineBasicMaterial({ color: 0xffd36b, transparent: true, opacity: 0.92, depthWrite: false }),
  );
  lines.renderOrder = 5;
  group.add(lines);

  const pawn = new THREE.Group();
  pawn.name = "zidane-walkmesh-marker";
  const body = new THREE.Mesh(
    new THREE.ConeGeometry(0.035, 0.11, 12),
    new THREE.MeshBasicMaterial({ color: 0xffd36b, depthWrite: false }),
  );
  body.rotation.x = Math.PI;
  body.position.z = 0.08;
  pawn.add(body);
  const shadow = new THREE.Mesh(
    new THREE.CircleGeometry(0.05, 20),
    new THREE.MeshBasicMaterial({ color: 0x111111, transparent: true, opacity: 0.45, depthWrite: false }),
  );
  shadow.position.z = 0.012;
  pawn.add(shadow);

  const charUv = walkmesh.charPos ? cameraProject(fieldCamera, walkmesh.charPos) : null;
  const start = charUv ? walkmeshPlatePoint(mesh, charUv, mode === "depth" ? 0.026 : 0.055, mode, positionMap, camera) : new THREE.Vector3();
  pawn.position.copy(start);
  pawn.renderOrder = 6;
  group.userData.player = pawn;
  group.add(pawn);

  group.userData.triangleCount = projectedTriangles.length;
  group.userData.floorMode = walkmeshFloorMode;
  group.userData.activeFloor = activeFloor;
  group.userData.availableFloors = availableWalkmeshFloors;
  return group;
}

function disposeObject3D(object: THREE.Object3D) {
  const positionMapTexture = object.userData.positionMapTexture as THREE.DataTexture | undefined;
  positionMapTexture?.dispose();
  object.traverse((child) => {
    const maybeMesh = child as THREE.Mesh;
    maybeMesh.geometry?.dispose();
    const material = maybeMesh.material;
    if (Array.isArray(material)) material.forEach((item) => item.dispose());
    else material?.dispose();
  });
}

function setViewModeButtonState() {
  viewModeButton.textContent = viewMode === "flat2d" ? "View 3D" : "View 2D";
  viewModeButton.classList.toggle("active", viewMode === "flat2d");
}

function setWalkmeshVisibleButtonState() {
  walkmeshVisibleButton.textContent = walkmeshVisible ? "Hide Walkmesh" : "Show Walkmesh";
  walkmeshVisibleButton.setAttribute("aria-pressed", String(walkmeshVisible));
  walkmeshVisibleButton.classList.toggle("active", walkmeshVisible);
}

function setFloorModeButtonState() {
  floorModeButton.textContent =
    walkmeshFloorMode === "active" ? "Floor Active" : walkmeshFloorMode === "all" ? "Floor All" : `Floor ${walkmeshFloorMode}`;
  floorModeButton.classList.toggle("active", walkmeshFloorMode === "all");
}

function setDepthSurfaceButtonState() {
  depthSurfaceButton.textContent =
    depthSurfaceMode === "ground" ? "Cast Ground" : depthSurfaceMode === "nearest" ? "Cast Near" : "Cast Far";
  depthSurfaceButton.classList.toggle("active", depthSurfaceMode !== "nearest");
}

function floorLabelForStatus(mode: WalkmeshFloorMode, activeFloor: number) {
  if (mode === "all") return "all floors";
  if (mode === "active") return `active floor ${activeFloor}`;
  return `floor ${mode}`;
}

function depthSurfaceLabelForStatus(mode: DepthSurfaceMode) {
  return mode === "ground" ? "ground cast" : mode === "nearest" ? "near cast" : "far cast";
}

function cycleWalkmeshFloorMode() {
  const floors = availableWalkmeshFloors.length ? availableWalkmeshFloors : [0];
  const options: WalkmeshFloorMode[] = ["active", ...floors, "all"];
  const currentIndex = options.findIndex((option) => option === walkmeshFloorMode);
  walkmeshFloorMode = options[(currentIndex + 1) % options.length] ?? "active";
}

function cycleDepthSurfaceMode() {
  depthSurfaceMode = depthSurfaceMode === "ground" ? "nearest" : depthSurfaceMode === "nearest" ? "farthest" : "ground";
}

async function syncWalkmeshOverlay(background: GalleryBackground) {
  if (!state.mesh) return;
  const previous = state.mesh.getObjectByName("walkmesh-preview");
  if (previous) {
    state.mesh.remove(previous);
    disposeObject3D(previous);
  }
  setFloorModeButtonState();
  setDepthSurfaceButtonState();
  setWalkmeshVisibleButtonState();

  if (!walkmeshVisible) {
    walkmeshStatus.textContent = "Walkmesh: hidden";
    return;
  }

  walkmeshStatus.textContent = "Walkmesh: loading";
  try {
    const mode = walkmeshMode;
    const group = await makeWalkmeshGroup(background, state.mesh, mode, state.camera);
    if (state.mesh && backgrounds[activeIndex]?.id === background.id && walkmeshMode === mode) {
      state.mesh.add(group);
      const floorLabel = floorLabelForStatus(group.userData.floorMode, group.userData.activeFloor);
      const surfaceLabel = mode === "depth" ? `, ${depthSurfaceLabelForStatus(depthSurfaceMode)}` : "";
      walkmeshStatus.textContent = `Walkmesh: ${mode}, ${floorLabel}${surfaceLabel}, ${group.userData.triangleCount} tris, WASD`;
    } else {
      disposeObject3D(group);
    }
  } catch (error) {
    walkmeshStatus.textContent = error instanceof Error ? error.message : "Walkmesh: unavailable";
  }
}

function moveWalkmeshPawn(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial>, deltaSeconds: number) {
  const group = mesh.getObjectByName("walkmesh-preview");
  const pawn = group?.userData.player as THREE.Group | undefined;
  const triangles = group?.userData.projectedTriangles as THREE.Vector2[][] | undefined;
  const surfaces = group?.userData.projectedSurfaces as THREE.Vector3[][] | undefined;
  if (!group || !pawn || !triangles?.length) return;

  const dx = (movementKeys.has("arrowright") || movementKeys.has("d") ? 1 : 0) - (movementKeys.has("arrowleft") || movementKeys.has("a") ? 1 : 0);
  const dy = (movementKeys.has("arrowup") || movementKeys.has("w") ? 1 : 0) - (movementKeys.has("arrowdown") || movementKeys.has("s") ? 1 : 0);
  if (!dx && !dy) return;

  const direction = new THREE.Vector2(dx, dy);
  direction.normalize().multiplyScalar(deltaSeconds * 0.52);
  const next = new THREE.Vector2(pawn.position.x + direction.x, pawn.position.y + direction.y);
  let nextZ: number | null = null;
  for (let index = 0; index < triangles.length; index += 1) {
    const surface = surfaces?.[index];
    const z = surface ? zOnTriangle(next, triangles[index], surface) : null;
    if (z !== null) {
      nextZ = z;
      break;
    }
    if (!surface && pointInTriangle(next, triangles[index])) {
      nextZ = pawn.position.z;
      break;
    }
  }
  if (nextZ === null) return;

  pawn.position.x = next.x;
  pawn.position.y = next.y;
  pawn.position.z = nextZ + 0.018;
  pawn.rotation.z = Math.atan2(direction.y, direction.x) - Math.PI / 2;
}

const state: SceneState = (() => {
  const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, alpha: false, powerPreference: "high-performance" });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  renderer.outputColorSpace = THREE.SRGBColorSpace;
  renderer.xr.enabled = true;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x050506);
  const camera = new THREE.PerspectiveCamera(38, 1, 0.05, 100);
  camera.position.set(0, 0, BASE_CAMERA_Z);
  camera.lookAt(0, 0, 0);

  return { renderer, scene, camera, mesh: null };
})();

function disposeMesh(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.ShaderMaterial> | null) {
  if (!mesh) return;
  for (const child of [...mesh.children]) {
    mesh.remove(child);
    disposeObject3D(child);
  }
  mesh.geometry.dispose();
  mesh.material.uniforms.colorMap.value.dispose();
  mesh.material.uniforms.depthMap.value.dispose();
  mesh.material.dispose();
}

async function showScene(index: number) {
  if (!backgrounds.length) return;
  activeIndex = (index + backgrounds.length) % backgrounds.length;
  const background = backgrounds[activeIndex];
  window.history.replaceState(null, "", `#/scene/${background.generatedAssetId ?? background.id}`);
  title.textContent = background.mapName;
  setViewModeButtonState();
  setWalkmeshVisibleButtonState();
  setFloorModeButtonState();
  setDepthSurfaceButtonState();
  depthStatus.textContent = "Depth: loading";

  const nextMesh = await makeDepthMesh(background, state, viewMode);
  if (state.mesh) {
    state.scene.remove(state.mesh);
    disposeMesh(state.mesh);
  }
  state.mesh = nextMesh;
  state.scene.add(nextMesh);
  const initialFocusDepth = sampleShaderDepth(nextMesh, focusUv);
  lastFocusDepth = initialFocusDepth;
  targetFocusDepth = initialFocusDepth;
  autofocusDepth = initialFocusDepth;
  autofocusVelocity = 0;
  displayedFocusDepth = initialFocusDepth;
  displayedDofAmount = viewMode === "flat2d" ? 0 : LOCKED_DOF;
  rackTargetDepth = initialFocusDepth;
  rackStartDepth = initialFocusDepth;
  rackElapsedSeconds = 99;
  focusOutput.value = initialFocusDepth.toFixed(2);
  depthStatus.textContent =
    viewMode === "flat2d" ? "Depth: 2D plate" : background.hasGeneratedDepth ? "Depth: Depth Anything" : "Depth: fallback until generated";
  void syncWalkmeshOverlay(background);
  renderThumbs();
}

function renderThumbs() {
  rail.innerHTML = "";
  backgrounds.forEach((background, index) => {
    const button = document.createElement("button");
    button.className = `thumb ${index === activeIndex ? "active" : ""}`;
    button.type = "button";
    button.innerHTML = `<img src="${background.thumbnail}" alt="" loading="lazy" /><strong>${background.mapName}</strong>`;
    button.addEventListener("click", () => void showScene(index));
    rail.appendChild(button);
  });
  rail.querySelector(".thumb.active")?.scrollIntoView({ block: "nearest", inline: "center" });
}

function resize() {
  const width = canvas.clientWidth;
  const height = canvas.clientHeight;
  state.camera.aspect = width / Math.max(1, height);
  state.camera.updateProjectionMatrix();
  state.renderer.setSize(width, height, false);
  if (state.mesh) fitMeshToViewport(state.mesh, state.camera);
}

window.addEventListener("resize", resize);
resize();

canvas.addEventListener("pointermove", (event) => {
  const rect = canvas.getBoundingClientRect();
  const x = (event.clientX - rect.left) / rect.width;
  const y = (event.clientY - rect.top) / rect.height;
  pointerUv.set(x, y);
  pointerNdc.set(x * 2 - 1, 1 - y * 2);
  targetYaw = (x - 0.5) * 0.42 * VIEW_ANGLE_MULTIPLIER;
  targetPitch = (0.5 - y) * 0.18 * VIEW_ANGLE_MULTIPLIER;
  focusDot.style.left = `${event.clientX - rect.left}px`;
  focusDot.style.top = `${event.clientY - rect.top}px`;
});

canvas.addEventListener(
  "wheel",
  (event) => {
    event.preventDefault();
  },
  { passive: false },
);

root.querySelector("#prev-btn")?.addEventListener("click", () => void showScene(activeIndex - 1));
root.querySelector("#next-btn")?.addEventListener("click", () => void showScene(activeIndex + 1));
viewModeButton.addEventListener("click", () => {
  viewMode = viewMode === "depth3d" ? "flat2d" : "depth3d";
  setViewModeButtonState();
  void showScene(activeIndex);
});
walkmeshVisibleButton.addEventListener("click", () => {
  walkmeshVisible = !walkmeshVisible;
  setWalkmeshVisibleButtonState();
  void syncWalkmeshOverlay(backgrounds[activeIndex]);
});
floorModeButton.addEventListener("click", () => {
  cycleWalkmeshFloorMode();
  setFloorModeButtonState();
  void syncWalkmeshOverlay(backgrounds[activeIndex]);
});
depthSurfaceButton.addEventListener("click", () => {
  cycleDepthSurfaceMode();
  setDepthSurfaceButtonState();
  void syncWalkmeshOverlay(backgrounds[activeIndex]);
});
root.querySelector("#reset-btn")?.addEventListener("click", () => {
  pointerUv.set(0.5, 0.5);
  pointerNdc.set(0, 0);
  focusUv.set(0.5, 0.5);
  lastFocusDepth = 0.5;
  targetFocusDepth = 0.5;
  autofocusDepth = 0.5;
  autofocusVelocity = 0;
  displayedFocusDepth = 0.5;
  displayedDofAmount = viewMode === "flat2d" ? 0 : LOCKED_DOF;
  rackTargetDepth = 0.5;
  rackStartDepth = 0.5;
  rackElapsedSeconds = 99;
  focusOutput.value = lastFocusDepth.toFixed(2);
  targetYaw = 0;
  targetPitch = 0;
  if (state.mesh) state.mesh.rotation.set(0, 0, 0);
});

window.addEventListener("keydown", (event) => {
  const key = event.key.toLowerCase();
  if (["arrowup", "arrowdown", "arrowleft", "arrowright", "w", "a", "s", "d"].includes(key)) {
    movementKeys.add(key);
    event.preventDefault();
  }
});

window.addEventListener("keyup", (event) => {
  movementKeys.delete(event.key.toLowerCase());
});

xrButton.disabled = true;
isImmersiveVrSupported().then((supported) => {
  xrButton.disabled = !supported;
  xrButton.textContent = supported ? "VR" : "No VR";
});
xrButton.addEventListener("click", () => void startImmersiveVr(state.renderer, () => (xrButton.textContent = "VR")));

const clock = new THREE.Clock();
state.renderer.setAnimationLoop(() => {
  resize();
  const deltaSeconds = Math.min(clock.getDelta(), 0.05);
  const t = clock.elapsedTime;
  const wiggle = LOCKED_WIGGLE;
  const mesh = state.mesh;
  if (mesh) {
    const autoWiggleX = Math.sin(t * 0.9) * 0.035 * wiggle;
    const autoWiggleY = Math.cos(t * 0.73 + 0.8) * 0.018 * wiggle;
    const autoWiggleZ = Math.sin(t * 0.57 + 1.35) * 0.12 * wiggle;
    if (viewMode === "flat2d") {
      mesh.rotation.x += (0 - mesh.rotation.x) * 0.2;
      mesh.rotation.y += (0 - mesh.rotation.y) * 0.2;
    } else {
      mesh.rotation.y += (targetYaw * wiggle * TILT_MULTIPLIER + autoWiggleX - mesh.rotation.y) * 0.075;
      mesh.rotation.x += (targetPitch * wiggle * TILT_MULTIPLIER + autoWiggleY - mesh.rotation.x) * 0.075;
    }
    state.camera.position.set(0, 0, BASE_CAMERA_Z + (viewMode === "flat2d" ? 0 : autoWiggleZ));
    state.camera.lookAt(0, 0, 0);
    const targetDepth = syncFocusFromPointer(mesh);
    const focusDepth = updateAutofocus(targetDepth, deltaSeconds, t);
    const focusLerp = 1 - Math.exp(-deltaSeconds * 1.875);
    const dofLerp = 1 - Math.exp(-deltaSeconds * 1.25);
    displayedFocusDepth = THREE.MathUtils.lerp(displayedFocusDepth, focusDepth, focusLerp);
    displayedDofAmount = THREE.MathUtils.lerp(displayedDofAmount, viewMode === "flat2d" ? 0 : LOCKED_DOF, dofLerp);
    mesh.material.uniforms.focusUv.value.copy(focusUv);
    mesh.material.uniforms.dofAmount.value = displayedDofAmount;
    mesh.material.uniforms.focusDepth.value = displayedFocusDepth;
    moveWalkmeshPawn(mesh, deltaSeconds);
  }
  state.renderer.render(state.scene, state.camera);
});

fetch("/assets/index.json", { cache: "no-store" })
  .then((response) => {
    if (!response.ok) throw new Error(`Manifest failed: ${response.status}`);
    return response.json() as Promise<AssetManifest>;
  })
  .then((manifest) => {
    backgrounds = toBackgrounds(manifest);
    const routeId = window.location.hash.replace(/^#\/scene\//, "");
    const routeIndex = backgrounds.findIndex((background) => background.id === routeId || background.generatedAssetId === routeId);
    renderThumbs();
    void showScene(routeIndex >= 0 ? routeIndex : 0).catch((error: unknown) => {
      depthStatus.textContent = error instanceof Error ? error.message : "Unable to build depth scene";
    });
  })
  .catch((error: unknown) => {
    title.textContent = error instanceof Error ? error.message : "Unable to load gallery";
  });
