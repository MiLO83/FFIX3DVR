import * as THREE from "three";

const SH_C0 = 0.28209479177387814;

type PlyHeader = {
  vertexCount: number;
  properties: string[];
  headerBytes: number;
};

function clamp01(value: number) {
  return Math.max(0, Math.min(1, value));
}

function parseHeader(buffer: ArrayBuffer): PlyHeader {
  const bytes = new Uint8Array(buffer);
  const marker = new TextEncoder().encode("end_header\n");

  let headerEnd = -1;
  for (let i = 0; i <= bytes.length - marker.length; i += 1) {
    let matched = true;
    for (let j = 0; j < marker.length; j += 1) {
      if (bytes[i + j] !== marker[j]) {
        matched = false;
        break;
      }
    }
    if (matched) {
      headerEnd = i + marker.length;
      break;
    }
  }

  if (headerEnd < 0) throw new Error("PLY header is missing end_header");

  const headerText = new TextDecoder("ascii").decode(bytes.slice(0, headerEnd));
  const lines = headerText.split(/\r?\n/);
  const vertexCountLine = lines.find((line) => line.startsWith("element vertex "));
  if (!vertexCountLine) throw new Error("PLY header is missing vertex count");

  const vertexCount = Number(vertexCountLine.split(/\s+/)[2]);
  const properties = lines
    .filter((line) => line.startsWith("property "))
    .map((line) => line.trim().split(/\s+/).at(-1))
    .filter((property): property is string => Boolean(property));

  return { vertexCount, properties, headerBytes: headerEnd };
}

export async function loadSphericalSplatPly(url: string) {
  const response = await fetch(url);
  if (!response.ok) throw new Error(`PLY request failed: ${response.status}`);

  const buffer = await response.arrayBuffer();
  const header = parseHeader(buffer);
  const data = new DataView(buffer, header.headerBytes);
  const stride = header.properties.length * 4;

  const xIndex = header.properties.indexOf("x");
  const yIndex = header.properties.indexOf("y");
  const zIndex = header.properties.indexOf("z");
  const rIndex = header.properties.indexOf("f_dc_0");
  const gIndex = header.properties.indexOf("f_dc_1");
  const bIndex = header.properties.indexOf("f_dc_2");

  if ([xIndex, yIndex, zIndex, rIndex, gIndex, bIndex].some((index) => index < 0)) {
    throw new Error("PLY is missing expected 3DGS position/color properties");
  }

  const positions = new Float32Array(header.vertexCount * 3);
  const colors = new Float32Array(header.vertexCount * 3);

  for (let vertex = 0; vertex < header.vertexCount; vertex += 1) {
    const base = vertex * stride;
    const out = vertex * 3;

    positions[out] = data.getFloat32(base + xIndex * 4, true);
    positions[out + 1] = data.getFloat32(base + yIndex * 4, true);
    positions[out + 2] = data.getFloat32(base + zIndex * 4, true);

    colors[out] = clamp01(0.5 + SH_C0 * data.getFloat32(base + rIndex * 4, true));
    colors[out + 1] = clamp01(0.5 + SH_C0 * data.getFloat32(base + gIndex * 4, true));
    colors[out + 2] = clamp01(0.5 + SH_C0 * data.getFloat32(base + bIndex * 4, true));
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.BufferAttribute(positions, 3));
  geometry.setAttribute("color", new THREE.BufferAttribute(colors, 3));
  geometry.computeBoundingSphere();

  const material = new THREE.PointsMaterial({
    size: 0.055,
    sizeAttenuation: true,
    vertexColors: true,
    transparent: false,
    depthWrite: false,
  });

  const points = new THREE.Points(geometry, material);
  points.frustumCulled = false;
  return points;
}
