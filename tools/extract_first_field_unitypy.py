from __future__ import annotations

import json
import re
from pathlib import Path

import UnityPy


ROOT = Path(__file__).resolve().parents[1]
GAME = Path(r"F:\SteamLibrary\steamapps\common\FINAL FANTASY IX")
BUNDLE = GAME / "StreamingAssets" / "p0data11.bin"
MAP_NAME = "FBG_N00_TSHP_MAP001_TH_CGR_0"
OUT = ROOT / "artifacts" / "first-field"
LOG = OUT / "logs" / "unitypy_assets.txt"
SOURCE = OUT / "source"


def safe_name(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("_") or "asset"


def obj_name(data) -> str:
    for attr in ("name", "m_Name"):
        value = getattr(data, attr, None)
        if value:
            return str(value)
    return ""


def main() -> None:
    SOURCE.mkdir(parents=True, exist_ok=True)
    LOG.parent.mkdir(parents=True, exist_ok=True)

    env = UnityPy.load(str(BUNDLE))
    lines: list[str] = [f"bundle={BUNDLE}", f"target={MAP_NAME}", ""]
    exports: list[str] = []
    target_lower = MAP_NAME.lower()

    for obj in env.objects:
        type_name = obj.type.name
        try:
            data = obj.read()
        except Exception as exc:
            lines.append(f"{type_name}\t<read-error>\t{exc}")
            continue

        name = obj_name(data)
        container = getattr(obj, "container", "") or ""
        descriptor = f"{container}/{name}".lower()
        lines.append(f"{type_name}\t{container}\t{name}")

        if target_lower not in descriptor and "map001" not in descriptor.lower():
            continue

        if type_name == "Texture2D":
            try:
                image = data.image
            except Exception as exc:
                lines.append(f"EXPORT_ERROR\t{name}\t{exc}")
                continue

            out_path = SOURCE / f"{safe_name(container)}__{safe_name(name)}.png"
            image.save(out_path)
            exports.append(str(out_path.relative_to(ROOT)))
            lines.append(f"EXPORTED\t{out_path}")
        elif type_name == "TextAsset":
            script = getattr(data, "script", None) or getattr(data, "m_Script", None)
            if script is None:
                lines.append(f"TEXT_EXPORT_SKIP\t{name}\tno script bytes")
                continue
            out_path = SOURCE / f"{safe_name(container)}__{safe_name(name)}.bytes"
            if isinstance(script, str):
                payload = script.encode("utf-8", "surrogateescape")
            else:
                payload = bytes(script)
            out_path.write_bytes(payload)
            exports.append(str(out_path.relative_to(ROOT)))
            lines.append(f"EXPORTED\t{out_path}")

    LOG.write_text("\n".join(lines), encoding="utf-8")

    manifest_path = OUT / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if exports:
        manifest["status"]["backgroundExtracted"] = True
        manifest["assets"]["sourcePlate"] = exports[0]
        manifest["exports"] = exports
        manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    print(f"scanned={len(env.objects)}")
    print(f"exports={len(exports)}")
    print(f"log={LOG}")


if __name__ == "__main__":
    main()
