from __future__ import annotations

import json
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ART = ROOT / "artifacts" / "first-field"
MANIFEST = ART / "manifest.json"
VERIFY = ART / "logs" / "verification.txt"
SOURCE = ART / "source" / "source_plate.png"
ERP = ART / "erp" / "field_0051_erp.png"
SPLAT = ART / "splat" / "field_0051_spherical_env.ply"


def main() -> None:
    lines = []
    source_img = Image.open(SOURCE)
    erp_img = Image.open(ERP)
    splat_size = SPLAT.stat().st_size
    is_2_to_1 = erp_img.width == erp_img.height * 2
    lines.append(f"source={SOURCE.relative_to(ROOT)} size={source_img.width}x{source_img.height}")
    lines.append(f"erp={ERP.relative_to(ROOT)} size={erp_img.width}x{erp_img.height} is_2_to_1={is_2_to_1}")
    lines.append(f"splat={SPLAT.relative_to(ROOT)} bytes={splat_size}")
    lines.append("dreamscene360=blocked locally: installer targets Linux/RunPod and Windows build needs MSVC cl.exe for CUDA extensions")
    VERIFY.write_text("\n".join(lines), encoding="utf-8")

    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    manifest["status"]["backgroundExtracted"] = SOURCE.exists()
    manifest["status"]["equirectangularGenerated"] = ERP.exists() and is_2_to_1
    manifest["status"]["gaussianSplatGenerated"] = SPLAT.exists() and splat_size > 0
    manifest["assets"]["sourcePlate"] = str(SOURCE.relative_to(ROOT)).replace("\\", "/")
    manifest["assets"]["equirectangularPng"] = str(ERP.relative_to(ROOT)).replace("\\", "/")
    manifest["assets"]["splatPly"] = str(SPLAT.relative_to(ROOT)).replace("\\", "/")
    manifest["notes"] = [
        "ERP generated with ComfyUI Panorama Stickers plus Flux.2 Klein 4B 360 ERP LoRA.",
        "PLY is a baseline spherical environment Gaussian Splat generated from the ERP.",
        "DreamScene360 depth-trained 3DGS remains the next upgrade once its CUDA extension environment is available.",
    ]
    MANIFEST.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print("\n".join(lines))


if __name__ == "__main__":
    main()
