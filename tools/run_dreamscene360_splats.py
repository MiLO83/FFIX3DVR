from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"
DEFAULT_ENGINE = Path(
    r"C:\Users\rxcam\ComfyUI_portable\ComfyUI_windows_portable\ComfyUI\custom_nodes"
    r"\ComfyUI-DreamScene360\dreamscene360_engine"
)
DEFAULT_PYTHON = Path(r"C:\Users\rxcam\ComfyUI_portable\ComfyUI_windows_portable\python_embeded\python.exe")


def safe_scene_name(map_name: str) -> str:
    return "".join(ch.lower() if ch.isalnum() else "_" for ch in map_name).strip("_")


def rel(path: Path) -> str:
    return str(path.resolve().relative_to(ROOT)).replace("\\", "/")


def select_jobs(queue: dict, limit: int | None, start_index: int, force: bool) -> list[dict]:
    jobs = []
    for job in queue["jobs"]:
        if job.get("index", 0) < start_index:
            continue
        if job.get("status") not in {"erp_done", "splat_done", "complete"}:
            continue
        if not force and job.get("dreamscene360", {}).get("status") == "complete":
            continue
        jobs.append(job)
        if limit is not None and len(jobs) >= limit:
            break
    return jobs


def copy_optional_ffix_guidance(job: dict, data_dir: Path) -> dict | None:
    guidance = job.get("cameraMovedInfill")
    if not guidance or guidance.get("status") != "prepared":
        return None
    camera_path_rel = guidance.get("cameraPath")
    if not camera_path_rel:
        return None
    camera_path = ROOT / camera_path_rel
    if not camera_path.exists():
        return {"status": "missing", "cameraPath": camera_path_rel}

    copied_camera_path = data_dir / "ffix_camera_path.json"
    shutil.copy2(camera_path, copied_camera_path)

    camera_data = json.loads(camera_path.read_text(encoding="utf-8"))
    copied_masks = []
    masks_dir = data_dir / "ffix_infill_masks"
    masks_dir.mkdir(parents=True, exist_ok=True)
    for mask in camera_data.get("masks", []):
        erp_mask_rel = mask.get("erpMask")
        if not erp_mask_rel:
            continue
        erp_mask = ROOT / erp_mask_rel
        if not erp_mask.exists():
            continue
        mask_out = masks_dir / Path(erp_mask).name
        shutil.copy2(erp_mask, mask_out)
        copied_masks.append(str(mask_out))

    return {
        "status": "copied",
        "cameraPath": str(copied_camera_path),
        "maskCount": len(copied_masks),
        "note": "DreamScene360 training uses this when the FFIX-guided perturbation patch is present.",
    }


def prepare_scene(job: dict, engine_dir: Path) -> tuple[str, Path, Path, dict | None]:
    scene_name = safe_scene_name(job["mapName"])
    erp_rel = (
        job.get("ffixInfill", {}).get("colorMatchedErpOutput")
        or job.get("colorHarmonized", {}).get("output")
        or job.get("ffixInfill", {}).get("exactErpOutput")
        or job.get("actualErpOutput")
        or job.get("aiErpOutput")
        or job["erpOutput"]
    )
    erp_path = ROOT / erp_rel
    if not erp_path.exists():
        raise FileNotFoundError(f"ERP missing: {erp_path}")

    data_dir = engine_dir / "data" / scene_name
    data_dir.mkdir(parents=True, exist_ok=True)
    pano_path = data_dir / f"{scene_name}_PANORAMA.png"
    shutil.copy2(erp_path, pano_path)
    ffix_guidance = copy_optional_ffix_guidance(job, data_dir)

    output_dir = engine_dir / "output" / scene_name
    return scene_name, data_dir, output_dir, ffix_guidance


def find_iteration_ply(output_dir: Path, iterations: int) -> Path | None:
    preferred = output_dir / "point_cloud" / f"iteration_{iterations}" / "point_cloud.ply"
    if preferred.exists():
        return preferred
    candidates = sorted((output_dir / "point_cloud").glob("iteration_*/point_cloud.ply"))
    return candidates[-1] if candidates else None


def run_job(job: dict, engine_dir: Path, python: Path, iterations: int, prepare_only: bool, force: bool) -> dict:
    scene_name, data_dir, output_dir, ffix_guidance = prepare_scene(job, engine_dir)
    out_path = ROOT / "artifacts" / "all-fields" / "splat_dreamscene360" / scene_name / "field_dreamscene360_gs.ply"
    out_path.parent.mkdir(parents=True, exist_ok=True)

    if out_path.exists() and not force:
        return {
            "status": "complete",
            "sceneName": scene_name,
            "splatOutput": rel(out_path),
            "ffixGuidance": ffix_guidance,
            "skipped": True,
        }

    if prepare_only:
        return {
            "status": "prepared",
            "sceneName": scene_name,
            "sourceDir": str(data_dir),
            "ffixGuidance": ffix_guidance,
            "skipped": True,
        }

    cmd = [
        str(python),
        "train.py",
        "-s",
        str(data_dir),
        "-m",
        str(output_dir),
        "--iterations",
        str(iterations),
        "--save_iterations",
        str(iterations),
        "--test_iterations",
        str(iterations),
    ]
    subprocess.run(cmd, cwd=engine_dir, check=True)

    ply_path = find_iteration_ply(output_dir, iterations)
    if ply_path is None:
        raise FileNotFoundError(f"DreamScene360 did not produce a point cloud under {output_dir}")
    shutil.copy2(ply_path, out_path)
    return {
        "status": "complete",
        "sceneName": scene_name,
        "sourceDir": str(data_dir),
        "engineOutput": str(output_dir),
        "splatOutput": rel(out_path),
        "ffixGuidance": ffix_guidance,
        "iterations": iterations,
        "skipped": False,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Run DreamScene360 panorama-to-3DGS for completed FFIX ERP jobs.")
    parser.add_argument("--limit", type=int, default=1)
    parser.add_argument("--start-index", type=int, default=1)
    parser.add_argument("--iterations", type=int, default=3000)
    parser.add_argument("--engine-dir", type=Path, default=DEFAULT_ENGINE)
    parser.add_argument("--python", type=Path, default=DEFAULT_PYTHON)
    parser.add_argument("--prepare-only", action="store_true")
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()

    queue = json.loads(QUEUE.read_text(encoding="utf-8"))
    jobs = select_jobs(queue, args.limit, args.start_index, args.force)
    if not jobs:
        print(json.dumps({"processed": 0, "reason": "no matching jobs"}))
        return

    results = []
    for offset, job in enumerate(jobs, start=1):
        print(f"[{offset}/{len(jobs)}] {job['mapName']}")
        try:
            result = run_job(job, args.engine_dir, args.python, args.iterations, args.prepare_only, args.force)
            job["dreamscene360"] = result
            if result.get("status") == "complete":
                job.setdefault("runtimeAssets", {})["highPowerDreamScene360"] = {
                    "type": "gaussian_splat_ply",
                    "path": result["splatOutput"],
                    "variant": "dreamscene360_panoramic_3dgs",
                    "iterations": args.iterations,
                }
            results.append({"mapName": job["mapName"], **result})
        except Exception as exc:
            job["dreamscene360"] = {"status": "error", "error": str(exc)}
            results.append({"mapName": job["mapName"], "status": "error", "error": str(exc)})
            print(f"ERROR: {exc}")
            if args.limit == 1:
                raise

    QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")
    print(json.dumps({"processed": len(results), "results": results}, indent=2))


if __name__ == "__main__":
    main()
