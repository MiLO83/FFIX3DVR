from __future__ import annotations

import json
import math
import struct
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
ERP = ROOT / "artifacts" / "first-field" / "erp" / "field_0051_erp.png"
PLY = ROOT / "artifacts" / "first-field" / "splat" / "field_0051_spherical_env.ply"
LOG = ROOT / "artifacts" / "first-field" / "logs" / "spherical_splat.txt"


def sigmoid_inverse(alpha: float) -> float:
    alpha = min(max(alpha, 1e-4), 1.0 - 1e-4)
    return math.log(alpha / (1.0 - alpha))


def sh_dc(value: float) -> float:
    # 3DGS stores RGB as degree-0 spherical harmonic coefficients.
    return (value - 0.5) / 0.28209479177387814


def main() -> None:
    PLY.parent.mkdir(parents=True, exist_ok=True)
    LOG.parent.mkdir(parents=True, exist_ok=True)
    img = Image.open(ERP).convert("RGB")
    sample_w = 512
    sample_h = 256
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

    with PLY.open("wb") as f:
        f.write(header)
        for row in rows:
            f.write(struct.pack("<17f", *row))

    LOG.write_text(
        json.dumps(
            {
                "source": str(ERP.relative_to(ROOT)),
                "output": str(PLY.relative_to(ROOT)),
                "vertices": len(rows),
                "sampleSize": [sample_w, sample_h],
                "radius": radius,
                "note": "Baseline spherical environment Gaussian Splat PLY. DreamScene360 depth-trained 3DGS remains the target upgrade.",
            },
            indent=2,
        ),
        encoding="utf-8",
    )
    print(f"wrote={PLY}")
    print(f"vertices={len(rows)}")


if __name__ == "__main__":
    main()
