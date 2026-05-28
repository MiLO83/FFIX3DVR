from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter, ImageStat

from reinsert_source_plate_into_erp import camera_basis, direction_from_erp, dot, sample_rgba_nearest


ROOT = Path(__file__).resolve().parents[1]
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"


def rel(path: Path) -> str:
    return str(path.resolve().relative_to(ROOT)).replace("\\", "/")


def source_projection_mask(job: dict, erp_size: tuple[int, int], source: Image.Image) -> Image.Image:
    width, height = erp_size
    mask = Image.new("L", erp_size, 0)
    mask_px = mask.load()

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

    for y in range(height):
        for x in range(width):
            direction = direction_from_erp(x, y, width, height)
            z = dot(direction, forward)
            if z <= 0:
                continue
            ndc_x = dot(direction, right) / (z * tan_h)
            ndc_y = dot(direction, up) / (z * tan_v)
            if ndc_x < -1.0 or ndc_x > 1.0 or ndc_y < -1.0 or ndc_y > 1.0:
                continue

            src_x = (ndc_x + 1.0) * 0.5 * (source.width - 1)
            src_y = (1.0 - ndc_y) * 0.5 * (source.height - 1)
            _, _, _, a = sample_rgba_nearest(source, src_x, src_y)
            if a > 0:
                mask_px[x, y] = 255
    return mask


def masked_stats(image: Image.Image, mask: Image.Image) -> tuple[list[float], list[float]]:
    if mask.getbbox() is None:
        raise ValueError("empty color sample mask")
    stat = ImageStat.Stat(image, mask)
    return stat.mean, [max(v, 8.0) for v in stat.stddev]


def luminance_from_mean(mean: list[float]) -> float:
    return 0.2126 * mean[0] + 0.7152 * mean[1] + 0.0722 * mean[2]


def linear_color_transfer(image: Image.Image, source_mean: list[float], source_std: list[float], target_mean: list[float], target_std: list[float]) -> Image.Image:
    channels = image.split()
    corrected_channels = []
    for channel, src_m, src_s, tgt_m, tgt_s in zip(channels, source_mean, source_std, target_mean, target_std):
        lut = []
        for value in range(256):
            out = (value - src_m) / src_s * tgt_s + tgt_m
            lut.append(max(0, min(255, int(round(out)))))
        corrected_channels.append(channel.point(lut))
    return Image.merge("RGB", corrected_channels)


def contrast_about_mean(image: Image.Image, mean: list[float], contrast: float) -> Image.Image:
    channels = image.split()
    corrected_channels = []
    for channel, channel_mean in zip(channels, mean):
        lut = []
        for value in range(256):
            out = channel_mean + (value - channel_mean) * contrast
            lut.append(max(0, min(255, int(round(out)))))
        corrected_channels.append(channel.point(lut))
    return Image.merge("RGB", corrected_channels)


def harmonize(
    erp_path: Path,
    source_path: Path,
    job: dict,
    output_path: Path,
    edge_px: int,
    global_strength: float,
    seam_strength: float,
    target_sample: str,
    contrast: float,
    force: bool,
) -> dict:
    if output_path.exists() and not force:
        return {"output": rel(output_path), "skipped": True}

    erp = Image.open(erp_path).convert("RGB")
    source = Image.open(source_path).convert("RGBA")
    plate_mask = source_projection_mask(job, erp.size, source)
    mask_dir = output_path.parent
    mask_dir.mkdir(parents=True, exist_ok=True)
    plate_mask_path = mask_dir / "source_projection_mask.png"
    plate_mask.save(plate_mask_path)

    eroded = plate_mask.filter(ImageFilter.MinFilter(edge_px * 2 + 1))
    dilated = plate_mask.filter(ImageFilter.MaxFilter(edge_px * 2 + 1))
    inside_edge = ImageChops.subtract(plate_mask, eroded)
    outside_edge = ImageChops.subtract(dilated, plate_mask)

    target_mask = plate_mask if target_sample == "plate" else inside_edge
    inside_mean, inside_std = masked_stats(erp, target_mask)
    outside_mean, outside_std = masked_stats(erp, outside_edge)

    corrected = linear_color_transfer(erp, outside_mean, outside_std, inside_mean, inside_std)
    if abs(contrast - 1.0) > 1e-6:
        corrected = contrast_about_mean(corrected, inside_mean, contrast)

    outside_mask = ImageChops.invert(plate_mask)
    global_mask = outside_mask.point(lambda p: int(round(p * global_strength)))
    seam_mask = plate_mask.filter(ImageFilter.GaussianBlur(edge_px * 1.5)).point(lambda p: int(round(p * seam_strength)))
    seam_mask = ImageChops.multiply(seam_mask, outside_mask)
    weight_mask = ImageChops.lighter(global_mask, seam_mask)
    out = Image.composite(corrected, erp, weight_mask)
    out = Image.composite(erp, out, plate_mask)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    out.save(output_path)

    after_outside_mean, _ = masked_stats(out, outside_edge)
    return {
        "output": rel(output_path),
        "sourceProjectionMask": rel(plate_mask_path),
        "skipped": False,
        "edgePx": edge_px,
        "globalStrength": global_strength,
        "seamStrength": seam_strength,
        "targetSample": target_sample,
        "contrast": contrast,
        "before": {
            "insideEdgeMeanRgb": [round(v, 3) for v in inside_mean],
            "outsideEdgeMeanRgb": [round(v, 3) for v in outside_mean],
            "insideEdgeLuma": round(luminance_from_mean(inside_mean), 3),
            "outsideEdgeLuma": round(luminance_from_mean(outside_mean), 3),
        },
        "after": {
            "outsideEdgeMeanRgb": [round(v, 3) for v in after_outside_mean],
            "outsideEdgeLuma": round(luminance_from_mean(after_outside_mean), 3),
        },
    }


