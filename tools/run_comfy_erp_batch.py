from __future__ import annotations

import json
import shutil
import time
import urllib.request
import uuid
from pathlib import Path


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


def make_prompt(input_name: str, prompt_text: str, sticker: dict, prefix: str, seed: int) -> dict:
    state = {
        "version": 1,
        "projection_model": "pinhole_rectilinear",
        "alpha_mode": "straight",
        "coverage": 360,
        "bg_color": "#00ff00",
        "output_preset": 2048,
        "assets": {},
        "stickers": [],
        "shots": [],
        "painting": {
            "version": 1,
            "groups": [],
            "paint": {"strokes": []},
            "mask": {"strokes": []},
            "raster_objects": [],
        },
        "painting_layer": None,
        "ui_settings": {"invert_view_x": False, "invert_view_y": False, "preview_quality": "balanced"},
        "active": {"selected_sticker_id": None, "selected_shot_id": None},
    }
    sticker_state = {
        "yaw_deg": float(sticker.get("yaw_deg", 0.0)),
        "pitch_deg": float(sticker.get("pitch_deg", -4.0)),
        "hFOV_deg": float(sticker.get("hFOV_deg", 104.0)),
        "roll_deg": float(sticker.get("roll_deg", 0.0)),
    }
    return {
        "1": {"class_type": "LoadImage", "inputs": {"image": input_name}},
        "6": {"class_type": "CLIPTextEncode", "inputs": {"clip": ["44", 0], "text": prompt_text}},
        "8": {"class_type": "VAEDecode", "inputs": {"samples": ["31", 0], "vae": ["43", 0]}},
        "31": {
            "class_type": "KSampler",
            "inputs": {
                "model": ["63", 0],
                "positive": ["49", 0],
                "negative": ["55", 0],
                "latent_image": ["52", 0],
                "seed": seed,
                "control_after_generate": "fixed",
                "steps": 20,
                "cfg": 5.0,
                "sampler_name": "euler",
                "scheduler": "simple",
                "denoise": 1.0,
            },
        },
        "33": {
            "class_type": "CLIPTextEncode",
            "inputs": {"clip": ["44", 0], "text": "text, worst quality, blurry, ugly, modern furniture, people, characters"},
        },
        "43": {"class_type": "VAELoader", "inputs": {"vae_name": "flux2-vae.safetensors"}},
        "44": {"class_type": "CLIPLoader", "inputs": {"clip_name": "qwen_3_4b.safetensors", "type": "flux2", "device": "default"}},
        "48": {"class_type": "UNETLoader", "inputs": {"unet_name": "flux-2-klein-base-4b.safetensors", "weight_dtype": "default"}},
        "49": {"class_type": "ReferenceLatent", "inputs": {"conditioning": ["6", 0], "latent": ["52", 0]}},
        "52": {"class_type": "VAEEncode", "inputs": {"pixels": ["70", 0], "vae": ["43", 0]}},
        "55": {"class_type": "ReferenceLatent", "inputs": {"conditioning": ["33", 0], "latent": ["52", 0]}},
        "63": {
            "class_type": "LoraLoaderModelOnly",
            "inputs": {
                "model": ["48", 0],
                "lora_name": "flux-2-klein-4B-360-erp-outpaint-lora_V1.safetensors",
                "strength_model": 0.9,
            },
        },
        "66": {"class_type": "SaveImage", "inputs": {"images": ["8", 0], "filename_prefix": prefix}},
        "70": {
            "class_type": "PanoramaStickers",
            "inputs": {
                "output_preset": "2048",
                "coverage": "360",
                "bg_color": "#00ff00",
                "state_json": json.dumps(state, separators=(",", ":")),
                "sticker_image": ["1", 0],
                "sticker_state": json.dumps(sticker_state, separators=(",", ":")),
                "fps": 24.0,
            },
        },
    }


def run_job(job: dict) -> dict:
    map_name = job["mapName"]
    input_path = ROOT / job["sourcePlate"]
    output_path = ROOT / job["erpOutput"]
    output_path.parent.mkdir(parents=True, exist_ok=True)

    input_name = f"batch_{map_name.lower()}_source_plate.png"
    shutil.copy2(input_path, COMFY / "input" / input_name)
    prefix = f"batch_{map_name.lower()}_erp"
    prompt = make_prompt(input_name, job["prompt"], job.get("sticker", {}), prefix, 510000 + int(job["index"]))
    prompt_id = str(uuid.uuid4())
    post_json("/prompt", {"prompt": prompt, "client_id": "ffix3dvr-batch", "prompt_id": prompt_id})
    LOG_DIR.mkdir(parents=True, exist_ok=True)
    (LOG_DIR / f"{map_name.lower()}_prompt.json").write_text(json.dumps(prompt, indent=2), encoding="utf-8")

    for _ in range(720):
        history = get_json(f"/history/{prompt_id}")
        if prompt_id not in history:
            time.sleep(5)
            continue
        entry = history[prompt_id]
        status = entry.get("status", {})
        if status.get("status_str") == "error":
            (LOG_DIR / f"{map_name.lower()}_error.json").write_text(json.dumps(entry, indent=2), encoding="utf-8")
            return {"ok": False, "error": status}
        images = entry.get("outputs", {}).get("66", {}).get("images", [])
        if not images:
            return {"ok": False, "error": "missing SaveImage output"}
        image = images[-1]
        comfy_out = COMFY / "output" / image["filename"]
        shutil.copy2(comfy_out, output_path)
        return {"ok": True, "erp": str(output_path.relative_to(ROOT)).replace("\\", "/")}
    return {"ok": False, "error": "timeout"}


def main() -> None:
    if not server_ready():
        raise RuntimeError("ComfyUI is not reachable at http://127.0.0.1:8188")
    queue = json.loads(QUEUE.read_text(encoding="utf-8"))
    selected = [job for job in queue["jobs"] if job.get("status") in {"queued", "error"}][:10]
    print(f"selected={len(selected)}")
    for offset, job in enumerate(selected, start=1):
        print(f"[{offset}/{len(selected)}] {job['mapName']}")
        job["status"] = "running"
        QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")
        result = run_job(job)
        if result["ok"]:
            job["status"] = "erp_done"
            job["actualErpOutput"] = result["erp"]
        else:
            job["status"] = "error"
            job["error"] = result["error"]
        QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")
        print(json.dumps({"mapName": job["mapName"], "status": job["status"]}, indent=2))


if __name__ == "__main__":
    main()
