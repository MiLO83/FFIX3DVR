import { Glasses, Loader2, RotateCcw } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { loadSphericalSplatPly } from "./ply";
import type { RenderMode, SceneAsset } from "./types";
import { isImmersiveVrSupported, startImmersiveVr } from "./webxr";

type SceneViewerProps = {
  asset: SceneAsset;
  mode: RenderMode;
  contrast: number;
  maskSourcePlate: boolean;
};

type ThreeState = {
  renderer: THREE.WebGLRenderer;
  scene: THREE.Scene;
  camera: THREE.PerspectiveCamera;
  controls: OrbitControls;
};

function disposeObject(object: THREE.Object3D | null) {
  if (!object) return;
  object.traverse((child) => {
    const mesh = child as THREE.Mesh;
    if (mesh.geometry) mesh.geometry.dispose();
    const material = mesh.material;
    if (Array.isArray(material)) material.forEach((entry) => entry.dispose());
    else if (material) material.dispose();
  });
}

function updateErpPlaneScale(camera: THREE.PerspectiveCamera, plane: THREE.Object3D) {
  const height = 2 * Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2);
  plane.scale.set(height * camera.aspect, height, 1);
}

function makeProjectionUniforms(asset: SceneAsset) {
  const projection = asset.sourceProjection;
  if (!projection) {
    return {
      hasSourceProjection: { value: false },
      sourceForward: { value: new THREE.Vector3(0, 0, 1) },
      sourceRight: { value: new THREE.Vector3(1, 0, 0) },
      sourceUp: { value: new THREE.Vector3(0, 1, 0) },
      sourceTanFov: { value: new THREE.Vector2(1, 1) },
    };
  }

  const yaw = THREE.MathUtils.degToRad(projection.yawDeg);
  const pitch = THREE.MathUtils.degToRad(projection.pitchDeg);
  const forward = new THREE.Vector3(Math.cos(pitch) * Math.sin(yaw), Math.sin(pitch), Math.cos(pitch) * Math.cos(yaw));
  const right = new THREE.Vector3(Math.cos(yaw), 0, -Math.sin(yaw));
  const up = new THREE.Vector3().crossVectors(forward, right);

  return {
    hasSourceProjection: { value: true },
    sourceForward: { value: forward },
    sourceRight: { value: right },
    sourceUp: { value: up },
    sourceTanFov: {
      value: new THREE.Vector2(
        Math.tan(THREE.MathUtils.degToRad(projection.hFovDeg) / 2),
        Math.tan(THREE.MathUtils.degToRad(projection.vFovDeg) / 2),
      ),
    },
  };
}

function makeEquirectMaterial(texture: THREE.Texture, asset: SceneAsset, contrast: number, maskSourcePlate: boolean) {
  return new THREE.ShaderMaterial({
    uniforms: {
      panorama: { value: texture },
      contrast: { value: contrast },
      maskSourcePlate: { value: maskSourcePlate },
      ...makeProjectionUniforms(asset),
    },
    vertexShader: `
      varying vec3 vWorldPosition;

      void main() {
        vec4 worldPosition = modelMatrix * vec4(position, 1.0);
        vWorldPosition = worldPosition.xyz;
        gl_Position = projectionMatrix * viewMatrix * worldPosition;
      }
    `,
    fragmentShader: `
      uniform sampler2D panorama;
      uniform float contrast;
      uniform bool maskSourcePlate;
      uniform bool hasSourceProjection;
      uniform vec3 sourceForward;
      uniform vec3 sourceRight;
      uniform vec3 sourceUp;
      uniform vec2 sourceTanFov;
      varying vec3 vWorldPosition;

      const float PI = 3.1415926535897932384626433832795;

      bool insideSourceProjection(vec3 direction) {
        if (!hasSourceProjection) return false;
        float z = dot(direction, sourceForward);
        if (z <= 0.0) return false;
        float ndcX = dot(direction, sourceRight) / (z * sourceTanFov.x);
        float ndcY = dot(direction, sourceUp) / (z * sourceTanFov.y);
        return abs(ndcX) <= 1.0 && abs(ndcY) <= 1.0;
      }

      void main() {
        vec3 direction = normalize(vWorldPosition);
        float u = atan(direction.x, direction.z) / (2.0 * PI) + 0.5;
        float v = 0.5 - asin(clamp(direction.y, -1.0, 1.0)) / PI;
        vec4 color = texture2D(panorama, vec2(u, v));
        bool sourceArea = insideSourceProjection(direction);

        if (!sourceArea) {
          color.rgb = clamp(vec3(0.5) + (color.rgb - vec3(0.5)) * contrast, 0.0, 1.0);
        }

        if (maskSourcePlate && sourceArea) {
          float checker = step(0.5, fract(floor(u * 64.0) * 0.5 + floor(v * 32.0) * 0.5));
          color.rgb = mix(vec3(0.025, 0.028, 0.03), vec3(0.11, 0.1, 0.085), checker);
        }

        gl_FragColor = color;
      }
    `,
    side: THREE.DoubleSide,
    depthWrite: false,
  });
}

