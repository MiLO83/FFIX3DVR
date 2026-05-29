from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import time
import urllib.request
import uuid
from datetime import datetime, timezone
from fractions import Fraction
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
COMFY = Path(r"C:\Users\rxcam\ComfyUI_portable\ComfyUI_windows_portable\ComfyUI")
FFIX = Path(r"F:\SteamLibrary\steamapps\common\FINAL FANTASY IX")
SERVER = "http://127.0.0.1:8188"
SOURCE_MOVIES = FFIX / "StreamingAssets" / "ma"
ARTIFACT_ROOT = ROOT / "artifacts" / "fmv-depth"
LOG_DIR = ARTIFACT_ROOT / "logs"


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def run(command: list[str]) -> None:
    subprocess.run(command, check=True)


def capture_json(command: list[str]) -> dict:
    completed = subprocess.run(command, check=True, capture_output=True, text=True)
    return json.loads(completed.stdout)


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
        get_json("/object_info/DepthAnythingV2Preprocessor")
        return True
    except Exception:
        return False


def ffprobe_movie(path: Path) -> dict:
    probe = capture_json(
        [
            "ffprobe",
            "-hide_banner",
            "-v",
            "error",
            "-select_streams",
            "v:0",
            "-count_frames",
            "-show_entries",
            "stream=width,height,r_frame_rate,time_base,duration,nb_frames,nb_read_frames",
            "-of",
            "json",
            str(path),
        ]
    )
    stream = probe["streams"][0]
    fps = Fraction(stream["r_frame_rate"])
    duration = float(stream.get("duration") or 0)
    decoded_frames = stream.get("nb_read_frames") or stream.get("nb_frames")
    expected_frames = int(decoded_frames) if decoded_frames and decoded_frames != "N/A" else None
    if expected_frames is None and duration:
        expected_frames = int(round(duration * float(fps)))
    return {
        "width": int(stream["width"]),
        "height": int(stream["height"]),
        "fps": stream["r_frame_rate"],
        "timeBase": stream.get("time_base"),
        "duration": duration,
        "expectedFrames": expected_frames,
    }


def movie_sources(ids: list[str]) -> list[Path]:
    requested = {item.lower().removesuffix(".bytes") for item in ids}
    movies = sorted(SOURCE_MOVIES.glob("*.bytes"))
    if not requested:
        return movies
    return [path for path in movies if path.stem.lower() in requested]


def extract_color_frames(movie: Path, color_dir: Path, expected_frames: int | None, force: bool, frame_limit: int) -> int:
    color_dir.mkdir(parents=True, exist_ok=True)
    existing = sorted(color_dir.glob("frame_*.png"))
    target_frames = frame_limit if frame_limit > 0 else expected_frames
    if existing and not force and (target_frames is None or len(existing) == target_frames):
        return len(existing)

    for path in existing:
        path.unlink()

    command = ["ffmpeg", "-hide_banner", "-y", "-i", str(movie), "-vsync", "0"]
    if frame_limit > 0:
        command.extend(["-frames:v", str(frame_limit)])
    command.append(str(color_dir / "frame_%06d.png"))
    run(command)
    return len(list(color_dir.glob("frame_*.png")))


def make_prompt(input_name: str, prefix: str, ckpt_name: str, resolution: int) -> dict:
    return {
        "1": {"class_type": "LoadImage", "inputs": {"image": input_name}},
        "2": {
            "class_type": "DepthAnythingV2Preprocessor",
            "inputs": {
                "image": ["1", 0],
                "ckpt_name": ckpt_name,
                "resolution": resolution,
            },
        },
        "3": {"class_type": "SaveImage", "inputs": {"images": ["2", 0], "filename_prefix": prefix}},
    }


def run_depth_frame(frame: Path, depth_path: Path, movie_key: str, ckpt_name: str, resolution: int, force: bool) -> dict:
    if depth_path.exists() and not force:
        return {"ok": True, "skipped": True}

    depth_path.parent.mkdir(parents=True, exist_ok=True)
    input_name = f"fmv_depth_{movie_key}_{frame.stem}.png"
    shutil.copy2(frame, COMFY / "input" / input_name)
    prefix = f"ffix_fmv_depth_{movie_key}_{frame.stem}"
    prompt = make_prompt(input_name, prefix, ckpt_name, resolution)
    prompt_id = str(uuid.uuid4())
    post_json("/prompt", {"prompt": prompt, "client_id": "ffix3dvr-fmv-depth", "prompt_id": prompt_id})

    for _ in range(720):
        history = get_json(f"/history/{prompt_id}")
        if prompt_id not in history:
            time.sleep(1)
            continue
        entry = history[prompt_id]
        status = entry.get("status", {})
        if status.get("status_str") == "error":
            return {"ok": False, "error": status}
        images = entry.get("outputs", {}).get("3", {}).get("images", [])
        if not images:
            return {"ok": False, "error": "missing SaveImage output"}
        image = images[-1]
        comfy_out = COMFY / "output" / image["filename"]
        shutil.copy2(comfy_out, depth_path)
        return {"ok": True, "skipped": False}
    return {"ok": False, "error": "timeout"}


def encode_depth_video(depth_dir: Path, output: Path, fps: str, force: bool) -> None:
    if output.exists() and not force:
        return
    output.parent.mkdir(parents=True, exist_ok=True)
    run(
        [
            "ffmpeg",
            "-hide_banner",
            "-y",
            "-framerate",
            fps,
            "-i",
            str(depth_dir / "frame_%06d.png"),
            "-an",
            "-c:v",
            "libtheora",
            "-pix_fmt",
            "yuv420p",
            "-q:v",
            "10",
            "-f",
            "ogg",
            str(output),
        ]
    )


