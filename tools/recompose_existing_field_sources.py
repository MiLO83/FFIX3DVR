from __future__ import annotations

import json
from pathlib import Path

import batch_extract_field_sources as fields


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "artifacts" / "all-fields" / "source"
MANIFEST = ROOT / "artifacts" / "all-fields" / "manifest.json"


def main() -> None:
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    recomposed = 0
    failed = 0

    for entry in manifest["maps"]:
        map_name = entry["mapName"]
        map_dir = SOURCE / fields.safe_name(map_name)
        result = fields.compose_plate(map_dir)
        entry.setdefault("status", {})["sourcePlateComposed"] = bool(result.get("ok"))
        entry["compose"] = result
        if result.get("ok"):
            recomposed += 1
        else:
            failed += 1
        print(f"{map_name}: {result.get('ok')} {result.get('reason', '')}")

    manifest["composedMaps"] = recomposed
    manifest["compositionFix"] = "per_camera_source_plates_atlas_top_origin"
    MANIFEST.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"recomposed": recomposed, "failed": failed}, indent=2))


if __name__ == "__main__":
    main()
