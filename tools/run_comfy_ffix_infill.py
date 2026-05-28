from __future__ import annotations

import argparse
import json
import shutil
import time
import urllib.request
import uuid
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter

from reinsert_source_plate_into_erp import reinsert_plate


ROOT = Path(__file__).resolve().parents[1]
COMFY = Path(r"C:\Users\rxcam\ComfyUI_portable\ComfyUI_windows_portable\ComfyUI")
SERVER = "http://127.0.0.1:8188"
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"
LOG_DIR = ROOT / "artifacts" / "all-fields" / "logs"


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
        return True
    except Exception:
        return False


def rel(path: Path) -> str:
    return str(path.resolve().relative_to(ROOT)).replace("\\", "/")


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def union_erp_masks(camera_path: Path, out_path: Path, feather_px: int, grow_px: int) -> Path:
    data = load_json(camera_path)
    masks = []
    for row in data.get("masks", []):
        mask_rel = row.get("erpMask")
        if not mask_rel:
            continue
        mask_path = ROOT / mask_rel
        if mask_path.exists():
            masks.append(Image.open(mask_path).convert("L"))
    if not masks:
        raise ValueError(f"No ERP masks found in {camera_path}")

    out = Image.new("L", masks[0].size, 0)
    for mask in masks:
        out = ImageChops.lighter(out, mask.resize(out.size, Image.Resampling.NEAREST))
    if grow_px > 0:
        out = out.filter(ImageFilter.MaxFilter(grow_px * 2 + 1))
    if feather_px > 0:
        out = out.filter(ImageFilter.GaussianBlur(feather_px))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out.save(out_path)
    return out_path


def make_inpaint_prompt(erp_name: str, mask_name: str, prompt_text: str, prefix: str, seed: int) -> dict:
    positive = (
        prompt_text
        + " Inpaint only the masked disocclusion and edge regions. Preserve the original FFIX plate, lighting, "
        + "painted texture, camera perspective, and 360 equirectangular continuity."
    )
    negative = (
        "text, watermark, people, characters, modern furniture, clean CGI, sharp photo, distorted geometry, "
        "changed source plate, seams, blur, worst quality"
    )
    return {
        "1": {"class_type": "LoadImage", "inputs": {"image": erp_name}},
        "2": {"class_type": "LoadImageMask", "inputs": {"image": mask_name, "channel": "red"}},
        "6": {"class_type": "CLIPTextEncode", "inputs": {"clip": ["44", 0], "text": positive}},
        "33": {"class_type": "CLIPTextEncode", "inputs": {"clip": ["44", 0], "text": negative}},
        "31": {
            "class_type": "KSampler",
            "inputs": {
                "model": ["63", 0],
                "positive": ["72", 0],
                "negative": ["72", 1],
                "latent_image": ["72", 2],
                "seed": seed,
                "control_after_generate": "fixed",
                "steps": 18,
                "cfg": 4.0,
                "sampler_name": "euler",
                "scheduler": "simple",
                "denoise": 0.72,
            },
        },
        "43": {"class_type": "VAELoader", "inputs": {"vae_name": "flux2-vae.safetensors"}},
        "44": {"class_type": "CLIPLoader", "inputs": {"clip_name": "qwen_3_4b.safetensors", "type": "flux2", "device": "default"}},
        "48": {"class_type": "UNETLoader", "inputs": {"unet_name": "flux-2-klein-base-4b.safetensors", "weight_dtype": "default"}},
        "63": {
            "class_type": "LoraLoaderModelOnly",
            "inputs": {
                "model": ["48", 0],
                "lora_name": "flux-2-klein-4B-360-erp-outpaint-lora_V1.safetensors",
                "strength_model": 0.45,
            },
        },
        "72": {
            "class_type": "InpaintModelConditioning",
            "inputs": {
                "positive": ["6", 0],
                "negative": ["33", 0],
                "vae": ["43", 0],
                "pixels": ["1", 0],
                "mask": ["2", 0],
                "noise_mask": True,
            },
        },
        "8": {"class_type": "VAEDecode", "inputs": {"samples": ["31", 0], "vae": ["43", 0]}},
        "66": {"class_type": "SaveImage", "inputs": {"images": ["8", 0], "filename_prefix": prefix}},
    }


def wait_for_image(prompt_id: str, save_node: str = "66", timeout_s: int = 3600) -> Path:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        history = get_json(f"/history/{prompt_id}")
        if prompt_id not in history:
            time.sleep(5)
            continue
        entry = history[prompt_id]
        status = entry.get("status", {})
        if status.get("status_str") == "error":
            raise RuntimeError(json.dumps(status, indent=2))
        images = entry.get("outputs", {}).get(save_node, {}).get("images", [])
        if not images:
            raise RuntimeError("missing SaveImage output")
        image = images[-1]
        return COMFY / "output" / image["filename"]
    raise TimeoutError(f"ComfyUI prompt timed out: {prompt_id}")


