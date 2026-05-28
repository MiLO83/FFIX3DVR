from __future__ import annotations

import argparse
import json
import shutil
import time
import urllib.request
import uuid
from datetime import datetime, timezone
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
COMFY = Path(r"C:\Users\rxcam\ComfyUI_portable\ComfyUI_windows_portable\ComfyUI")
SERVER = "http://127.0.0.1:8188"
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"
ARTIFACT_DEPTH = ROOT / "artifacts" / "all-fields" / "depth"
PUBLIC_DEPTH = ROOT / "viewer" / "public" / "assets" / "depth"
LOG_DIR = ROOT / "artifacts" / "all-fields" / "logs"
PROGRESS = ROOT / "artifacts" / "all-fields" / "depth_batch_status.json"


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def post_json(path: str, payload: dict) -> dict:
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(f"{SERVER}{path}", data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def get_json(path: str) -> dict:
    with urllib.request.urlopen(f"{SERVER}{path}", timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def server_ready() -> bool:
    try:
        get_json("/system_stats")
        get_json("/object_info/DepthAnythingV2Preprocessor")
        return True
    except Exception:
        return False


def make_prompt(input_name: str, prefix: str, ckpt_name: str, resolution: int) -> dict:
    return {
        "1": {"class_type": "LoadImage", "inputs": {"image": input_name}},
        "2": {
            "class_type": "DepthAnythingV2Preprocessor",
            "inputs": {
                "image": ["1", 0],
                "ckpt_name": ckpt_name,
                "resolution": resolution,
            },
        },
        "3": {"class_type": "SaveImage", "inputs": {"images": ["2", 0], "filename_prefix": prefix}},
    }


def read_queue() -> dict:
    return json.loads(QUEUE.read_text(encoding="utf-8-sig"))


def write_progress(payload: dict) -> None:
    PROGRESS.parent.mkdir(parents=True, exist_ok=True)
    PROGRESS.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def select_jobs(queue: dict, ids: list[str], limit: int) -> list[dict]:
    requested = {scene_id.lower() for scene_id in ids}
    jobs = [job for job in queue["jobs"] if not requested or job["mapName"].lower() in requested]
    return jobs[:limit] if limit > 0 else jobs


def run_job(job: dict, ckpt_name: str, resolution: int, force: bool) -> dict:
    scene_id = job["mapName"].lower()
    source_path = ROOT / job["sourcePlate"]
    artifact_path = ARTIFACT_DEPTH / scene_id / "depth.png"
    public_path = PUBLIC_DEPTH / scene_id / "depth.png"
    if public_path.exists() and artifact_path.exists() and not force:
        return {"ok": True, "skipped": True, "depth": str(public_path.relative_to(ROOT)).replace("\\", "/")}

    artifact_path.parent.mkdir(parents=True, exist_ok=True)
    public_path.parent.mkdir(parents=True, exist_ok=True)
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    input_name = f"depth_{scene_id}_source_plate.png"
    shutil.copy2(source_path, COMFY / "input" / input_name)
    prefix = f"ffix_depth_{scene_id}"
    prompt = make_prompt(input_name, prefix, ckpt_name, resolution)
    prompt_id = str(uuid.uuid4())
    (LOG_DIR / f"{scene_id}_depth_prompt.json").write_text(json.dumps(prompt, indent=2), encoding="utf-8")
    post_json("/prompt", {"prompt": prompt, "client_id": "ffix3dvr-depth-batch", "prompt_id": prompt_id})

    for _ in range(720):
        history = get_json(f"/history/{prompt_id}")
        if prompt_id not in history:
            time.sleep(2)
            continue
        entry = history[prompt_id]
        status = entry.get("status", {})
        if status.get("status_str") == "error":
            (LOG_DIR / f"{scene_id}_depth_error.json").write_text(json.dumps(entry, indent=2), encoding="utf-8")
            return {"ok": False, "error": status}
        images = entry.get("outputs", {}).get("3", {}).get("images", [])
        if not images:
            return {"ok": False, "error": "missing SaveImage output"}
        image = images[-1]
        comfy_out = COMFY / "output" / image["filename"]
        shutil.copy2(comfy_out, artifact_path)
        shutil.copy2(comfy_out, public_path)
        return {
            "ok": True,
            "skipped": False,
            "depth": str(public_path.relative_to(ROOT)).replace("\\", "/"),
        }
    return {"ok": False, "error": "timeout"}


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate Depth Anything V2 maps for FFIX background plates.")
    parser.add_argument("--ids", nargs="*", default=[], help="Lower-case scene ids to process.")
    parser.add_argument("--limit", type=int, default=0, help="Maximum jobs to process. 0 means all selected jobs.")
    parser.add_argument("--ckpt", default="depth_anything_v2_vitl.pth", help="Depth Anything V2 checkpoint.")
    parser.add_argument("--resolution", type=int, default=1024, help="Depth preprocessor resolution.")
    parser.add_argument("--force", action="store_true", help="Regenerate existing depth maps.")
    args = parser.parse_args()

    if not server_ready():
        raise RuntimeError("ComfyUI or DepthAnythingV2Preprocessor is not reachable at http://127.0.0.1:8188")

    queue = read_queue()
    jobs = select_jobs(queue, args.ids, args.limit)
    total = len(jobs)
    done = 0
    failed = 0
    skipped = 0
    write_progress({"status": "running", "startedAt": now(), "total": total, "done": done, "failed": failed, "skipped": skipped})

    for index, job in enumerate(jobs, start=1):
        scene_id = job["mapName"].lower()
        print(f"[{index}/{total}] {scene_id}", flush=True)
        write_progress(
            {
                "status": "running",
                "updatedAt": now(),
                "current": scene_id,
                "total": total,
                "done": done,
                "failed": failed,
                "skipped": skipped,
                "ckpt": args.ckpt,
                "resolution": args.resolution,
            }
        )
        result = run_job(job, args.ckpt, args.resolution, args.force)
        if result.get("ok"):
            done += 1
            if result.get("skipped"):
                skipped += 1
        else:
            failed += 1
            (LOG_DIR / f"{scene_id}_depth_batch_result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps({"scene": scene_id, **result}, indent=2), flush=True)

    write_progress(
        {
            "status": "complete" if failed == 0 else "complete_with_errors",
            "updatedAt": now(),
            "total": total,
            "done": done,
            "failed": failed,
            "skipped": skipped,
            "ckpt": args.ckpt,
            "resolution": args.resolution,
        }
    )


if __name__ == "__main__":
    main()
