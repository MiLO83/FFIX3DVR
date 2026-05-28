from __future__ import annotations

import json
import math
import struct
import argparse
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"
RUNTIME_MANIFEST = ROOT / "artifacts" / "all-fields" / "runtime_assets_sample.json"


def sigmoid_inverse(alpha: float) -> float:
    alpha = min(max(alpha, 1e-4), 1.0 - 1e-4)
    return math.log(alpha / (1.0 - alpha))


def sh_dc(value: float) -> float:
    return (value - 0.5) / 0.28209479177387814


def write_spherical_splat(erp_path: Path, ply_path: Path, sample_w: int = 512, sample_h: int = 256) -> int:
    ply_path.parent.mkdir(parents=True, exist_ok=True)
    img = Image.open(erp_path).convert("RGB")
    small = img.resize((sample_w, sample_h), Image.Resampling.LANCZOS)
    radius = 12.0
    scale = math.log(0.045)
    opacity = sigmoid_inverse(0.82)
    rows = []

    for y in range(sample_h):
        v = (y + 0.5) / sample_h
        theta = v * math.pi
        sin_theta = math.sin(theta)
        cos_theta = math.cos(theta)
        for x in range(sample_w):
            u = (x + 0.5) / sample_w
            phi = (u - 0.5) * 2.0 * math.pi
            px = radius * sin_theta * math.sin(phi)
            py = radius * cos_theta
            pz = radius * sin_theta * math.cos(phi)
            r, g, b = small.getpixel((x, y))
            rows.append(
                (
                    px,
                    py,
                    pz,
                    0.0,
                    0.0,
                    0.0,
                    sh_dc(r / 255.0),
                    sh_dc(g / 255.0),
                    sh_dc(b / 255.0),
                    opacity,
                    scale,
                    scale,
                    scale,
                    1.0,
                    0.0,
                    0.0,
                    0.0,
                )
            )

    header = "\n".join(
        [
            "ply",
            "format binary_little_endian 1.0",
            f"element vertex {len(rows)}",
            "property float x",
            "property float y",
            "property float z",
            "property float nx",
            "property float ny",
            "property float nz",
            "property float f_dc_0",
            "property float f_dc_1",
            "property float f_dc_2",
            "property float opacity",
            "property float scale_0",
            "property float scale_1",
            "property float scale_2",
            "property float rot_0",
            "property float rot_1",
            "property float rot_2",
            "property float rot_3",
            "end_header\n",
        ]
    ).encode("ascii")

    with ply_path.open("wb") as f:
        f.write(header)
        for row in rows:
            f.write(struct.pack("<17f", *row))
    return len(rows)


def main() -> None:
    parser = argparse.ArgumentParser(description="Convert completed ERP images into spherical Gaussian-splat-style PLYs.")
    parser.add_argument("--force", action="store_true", help="Regenerate PLY files even when they already exist.")
    args = parser.parse_args()

    queue = json.loads(QUEUE.read_text(encoding="utf-8"))
    runtime_assets = []
    generated = 0
    for job in queue["jobs"]:
        if job.get("status") not in {"erp_done", "splat_done", "complete"}:
            continue
        erp_rel = job.get("actualErpOutput") or job.get("erpOutput")
        erp_path = ROOT / erp_rel
        if not erp_path.exists():
            job["status"] = "error"
            job["error"] = f"ERP missing: {erp_rel}"
            continue
        splat_rel = job.get("splatOutput")
        splat_path = ROOT / splat_rel
        if args.force or not splat_path.exists():
            vertices = write_spherical_splat(erp_path, splat_path)
            generated += 1
        else:
            vertices = None
        job["status"] = "complete"
        job["runtimeAssets"] = {
            "lowPower": {
                "type": "erp360",
                "path": erp_rel,
                "projection": "equirectangular",
            },
            "highPower": {
                "type": "gaussian_splat_ply",
                "path": splat_rel,
                "variant": "spherical_env",
            },
        }
        if vertices is not None:
            job["runtimeAssets"]["highPower"]["vertices"] = vertices
        runtime_assets.append(
            {
                "mapName": job["mapName"],
                "lowPowerErp360": erp_rel,
                "highPowerSplat": splat_rel,
                "sourcePlate": job["sourcePlate"],
            }
        )
    QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")
    RUNTIME_MANIFEST.write_text(json.dumps({"assets": runtime_assets}, indent=2), encoding="utf-8")
    print(f"generated={generated}")
    print(f"runtime_assets={len(runtime_assets)}")
    print(f"manifest={RUNTIME_MANIFEST}")


if __name__ == "__main__":
    main()
