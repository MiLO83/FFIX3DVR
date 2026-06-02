import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { loadSphericalSplatPly } from "./ply";
import type { AssetManifest, OriginalBackground, SceneAsset } from "./types";

type PreviewMode = "flat" | "depth" | "splat";

type PreviewBackground = OriginalBackground & {
  depthUrl: string;
  asset?: SceneAsset;
};

type PreviewState = {
  renderer: THREE.WebGLRenderer;
  scene: THREE.Scene;
  camera: THREE.PerspectiveCamera;
  controls: OrbitControls;
  content: THREE.Object3D | null;
};

const DEPTH_STRENGTH = 2.5;
const CAMERA_Z = 3.2;
const MODE_ORDER: PreviewMode[] = ["flat", "depth", "splat"];
const MODE_LABELS: Record<PreviewMode, string> = {
  flat: "Flat BG",
  depth: "Depth BG",
  splat: "Splat",
};

function requireElement<T extends Element>(selector: string) {
  const element = document.querySelector<T>(selector);
  if (!element) throw new Error(`Missing element: ${selector}`);
  return element;
}

const root = requireElement<HTMLDivElement>("#depth-preview");

root.innerHTML = `
  <main class="preview-page">
    <section class="preview-stage">
      <canvas class="preview-canvas"></canvas>
      <header class="preview-topbar">
        <div>
          <p>FFIX Depth Preview</p>
          <h1 id="scene-title">Loading backgrounds</h1>
        </div>
        <div class="preview-actions">
          <button id="prev-scene" type="button" title="Previous background">Prev</button>
          <select id="scene-select" title="Scene"></select>
          <button id="next-scene" type="button" title="Next background">Next</button>
        </div>
      </header>
      <div class="preview-status" id="preview-status">Loading</div>
    </section>
  </main>
`;

const style = document.createElement("style");
style.textContent = `
  :root {
    color-scheme: dark;
    font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    background: #050606;
    color: #f6efe4;
  }

  * { box-sizing: border-box; }
  html, body, #depth-preview { width: 100%; height: 100%; margin: 0; overflow: hidden; }
  button, select { font: inherit; color: inherit; }

  .preview-page {
    width: 100vw;
    height: 100vh;
    background: #050606;
  }

  .preview-stage {
    position: relative;
    width: 100%;
    height: 100%;
    overflow: hidden;
    background:
      linear-gradient(135deg, rgba(122, 40, 45, 0.18), transparent 36%),
      linear-gradient(315deg, rgba(30, 90, 86, 0.16), transparent 38%),
      #050606;
  }

  .preview-canvas {
    display: block;
    width: 100%;
    height: 100%;
  }

  .preview-topbar {
    position: absolute;
    z-index: 4;
    top: 0;
    left: 0;
    right: 0;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    padding: 18px 20px;
    background: linear-gradient(180deg, rgba(0, 0, 0, 0.78), transparent);
    pointer-events: none;
  }

  .preview-topbar p {
    margin: 0 0 4px;
    color: #d8b56d;
    font-size: 0.72rem;
    font-weight: 900;
    letter-spacing: 0;
    text-transform: uppercase;
  }

  .preview-topbar h1 {
    max-width: 42vw;
    margin: 0;
    overflow-wrap: anywhere;
    font-size: clamp(1.05rem, 2.4vw, 1.8rem);
    line-height: 1.05;
  }

  .preview-actions {
    display: grid;
    grid-template-columns: auto minmax(220px, 34vw) auto;
    gap: 8px;
    pointer-events: auto;
  }

  .preview-actions button,
  .preview-actions select {
    min-height: 38px;
    border: 1px solid rgba(255, 255, 255, 0.14);
    border-radius: 8px;
    background: rgba(8, 10, 12, 0.78);
    padding: 8px 11px;
    color: #f6efe4;
  }

  .preview-actions button:hover,
  .preview-actions select:hover {
    border-color: rgba(216, 181, 109, 0.58);
  }

  .preview-status {
    position: absolute;
    z-index: 4;
    left: 18px;
    bottom: 16px;
    max-width: min(680px, calc(100vw - 36px));
    border: 1px solid rgba(255, 255, 255, 0.11);
    border-radius: 8px;
    background: rgba(5, 6, 6, 0.72);
    padding: 8px 10px;
    color: rgba(246, 239, 228, 0.82);
    font-size: 0.83rem;
  }

  @media (max-width: 760px) {
    .preview-topbar {
      align-items: flex-start;
      flex-direction: column;
    }

    .preview-topbar h1 {
      max-width: calc(100vw - 40px);
    }

    .preview-actions {
      width: 100%;
      grid-template-columns: auto minmax(0, 1fr) auto;
    }
  }
`;
document.head.appendChild(style);

const canvas = requireElement<HTMLCanvasElement>(".preview-canvas");
const title = requireElement<HTMLHeadingElement>("#scene-title");
const status = requireElement<HTMLDivElement>("#preview-status");
const select = requireElement<HTMLSelectElement>("#scene-select");
const prevButton = requireElement<HTMLButtonElement>("#prev-scene");
const nextButton = requireElement<HTMLButtonElement>("#next-scene");

