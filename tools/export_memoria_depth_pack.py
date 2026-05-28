#!/usr/bin/env python3
"""Export FFIX3DVR browser assets as a Memoria-compatible mod asset pack."""

from __future__ import annotations

import argparse
import json
import shutil
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from xml.sax.saxutils import escape


ROOT = Path(__file__).resolve().parents[1]
VIEWER_ASSETS = ROOT / "viewer" / "public" / "assets"
INDEX_PATH = VIEWER_ASSETS / "index.json"
DEFAULT_OUTPUT = ROOT / "artifacts" / "memoria-mod" / "FF9DepthVR"
DATA_ROOT = Path("Data") / "FF9DepthVR"
MOD_NAME = "FF9 Depth VR"
MOD_FOLDER = "FF9DepthVR"


def load_json(path: Path) -> Any:
    return json.loads(path.read_text(encoding="utf-8-sig"))


def asset_url_to_path(url: str) -> Path:
    relative = url.lstrip("/").replace("/", "\\")
    if not relative.lower().startswith("assets\\"):
        raise ValueError(f"Unexpected asset URL: {url}")
    return VIEWER_ASSETS.parent / relative


def copy_if_present(source: Path, destination: Path) -> bool:
    if not source.exists():
        return False
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    return True


def scene_asset_path(scene_id: str, filename: str) -> str:
    return (DATA_ROOT / "scenes" / scene_id / filename).as_posix()


def write_mod_description(output: Path, manifest: dict[str, Any]) -> None:
    description = (
        "Prototype depth-reconstructed field backgrounds for Final Fantasy IX. "
        "Uses all extracted original source plates plus Depth Anything maps and "
        "a Memoria runtime renderer; vanilla fields remain the fallback if an asset is missing."
    )
    xml = f"""<Mod>
    <Name>{escape(MOD_NAME)}</Name>
    <Version>0.1.0</Version>
    <Author>rxcam + Codex</Author>
    <InstallationPath>{escape(MOD_FOLDER)}</InstallationPath>
    <Category>Graphics</Category>
    <Description>{escape(description)}</Description>
    <ReleaseDate>{escape(datetime.now(timezone.utc).date().isoformat())}</ReleaseDate>
    <FullSize>{sum(path.stat().st_size for path in output.rglob('*') if path.is_file())}</FullSize>
</Mod>
"""
    (output / "ModDescription.xml").write_text(xml, encoding="utf-8")


def write_mod_file_list(output: Path) -> None:
    streaming_root = output / "StreamingAssets"
    entries: list[str] = []
    for path in sorted(streaming_root.rglob("*")):
        if path.is_file():
            entries.append(path.relative_to(streaming_root).as_posix())
    (output / "ModFileList.txt").write_text("\n".join(entries) + "\n", encoding="utf-8")


def build_pack(output: Path, clean: bool) -> dict[str, Any]:
    if clean and output.exists():
        shutil.rmtree(output)

    streaming_root = output / "StreamingAssets"
    export_root = streaming_root / DATA_ROOT
    export_root.mkdir(parents=True, exist_ok=True)

    index = load_json(INDEX_PATH)
    originals = index.get("originalBackgrounds", [])
    color_corrections_path = VIEWER_ASSETS / "color-corrections.json"
    color_corrections = load_json(color_corrections_path) if color_corrections_path.exists() else {}

    scenes: list[dict[str, Any]] = []
    copied = 0
    skipped = 0

    for original in originals:
        scene_id = original["id"]
        source_url = original.get("sourcePlate", {}).get("url")
        camera_url = original.get("cameraMetadata", {}).get("url")
        walkmesh_url = original.get("walkmesh", {}).get("url")

        if not source_url or not camera_url or not walkmesh_url:
            skipped += 1
            continue

        source_plate = asset_url_to_path(source_url)
        cameras = asset_url_to_path(camera_url)
        walkmesh = asset_url_to_path(walkmesh_url)
        depth = VIEWER_ASSETS / "depth" / scene_id / "depth.png"

        missing = [
            str(path.relative_to(ROOT))
            for path in (source_plate, cameras, walkmesh, depth)
            if not path.exists()
        ]
        if missing:
            skipped += 1
            scenes.append(
                {
                    "id": scene_id,
                    "index": original.get("index"),
                    "mapName": original.get("mapName"),
                    "bundle": original.get("bundle"),
                    "enabled": False,
                    "missing": missing,
                }
            )
            continue

        scene_dir = export_root / "scenes" / scene_id
        copy_if_present(source_plate, scene_dir / "source_plate.png")
        copy_if_present(depth, scene_dir / "depth.png")
        copy_if_present(cameras, scene_dir / "cameras.json")
        copy_if_present(walkmesh, scene_dir / "walkmesh.json")

        scenes.append(
            {
                "id": scene_id,
                "index": original.get("index"),
                "mapName": original.get("mapName"),
                "bundle": original.get("bundle"),
                "enabled": True,
                "sourcePlate": scene_asset_path(scene_id, "source_plate.png"),
                "depth": scene_asset_path(scene_id, "depth.png"),
                "cameras": scene_asset_path(scene_id, "cameras.json"),
                "walkmesh": scene_asset_path(scene_id, "walkmesh.json"),
                "colorCorrection": color_corrections.get(scene_id, {}),
            }
        )
        copied += 1

    manifest = {
        "schema": 1,
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "sourceIndex": str(INDEX_PATH.relative_to(ROOT)).replace("\\", "/"),
        "sceneCount": len(scenes),
        "enabledSceneCount": copied,
        "skippedSceneCount": skipped,
        "defaults": {
            "projectionYMode": "flipped",
            "depthSurfaceMode": "farthest",
            "depthStrength": 2.5,
            "geometryDepthScale": 0.0,
            "parallaxStrength": 9.0,
            "idleParallaxStrength": 1.25,
            "renderQueue": 1500,
            "actorRenderQueue": -1,
            "alphaCutoff": 0.5,
            "wiggleStrength": 1.2,
            "dofStrength": 1.0,
            "sbs3d": False,
            "meshColumns": 96,
            "meshRows": 54,
            "sourceScale": 0.5,
            "hideOriginalBackground": False,
        },
        "scenes": scenes,
    }

    manifest_path = export_root / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    write_mod_file_list(output)
    write_mod_description(output, manifest)
    return {
        "output": str(output),
        "manifest": str(manifest_path),
        "scenes": len(scenes),
        "copied": copied,
        "skipped": skipped,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--no-clean", action="store_true", help="Do not remove the previous output folder first.")
    args = parser.parse_args()

    summary = build_pack(args.output.resolve(), clean=not args.no_clean)
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