def process_movie(movie: Path, args: argparse.Namespace) -> dict:
    movie_key = movie.stem
    movie_dir = ARTIFACT_ROOT / movie_key
    color_dir = movie_dir / "color_frames"
    depth_dir = movie_dir / "depth_frames"
    output_video = movie_dir / f"{movie_key}_depth.bytes"
    spec_path = movie_dir / "spec.json"
    progress_path = movie_dir / "progress.json"
    spec = ffprobe_movie(movie)
    spec.update(
        {
            "schema": 1,
            "generatedAt": now(),
            "movieKey": movie_key,
            "sourceMovie": str(movie),
            "colorReference": "color_frames/frame_%06d.png",
            "depthFrames": "depth_frames/frame_%06d.png",
            "depthVideo": output_video.name,
            "depthSource": "per-frame color reference via DepthAnythingV2Preprocessor",
            "rendering": {
                "mode": "walkmeshDepth",
                "walkmeshMode": "depth",
                "depthSurfaceMode": "farthest",
                "projectionYMode": "flipped",
                "frameSync": "movieMaterial.Frame",
                "colorReference": "decoded source movie frame with matching frame index",
                "depthReference": "depth frame generated from the matching color frame",
                "notes": "Equivalent to depth-gallery.html with Walkmesh Depth enabled; runtime projection must cast actors, masks, and any walkmesh-aware overlays against the depth-deformed movie surface, not the flat movie quad.",
            },
            "ckpt": args.ckpt,
            "resolution": args.resolution,
            "frameLimit": args.frame_limit,
        }
    )
    spec_path.parent.mkdir(parents=True, exist_ok=True)
    spec_path.write_text(json.dumps(spec, indent=2), encoding="utf-8")

    frame_count = extract_color_frames(movie, color_dir, spec["expectedFrames"], args.force_extract, args.frame_limit)
    frames = sorted(color_dir.glob("frame_*.png"))
    done = 0
    failed = 0
    skipped = 0
    for index, frame in enumerate(frames, start=1):
        depth_path = depth_dir / frame.name
        progress_path.write_text(
            json.dumps(
                {
                    "status": "running",
                    "updatedAt": now(),
                    "movieKey": movie_key,
                    "currentFrame": index,
                    "totalFrames": frame_count,
                    "done": done,
                    "failed": failed,
                    "skipped": skipped,
                },
                indent=2,
            ),
            encoding="utf-8",
        )
        result = run_depth_frame(frame, depth_path, movie_key, args.ckpt, args.resolution, args.force_depth)
        if result.get("ok"):
            done += 1
            if result.get("skipped"):
                skipped += 1
        else:
            failed += 1
            LOG_DIR.mkdir(parents=True, exist_ok=True)
            (LOG_DIR / f"{movie_key}_{frame.stem}_error.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
            if not args.keep_going:
                break
        print(json.dumps({"movie": movie_key, "frame": index, **result}), flush=True)

    if failed == 0 and done == frame_count and not args.no_encode:
        encode_depth_video(depth_dir, output_video, spec["fps"], args.force_encode)

    summary = {
        "status": "complete" if failed == 0 else "complete_with_errors",
        "updatedAt": now(),
        "movieKey": movie_key,
        "frames": frame_count,
        "done": done,
        "failed": failed,
        "skipped": skipped,
        "spec": str(spec_path),
        "depthVideo": str(output_video) if output_video.exists() else None,
    }
    progress_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    return summary


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate frame-exact FMV depth videos using each color video frame as reference.")
    parser.add_argument("--ids", nargs="*", default=[], help="Movie keys, e.g. FMV000 mbg101. Empty means all movies.")
    parser.add_argument("--limit", type=int, default=0, help="Maximum movies to process.")
    parser.add_argument("--frame-limit", type=int, default=0, help="Maximum frames per movie, for smoke tests.")
    parser.add_argument("--ckpt", default="depth_anything_v2_vitl.pth", help="Depth Anything V2 checkpoint.")
    parser.add_argument("--resolution", type=int, default=1024, help="Depth preprocessor resolution.")
    parser.add_argument("--force-extract", action="store_true", help="Re-extract color frames.")
    parser.add_argument("--force-depth", action="store_true", help="Regenerate existing depth frames.")
    parser.add_argument("--force-encode", action="store_true", help="Regenerate existing depth video.")
    parser.add_argument("--no-encode", action="store_true", help="Leave depth frames unencoded.")
    parser.add_argument("--keep-going", action="store_true", help="Continue after per-frame errors.")
    args = parser.parse_args()

    if not server_ready():
        raise RuntimeError("ComfyUI or DepthAnythingV2Preprocessor is not reachable at http://127.0.0.1:8188")

    movies = movie_sources(args.ids)
    if args.limit > 0:
        movies = movies[: args.limit]
    if not movies:
        raise RuntimeError("No matching FMV/MBG .bytes files found")

    summaries = []
    for movie in movies:
        try:
            summaries.append(process_movie(movie, args))
        except Exception as error:
            if not args.keep_going:
                raise
            summary = {"status": "failed", "updatedAt": now(), "movieKey": movie.stem, "error": str(error)}
            movie_dir = ARTIFACT_ROOT / movie.stem
            movie_dir.mkdir(parents=True, exist_ok=True)
            (movie_dir / "progress.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
            summaries.append(summary)
            print(json.dumps(summary), flush=True)
    (ARTIFACT_ROOT / "summary.json").write_text(json.dumps({"updatedAt": now(), "movies": summaries}, indent=2), encoding="utf-8")
    print(json.dumps(summaries, indent=2))


if __name__ == "__main__":
    main()