def source_erp(job: dict) -> str:
    return (
        job.get("ffixInfill", {}).get("inputErp")
        or job.get("actualErpOutput")
        or job.get("aiErpOutput")
        or job["erpOutput"]
    )


def run_job(job: dict, force: bool, feather_px: int, grow_px: int) -> dict:
    guidance = job.get("cameraMovedInfill")
    if not guidance or guidance.get("status") != "prepared":
        raise ValueError(f"{job['mapName']} has no prepared cameraMovedInfill metadata")

    map_slug = job["mapName"].lower()
    input_erp = ROOT / source_erp(job)
    camera_path = ROOT / guidance["cameraPath"]
    if not input_erp.exists():
        raise FileNotFoundError(f"ERP missing: {input_erp}")
    if not camera_path.exists():
        raise FileNotFoundError(f"camera path missing: {camera_path}")

    out_dir = ROOT / "artifacts" / "all-fields" / "erp_infilled" / map_slug
    out_dir.mkdir(parents=True, exist_ok=True)
    union_mask = union_erp_masks(camera_path, out_dir / "ffix_infill_mask_union.png", feather_px, grow_px)
    ai_out = out_dir / "field_erp_ai.png"
    exact_out = out_dir / "field_erp.png"

    if not ai_out.exists() or force:
        erp_input_name = f"ffix_infill_{map_slug}_erp.png"
        mask_input_name = f"ffix_infill_{map_slug}_mask.png"
        shutil.copy2(input_erp, COMFY / "input" / erp_input_name)
        shutil.copy2(union_mask, COMFY / "input" / mask_input_name)

        prompt = make_inpaint_prompt(
            erp_input_name,
            mask_input_name,
            job["prompt"],
            f"ffix_infill_{map_slug}",
            720000 + int(job["index"]),
        )
        prompt_id = str(uuid.uuid4())
        post_json("/prompt", {"prompt": prompt, "client_id": "ffix3dvr-infill", "prompt_id": prompt_id})
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        (LOG_DIR / f"{map_slug}_infill_prompt.json").write_text(json.dumps(prompt, indent=2), encoding="utf-8")
        comfy_out = wait_for_image(prompt_id)
        shutil.copy2(comfy_out, ai_out)

    temp_job = dict(job)
    temp_job["aiErpOutput"] = rel(ai_out)
    temp_job["actualErpOutput"] = rel(ai_out)
    reinsert_plate(temp_job, overwrite=True, erp_input_rel=rel(ai_out), out_rel=rel(exact_out), update_low_power=False)

    job["ffixInfill"] = {
        "status": "complete",
        "inputErp": rel(input_erp),
        "unionMask": rel(union_mask),
        "aiErpOutput": rel(ai_out),
        "exactErpOutput": rel(exact_out),
        "cameraPath": guidance["cameraPath"],
        "maskCount": guidance.get("maskCount"),
        "featherPx": feather_px,
        "growPx": grow_px,
    }
    job.setdefault("runtimeAssets", {})["lowPower"] = {
        "type": "erp360",
        "path": rel(exact_out),
        "projection": "equirectangular",
        "sourcePlateReinserted": True,
        "ffixCameraMovedInfill": True,
    }
    return job["ffixInfill"]


def selectable_jobs(queue: dict, limit: int | None, start_index: int, force: bool) -> list[dict]:
    rows = []
    for job in queue.get("jobs", []):
        if int(job.get("index", 0)) < start_index:
            continue
        if not force and job.get("ffixInfill", {}).get("status") == "complete":
            continue
        if job.get("cameraMovedInfill", {}).get("status") != "prepared":
            continue
        rows.append(job)
        if limit is not None and len(rows) >= limit:
            break
    return rows


def main() -> None:
    parser = argparse.ArgumentParser(description="Use ComfyUI to inpaint FFIX camera-moved ERP occlusion masks.")
    parser.add_argument("--limit", type=int, default=1)
    parser.add_argument("--start-index", type=int, default=1)
    parser.add_argument("--feather-px", type=int, default=4)
    parser.add_argument("--grow-px", type=int, default=2)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()

    if not server_ready():
        raise RuntimeError("ComfyUI is not reachable at http://127.0.0.1:8188")

    queue = load_json(QUEUE)
    jobs = selectable_jobs(queue, args.limit, args.start_index, args.force)
    results = []
    for offset, job in enumerate(jobs, start=1):
        print(f"[{offset}/{len(jobs)}] {job['mapName']}")
        try:
            result = run_job(job, args.force, args.feather_px, args.grow_px)
            results.append({"mapName": job["mapName"], **result})
        except Exception as exc:
            job["ffixInfill"] = {"status": "error", "error": str(exc)}
            results.append({"mapName": job["mapName"], "status": "error", "error": str(exc)})
            if args.limit == 1:
                raise
        finally:
            QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")

    print(json.dumps({"processed": len(results), "results": results}, indent=2))


if __name__ == "__main__":
    main()
