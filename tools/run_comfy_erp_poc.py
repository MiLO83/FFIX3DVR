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
SOURCE_PLATE = ROOT / "artifacts" / "first-field" / "source" / "source_plate.png"
COMFY_INPUT = COMFY / "input" / "ffix_first_field_source_plate.png"
OUT_DIR = ROOT / "artifacts" / "first-field" / "erp"
LOG = ROOT / "artifacts" / "first-field" / "logs" / "comfy_erp_prompt.json"


def post_json(path: str, payload: dict) -> dict:
    data = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(f"{SERVER}{path}", data=data, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def get_json(path: str) -> dict:
    with urllib.request.urlopen(f"{SERVER}{path}", timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def prompt() -> dict:
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
        "yaw_deg": 0.0,
        "pitch_deg": -4.0,
        "hFOV_deg": 104.0,
        "roll_deg": 0.0,
        "source_aspect": 656 / 336,
    }
    return {
        "1": {"class_type": "LoadImage", "inputs": {"image": COMFY_INPUT.name}},
        "6": {
            "class_type": "CLIPTextEncode",
            "inputs": {
                "clip": ["44", 0],
                "text": (
                    "Fill the green spaces according to the reference image. "
                    "Outpaint as a seamless 360 equirectangular panorama, 2:1 aspect ratio. "
                    "Keep the horizon level. Match left and right edges. Preserve a dark wooden airship cargo room, "
                    "FFIX pre-rendered background style, warm stage lighting, painted fantasy RPG environment."
                ),
            },
        },
        "8": {"class_type": "VAEDecode", "inputs": {"samples": ["31", 0], "vae": ["43", 0]}},
        "31": {
            "class_type": "KSampler",
            "inputs": {
                "model": ["63", 0],
                "positive": ["49", 0],
                "negative": ["55", 0],
                "latent_image": ["52", 0],
                "seed": 510051,
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
        "66": {"class_type": "SaveImage", "inputs": {"images": ["8", 0], "filename_prefix": "ffix_field_0051_erp"}},
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


def main() -> None:
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    COMFY_INPUT.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(SOURCE_PLATE, COMFY_INPUT)
    p = prompt()
    LOG.write_text(json.dumps(p, indent=2), encoding="utf-8")
    prompt_id = str(uuid.uuid4())
    queued = post_json("/prompt", {"prompt": p, "client_id": "ffix3dvr", "prompt_id": prompt_id})
    print(f"queued={queued}")
    for _ in range(720):
        history = get_json(f"/history/{prompt_id}")
        if prompt_id in history:
            outputs = history[prompt_id].get("outputs", {})
            print(json.dumps(outputs, indent=2))
            images = outputs.get("66", {}).get("images", [])
            if not images:
                raise RuntimeError("Comfy finished without a SaveImage output")
            image = images[-1]
            comfy_out = COMFY / "output" / image["filename"]
            target = OUT_DIR / "field_0051_erp.png"
            shutil.copy2(comfy_out, target)
            print(f"wrote={target}")
            return
        time.sleep(5)
    raise TimeoutError("Timed out waiting for ComfyUI prompt")


if __name__ == "__main__":
    main()
