from __future__ import annotations

import argparse
import json
import shutil
import sys
import traceback
from datetime import datetime, timezone
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"
DEFAULT_REPO = ROOT / ".external" / "TripoSplat"
DEFAULT_CKPT_ROOT = DEFAULT_REPO / "ckpts"
OUTPUT_ROOT = ROOT / "artifacts" / "all-fields" / "splat_triposplat"
DEFAULT_PUBLIC_ROOT = ROOT / "viewer" / "public" / "assets" / "maps"


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def rel(path: Path) -> str:
    return str(path.resolve().relative_to(ROOT)).replace("\\", "/")


def scene_id(job: dict) -> str:
    return str(job["mapName"]).lower()


def load_queue() -> dict:
    return json.loads(QUEUE.read_text(encoding="utf-8"))


def save_queue(queue: dict) -> None:
    QUEUE.write_text(json.dumps(queue, indent=2), encoding="utf-8")


def select_jobs(queue: dict, ids: list[str], limit: int, force: bool) -> list[dict]:
    requested = {entry.lower() for entry in ids}
    jobs = []
    for job in queue.get("jobs", []):
        job_id = scene_id(job)
        if requested and job_id not in requested:
            continue
        if not requested and not force and job.get("triposplat", {}).get("status") == "complete":
            continue
        jobs.append(job)
    return jobs[:limit] if limit > 0 else jobs


def resolve_input(job: dict, input_source: str) -> Path:
    candidates: list[str | None]
    if input_source == "source":
        candidates = [job.get("sourcePlate")]
    elif input_source == "erp":
        candidates = [job.get("actualErpOutput"), job.get("erpOutput")]
    elif input_source == "color":
        candidates = [
            job.get("ffixInfill", {}).get("colorMatchedErpOutput"),
            job.get("colorHarmonized", {}).get("output"),
            job.get("actualErpOutput"),
            job.get("erpOutput"),
            job.get("sourcePlate"),
        ]
    else:
        candidates = [
            job.get("ffixInfill", {}).get("colorMatchedErpOutput"),
            job.get("colorHarmonized", {}).get("output"),
            job.get("actualErpOutput"),
            job.get("erpOutput"),
            job.get("sourcePlate"),
        ]

    for candidate in candidates:
        if not candidate:
            continue
        path = ROOT / candidate
        if path.exists():
            return path
    raise FileNotFoundError(f"No usable input image for {scene_id(job)} using source={input_source}")


def preserve_full_plate_alpha(input_path: Path, output_path: Path) -> Path:
    image = Image.open(input_path).convert("RGBA")
    alpha = image.getchannel("A")
    extrema = alpha.getextrema()
    if extrema[0] == 255 and extrema[1] == 255:
        image.putalpha(254)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    image.save(output_path)
    return output_path


def require_file(path: Path, label: str) -> str:
    if not path.exists():
        raise FileNotFoundError(f"{label} missing: {path}")
    return str(path)


def make_pipeline(repo: Path, ckpt_root: Path, device: str):
    if not repo.exists():
        raise FileNotFoundError(
            f"TripoSplat repo missing: {repo}. Clone https://github.com/VAST-AI-Research/TripoSplat there or pass --repo."
        )
    sys.path.insert(0, str(repo))
    from triposplat import TripoSplatPipeline  # type: ignore

    return TripoSplatPipeline(
        ckpt_path=require_file(ckpt_root / "diffusion_models" / "triposplat_fp16.safetensors", "TripoSplat diffusion model"),
        decoder_path=require_file(ckpt_root / "vae" / "triposplat_vae_decoder_fp16.safetensors", "TripoSplat decoder"),
        dinov3_path=require_file(ckpt_root / "clip_vision" / "dino_v3_vit_h.safetensors", "DINOv3 vision model"),
        flux2_vae_encoder_path=require_file(ckpt_root / "vae" / "flux2-vae.safetensors", "Flux2 VAE encoder"),
        rmbg_path=require_file(ckpt_root / "background_removal" / "birefnet.safetensors", "BiRefNet background remover"),
        device=device,
    )


def copy_to_public(scene: str, out_dir: Path, public_root: Path, ply_path: Path, splat_path: Path | None, prepared_path: Path) -> dict:
    public_dir = public_root / scene
    public_dir.mkdir(parents=True, exist_ok=True)
    public_ply = public_dir / "field_triposplat.ply"
    public_prepared = public_dir / "field_triposplat_preprocessed.webp"
    shutil.copy2(ply_path, public_ply)
    shutil.copy2(prepared_path, public_prepared)

    payload = {
        "ply": rel(public_ply),
        "prepared": rel(public_prepared),
    }
    if splat_path is not None and splat_path.exists():
        public_splat = public_dir / "field_triposplat.splat"
        shutil.copy2(splat_path, public_splat)
        payload["splat"] = rel(public_splat)
    return payload


