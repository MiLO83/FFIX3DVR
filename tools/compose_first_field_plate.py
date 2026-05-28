from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field
from pathlib import Path

from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "artifacts" / "first-field" / "source"
OUT = SOURCE / "source_plate.png"
LOG = ROOT / "artifacts" / "first-field" / "logs" / "compose_plate.txt"
TILE_SIZE = 32
PSX_TILE_SIZE = 16
PADDING = 4
ACTIVE = 2


@dataclass
class Sprite:
    off_x: int
    off_y: int
    atlas_x: int
    atlas_y: int
    w: int = TILE_SIZE
    h: int = TILE_SIZE
    depth: int = 0


@dataclass
class Overlay:
    index: int
    flags: int
    cur_z: int
    org_z: int
    w: int
    h: int
    org_x: int
    org_y: int
    cur_x: int
    cur_y: int
    cam_ndx: int
    viewport_ndx: int
    is_x_offset: int
    sprite_count: int
    loc_offset: int
    prm_offset: int
    sprites: list[Sprite] = field(default_factory=list)


class Reader:
    def __init__(self, data: bytes):
        self.data = data
        self.pos = 0

    def seek(self, pos: int) -> None:
        self.pos = pos

    def u16(self) -> int:
        value = struct.unpack_from("<H", self.data, self.pos)[0]
        self.pos += 2
        return value

    def i16(self) -> int:
        value = struct.unpack_from("<h", self.data, self.pos)[0]
        self.pos += 2
        return value

    def u32(self) -> int:
        value = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return value


def bits(value: int, start: int, count: int) -> int:
    return (value >> start) & ((1 << count) - 1)


def find_one(pattern: str) -> Path:
    matches = list(SOURCE.glob(pattern))
    if len(matches) != 1:
        raise RuntimeError(f"Expected one {pattern}, found {len(matches)}")
    return matches[0]


def parse_bgs(path: Path, atlas_width: int) -> tuple[dict[str, int], list[Overlay]]:
    reader = Reader(path.read_bytes())
    header = {
        "sceneLength": reader.u16(),
        "depthBitShift": reader.u16(),
        "animCount": reader.u16(),
        "overlayCount": reader.u16(),
        "lightCount": reader.u16(),
        "cameraCount": reader.u16(),
        "animOffset": reader.u32(),
        "overlayOffset": reader.u32(),
        "lightOffset": reader.u32(),
        "cameraOffset": reader.u32(),
        "orgZ": reader.i16(),
        "curZ": reader.i16(),
        "orgX": reader.i16(),
        "orgY": reader.i16(),
        "curX": reader.i16(),
        "curY": reader.i16(),
        "minX": reader.i16(),
        "maxX": reader.i16(),
        "minY": reader.i16(),
        "maxY": reader.i16(),
        "scrX": reader.i16(),
        "scrY": reader.i16(),
    }

    overlays: list[Overlay] = []
    reader.seek(header["overlayOffset"])
    for index in range(header["overlayCount"]):
        packed = reader.u32()
        flags = packed & 0xFF
        cur_z = (packed >> 8) & 0xFFF
        org_z = (packed >> 20) & 0xFFF
        w = reader.u16()
        h = reader.u16()
        org_x = reader.i16()
        org_y = reader.i16()
        cur_x = reader.i16()
        cur_y = reader.i16()
        reader.i16()
        reader.i16()
        reader.i16()
        reader.i16()
        reader.i16()
        reader.i16()
        reader.i16()
        reader.i16()
        reader.i16()
        reader.i16()
        packed_2 = reader.u32()
        cam_ndx = bits(packed_2, 0, 8)
        is_x_offset = bits(packed_2, 8, 1)
        viewport_ndx = bits(packed_2, 9, 7)
        sprite_count = bits(packed_2, 16, 16)
        loc_offset = reader.u32()
        prm_offset = reader.u32()
        reader.u32()
        reader.u32()
        overlays.append(
            Overlay(
                index=index,
                flags=flags,
                cur_z=cur_z,
                org_z=org_z,
                w=w,
                h=h,
                org_x=org_x,
                org_y=org_y,
                cur_x=cur_x,
                cur_y=cur_y,
                cam_ndx=cam_ndx,
                viewport_ndx=viewport_ndx,
                is_x_offset=is_x_offset,
                sprite_count=sprite_count,
                loc_offset=loc_offset,
                prm_offset=prm_offset,
            )
        )

    count_per_row = atlas_width // (TILE_SIZE + PADDING)
    sprite_index = 0
    for overlay in overlays:
        raw_prm: list[tuple[int, int, int]] = []
        reader.seek(overlay.prm_offset)
        for _ in range(overlay.sprite_count):
            first = reader.u32()
            second = reader.u32()
            raw_prm.append((bits(second, 8, 10), bits(second, 18, 10), bits(first, 24, 8)))

        reader.seek(overlay.loc_offset)
        for _ in range(overlay.sprite_count):
            packed = reader.u32()
            depth = bits(packed, 0, 12)
            off_y = bits(packed, 12, 10)
            off_x = bits(packed, 22, 10)
            tile_x = 2 + (sprite_index % count_per_row) * (TILE_SIZE + PADDING)
            tile_y = 2 + (sprite_index // count_per_row) * (TILE_SIZE + PADDING)
            overlay.sprites.append(Sprite(off_x=off_x, off_y=off_y, atlas_x=tile_x, atlas_y=tile_y, depth=depth))
            sprite_index += 1

    return header, overlays


def main() -> None:
    atlas_path = find_one("*atlas.png")
    bgs_path = find_one("*.bgs.bytes")
    atlas = Image.open(atlas_path).convert("RGBA")
    header, overlays = parse_bgs(bgs_path, atlas.width)

    draw_items = []
    for overlay in overlays:
        if not (overlay.flags & ACTIVE):
            continue
        if overlay.cam_ndx != 0:
            continue
        for sprite in overlay.sprites:
            x = overlay.cur_x + sprite.off_x
            y = overlay.cur_y + sprite.off_y
            draw_items.append((overlay.index, overlay.cur_z + sprite.depth, x, y, sprite))

    scale = TILE_SIZE // PSX_TILE_SIZE
    min_x = min(x for _, _, x, _, _ in draw_items)
    min_y = min(y for _, _, _, y, _ in draw_items)
    max_x = max(x + PSX_TILE_SIZE for _, _, x, _, _ in draw_items)
    max_y = max(y + PSX_TILE_SIZE for _, _, _, y, _ in draw_items)
    canvas = Image.new("RGBA", ((max_x - min_x) * scale, (max_y - min_y) * scale), (0, 0, 0, 0))

    for _, _, x, y, sprite in draw_items:
        atlas_y = sprite.atlas_y
        tile = atlas.crop((sprite.atlas_x, atlas_y, sprite.atlas_x + sprite.w, atlas_y + sprite.h))
        canvas.alpha_composite(tile, ((x - min_x) * scale, (y - min_y) * scale))

    canvas.save(OUT)
    LOG.write_text(
        "\n".join(
            [
                f"atlas={atlas_path}",
                f"bgs={bgs_path}",
                f"header={json.dumps(header, sort_keys=True)}",
                f"overlays={len(overlays)}",
                f"activeSprites={len(draw_items)}",
                f"bounds=({min_x},{min_y})-({max_x},{max_y})",
                f"output={OUT}",
                f"size={canvas.size}",
            ]
        ),
        encoding="utf-8",
    )
    print(f"wrote={OUT}")
    print(f"size={canvas.size}")


if __name__ == "__main__":
    main()
