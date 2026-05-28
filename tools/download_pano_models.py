from __future__ import annotations

from pathlib import Path

from huggingface_hub import hf_hub_download


COMFY = Path(r"C:\Users\rxcam\ComfyUI_portable\ComfyUI_windows_portable\ComfyUI")

FILES = [
    {
        "repo_id": "Comfy-Org/vae-text-encorder-for-flux-klein-4b",
        "filename": "split_files/diffusion_models/flux-2-klein-base-4b.safetensors",
        "target": COMFY / "models" / "diffusion_models" / "flux-2-klein-base-4b.safetensors",
    },
    {
        "repo_id": "nomadoor/flux-2-klein-4B-360-erp-outpaint-lora",
        "filename": "flux-2-klein-4B-360-erp-outpaint-lora_V1.safetensors",
        "target": COMFY / "models" / "loras" / "flux-2-klein-4B-360-erp-outpaint-lora_V1.safetensors",
    },
    {
        "repo_id": "Comfy-Org/vae-text-encorder-for-flux-klein-4b",
        "filename": "split_files/text_encoders/qwen_3_4b.safetensors",
        "target": COMFY / "models" / "text_encoders" / "qwen_3_4b.safetensors",
    },
    {
        "repo_id": "Comfy-Org/vae-text-encorder-for-flux-klein-9b",
        "filename": "split_files/vae/flux2-vae.safetensors",
        "target": COMFY / "models" / "vae" / "flux2-vae.safetensors",
    },
]


def main() -> None:
    for item in FILES:
        target: Path = item["target"]
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists() and target.stat().st_size > 0:
            print(f"exists {target}")
            continue
        print(f"downloading {item['repo_id']}::{item['filename']}")
        downloaded = hf_hub_download(
            repo_id=item["repo_id"],
            filename=item["filename"],
            local_dir=str(target.parent),
            local_dir_use_symlinks=False,
        )
        downloaded_path = Path(downloaded)
        if downloaded_path != target:
            downloaded_path.replace(target)
        print(f"wrote {target} {target.stat().st_size}")


if __name__ == "__main__":
    main()
