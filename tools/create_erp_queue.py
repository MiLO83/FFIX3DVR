from __future__ import annotations

import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
ALL_FIELDS = ROOT / "artifacts" / "all-fields" / "manifest.json"
QUEUE = ROOT / "artifacts" / "all-fields" / "erp_queue.json"


def prompt_for(map_name: str) -> str:
    zone = "FFIX pre-rendered fantasy RPG environment"
    if "_TSHP_" in map_name:
        zone = "dark wooden theater airship interior, warm lamps, ornate fantasy airship, FFIX pre-rendered RPG background"
    elif "_ALXT_" in map_name:
        zone = "Alexandria medieval fantasy city, stone streets, warm lanterns, FFIX pre-rendered RPG background"
    elif "_ALXC_" in map_name:
        zone = "Alexandria Castle fantasy palace interior, stone arches, royal details, FFIX pre-rendered RPG background"
    return (
        "Fill the green spaces according to the reference image. "
        "Outpaint as a seamless 360 equirectangular panorama, 2:1 aspect ratio. "
        "Keep the horizon level. Match left and right edges. "
        f"Preserve the original scene identity: {zone}."
    )


def main() -> None:
    manifest = json.loads(ALL_FIELDS.read_text(encoding="utf-8"))
    jobs = []
    for index, entry in enumerate(manifest["maps"], start=1):
        composed = entry.get("compose", {})
        if not composed.get("ok"):
            continue
        map_name = entry["mapName"]
        jobs.append(
            {
                "index": index,
                "mapName": map_name,
                "bundle": entry.get("bundle"),
                "sourcePlate": composed["sourcePlate"],
                "erpOutput": f"artifacts/all-fields/erp/{map_name.lower()}/field_erp.png",
                "splatOutput": f"artifacts/all-fields/splat/{map_name.lower()}/field_spherical_env.ply",
                "status": "queued",
                "prompt": prompt_for(map_name),
                "sticker": {
                    "yaw_deg": 0.0,
                    "pitch_deg": -4.0,
                    "hFOV_deg": 104.0,
                    "roll_deg": 0.0,
                },
            }
        )
    QUEUE.write_text(json.dumps({"totalJobs": len(jobs), "jobs": jobs}, indent=2), encoding="utf-8")
    print(f"queued={len(jobs)}")
    print(f"queue={QUEUE}")


if __name__ == "__main__":
    main()