export default function SceneViewer({ asset, mode, contrast, maskSourcePlate }: SceneViewerProps) {
  const mountRef = useRef<HTMLDivElement | null>(null);
  const stateRef = useRef<ThreeState | null>(null);
  const contentRef = useRef<THREE.Object3D | null>(null);
  const erpMaterialRef = useRef<THREE.ShaderMaterial | null>(null);
  const textureRef = useRef<THREE.Texture | null>(null);
  const pressedKeysRef = useRef(new Set<string>());
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState("Loading scene");
  const [vrSupported, setVrSupported] = useState(false);
  const [isPresenting, setIsPresenting] = useState(false);

  useEffect(() => {
    isImmersiveVrSupported().then(setVrSupported);
  }, []);

  useEffect(() => {
    const mount = mountRef.current;
    if (!mount) return;

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, powerPreference: "high-performance" });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setSize(mount.clientWidth, mount.clientHeight);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.xr.enabled = true;
    mount.appendChild(renderer.domElement);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x050506);

    const camera = new THREE.PerspectiveCamera(150, mount.clientWidth / mount.clientHeight, 0.01, 1000);
    camera.position.set(0, 0, 0);
    camera.lookAt(0, 0, 1);
    scene.add(camera);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.enablePan = false;
    controls.rotateSpeed = -0.35;
    controls.target.set(0, 0, 1);

    const state = { renderer, scene, camera, controls };
    controls.update();
    stateRef.current = state;
    const clock = new THREE.Clock();
    const walkForward = new THREE.Vector3();
    const walkRight = new THREE.Vector3();
    const walkDelta = new THREE.Vector3();

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.target instanceof HTMLInputElement) return;
      pressedKeysRef.current.add(event.code);
    };
    const onKeyUp = (event: KeyboardEvent) => {
      pressedKeysRef.current.delete(event.code);
    };
    const onBlur = () => pressedKeysRef.current.clear();

    window.addEventListener("keydown", onKeyDown);
    window.addEventListener("keyup", onKeyUp);
    window.addEventListener("blur", onBlur);

    const resizeObserver = new ResizeObserver(() => {
      const width = mount.clientWidth;
      const height = mount.clientHeight;
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
      renderer.setSize(width, height);
      if (contentRef.current?.userData.erpPlane) updateErpPlaneScale(camera, contentRef.current);
    });
    resizeObserver.observe(mount);

    renderer.setAnimationLoop(() => {
      const deltaSeconds = Math.min(clock.getDelta(), 0.05);
      controls.enabled = !renderer.xr.isPresenting;
      if (!renderer.xr.isPresenting) {
        walkDelta.set(0, 0, 0);
        camera.getWorldDirection(walkForward);
        walkForward.y = 0;
        if (walkForward.lengthSq() > 0) walkForward.normalize();
        walkRight.crossVectors(camera.up, walkForward);
        if (walkRight.lengthSq() > 0) walkRight.normalize();

        const keys = pressedKeysRef.current;
        if (keys.has("KeyW") || keys.has("ArrowUp")) walkDelta.add(walkForward);
        if (keys.has("KeyS") || keys.has("ArrowDown")) walkDelta.sub(walkForward);
        if (keys.has("KeyD") || keys.has("ArrowRight")) walkDelta.add(walkRight);
        if (keys.has("KeyA") || keys.has("ArrowLeft")) walkDelta.sub(walkRight);
        if (keys.has("KeyE") || keys.has("Space")) walkDelta.y += 1;
        if (keys.has("KeyQ") || keys.has("ControlLeft") || keys.has("ControlRight")) walkDelta.y -= 1;

        if (walkDelta.lengthSq() > 0) {
          const speed = keys.has("ShiftLeft") || keys.has("ShiftRight") ? 4.0 : 1.8;
          walkDelta.normalize().multiplyScalar(speed * deltaSeconds);
          camera.position.add(walkDelta);
          controls.target.add(walkDelta);
        }
        controls.update();
      }
      renderer.render(scene, camera);
    });

    return () => {
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("keyup", onKeyUp);
      window.removeEventListener("blur", onBlur);
      resizeObserver.disconnect();
      renderer.setAnimationLoop(null);
      disposeObject(contentRef.current);
      erpMaterialRef.current = null;
      textureRef.current?.dispose();
      controls.dispose();
      renderer.dispose();
      mount.removeChild(renderer.domElement);
      stateRef.current = null;
    };
  }, []);

  useEffect(() => {
    const state = stateRef.current;
    if (!state) return;
    const three = state;

    let cancelled = false;
    setLoading(true);
    setMessage(mode === "erp" ? "Loading ERP panorama" : "Loading splat cloud");

    async function swapContent() {
      const current = contentRef.current;
      if (current) {
        current.parent?.remove(current);
        disposeObject(current);
        contentRef.current = null;
      }
      erpMaterialRef.current = null;
      textureRef.current?.dispose();
      textureRef.current = null;

      if (mode === "erp") {
        const texture = await new THREE.TextureLoader().loadAsync(asset.erp360.url);
        if (cancelled) {
          texture.dispose();
          return;
        }

        texture.colorSpace = THREE.SRGBColorSpace;
        texture.flipY = false;
        texture.needsUpdate = true;
        const geometry = new THREE.SphereGeometry(80, 96, 48);
        geometry.scale(-1, 1, 1);
        const material = makeEquirectMaterial(texture, asset, contrast, maskSourcePlate);
        const sphere = new THREE.Mesh(geometry, material);
        sphere.frustumCulled = false;
        three.scene.add(sphere);
        textureRef.current = texture;
        erpMaterialRef.current = material;
        contentRef.current = sphere;
      } else if (asset.splat) {
        const points = await loadSphericalSplatPly(asset.splat.url);
        if (cancelled) {
          disposeObject(points);
          return;
        }
        three.scene.add(points);
        contentRef.current = points;
      }

      if (!cancelled) setLoading(false);
    }

    swapContent().catch((error: unknown) => {
      if (cancelled) return;
      setMessage(error instanceof Error ? error.message : "Scene load failed");
      setLoading(false);
    });

    return () => {
      cancelled = true;
    };
  }, [asset, mode]);

  useEffect(() => {
    const material = erpMaterialRef.current;
    if (!material) return;
    material.uniforms.contrast.value = contrast;
    material.uniforms.maskSourcePlate.value = maskSourcePlate;
  }, [contrast, maskSourcePlate]);

  function resetView() {
    const state = stateRef.current;
    if (!state) return;
    state.camera.fov = 150;
    state.camera.updateProjectionMatrix();
    state.camera.position.set(0, 0, 0);
    state.controls.target.set(0, 0, 1);
    state.controls.update();
  }

  async function enterVr() {
    const state = stateRef.current;
    if (!state) return;
    setMessage("Starting VR");
    try {
      await startImmersiveVr(state.renderer, () => setIsPresenting(false));
      setIsPresenting(true);
    } catch (error: unknown) {
      setMessage(error instanceof Error ? error.message : "Unable to start VR");
    }
  }

  return (
    <div className="scene-viewer" ref={mountRef}>
      <div className="scene-controls">
        <button className="icon-button glass" onClick={resetView} title="Reset view" type="button">
          <RotateCcw aria-hidden="true" size={18} />
        </button>
        <button
          className="xr-button"
          disabled={!vrSupported || isPresenting}
          onClick={enterVr}
          title={vrSupported ? "Enter VR" : "WebXR VR is not available here"}
          type="button"
        >
          <Glasses aria-hidden="true" size={18} />
          VR
        </button>
      </div>
      {loading ? (
        <div className="loading-overlay">
          <Loader2 aria-hidden="true" size={24} />
          <span>{message}</span>
        </div>
      ) : null}
    </div>
  );
}