def run_job(pipe, job: dict, args: argparse.Namespace) -> dict:
    scene = scene_id(job)
    out_dir = OUTPUT_ROOT / scene
    ply_path = out_dir / "field_triposplat.ply"
    splat_path = out_dir / "field_triposplat.splat"
    prepared_path = out_dir / "field_triposplat_preprocessed.webp"
    input_path = resolve_input(job, args.input)
    prepared_input = out_dir / "input_preserve_alpha.png"

    if ply_path.exists() and not args.force:
        return {
            "status": "complete",
            "skipped": True,
            "input": rel(input_path),
            "ply": rel(ply_path),
            "splat": rel(splat_path) if splat_path.exists() else None,
        }

    out_dir.mkdir(parents=True, exist_ok=True)
    triposplat_input = input_path if args.allow_background_removal else preserve_full_plate_alpha(input_path, prepared_input)
    gaussian, prepared = pipe.run(
        str(triposplat_input),
        seed=args.seed,
        steps=args.steps,
        guidance_scale=args.guidance_scale,
        shift=args.shift,
        num_gaussians=args.num_gaussians,
        erode_radius=args.erode_radius,
        show_progress=True,
    )
    prepared.save(prepared_path)
    gaussian.save_ply(ply_path)
    if not args.no_splat:
        gaussian.save_splat(splat_path)

    public = {}
    if args.publish:
        public = copy_to_public(scene, out_dir, args.public_root, ply_path, None if args.no_splat else splat_path, prepared_path)

    return {
        "status": "complete",
        "skipped": False,
        "input": rel(input_path),
        "triposplatInput": rel(triposplat_input),
        "prepared": rel(prepared_path),
        "ply": rel(ply_path),
        "splat": rel(splat_path) if splat_path.exists() else None,
        "public": public,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate TripoSplat field-background splats for FFIX3DVR preview/runtime experiments.")
    parser.add_argument("--ids", nargs="*", default=[], help="Scene ids to process, e.g. fbg_n00_tshp_map001_th_cgr_0.")
    parser.add_argument("--limit", type=int, default=1, help="Maximum scenes when --ids is omitted. 0 means all selected scenes.")
    parser.add_argument("--repo", type=Path, default=DEFAULT_REPO, help="Local VAST-AI-Research/TripoSplat checkout.")
    parser.add_argument("--ckpt-root", type=Path, default=DEFAULT_CKPT_ROOT, help="Directory containing TripoSplat ckpts/ subfolders.")
    parser.add_argument("--device", default="cuda", help="Torch device, normally cuda.")
    parser.add_argument("--input", choices=["source", "erp", "color", "auto"], default="source", help="Image source used for TripoSplat.")
    parser.add_argument("--num-gaussians", type=int, default=131072, help="Gaussian count, 32768..262144.")
    parser.add_argument("--steps", type=int, default=20)
    parser.add_argument("--guidance-scale", type=float, default=3.0)
    parser.add_argument("--shift", type=float, default=3.0)
    parser.add_argument("--seed", type=int, default=42)
    parser.add_argument("--erode-radius", type=int, default=0, help="Alpha erosion radius. 0 preserves the full scene plate edge.")
    parser.add_argument("--allow-background-removal", action="store_true", help="Let TripoSplat segment the image. Off by default for FFIX scene plates.")
    parser.add_argument("--no-splat", action="store_true", help="Only export PLY, not .splat.")
    parser.add_argument("--publish", action="store_true", help="Also copy results into viewer/public/assets/maps/<scene>.")
    parser.add_argument("--public-root", type=Path, default=DEFAULT_PUBLIC_ROOT)
    parser.add_argument("--force", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    queue = load_queue()
    jobs = select_jobs(queue, args.ids, args.limit, args.force)
    print(json.dumps({"selected": [scene_id(job) for job in jobs], "count": len(jobs), "input": args.input}, indent=2))
    if args.dry_run:
        return

    pipe = make_pipeline(args.repo, args.ckpt_root, args.device)
    results = []
    for job in jobs:
        scene = scene_id(job)
        job["triposplat"] = {
            "status": "running",
            "updatedAt": now(),
            "inputMode": args.input,
            "numGaussians": args.num_gaussians,
        }
        save_queue(queue)
        try:
            result = run_job(pipe, job, args)
            job["triposplat"] = {
                **result,
                "updatedAt": now(),
                "repo": str(args.repo),
                "numGaussians": args.num_gaussians,
                "inputMode": args.input,
                "backgroundRemoval": args.allow_background_removal,
            }
            job.setdefault("runtimeAssets", {})["highPowerTripoSplat"] = {
                "type": "gaussian_splat_ply",
                "path": result["ply"],
                "variant": "triposplat_scene_plate",
                "vertices": args.num_gaussians,
            }
            results.append({"scene": scene, **result})
        except Exception as error:
            job["triposplat"] = {
                "status": "error",
                "updatedAt": now(),
                "error": str(error),
                "traceback": traceback.format_exc(),
            }
            results.append({"scene": scene, "status": "error", "error": str(error)})
            print(f"[error] {scene}: {error}", file=sys.stderr)
        save_queue(queue)

    print(json.dumps({"processed": len(results), "results": results}, indent=2))


if __name__ == "__main__":
    main()
