import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { mkdir, readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(__dirname, "..");
const publicCorrectionsPath = path.resolve(__dirname, "public", "assets", "color-corrections.json");
const artifactCorrectionsPath = path.resolve(repoRoot, "artifacts", "all-fields", "color_corrections.json");
const queuePath = path.resolve(repoRoot, "artifacts", "all-fields", "erp_queue.json");
const pipelineLogPath = path.resolve(repoRoot, "artifacts", "all-fields", "logs", "background_pipeline_api.log");

let pipelineProcess: ChildProcessWithoutNullStreams | null = null;
let pipelineStartedAt = "";
let pipelineLastExit: null | { code: number | null; signal: NodeJS.Signals | null; at: string } = null;

async function readCorrections() {
  for (const filePath of [artifactCorrectionsPath, publicCorrectionsPath]) {
    try {
      return JSON.parse(await readFile(filePath, "utf8")) as Record<string, unknown>;
    } catch {
      // Missing correction files are normal before the first save.
    }
  }
  return {};
}

function readRequestBody(request: import("node:http").IncomingMessage) {
  return new Promise<string>((resolve, reject) => {
    const chunks: Buffer[] = [];
    request.on("data", (chunk) => chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)));
    request.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    request.on("error", reject);
  });
}

function sendJson(response: import("node:http").ServerResponse, status: number, payload: unknown) {
  response.statusCode = status;
  response.setHeader("Content-Type", "application/json");
  response.end(`${JSON.stringify(payload)}\n`);
}

async function readJsonBody<T>(request: import("node:http").IncomingMessage): Promise<T> {
  return JSON.parse(await readRequestBody(request)) as T;
}

async function appendPipelineLog(message: string) {
  await mkdir(path.dirname(pipelineLogPath), { recursive: true });
  await writeFile(pipelineLogPath, message, { flag: "a" });
}

async function readQueue() {
  return JSON.parse(await readFile(queuePath, "utf8")) as { totalJobs?: number; jobs?: Array<Record<string, unknown>> };
}

async function writeQueue(queue: { totalJobs?: number; jobs?: Array<Record<string, unknown>> }) {
  await writeFile(queuePath, `${JSON.stringify(queue, null, 2)}\n`);
}

async function markPipelineInterrupted(ids: string[], signal: NodeJS.Signals | null) {
  const requested = new Set(ids);
  const queue = await readQueue();
  let changed = false;
  for (const job of queue.jobs ?? []) {
    const id = String(job.mapName ?? "").toLowerCase();
    if (job.status !== "running" || (requested.size && !requested.has(id))) continue;
    job.status = "stopped";
    job.pipelineStage = "stopped";
    job.pipelineMessage = signal ? `Pipeline stopped by ${signal}` : "Pipeline stopped";
    job.updatedAt = new Date().toISOString();
    changed = true;
  }
  if (changed) await writeQueue(queue);
}

async function pipelineStatus() {
  const queue = await readQueue();
  const jobs = queue.jobs ?? [];
  const counts = jobs.reduce<Record<string, number>>((acc, job) => {
    const status = String(job.status ?? "unknown");
    acc[status] = (acc[status] ?? 0) + 1;
    return acc;
  }, {});
  const activeJobs = jobs
    .filter((job) => job.status === "running")
    .map((job) => ({
      id: String(job.mapName ?? "").toLowerCase(),
      mapName: job.mapName,
      status: job.status,
      stage: job.pipelineStage,
      message: job.pipelineMessage,
      updatedAt: job.updatedAt,
    }));
  return {
    available: true,
    running: pipelineProcess !== null,
    startedAt: pipelineStartedAt || null,
    lastExit: pipelineLastExit,
    total: queue.totalJobs ?? jobs.length,
    counts,
    active: activeJobs,
    jobs: jobs.map((job) => ({
      id: String(job.mapName ?? "").toLowerCase(),
      mapName: job.mapName,
      status: job.status,
      stage: job.pipelineStage,
      message: job.pipelineMessage,
      updatedAt: job.updatedAt,
      backend: job.splatBackend,
      preferredBackend: job.preferredSplatBackend,
    })),
  };
}