def source_erp_for_job(job: dict) -> str:
    return (
        job.get("ffixInfill", {}).get("exactErpOutput")
        or job.get("actualErpOutput")
        or job.get("aiErpOutput")
        or job["erpOutput"]
    )


def selectable_jobs(queue: dict, limit: int | None, start_index: int, force: bool) -> list[dict]:
    rows = []
    for job in queue.get("jobs", []):
        if int(job.get("index", 0)) < start_index:
            continue
        if not force and job.get("colorHarmonized", {}).get("status") == "complete":
            continue
        if not (ROOT / source_erp_for_job(job)).exists():
            continue
        rows.append(job)
        if limit is not None and len(rows) >= limit:
            break
    return rows


def main() -> None:
    parser = argparse.ArgumentParser(description="Color harmonize generated ERP outfill against the exact FFIX source plate.")
    parser.add_argument("--limit", type=int, default=1)
    parser.add_argument("--start-index", type=int, default=1)
    parser.add_argument("--edge-px", type=int, default=48)
    parser.add_argument("--global-strength", type=float, default=0.72)
    parser.add_argument("--seam-strength", type=float, default=1.0)
    parser.add_argument("--target-sample", choices=["edge", "plate"], default="edge")
    parser.add_argument("--contrast", type=float, default=1.0)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()

    queue = json.loads(QUEUE.read_text(encoding="utf-8"))
    jobs = selectable_jobs(queue, args.limit, args.start_index, args.force)
    results = []
    for offset, job in enumerate(jobs, start=1):
        print(f"[{offset}/{len(jobs)}] {job['mapName']}")
        try:
            map_slug = job["mapName"].lower()
            erp_path = ROOT / source_erp_for_job(job)
            source_path = ROOT / job["sourcePlate"]
            output_path = ROOT / "artifacts" / "all-fields" / "erp_color" / map_slug / "field_erp.png"
            result = harmonize(
                erp_path,
                source_path,
                job,
                output_path,
                args.edge_px,
                args.global_strength,
                args.seam_strength,
                args.target_sample,
                args.contrast,
                args.force,
            )
            job["colorHarmonized"] = {"status": "complete", "inputErp": rel(erp_path), **result}
            if job.get("ffixInfill", {}).get("status") == "complete":
                job["ffixInfill"]["colorMatchedErpOutput"] = result["output"]
            job.setdefault("runtimeAssets", {})["lowPower"] = {
                "type": "erp360",
                "path": result["output"],
                "projection": "equirectangular",
                "sourcePlateReinserted": True,
                "colorHarmonized": True,
            }
            results.append({"mapName": job["mapName"], **result})
        except Exception as exc:
            job["colorHarmonized"] = {"status": "error", "error": str(exc)}
            results.append({"mapName": job["mapName"], "status": "error", "error": str(exc)})
            if args.limit == 1:
                raise
        finally:
            QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")
    print(json.dumps({"processed": len(results), "results": results}, indent=2))


if __name__ == "__main__":
    main()
