from __future__ import annotations

import argparse
import json
import math
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter


ROOT = Path(__file__).resolve().parents[1]
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"


@dataclass(frozen=True)
class PoseSample:
    name: str
    position: tuple[float, float, float]
    delta: tuple[float, float, float]
    triangle_index: int | None
    screen_shift: tuple[int, int]


def rel(path: Path) -> str:
    return str(path.resolve().relative_to(ROOT)).replace("\\", "/")


def load_json(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def clamp(value: float, lo: int, hi: int) -> int:
    return max(lo, min(hi, int(round(value))))


def normalize_screen_shift(dx: float, dy: float, max_shift_px: int) -> tuple[int, int]:
    length = math.hypot(dx, dy)
    if length > max_shift_px and length > 0:
        scale = max_shift_px / length
        dx *= scale
        dy *= scale
    return int(round(dx)), int(round(dy))


def camera_project(camera: dict, point: tuple[float, float, float], t_override: tuple[float, float, float] | None = None) -> tuple[float, float] | None:
    r = camera["r"]
    t = t_override if t_override is not None else camera["t"]
    vx = (r[0][0] * point[0] + r[0][1] * point[1] + r[0][2] * point[2]) / 4096.0 + t[0]
    vy = (r[1][0] * point[0] + r[1][1] * point[1] + r[1][2] * point[2]) / 4096.0 + t[1]
    vz = (r[2][0] * point[0] + r[2][1] * point[1] + r[2][2] * point[2]) / 4096.0 + t[2]
    if abs(vz) < 1e-5:
        return None
    cx = camera["w"] * 0.5 + camera.get("center_offset", [0, 0])[0]
    cy = camera["h"] * 0.5 + camera.get("center_offset", [0, 0])[1]
    sx = cx + camera["proj"] * vx / vz
    sy = cy - camera["proj"] * vy / vz
    return sx, sy


def translated_camera_t(camera: dict, delta: tuple[float, float, float]) -> tuple[float, float, float]:
    r = camera["r"]
    # Moving the camera through the world has the opposite effect on the world-to-camera translation.
    return (
        camera["t"][0] - (r[0][0] * delta[0] + r[0][1] * delta[1] + r[0][2] * delta[2]) / 4096.0,
        camera["t"][1] - (r[1][0] * delta[0] + r[1][1] * delta[1] + r[1][2] * delta[2]) / 4096.0,
        camera["t"][2] - (r[2][0] * delta[0] + r[2][1] * delta[1] + r[2][2] * delta[2]) / 4096.0,
    )


def screen_shift_for_pose(
    camera: dict,
    anchor: tuple[float, float, float],
    delta: tuple[float, float, float],
    source_size: tuple[int, int],
    max_shift_px: int,
) -> tuple[int, int]:
    base = camera_project(camera, anchor)
    moved = camera_project(camera, anchor, translated_camera_t(camera, delta))
    if base is None or moved is None:
        return normalize_screen_shift(delta[0] / 24.0, -delta[2] / 48.0, max_shift_px)
    scale_x = source_size[0] / max(camera["w"], 1)
    scale_y = source_size[1] / max(camera["h"], 1)
    return normalize_screen_shift((moved[0] - base[0]) * scale_x, (moved[1] - base[1]) * scale_y, max_shift_px)


def dist_xz(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
    return math.hypot(a[0] - b[0], a[2] - b[2])


def triangle_center(triangle: dict) -> tuple[float, float, float]:
    verts = triangle.get("worldVertices") or []
    if len(verts) == 3:
        return (
            sum(v[0] for v in verts) / 3.0,
            sum(v[1] for v in verts) / 3.0,
            sum(v[2] for v in verts) / 3.0,
        )
    center = triangle.get("center", [0, 0, 0])
    return float(center[0]), float(center[1]), float(center[2])


def nearest_walkmesh_center(
    walkmesh: dict,
    target: tuple[float, float, float],
    active_floor: int,
) -> tuple[tuple[float, float, float], int | None]:
    best: tuple[float, tuple[float, float, float], int | None] | None = None
    for tri in walkmesh.get("triangles", []):
        if tri.get("floorIndex") != active_floor:
            continue
        center = triangle_center(tri)
        score = dist_xz(center, target)
        if best is None or score < best[0]:
            best = (score, center, tri.get("triIndex"))
    if best is None:
        return target, None
    return best[1], best[2]


def build_pose_samples(
    walkmesh: dict,
    camera: dict,
    source_size: tuple[int, int],
    radius: int,
    max_shift_px: int,
) -> list[PoseSample]:
    char = walkmesh.get("charPos") or [0, 0, 0]
    anchor = (float(char[0]), float(char[1]), float(char[2]))
    active_floor = int(walkmesh.get("activeFloor", 0))

    desired = [
        ("anchor", (0.0, 0.0, 0.0)),
        ("left", (-radius, 0.0, 0.0)),
        ("right", (radius, 0.0, 0.0)),
        ("forward", (0.0, 0.0, radius)),
        ("back", (0.0, 0.0, -radius)),
        ("left_forward", (-radius, 0.0, radius)),
        ("right_forward", (radius, 0.0, radius)),
        ("left_back", (-radius, 0.0, -radius)),
        ("right_back", (radius, 0.0, -radius)),
    ]
    samples: list[PoseSample] = []
    seen: set[tuple[int, int, int]] = set()
    for name, offset in desired:
        target = (anchor[0] + offset[0], anchor[1] + offset[1], anchor[2] + offset[2])
        position, tri_index = nearest_walkmesh_center(walkmesh, target, active_floor)
        if name == "anchor":
            position = anchor
        key = (round(position[0]), round(position[1]), round(position[2]))
        if key in seen and name != "anchor":
            continue
        seen.add(key)
        delta = (position[0] - anchor[0], position[1] - anchor[1], position[2] - anchor[2])
        shift = screen_shift_for_pose(camera, anchor, delta, source_size, max_shift_px)
        samples.append(PoseSample(name, position, delta, tri_index, shift))
    return samples


def shifted_luminance(image: Image.Image, dx: int, dy: int) -> Image.Image:
    out = Image.new("L", image.size, 0)
    src_x0 = max(0, -dx)
    src_y0 = max(0, -dy)
    src_x1 = min(image.width, image.width - dx)
    src_y1 = min(image.height, image.height - dy)
    if src_x1 <= src_x0 or src_y1 <= src_y0:
        return out
    crop = image.crop((src_x0, src_y0, src_x1, src_y1))
    out.paste(crop, (src_x0 + dx, src_y0 + dy))
    return out


def threshold_mask(image: Image.Image, cutoff: int = 8) -> Image.Image:
    return image.point(lambda p: 255 if p > cutoff else 0, mode="L")


def make_source_infill_mask(source: Image.Image, shift: tuple[int, int], edge_band_px: int) -> Image.Image:
    alpha = source.getchannel("A")
    visible = threshold_mask(alpha, 1)
    transparent = visible.point(lambda p: 0 if p else 255, mode="L")
    dilated = visible.filter(ImageFilter.MaxFilter(edge_band_px * 2 + 1))
    eroded = visible.filter(ImageFilter.MinFilter(edge_band_px * 2 + 1))
    edge_band = ImageChops.subtract(dilated, eroded)
    visual_edges = source.convert("L").filter(ImageFilter.FIND_EDGES)
    visual_edges = threshold_mask(visual_edges, 112)
    visual_edges = visual_edges.filter(ImageFilter.MaxFilter(7))
    shifted = shifted_luminance(visible, shift[0], shift[1])
    disocclusion = threshold_mask(ImageChops.difference(visible, shifted), 1)
    return ImageChops.lighter(ImageChops.lighter(ImageChops.lighter(transparent, edge_band), visual_edges), disocclusion)


def direction_from_erp(x: int, y: int, width: int, height: int) -> tuple[float, float, float]:
    yaw = ((x + 0.5) / width - 0.5) * 2.0 * math.pi
    pitch = (0.5 - (y + 0.5) / height) * math.pi
    cp = math.cos(pitch)
    return cp * math.sin(yaw), math.sin(pitch), cp * math.cos(yaw)


def camera_basis(yaw_deg: float, pitch_deg: float) -> tuple[tuple[float, float, float], ...]:
    yaw = math.radians(yaw_deg)
    pitch = math.radians(pitch_deg)
    forward = (math.cos(pitch) * math.sin(yaw), math.sin(pitch), math.cos(pitch) * math.cos(yaw))
    right = (math.cos(yaw), 0.0, -math.sin(yaw))
    up = (
        forward[1] * right[2] - forward[2] * right[1],
        forward[2] * right[0] - forward[0] * right[2],
        forward[0] * right[1] - forward[1] * right[0],
    )
    return right, up, forward


def dot(a: tuple[float, float, float], b: tuple[float, float, float]) -> float:
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def project_source_mask_to_erp(mask: Image.Image, job: dict, size: tuple[int, int] = (2048, 1024)) -> Image.Image:
    sticker = job.get("sticker", {})
    yaw_deg = float(sticker.get("yaw_deg", 0.0))
    pitch_deg = float(sticker.get("pitch_deg", -4.0))
    h_fov = math.radians(float(sticker.get("hFOV_deg", 104.0)))
    v_fov = 2.0 * math.atan(math.tan(h_fov / 2.0) * (mask.height / mask.width))
    tan_h = math.tan(h_fov / 2.0)
    tan_v = math.tan(v_fov / 2.0)
    right, up, forward = camera_basis(yaw_deg, pitch_deg)

    out = Image.new("L", size, 0)
    out_px = out.load()
    mask_px = mask.load()
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
            src_x = clamp((ndc_x + 1.0) * 0.5 * (mask.width - 1), 0, mask.width - 1)
            src_y = clamp((1.0 - ndc_y) * 0.5 * (mask.height - 1), 0, mask.height - 1)
            out_px[x, y] = mask_px[src_x, src_y]
    return out


def camera_index_from_job(job: dict) -> int:
    source_name = Path(job["sourcePlate"]).stem
    if "_cam" in source_name:
        try:
            return int(source_name.rsplit("_cam", 1)[1])
        except ValueError:
            pass
    return int(job.get("cameraIndex", 0))


def prepare_job(job: dict, radius: int, edge_band_px: int, max_shift_px: int, force: bool) -> dict:
    source_path = ROOT / job["sourcePlate"]
    source_dir = source_path.parent
    cameras_path = source_dir / "cameras.json"
    walkmesh_path = source_dir / "walkmesh.json"
    if not source_path.exists():
        raise FileNotFoundError(f"source plate missing: {source_path}")
    if not cameras_path.exists():
        raise FileNotFoundError(f"camera metadata missing: {cameras_path}")
    if not walkmesh_path.exists():
        raise FileNotFoundError(f"walkmesh missing: {walkmesh_path}")

    source = Image.open(source_path).convert("RGBA")
    cameras = load_json(cameras_path)["cameras"]
    camera_index = min(max(camera_index_from_job(job), 0), len(cameras) - 1)
    camera = cameras[camera_index]
    walkmesh = load_json(walkmesh_path)
    samples = build_pose_samples(walkmesh, camera, source.size, radius, max_shift_px)

    out_dir = source_dir / "camera_moved_infill"
    out_dir.mkdir(parents=True, exist_ok=True)
    mask_rows = []
    for index, sample in enumerate(samples):
        source_mask = make_source_infill_mask(source, sample.screen_shift, edge_band_px)
        source_mask_path = out_dir / f"source_mask_{index:02d}_{sample.name}.png"
        erp_mask_path = out_dir / f"erp_mask_{index:02d}_{sample.name}.png"
        if force or not source_mask_path.exists():
            source_mask.save(source_mask_path)
        if force or not erp_mask_path.exists():
            project_source_mask_to_erp(source_mask, job).save(erp_mask_path)
        mask_rows.append(
            {
                "name": sample.name,
                "sourceMask": rel(source_mask_path),
                "erpMask": rel(erp_mask_path),
                "screenShiftPx": list(sample.screen_shift),
            }
        )

    camera_path = {
        "version": 1,
        "method": "memoria_bgs_camera_plus_bgi_walkmesh_perturbations",
        "sourcePlate": rel(source_path),
        "cameraMetadata": rel(cameras_path),
        "walkmesh": rel(walkmesh_path),
        "cameraIndex": camera_index,
        "radiusWorldUnits": radius,
        "edgeBandPx": edge_band_px,
        "maxShiftPx": max_shift_px,
        "anchorCharPos": walkmesh.get("charPos"),
        "samples": [
            {
                "name": sample.name,
                "position": [round(v, 3) for v in sample.position],
                "deltaFromAnchor": [round(v, 3) for v in sample.delta],
                "triangleIndex": sample.triangle_index,
                "screenShiftPx": list(sample.screen_shift),
            }
            for sample in samples
        ],
        "masks": mask_rows,
    }
    camera_path_path = out_dir / "camera_path.json"
    camera_path_path.write_text(json.dumps(camera_path, indent=2), encoding="utf-8")

    return {
        "status": "prepared",
        "cameraPath": rel(camera_path_path),
        "poseCount": len(samples),
        "maskCount": len(mask_rows),
        "cameraIndex": camera_index,
        "radiusWorldUnits": radius,
        "usesWalkmesh": True,
    }


def selectable_jobs(queue: dict, limit: int | None, start_index: int, force: bool) -> list[dict]:
    rows = []
    for job in queue.get("jobs", []):
        if int(job.get("index", 0)) < start_index:
            continue
        if not force and job.get("cameraMovedInfill", {}).get("status") == "prepared":
            continue
        rows.append(job)
        if limit is not None and len(rows) >= limit:
            break
    return rows


def main() -> None:
    parser = argparse.ArgumentParser(description="Prepare FFIX walkmesh camera perturbations and occlusion infill masks.")
    parser.add_argument("--limit", type=int, default=1)
    parser.add_argument("--start-index", type=int, default=1)
    parser.add_argument("--radius", type=int, default=256, help="Walkmesh search radius in FFIX world units.")
    parser.add_argument("--edge-band-px", type=int, default=24)
    parser.add_argument("--max-shift-px", type=int, default=96)
    parser.add_argument("--force", action="store_true")
    args = parser.parse_args()

    queue = load_json(QUEUE)
    jobs = selectable_jobs(queue, args.limit, args.start_index, args.force)
    results = []
    for offset, job in enumerate(jobs, start=1):
        print(f"[{offset}/{len(jobs)}] {job['mapName']}")
        try:
            result = prepare_job(job, args.radius, args.edge_band_px, args.max_shift_px, args.force)
            job["cameraMovedInfill"] = result
            results.append({"mapName": job["mapName"], **result})
        except Exception as exc:
            job["cameraMovedInfill"] = {"status": "error", "error": str(exc)}
            results.append({"mapName": job["mapName"], "status": "error", "error": str(exc)})
            if args.limit == 1:
                raise
    QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")
    print(json.dumps({"processed": len(results), "results": results}, indent=2))


if __name__ == "__main__":
    main()
