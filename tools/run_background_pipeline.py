from __future__ import annotations

import argparse
import json
import subprocess
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path

from batch_erp_to_spherical_splat import write_spherical_splat
from run_comfy_erp_batch import run_job, server_ready


ROOT = Path(__file__).resolve().parents[1]
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"
SPAG4D = ROOT / ".external" / "SPAG4d"


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def load_queue() -> dict:
    return json.loads(QUEUE.read_text(encoding="utf-8"))


def save_queue(queue: dict) -> None:
    QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")


def scene_id(job: dict) -> str:
    return str(job["mapName"]).lower()


def rel(path: Path) -> str:
    return str(path.relative_to(ROOT)).replace("\\", "/")


def mark(job: dict, status: str, stage: str, message: str = "") -> None:
    job["status"] = status
    job["pipelineStage"] = stage
    job["pipelineMessage"] = message
    job["updatedAt"] = now()


def select_jobs(queue: dict, ids: list[str], limit: int) -> list[dict]:
    requested = {entry.lower() for entry in ids}
    jobs = []
    for job in queue["jobs"]:
        if requested and scene_id(job) not in requested:
            continue
        jobs.append(job)
    if not requested:
        jobs = [job for job in jobs if job.get("status") in {"queued", "error", "erp_done", "splat_done"}]
    return jobs[:limit] if limit > 0 else jobs


def ensure_erp(job: dict) -> str:
    erp_rel = job.get("actualErpOutput") or job.get("erpOutput")
    erp_path = ROOT / erp_rel
    if erp_path.exists():
        job["actualErpOutput"] = erp_rel
        return erp_rel

    if not server_ready():
        raise RuntimeError("ComfyUI is not reachable at http://127.0.0.1:8188 and no ERP exists yet")

    result = run_job(job)
    if not result.get("ok"):
      raise RuntimeError(json.dumps(result.get("error", "ERP generation failed")))
    job["actualErpOutput"] = result["erp"]
    return result["erp"]


def ensure_preview_splat(job: dict, erp_rel: str) -> int:
    erp_path = ROOT / erp_rel
    splat_rel = job.get("splatOutput")
    if not splat_rel:
        splat_rel = f"artifacts/all-fields/splat/{scene_id(job)}/field_spherical_env.ply"
        job["splatOutput"] = splat_rel
    splat_path = ROOT / splat_rel
    return write_spherical_splat(erp_path, splat_path)


def complete_job(job: dict, erp_rel: str, vertices: int, backend: str) -> None:
    splat_rel = job["splatOutput"]
    job["runtimeAssets"] = {
        "lowPower": {
            "type": "erp360",
            "path": erp_rel,
            "projection": "equirectangular",
        },
        "highPower": {
            "type": "gaussian_splat_ply",
            "path": splat_rel,
            "variant": "spherical_env_preview",
            "vertices": vertices,
        },
    }
    job["splatBackend"] = "preview"
    if backend == "spag4d":
        job["preferredSplatBackend"] = "spag4d"
        job["spag4d"] = {
            "repo": rel(SPAG4D),
            "status": "env_missing" if SPAG4D.exists() else "repo_missing",
            "message": "Preview splat generated. Install the SPAG-4D CUDA/Python environment before high-quality SPAG-4D refinement.",
        }
    mark(job, "complete", "done", "ERP and preview splat ready")


def process_job(queue: dict, job: dict, backend: str) -> None:
    try:
        mark(job, "running", "erp", "Preparing ERP panorama")
        save_queue(queue)
        erp_rel = ensure_erp(job)

        mark(job, "running", "splat_preview", "Generating realtime preview splat")
        save_queue(queue)
        vertices = ensure_preview_splat(job, erp_rel)

        complete_job(job, erp_rel, vertices, backend)
        save_queue(queue)
    except Exception as error:
        mark(job, "error", "failed", str(error))
        job["error"] = str(error)
        job["traceback"] = traceback.format_exc()
        save_queue(queue)
        print(f"[error] {scene_id(job)}: {error}", file=sys.stderr)


def publish_assets() -> None:
    subprocess.run(["node", "viewer/scripts/publish-assets.mjs"], cwd=ROOT, check=True)


def main() -> None:
    parser = argparse.ArgumentParser(description="Run FFIX3DVR background ERP and splat jobs.")
    parser.add_argument("--ids", nargs="*", default=[], help="Scene ids to process, lower-case map names.")
    parser.add_argument("--limit", type=int, default=1, help="Maximum jobs to process when ids are omitted.")
    parser.add_argument("--backend", choices=["preview", "spag4d"], default="preview", help="Preferred splat backend.")
    args = parser.parse_args()

    queue = load_queue()
    jobs = select_jobs(queue, args.ids, args.limit)
    print(json.dumps({"selected": [scene_id(job) for job in jobs], "backend": args.backend}, indent=2))
    for job in jobs:
        process_job(queue, job, args.backend)
    publish_assets()


if __name__ == "__main__":
    main()
