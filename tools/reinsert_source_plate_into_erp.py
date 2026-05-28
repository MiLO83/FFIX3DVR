from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"


def clamp(value: float, lo: int, hi: int) -> int:
    return max(lo, min(hi, int(round(value))))


def sample_rgba_nearest(image: Image.Image, x: float, y: float) -> tuple[int, int, int, int]:
    px = clamp(x, 0, image.width - 1)
    py = clamp(y, 0, image.height - 1)
    return image.getpixel((px, py))


def direction_from_erp(x: int, y: int, width: int, height: int) -> tuple[float, float, float]:
    yaw = ((x + 0.5) / width - 0.5) * 2.0 * math.pi
    pitch = (0.5 - (y + 0.5) / height) * math.pi
    cp = math.cos(pitch)
    return cp * math.sin(yaw), math.sin(pitch), cp * math.cos(yaw)


def camera_basis(yaw_deg: float, pitch_deg: float) -> tuple[tuple[float, float, float], ...]:
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    forward = (
        math.cos(pitch) * math.sin(yaw),
        math.sin(pitch),
        math.cos(pitch) * math.cos(yaw),
    )
    right = (math.cos(yaw), 0.0, -math.sin(yaw))
    up = (
        forward[1] * right[2] - forward[2] * right[1],
        forward[2] * right[0] - forward[0] * right[2],
        forward[0] * right[1] - forward[1] * right[0],
    )
    return right, up, forward


def dot(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def reinsert_plate(
    job: dict,
    overwrite: bool = False,
    erp_input_rel: str | None = None,
    out_rel: str | None = None,
    update_low_power: bool = True,
) -> dict:
    source_path = ROOT / job["sourcePlate"]
    erp_in_rel = erp_input_rel or job.get("aiErpOutput") or job.get("actualErpOutput") or job["erpOutput"]
    erp_in = ROOT / erp_in_rel
    out_rel = out_rel or f"artifacts/all-fields/erp_exact/{job['mapName'].lower()}/field_erp.png"
    out_path = ROOT / out_rel

    if not erp_in.exists():
        raise FileNotFoundError(f"ERP missing: {erp_in}")
    if not source_path.exists():
        raise FileNotFoundError(f"source plate missing: {source_path}")
    if out_path.exists() and not overwrite:
        return {"output": out_rel, "skipped": True}

    erp = Image.open(erp_in).convert("RGBA")
    source = Image.open(source_path).convert("RGBA")
    out = erp.copy()

    sticker = job.get("sticker", {})
    yaw_deg = float(sticker.get("yaw_deg", 0.0))
    pitch_deg = float(sticker.get("pitch_deg", -4.0))
    h_fov = math.radians(float(sticker.get("hFOV_deg", 104.0)))
    roll_deg = float(sticker.get("roll_deg", 0.0))
    if abs(roll_deg) > 1e-6:
        raise ValueError(f"roll is not implemented for {job['mapName']}: {roll_deg}")

    v_fov = 2.0 * math.atan(math.tan(h_fov / 2.0) * (source.height / source.width))
    tan_h = math.tan(h_fov / 2.0)
    tan_v = math.tan(v_fov / 2.0)
    right, up, forward = camera_basis(yaw_deg, pitch_deg)

    src_alpha = source.getchannel("A")
    src_bbox = src_alpha.getbbox()
    if src_bbox is None:
        raise ValueError(f"source plate has no visible alpha: {source_path}")

    pasted = 0
    out_px = out.load()
    for y in range(out.height):
        for x in range(out.width):
            direction = direction_from_erp(x, y, out.width, out.height)
            z = dot(direction, forward)
            if z <= 0:
                continue
            ndc_x = dot(direction, right) / (z * tan_h)
            ndc_y = dot(direction, up) / (z * tan_v)
            if ndc_x < -1.0 or ndc_x > 1.0 or ndc_y < -1.0 or ndc_y > 1.0:
                continue

            src_x = (ndc_x + 1.0) * 0.5 * (source.width - 1)
            src_y = (1.0 - ndc_y) * 0.5 * (source.height - 1)
            r, g, b, a = sample_rgba_nearest(source, src_x, src_y)
            if a == 0:
                continue

            if a == 255:
                out_px[x, y] = (r, g, b, 255)
            else:
                br, bg, bb, ba = out_px[x, y]
                alpha = a / 255.0
                out_px[x, y] = (
                    round(r * alpha + br * (1.0 - alpha)),
                    round(g * alpha + bg * (1.0 - alpha)),
                    round(b * alpha + bb * (1.0 - alpha)),
                    ba,
                )
            pasted += 1

    out_path.parent.mkdir(parents=True, exist_ok=True)
    out.convert("RGB").save(out_path)
    if update_low_power:
        job["aiErpOutput"] = erp_in_rel
        job["actualErpOutput"] = out_rel
    overlay = {
        "sourcePlate": job["sourcePlate"],
        "projection": "pinhole_rectilinear_to_equirectangular",
        "yaw_deg": yaw_deg,
        "pitch_deg": pitch_deg,
        "hFOV_deg": math.degrees(h_fov),
        "vFOV_deg": math.degrees(v_fov),
        "pastedErpPixels": pasted,
    }
    if update_low_power:
        job["exactSourcePlateOverlay"] = overlay
        job.setdefault("runtimeAssets", {}).setdefault("lowPower", {})
        job["runtimeAssets"]["lowPower"] = {
            "type": "erp360",
            "path": out_rel,
            "projection": "equirectangular",
            "sourcePlateReinserted": True,
        }
    return {"output": out_rel, "pasted": pasted, "skipped": False}


def main() -> None:
    parser = argparse.ArgumentParser(description="Bake exact extracted FFIX source plates back into generated ERPs.")
    parser.add_argument("--limit", type=int, default=None, help="Maximum completed jobs to process.")
    parser.add_argument("--force", action="store_true", help="Overwrite corrected ERPs if they already exist.")
    args = parser.parse_args()

    queue = json.loads(QUEUE.read_text(encoding="utf-8"))
    jobs = [job for job in queue["jobs"] if job.get("status") in {"erp_done", "splat_done", "complete"}]
    if args.limit is not None:
        jobs = jobs[: args.limit]

    results = []
    for index, job in enumerate(jobs, start=1):
        result = reinsert_plate(job, overwrite=args.force)
        results.append({"mapName": job["mapName"], **result})
        print(f"[{index}/{len(jobs)}] {job['mapName']} pasted={result.get('pasted', 0)} skipped={result['skipped']}")

    QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")
    print(json.dumps({"processed": len(results), "queue": str(QUEUE)}, indent=2))


if __name__ == "__main__":
    main()