function pipelineApi() {
  return {
    name: "ffix3dvr-pipeline-api",
    configureServer(server: import("vite").ViteDevServer) {
      server.middlewares.use("/api/pipeline", async (request, response) => {
        try {
          const route = request.url?.replace(/\?.*$/, "") ?? "";
          if (request.method === "GET" && route === "/status") {
            sendJson(response, 200, await pipelineStatus());
            return;
          }

          if (request.method === "POST" && route === "/stop") {
            if (pipelineProcess) {
              pipelineProcess.kill();
              pipelineProcess = null;
            }
            sendJson(response, 200, await pipelineStatus());
            return;
          }

          if (request.method === "POST" && route === "/start") {
            if (pipelineProcess) {
              sendJson(response, 409, { error: "pipeline_already_running", status: await pipelineStatus() });
              return;
            }

            const body = await readJsonBody<{ ids?: string[]; limit?: number; backend?: string }>(request);
            const ids = (body.ids ?? []).filter((id) => /^[a-z0-9_]+$/.test(id));
            const limit = Math.max(1, Math.min(25, Number(body.limit ?? (ids.length || 1))));
            const backend = body.backend === "spag4d" ? "spag4d" : "preview";
            const args = ["tools/run_background_pipeline.py", "--limit", String(limit), "--backend", backend];
            if (ids.length) args.push("--ids", ...ids);

            pipelineStartedAt = new Date().toISOString();
            pipelineLastExit = null;
            pipelineProcess = spawn("python", args, { cwd: repoRoot });
            const launchedIds = ids;
            await appendPipelineLog(`\n[${pipelineStartedAt}] python ${args.join(" ")}\n`);
            pipelineProcess.stdout.on("data", (chunk) => void appendPipelineLog(String(chunk)));
            pipelineProcess.stderr.on("data", (chunk) => void appendPipelineLog(String(chunk)));
            pipelineProcess.on("exit", (code, signal) => {
              pipelineLastExit = { code, signal, at: new Date().toISOString() };
              pipelineProcess = null;
              if (signal || code !== 0) void markPipelineInterrupted(launchedIds, signal);
              void appendPipelineLog(`[exit] code=${code} signal=${signal}\n`);
            });

            sendJson(response, 202, await pipelineStatus());
            return;
          }

          sendJson(response, 404, { error: "not_found" });
        } catch (error) {
          sendJson(response, 500, { error: error instanceof Error ? error.message : "pipeline_api_failed" });
        }
      });
    },
  };
}

function colorCorrectionApi() {
  return {
    name: "ffix3dvr-color-correction-api",
    configureServer(server: import("vite").ViteDevServer) {
      server.middlewares.use("/api/color-corrections", async (request, response) => {
        try {
          if (request.method === "GET") {
            sendJson(response, 200, await readCorrections());
            return;
          }

          if (request.method !== "POST") {
            sendJson(response, 405, { error: "method_not_allowed" });
            return;
          }

          const body = JSON.parse(await readRequestBody(request)) as {
            sceneId?: string;
            contrast?: number;
            brightness?: number;
            saturation?: number;
            match?: number;
            imageDataUrl?: string;
          };
          const sceneId = body.sceneId?.match(/^[a-z0-9_]+$/) ? body.sceneId : "";
          const contrast = Number(body.contrast);
          const brightness = Number(body.brightness ?? 1);
          const saturation = Number(body.saturation ?? 1);
          const match = Number(body.match ?? 0);
          const imageDataUrl = body.imageDataUrl ?? "";
          const encoded = imageDataUrl.match(/^data:image\/png;base64,(.+)$/)?.[1];
          if (
            !sceneId ||
            !Number.isFinite(contrast) ||
            !Number.isFinite(brightness) ||
            !Number.isFinite(saturation) ||
            !Number.isFinite(match) ||
            !encoded
          ) {
            sendJson(response, 400, { error: "invalid_payload" });
            return;
          }

          const png = Buffer.from(encoded, "base64");
          const artifactSceneDir = path.resolve(repoRoot, "artifacts", "all-fields", "erp_tuned", sceneId);
          const publicSceneDir = path.resolve(__dirname, "public", "assets", "maps", sceneId);
          await mkdir(artifactSceneDir, { recursive: true });
          await mkdir(publicSceneDir, { recursive: true });
          await writeFile(path.join(artifactSceneDir, "field_erp.png"), png);
          await writeFile(path.join(publicSceneDir, "field_erp_tuned.png"), png);

          const correction = {
            contrast,
            brightness,
            saturation,
            match,
            url: `/assets/maps/${sceneId}/field_erp_tuned.png`,
            artifactPath: `artifacts/all-fields/erp_tuned/${sceneId}/field_erp.png`,
            savedAt: new Date().toISOString(),
          };
          const corrections = { ...(await readCorrections()), [sceneId]: correction };
          await mkdir(path.dirname(artifactCorrectionsPath), { recursive: true });
          await mkdir(path.dirname(publicCorrectionsPath), { recursive: true });
          await writeFile(artifactCorrectionsPath, `${JSON.stringify(corrections, null, 2)}\n`);
          await writeFile(publicCorrectionsPath, `${JSON.stringify(corrections, null, 2)}\n`);
          sendJson(response, 200, correction);
        } catch (error) {
          sendJson(response, 500, { error: error instanceof Error ? error.message : "save_failed" });
        }
      });
    },
  };
}

export default defineConfig({
  plugins: [react(), colorCorrectionApi(), pipelineApi()],
  server: {
    allowedHosts: true,
  },
  build: {
    rollupOptions: {
      input: {
        app: path.resolve(__dirname, "index.html"),
        depthGallery: path.resolve(__dirname, "depth-gallery.html"),
      },
    },
    target: "es2022",
  },
});
