from __future__ import annotations

import sys
from pathlib import Path

import UnityPy


GAME = Path(r"F:\SteamLibrary\steamapps\common\FINAL FANTASY IX")
STREAMING = GAME / "StreamingAssets"


def main() -> None:
    target = (sys.argv[1] if len(sys.argv) > 1 else "fbg_n00_tshp_map001_th_cgr_0").lower()
    bundles = [STREAMING / f"p0data1{i}.bin" for i in range(1, 10)]
    for bundle in bundles:
        if not bundle.exists():
            continue
        found = False
        env = UnityPy.load(str(bundle))
        for obj in env.objects:
            container = (getattr(obj, "container", "") or "").replace("\\", "/")
            if target not in container.lower():
                continue
            found = True
            try:
                data = obj.read()
            except Exception as exc:
                print(bundle.name, obj.type.name, container, f"read-error={exc}")
                continue
            name = getattr(data, "name", None) or getattr(data, "m_Name", None) or ""
            extra = ""
            if obj.type.name == "Texture2D":
                extra = (
                    f" size={getattr(data, 'm_Width', None)}x{getattr(data, 'm_Height', None)}"
                    f" format={getattr(data, 'm_TextureFormat', None)}"
                )
            elif obj.type.name == "TextAsset":
                script = getattr(data, "script", None) or getattr(data, "m_Script", None)
                extra = f" bytes={len(script) if script is not None else 0}"
            print(f"{bundle.name}\t{obj.type.name}\t{name}\t{container}\t{extra}")
        if found:
            print(f"--- {bundle.name} done")


if __name__ == "__main__":
    main()
