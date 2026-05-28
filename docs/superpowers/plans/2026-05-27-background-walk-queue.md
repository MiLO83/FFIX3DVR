# Background Walk Queue Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a local queue loop that lets the viewer walk through FFIX backgrounds while ERP and Gaussian splat artifacts are generated and previewed.

**Architecture:** The Vite dev server owns a tiny local-only process API and launches a Python runner. The runner updates `artifacts/all-fields/erp_queue.json` as the source of truth, while the React viewer polls status and exposes per-background/current/selected queue controls. SPAG-4D is cloned under `.external/SPAG4d` and represented as the preferred high-quality backend, with the current spherical PLY converter retained as the fast preview path.

**Tech Stack:** React, Vite middleware, Node `child_process`, Python queue scripts, ComfyUI HTTP API, SPAG-4D checkout, existing Three.js/WebXR viewer.

---

### Task 1: Local Pipeline Runner

**Files:**
- Create: `tools/run_background_pipeline.py`
- Reuse: `tools/run_comfy_erp_batch.py`
- Reuse: `tools/batch_erp_to_spherical_splat.py`
- Read/write: `artifacts/all-fields/erp_queue.json`

- [ ] **Step 1: Add a runner that accepts background ids**

Create `tools/run_background_pipeline.py` with an argparse CLI:

```python
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"
LOG_DIR = ROOT / "artifacts" / "all-fields" / "logs"

def now() -> str:
    return datetime.now(timezone.utc).isoformat()

def load_queue() -> dict:
    return json.loads(QUEUE.read_text(encoding="utf-8"))

def save_queue(queue: dict) -> None:
    QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")

def job_id(job: dict) -> str:
    return str(job["mapName"]).lower()

def select_jobs(queue: dict, ids: list[str], limit: int) -> list[dict]:
    requested = {entry.lower() for entry in ids}
    jobs = [job for job in queue["jobs"] if not requested or job_id(job) in requested]
    return jobs[:limit] if limit else jobs

def mark(job: dict, status: str, stage: str, message: str = "") -> None:
    job["status"] = status
    job["pipelineStage"] = stage
    job["pipelineMessage"] = message
    job["updatedAt"] = now()

def run(cmd: list[str]) -> None:
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    subprocess.run(cmd, cwd=ROOT, check=True)

def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--ids", nargs="*", default=[])
    parser.add_argument("--limit", type=int, default=1)
    parser.add_argument("--backend", choices=["preview", "spag4d"], default="preview")
    args = parser.parse_args()

    queue = load_queue()
    selected = select_jobs(queue, args.ids, args.limit)
    for job in selected:
        mark(job, "running", "erp", "Generating ERP in ComfyUI")
    save_queue(queue)

    run([sys.executable, "tools/run_comfy_erp_batch.py"])

    queue = load_queue()
    selected_ids = {job_id(job) for job in selected}
    for job in queue["jobs"]:
        if job_id(job) in selected_ids:
            mark(job, "running", "splat_preview", "Generating preview spherical splat")
    save_queue(queue)

    run([sys.executable, "tools/batch_erp_to_spherical_splat.py"])

    queue = load_queue()
    for job in queue["jobs"]:
        if job_id(job) in selected_ids:
            mark(job, "complete", "done", f"Done with {args.backend} backend")
            job["splatBackend"] = args.backend
    save_queue(queue)

if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Smoke runner with one already-generated background**

Run:

```powershell
python tools/run_background_pipeline.py --ids fbg_n00_tshp_map001_th_cgr_0 --limit 1 --backend preview
```

Expected: the command either completes, or fails only because ComfyUI is unavailable; `erp_queue.json` still records the current pipeline stage/message.

---

### Task 2: Vite Local Process API

**Files:**
- Modify: `viewer/vite.config.ts`

- [ ] **Step 1: Add `/api/pipeline/status`**

The endpoint reads `artifacts/all-fields/erp_queue.json` and returns `total`, `counts`, `active`, and `jobs` fields. Status is read-only and must work even when no process is running.

- [ ] **Step 2: Add `/api/pipeline/start` and `/api/pipeline/stop`**

`start` spawns:

```powershell
python tools/run_background_pipeline.py --ids <ids> --limit <limit> --backend <backend>
```

`stop` kills the active child process. Only one active child is allowed.

- [ ] **Step 3: Validate API manually**

Run:

```powershell
Invoke-RestMethod http://127.0.0.1:5173/api/pipeline/status
```

Expected: JSON with queue counts and no stack trace.

---

### Task 3: Viewer Queue Controls

**Files:**
- Modify: `viewer/src/types.ts`
- Modify: `viewer/src/App.tsx`
- Modify: `viewer/src/styles.css`

- [ ] **Step 1: Add pipeline types**

Add `PipelineStatus`, `PipelineJobSummary`, and request/response types for the local API.

- [ ] **Step 2: Poll status in `App.tsx`**

Fetch `/api/pipeline/status` every 2 seconds. If unavailable, show a static-site note instead of failing the viewer.

- [ ] **Step 3: Add controls**

Add buttons for `Current`, `Selected`, `Next 5`, `Stop`, and a backend selector with `Preview` and `SPAG-4D`. Show current stage and queue counts.

- [ ] **Step 4: Verify UI**

Run:

```powershell
npm run build
npm run smoke
```

Expected: both pass, and the viewer still renders ERP, 360, and Splat.

---

### Task 4: SPAG-4D Integration Preparation

**Files:**
- Existing: `.external/SPAG4d`
- Create: `docs/spag4d-integration.md`

- [ ] **Step 1: Record the chosen backend**

Document that SPAG-4D is the preferred pano-to-splat backend, with preview mode retained for instant review.

- [ ] **Step 2: Record adapter contract**

The adapter input is `field_erp.png`; output is `field_spag4d.ply` or `.splat`; logs go under `artifacts/all-fields/logs/<scene>_spag4d.log`.

- [ ] **Step 3: Keep runtime safe**

The UI may offer `SPAG-4D`, but the runner must report `spag4d_env_missing` until the Python/CUDA environment is installed and verified.

---

### Self-Review

Spec coverage: queue walking, realtime preview status, splat generation path, and SPAG-4D backend preparation are covered. Full GSFix3D occlusion refinement is intentionally deferred behind the SPAG-4D adapter because it depends on the high-quality backend output.

Placeholder scan: no placeholder steps remain.

Type consistency: `pipelineStage`, `pipelineMessage`, `PipelineStatus`, `backend`, `ids`, and `limit` are consistent across runner, API, and UI.
