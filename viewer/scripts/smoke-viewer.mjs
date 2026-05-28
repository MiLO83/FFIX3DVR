import { existsSync } from "node:fs";
import { mkdir } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const viewerRoot = path.resolve(__dirname, "..");
const edgePath = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
const outputDir = path.join(viewerRoot, ".logs");
const baseUrl = process.env.VIEWER_URL ?? "http://127.0.0.1:5173/";

if (!existsSync(edgePath)) {
  throw new Error(`Microsoft Edge was not found at ${edgePath}`);
}

await mkdir(outputDir, { recursive: true });

const browser = await chromium.launch({
  executablePath: edgePath,
  headless: true,
  args: ["--enable-webgl", "--ignore-gpu-blocklist"],
});

const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });

async function canvasSample() {
  return page.evaluate(async () => {
    await new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const canvas = document.querySelector("canvas");
    if (!canvas) return { hasCanvas: false, nonZeroPixels: 0, samplePixels: 0, clientWidth: 0, clientHeight: 0, width: 0, height: 0 };
    const gl = canvas.getContext("webgl2") ?? canvas.getContext("webgl");
    if (!gl) {
      return {
        hasCanvas: true,
        nonZeroPixels: 0,
        samplePixels: 0,
        clientWidth: canvas.clientWidth,
        clientHeight: canvas.clientHeight,
        width: canvas.width,
        height: canvas.height,
      };
    }
    const width = Math.min(96, canvas.width);
    const height = Math.min(96, canvas.height);
    const pixels = new Uint8Array(width * height * 4);
    gl.readPixels(0, 0, width, height, gl.RGBA, gl.UNSIGNED_BYTE, pixels);
    let nonZeroPixels = 0;
    for (let index = 0; index < pixels.length; index += 4) {
      if (pixels[index] || pixels[index + 1] || pixels[index + 2]) nonZeroPixels += 1;
    }
    return {
      hasCanvas: true,
      nonZeroPixels,
      samplePixels: width * height,
      clientWidth: canvas.clientWidth,
      clientHeight: canvas.clientHeight,
      width: canvas.width,
      height: canvas.height,
    };
  });
}

async function flatPreviewSample() {
  return page.evaluate(() => {
    const image = document.querySelector(".viewer-stage img");
    if (!image) return { hasImage: false };
    return {
      hasImage: true,
      complete: image.complete,
      naturalWidth: image.naturalWidth,
      naturalHeight: image.naturalHeight,
      clientWidth: image.clientWidth,
      clientHeight: image.clientHeight,
    };
  });
}

try {
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.waitForSelector(".scene-card", { timeout: 15000 });
  await page.waitForSelector(".loading-overlay", { state: "hidden", timeout: 20000 });

  const manifest = await page.evaluate(async () => {
    const response = await fetch("/assets/index.json");
    return response.json();
  });

  await page.getByRole("button", { name: "ERP" }).click();
  await page.waitForTimeout(500);
  const erpSample = await flatPreviewSample();
  await page.screenshot({ path: path.join(outputDir, "viewer-erp.png"), fullPage: false });

  await page.locator(".viewer-toolbar").getByRole("button", { name: "360" }).click();
  await page.waitForSelector(".loading-overlay", { state: "hidden", timeout: 30000 });
  const panoSample = await canvasSample();
  await page.screenshot({ path: path.join(outputDir, "viewer-360.png"), fullPage: false });

  await page.locator(".viewer-toolbar").getByRole("button", { name: "Splat" }).click();
  await page.waitForSelector(".loading-overlay", { state: "hidden", timeout: 30000 });
  const splatSample = await canvasSample();
  await page.screenshot({ path: path.join(outputDir, "viewer-splat.png"), fullPage: false });

  if (!erpSample.hasImage || erpSample.clientHeight < 100) {
    throw new Error(`ERP preview did not render at a usable size: ${JSON.stringify(erpSample)}`);
  }
  if (!panoSample.hasCanvas || panoSample.clientHeight < 100 || panoSample.nonZeroPixels === 0) {
    throw new Error(`360 canvas did not render visible pixels: ${JSON.stringify(panoSample)}`);
  }
  if (!splatSample.hasCanvas || splatSample.clientHeight < 100 || splatSample.nonZeroPixels === 0) {
    throw new Error(`Splat canvas did not render visible pixels: ${JSON.stringify(splatSample)}`);
  }

  console.log(
    JSON.stringify(
      {
        url: page.url(),
        title: await page.title(),
        totalGenerated: manifest.totalGenerated,
        firstScene: manifest.assets[0]?.mapName,
        erpSample,
        panoSample,
        splatSample,
        screenshots: {
          erp: path.join(outputDir, "viewer-erp.png"),
          pano: path.join(outputDir, "viewer-360.png"),
          splat: path.join(outputDir, "viewer-splat.png"),
        },
      },
      null,
      2,
    ),
  );
} finally {
  await browser.close();
}
