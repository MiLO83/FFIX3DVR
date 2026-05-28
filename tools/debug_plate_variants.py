from __future__ import annotations

import struct
import sys
from pathlib import Path

from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import batch_extract_field_sources as fields  # noqa: E402


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "artifacts" / "all-fields" / "source"
OUT = ROOT / "artifacts" / "all-fields" / "debug"


def attach_camera_indices(bgs_path: Path, header: dict[str, int], overlays: list[fields.Overlay]) -> None:
    data = bgs_path.read_bytes()
    for index, overlay in enumerate(overlays):
        packed_2 = struct.unpack_from("<I", data, header["overlayOffset"] + index * 56 + 36)[0]
        overlay.cam_ndx = packed_2 & 0xFF


def compose_variant(map_slug: str) -> None:
    map_dir = SOURCE / map_slug
    out_dir = OUT / f"plate_variants_{map_slug}"
    out_dir.mkdir(parents=True, exist_ok=True)

    atlas = Image.open(map_dir / "atlas.png").convert("RGBA")
    bgs_path = map_dir / "scene.bgs.bytes"
    header, overlays, _ = fields.parse_bgs(bgs_path, atlas.width)
    attach_camera_indices(bgs_path, header, overlays)
    scale = fields.TILE_SIZE // fields.PSX_TILE_SIZE

    for cam_index in sorted({overlay.cam_ndx for overlay in overlays}):
        items = []
        for overlay in overlays:
            if not (overlay.flags & fields.ACTIVE) or overlay.cam_ndx != cam_index:
                continue
            for sprite in overlay.sprites:
                x = overlay.cur_x + sprite.off_x
                y = overlay.cur_y + sprite.off_y
                items.append((overlay.index, overlay.cur_z + sprite.depth, x, y, sprite))

        if not items:
            continue

        min_x = min(x for _, _, x, _, _ in items)
        min_y = min(y for _, _, _, y, _ in items)
        max_x = max(x + fields.PSX_TILE_SIZE for _, _, x, _, _ in items)
        max_y = max(y + fields.PSX_TILE_SIZE for _, _, _, y, _ in items)

        for atlas_flip in (True, False):
            for dest_flip in (True, False):
                canvas = Image.new(
                    "RGBA",
                    ((max_x - min_x) * scale, (max_y - min_y) * scale),
                    (0, 0, 0, 255),
                )
                for _, _, x, y, sprite in items:
                    atlas_y = atlas.height - sprite.atlas_y - sprite.h if atlas_flip else sprite.atlas_y
                    tile = atlas.crop((sprite.atlas_x, atlas_y, sprite.atlas_x + sprite.w, atlas_y + sprite.h))
                    dest_x = (x - min_x) * scale
                    if dest_flip:
                        dest_y = canvas.height - ((y - min_y) * scale) - fields.TILE_SIZE
                    else:
                        dest_y = (y - min_y) * scale
                    canvas.alpha_composite(tile, (dest_x, dest_y))

                variant = (
                    f"cam{cam_index}_"
                    f"atlas{'flip' if atlas_flip else 'top'}_"
                    f"dest{'flip' if dest_flip else 'top'}.png"
                )
                canvas.save(out_dir / variant)
                print(variant, canvas.size, len(items))


def main() -> None:
    map_slug = sys.argv[1] if len(sys.argv) > 1 else "fbg_n00_tshp_map001_th_cgr_0"
    compose_variant(map_slug)


if __name__ == "__main__":
    main()