let backgrounds: PreviewBackground[] = [];
let activeIndex = 0;
let previewMode: PreviewMode = "splat";
let loadToken = 0;

const renderer = new THREE.WebGLRenderer({ canvas, antialias: true, powerPreference: "high-performance" });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x050606);
const camera = new THREE.PerspectiveCamera(48, 1, 0.01, 2000);
camera.position.set(0, 0, CAMERA_Z);
camera.lookAt(0, 0, 0);
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.target.set(0, 0, 0);
controls.update();

const state: PreviewState = { renderer, scene, camera, controls, content: null };
const clock = new THREE.Clock();

function setStatus(message: string) {
  status.textContent = message;
}

function disposeObject(object: THREE.Object3D | null) {
  if (!object) return;
  object.traverse((child) => {
    const mesh = child as THREE.Mesh;
    mesh.geometry?.dispose();
    const material = mesh.material;
    const disposeMaterial = (entry: THREE.Material) => {
      const mapped = entry as THREE.MeshBasicMaterial;
      mapped.map?.dispose();
      entry.dispose();
    };
    if (Array.isArray(material)) material.forEach(disposeMaterial);
    else if (material) disposeMaterial(material);
  });
}

function imageExists(url: string) {
  return fetch(url, { method: "HEAD", cache: "no-store" })
    .then((response) => response.ok && (response.headers.get("content-type")?.startsWith("image/") ?? false))
    .catch(() => false);
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

function makeFallbackDepth(image: HTMLImageElement) {
  const depthCanvas = document.createElement("canvas");
  depthCanvas.width = image.naturalWidth;
  depthCanvas.height = image.naturalHeight;
  const context = depthCanvas.getContext("2d", { willReadFrequently: true });
  if (!context) throw new Error("Depth canvas unavailable");
  context.drawImage(image, 0, 0);
  const frame = context.getImageData(0, 0, depthCanvas.width, depthCanvas.height);
  const pixels = frame.data;
  for (let index = 0; index < pixels.length; index += 4) {
    const luma = pixels[index] * 0.2126 + pixels[index + 1] * 0.7152 + pixels[index + 2] * 0.0722;
    const depth = Math.max(0, Math.min(255, 255 - luma * 0.86));
    pixels[index] = depth;
    pixels[index + 1] = depth;
    pixels[index + 2] = depth;
  }
  context.putImageData(frame, 0, 0);
  return depthCanvas;
}

function makeDepthSampler(source: HTMLImageElement | HTMLCanvasElement) {
  const depthCanvas = document.createElement("canvas");
  depthCanvas.width = source instanceof HTMLImageElement ? source.naturalWidth : source.width;
  depthCanvas.height = source instanceof HTMLImageElement ? source.naturalHeight : source.height;
  const context = depthCanvas.getContext("2d", { willReadFrequently: true });
  if (!context) throw new Error("Depth sampling unavailable");
  context.drawImage(source, 0, 0);
  const data = context.getImageData(0, 0, depthCanvas.width, depthCanvas.height).data;
  return (u: number, v: number) => {
    const x = Math.max(0, Math.min(depthCanvas.width - 1, Math.round(u * (depthCanvas.width - 1))));
    const y = Math.max(0, Math.min(depthCanvas.height - 1, Math.round(v * (depthCanvas.height - 1))));
    return data[(y * depthCanvas.width + x) * 4] / 255;
  };
}

function fitPlaneToViewport(mesh: THREE.Mesh<THREE.PlaneGeometry, THREE.Material>) {
  const baseWidth = Number(mesh.userData.baseWidth) || 1;
  const baseHeight = Number(mesh.userData.baseHeight) || 1;
  const visibleHeight = 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2) * CAMERA_Z;
  const visibleWidth = visibleHeight * camera.aspect;
  const scale = Math.min((visibleWidth * 0.86) / baseWidth, (visibleHeight * 0.86) / baseHeight);
  mesh.scale.set(scale, scale, scale);
}

function fitObjectToView(object: THREE.Object3D) {
  const box = new THREE.Box3().setFromObject(object);
  if (box.isEmpty()) return;
  const center = box.getCenter(new THREE.Vector3());
  const size = box.getSize(new THREE.Vector3());
  object.position.sub(center);
  const radius = Math.max(size.x, size.y, size.z) * 0.5 || 1;
  const distance = radius / Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2);
  camera.position.set(0, 0, Math.max(2.2, distance * 1.3));
  controls.target.set(0, 0, 0);
  controls.update();
}

async function makePlate(background: PreviewBackground, mode: "flat" | "depth") {
  const colorImage = await loadImage(background.sourcePlate.url);
  const depthReady = await imageExists(background.depthUrl);
  const depthImage = depthReady ? await loadImage(background.depthUrl) : makeFallbackDepth(colorImage);
  const colorTexture = new THREE.Texture(colorImage);
  colorTexture.colorSpace = THREE.SRGBColorSpace;
  colorTexture.needsUpdate = true;

  const aspect = colorImage.naturalWidth / colorImage.naturalHeight;
  const geometry = new THREE.PlaneGeometry(aspect, 1, 168, 96);
  const positions = geometry.attributes.position;
  const sampleDepth = makeDepthSampler(depthImage);
  for (let index = 0; index < positions.count; index += 1) {
    const u = geometry.attributes.uv.getX(index);
    const v = geometry.attributes.uv.getY(index);
    positions.setZ(index, mode === "depth" ? (sampleDepth(u, 1 - v) - 0.5) * DEPTH_STRENGTH : 0);
  }
  positions.needsUpdate = true;
  geometry.computeVertexNormals();

  const material = new THREE.MeshBasicMaterial({
    map: colorTexture,
    side: THREE.DoubleSide,
  });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.userData.baseWidth = aspect;
  mesh.userData.baseHeight = 1;
  fitPlaneToViewport(mesh);
  setStatus(`${MODE_LABELS[mode]}: ${depthReady ? "generated depth" : "fallback luminance depth"} | ${colorImage.naturalWidth}x${colorImage.naturalHeight}`);
  return mesh;
}

async function makeSplat(background: PreviewBackground) {
  const splat = background.asset?.triposplat ?? background.asset?.splat;
  if (!splat) throw new Error("No completed splat for this background yet");
  const points = await loadSphericalSplatPly(splat.url);
  points.name = "field-splat-preview";
  fitObjectToView(points);
  setStatus(`${splat.variant}: ${(splat.bytes / (1024 * 1024)).toFixed(1)} MB | ${splat.vertices.toLocaleString()} points`);
  return points;
}

function updateControls() {
  const background = backgrounds[activeIndex];
  title.textContent = background ? `${background.index}. ${background.mapName}` : "No splats";
  select.value = background?.id ?? "";
}

async function showScene(index: number) {
  if (!backgrounds.length) return;
  activeIndex = (index + backgrounds.length) % backgrounds.length;
  previewMode = "splat";
  const background = backgrounds[activeIndex];
  const token = ++loadToken;
  updateControls();
  setStatus(`Loading ${MODE_LABELS[previewMode]}`);
  window.location.hash = `#/scene/${background.id}`;

  const oldContent = state.content;
  if (oldContent) {
    oldContent.parent?.remove(oldContent);
    disposeObject(oldContent);
    state.content = null;
  }

  try {
    const content = previewMode === "splat" ? await makeSplat(background) : await makePlate(background, previewMode);
    if (token !== loadToken) {
      disposeObject(content);
      return;
    }
    state.scene.add(content);
    state.content = content;
  } catch (error) {
    if (token !== loadToken) return;
    setStatus(error instanceof Error ? error.message : "Preview failed");
  }
}

function toBackgrounds(manifest: AssetManifest): PreviewBackground[] {
  const assetsById = new Map(manifest.assets.map((asset) => [asset.id, asset]));
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

  return base.map((background) => {
    const asset = assetsById.get(background.generatedAssetId ?? background.id);
    return {
      ...background,
      asset,
      depthUrl: `/assets/depth/${background.id}/depth.png`,
    };
  }).filter((background) => Boolean(background.asset?.triposplat ?? background.asset?.splat));
}

function renderOptions() {
  select.innerHTML = backgrounds
    .map((background) => {
      const splatMark = background.asset?.triposplat ? " TS" : background.asset?.splat ? " GS" : "";
      return `<option value="${background.id}">${background.index}. ${background.id}${splatMark}</option>`;
    })
    .join("");
}

function resize() {
  const width = root.clientWidth;
  const height = root.clientHeight;
  renderer.setSize(width, height, false);
  camera.aspect = width / height;
  camera.updateProjectionMatrix();
  if (state.content instanceof THREE.Mesh && state.content.geometry instanceof THREE.PlaneGeometry) {
    fitPlaneToViewport(state.content);
  }
}

prevButton.addEventListener("click", () => void showScene(activeIndex - 1));
nextButton.addEventListener("click", () => void showScene(activeIndex + 1));
select.addEventListener("change", () => {
  const index = backgrounds.findIndex((background) => background.id === select.value);
  if (index >= 0) void showScene(index);
});
window.addEventListener("resize", resize);
resize();

renderer.setAnimationLoop(() => {
  clock.getDelta();
  controls.update();
  renderer.render(scene, camera);
});

fetch("/assets/index.json", { cache: "no-store" })
  .then((response) => {
    if (!response.ok) throw new Error(`Manifest failed: ${response.status}`);
    return response.json() as Promise<AssetManifest>;
  })
  .then((manifest) => {
    backgrounds = toBackgrounds(manifest);
    renderOptions();
    const routeId = window.location.hash.replace(/^#\/scene\//, "");
    const routeIndex = backgrounds.findIndex((background) => background.id === routeId || background.generatedAssetId === routeId);
    void showScene(routeIndex >= 0 ? routeIndex : 0);
  })
  .catch((error: unknown) => {
    title.textContent = error instanceof Error ? error.message : "Unable to load preview";
    setStatus("Preview unavailable");
  });
