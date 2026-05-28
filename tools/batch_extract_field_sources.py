from __future__ import annotations

import json
import re
import struct
from dataclasses import dataclass, field
from pathlib import Path

import UnityPy
from PIL import Image


ROOT = Path(__file__).resolve().parents[1]
GAME = Path(r"F:\SteamLibrary\steamapps\common\FINAL FANTASY IX")
STREAMING = GAME / "StreamingAssets"
OUT = ROOT / "artifacts" / "all-fields"
SOURCE = OUT / "source"
LOGS = OUT / "logs"
MANIFEST = OUT / "manifest.json"
FIELD_BUNDLES = [STREAMING / f"p0data1{i}.bin" for i in range(1, 10)]
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
class Camera:
    index: int
    proj: int
    r: list[list[int]]
    t: list[int]
    center_offset: list[int]
    w: int
    h: int
    vrp_min_x: int
    vrp_max_x: int
    vrp_min_y: int
    vrp_max_y: int
    depth_offset: int


@dataclass
class Overlay:
    index: int
    flags: int
    cur_z: int
    w: int
    h: int
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

    def i32(self) -> int:
        value = struct.unpack_from("<i", self.data, self.pos)[0]
        self.pos += 4
        return value

    def u32(self) -> int:
        value = struct.unpack_from("<I", self.data, self.pos)[0]
        self.pos += 4
        return value


def bits(value: int, start: int, count: int) -> int:
    return (value >> start) & ((1 << count) - 1)


def safe_name(name: str) -> str:
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("_").lower()


def map_name_from_container(container: str) -> str | None:
    match = re.search(r"fieldmaps/([^/]+)/", container.replace("\\", "/"), re.IGNORECASE)
    return match.group(1).upper() if match else None


def obj_name(data) -> str:
    for attr in ("name", "m_Name"):
        value = getattr(data, attr, None)
        if value:
            return str(value)
    return ""


def text_payload(data) -> bytes | None:
    script = getattr(data, "script", None) or getattr(data, "m_Script", None)
    if script is None:
        return None
    if isinstance(script, str):
        return script.encode("utf-8", "surrogateescape")
    return bytes(script)


def parse_cameras(reader: Reader, header: dict[str, int]) -> list[Camera]:
    cameras: list[Camera] = []
    reader.seek(header["cameraOffset"])
    for index in range(header["cameraCount"]):
        proj = reader.u16()
        r = [[reader.i16() for _ in range(3)] for _ in range(3)]
        t = [reader.i32(), reader.i32(), reader.i32()]
        center_offset = [reader.i16(), reader.i16()]
        w = reader.i16()
        h = reader.i16()
        vrp_min_x = reader.i16()
        vrp_max_x = reader.i16()
        vrp_min_y = reader.i16()
        vrp_max_y = reader.i16()
        depth_offset = reader.i32()
        cameras.append(
            Camera(
                index=index,
                proj=proj,
                r=r,
                t=t,
                center_offset=center_offset,
                w=w,
                h=h,
                vrp_min_x=vrp_min_x,
                vrp_max_x=vrp_max_x,
                vrp_min_y=vrp_min_y,
                vrp_max_y=vrp_max_y,
                depth_offset=depth_offset,
            )
        )
    return cameras


def parse_bgs(path: Path, atlas_width: int) -> tuple[dict[str, int], list[Overlay], list[Camera]]:
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
    cameras = parse_cameras(reader, header)

    overlays: list[Overlay] = []
    reader.seek(header["overlayOffset"])
    for index in range(header["overlayCount"]):
        packed = reader.u32()
        flags = packed & 0xFF
        cur_z = (packed >> 8) & 0xFFF
        w = reader.u16()
        h = reader.u16()
        reader.i16()
        reader.i16()
        cur_x = reader.i16()
        cur_y = reader.i16()
        for _ in range(10):
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
                w=w,
                h=h,
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
        reader.seek(overlay.prm_offset)
        for _ in range(overlay.sprite_count):
            reader.u32()
            reader.u32()
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
    return header, overlays, cameras


def read_vec(reader: Reader) -> list[int]:
    return [reader.i16(), reader.i16(), reader.i16()]


def parse_bgi_walkmesh(path: Path) -> dict:
    if not path.exists():
        return {"ok": False, "reason": "missing walkmesh"}

    reader = Reader(path.read_bytes())
    magic = reader.u32()
    if magic != 0xACDCDEAD:
        return {"ok": False, "reason": f"unexpected magic 0x{magic:08x}"}

    data_size = reader.u16()
    org_pos = read_vec(reader)
    cur_pos = read_vec(reader)
    min_pos = read_vec(reader)
    max_pos = read_vec(reader)
    char_pos = read_vec(reader)
    active_floor = reader.i16()
    active_tri = reader.i16()
    tri_count = reader.u16()
    tri_offset = reader.u16()
    edge_count = reader.u16()
    edge_offset = reader.u16()
    anm_count = reader.u16()
    anm_offset = reader.u16()
    floor_count = reader.u16()
    floor_offset = reader.u16()
    normal_count = reader.u16()
    normal_offset = reader.u16()
    vertex_count = reader.u16()
    vertex_offset = reader.u16()

    vertices: list[list[int]] = []
    reader.seek(4 + vertex_offset)
    for _ in range(vertex_count):
        vertices.append(read_vec(reader))

    floors: list[dict] = []
    reader.seek(4 + floor_offset)
    for _ in range(floor_count):
        floor_flags = reader.u16()
        floor_ndx = reader.u16()
        floor_org = read_vec(reader)
        floor_cur = read_vec(reader)
        floor_min = read_vec(reader)
        floor_max = read_vec(reader)
        floor_tri_count = reader.u16()
        floor_tri_ndx_offset = reader.u16()
        saved = reader.pos
        reader.seek(4 + floor_tri_ndx_offset)
        tri_indices = [reader.i32() for _ in range(floor_tri_count)]
        reader.seek(saved)
        floors.append(
            {
                "floorFlags": floor_flags,
                "floorIndex": floor_ndx,
                "orgPos": floor_org,
                "curPos": floor_cur,
                "minPos": floor_min,
                "maxPos": floor_max,
                "triIndices": tri_indices,
            }
        )

    triangles: list[dict] = []
    reader.seek(4 + tri_offset)
    for tri_index in range(tri_count):
        tri_flags = reader.u16()
        tri_data = reader.u16()
        floor_ndx = reader.i16()
        normal_ndx = reader.i16()
        theta_x = reader.i16()
        theta_z = reader.i16()
        vertex_indices = [reader.i16(), reader.i16(), reader.i16()]
        edge_indices = [reader.i16(), reader.i16(), reader.i16()]
        neighbor_indices = [reader.i16(), reader.i16(), reader.i16()]
        center = read_vec(reader)
        d = reader.i32()
        floor_org = floors[floor_ndx]["orgPos"] if 0 <= floor_ndx < len(floors) else [0, 0, 0]
        absolute_vertices = [
            [org_pos[axis] + floor_org[axis] + vertices[vertex_index][axis] for axis in range(3)]
            for vertex_index in vertex_indices
            if 0 <= vertex_index < len(vertices)
        ]
        triangles.append(
            {
                "triIndex": tri_index,
                "triFlags": tri_flags,
                "triData": tri_data,
                "floorIndex": floor_ndx,
                "normalIndex": normal_ndx,
                "thetaX": theta_x,
                "thetaZ": theta_z,
                "vertexIndices": vertex_indices,
                "edgeIndices": edge_indices,
                "neighborIndices": neighbor_indices,
                "center": center,
                "d": d,
                "worldVertices": absolute_vertices,
            }
        )

    return {
        "ok": True,
        "dataSize": data_size,
        "orgPos": org_pos,
        "curPos": cur_pos,
        "minPos": min_pos,
        "maxPos": max_pos,
        "charPos": char_pos,
        "activeFloor": active_floor,
        "activeTri": active_tri,
        "counts": {
            "triangles": tri_count,
            "edges": edge_count,
            "animations": anm_count,
            "floors": floor_count,
            "normals": normal_count,
            "vertices": vertex_count,
        },
        "offsets": {
            "triangles": tri_offset,
            "edges": edge_offset,
            "animations": anm_offset,
            "floors": floor_offset,
            "normals": normal_offset,
            "vertices": vertex_offset,
        },
        "floors": floors,
        "vertices": vertices,
        "triangles": triangles,
    }


def compose_camera_plate(map_dir: Path, camera_index: int, *, write_primary: bool = False) -> dict:
    map_dir = map_dir.resolve()
    atlas_path = map_dir / "atlas.png"
    bgs_path = map_dir / "scene.bgs.bytes"
    if not atlas_path.exists() or not bgs_path.exists():
        return {"ok": False, "reason": "missing atlas or bgs"}

    atlas = Image.open(atlas_path).convert("RGBA")
    header, overlays, cameras = parse_bgs(bgs_path, atlas.width)
    draw_items = []
    for overlay in overlays:
        if not (overlay.flags & ACTIVE):
            continue
        if overlay.cam_ndx != camera_index:
            continue
        for sprite in overlay.sprites:
            x = overlay.cur_x + sprite.off_x
            y = overlay.cur_y + sprite.off_y
            draw_items.append((overlay.index, overlay.cur_z + sprite.depth, x, y, sprite))

    if not draw_items:
        return {"ok": False, "reason": "no active sprites", "cameraIndex": camera_index}

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

    plate_path = map_dir / f"source_plate_cam{camera_index:02}.png"
    canvas.save(plate_path)
    if write_primary:
        canvas.save(map_dir / "source_plate.png")
    return {
        "ok": True,
        "sourcePlate": str(plate_path.relative_to(ROOT)).replace("\\", "/"),
        "size": [canvas.width, canvas.height],
        "activeSprites": len(draw_items),
        "overlayCount": len(overlays),
        "cameraCount": header["cameraCount"],
        "cameraIndex": camera_index,
        "camera": cameras[camera_index].__dict__ if 0 <= camera_index < len(cameras) else None,
        "boundsPsx": [min_x, min_y, max_x, max_y],
    }


def compose_plate(map_dir: Path) -> dict:
    map_dir = map_dir.resolve()
    atlas_path = map_dir / "atlas.png"
    bgs_path = map_dir / "scene.bgs.bytes"
    if not atlas_path.exists() or not bgs_path.exists():
        return {"ok": False, "reason": "missing atlas or bgs"}

    atlas = Image.open(atlas_path).convert("RGBA")
    header, overlays, cameras = parse_bgs(bgs_path, atlas.width)
    camera_indices = sorted({overlay.cam_ndx for overlay in overlays if overlay.flags & ACTIVE})
    camera_results = []
    primary = None
    for camera_index in camera_indices:
        result = compose_camera_plate(map_dir, camera_index, write_primary=primary is None)
        camera_results.append(result)
        if result.get("ok") and primary is None:
            primary = result

    if primary is None:
        return {"ok": False, "reason": "no active sprites", "cameraCount": header["cameraCount"]}

    camera_metadata = {
        "cameraCount": header["cameraCount"],
        "cameras": [camera.__dict__ for camera in cameras],
        "activeOverlayCameras": camera_indices,
    }
    camera_metadata_path = map_dir / "cameras.json"
    camera_metadata_path.write_text(json.dumps(camera_metadata, indent=2), encoding="utf-8")

    walkmesh = parse_bgi_walkmesh(map_dir / "walkmesh.bgi.bytes")
    walkmesh_path = None
    if walkmesh.get("ok"):
        walkmesh_path = map_dir / "walkmesh.json"
        walkmesh_path.write_text(json.dumps(walkmesh, indent=2), encoding="utf-8")

    result = {
        **primary,
        "sourcePlate": str((map_dir / "source_plate.png").relative_to(ROOT)).replace("\\", "/"),
        "cameraPlates": camera_results,
        "selectedCameraIndex": primary["cameraIndex"],
        "cameraMetadata": str(camera_metadata_path.relative_to(ROOT)).replace("\\", "/"),
    }
    if walkmesh_path:
        result["walkmesh"] = str(walkmesh_path.relative_to(ROOT)).replace("\\", "/")
        result["walkmeshCounts"] = walkmesh["counts"]
    else:
        result["walkmesh"] = None
        result["walkmeshError"] = walkmesh.get("reason", "unknown")
    return result


def main() -> None:
    SOURCE.mkdir(parents=True, exist_ok=True)
    LOGS.mkdir(parents=True, exist_ok=True)
    maps: dict[str, dict] = {}
    log_lines: list[str] = []

    for bundle in FIELD_BUNDLES:
        if not bundle.exists():
            log_lines.append(f"missing bundle {bundle}")
            continue
        print(f"Scanning {bundle.name}")
        env = UnityPy.load(str(bundle))
        for obj in env.objects:
            try:
                data = obj.read()
            except Exception as exc:
                log_lines.append(f"{bundle.name}\t{obj.type.name}\tread-error\t{exc}")
                continue

            container = getattr(obj, "container", "") or ""
            map_name = map_name_from_container(container)
            if not map_name:
                continue
            name = obj_name(data).lower()
            map_dir = SOURCE / safe_name(map_name)
            map_dir.mkdir(parents=True, exist_ok=True)
            entry = maps.setdefault(map_name, {"mapName": map_name, "bundle": bundle.name, "files": {}, "status": {}})

            if obj.type.name == "Texture2D" and container.lower().endswith("/atlas.png"):
                out_path = map_dir / "atlas.png"
                data.image.save(out_path)
                entry["files"]["atlas"] = str(out_path.relative_to(ROOT)).replace("\\", "/")
            elif obj.type.name == "TextAsset" and name.endswith(".bgs"):
                payload = text_payload(data)
                if payload:
                    out_path = map_dir / "scene.bgs.bytes"
                    out_path.write_bytes(payload)
                    entry["files"]["bgs"] = str(out_path.relative_to(ROOT)).replace("\\", "/")
            elif obj.type.name == "TextAsset" and name.endswith(".bgi"):
                payload = text_payload(data)
                if payload:
                    out_path = map_dir / "walkmesh.bgi.bytes"
                    out_path.write_bytes(payload)
                    entry["files"]["bgi"] = str(out_path.relative_to(ROOT)).replace("\\", "/")

    print(f"Composing {len(maps)} maps")
    for map_name, entry in sorted(maps.items()):
        map_dir = SOURCE / safe_name(map_name)
        result = compose_plate(map_dir)
        entry["status"]["sourcePlateComposed"] = bool(result.get("ok"))
        entry["compose"] = result

    summary = {
        "sourceGamePath": str(GAME),
        "fieldBundles": [p.name for p in FIELD_BUNDLES],
        "totalMaps": len(maps),
        "composedMaps": sum(1 for entry in maps.values() if entry["status"].get("sourcePlateComposed")),
        "maps": [maps[k] for k in sorted(maps)],
    }
    MANIFEST.write_text(json.dumps(summary, indent=2), encoding="utf-8")
    (LOGS / "batch_extract_field_sources.txt").write_text("\n".join(log_lines), encoding="utf-8")
    print(json.dumps({k: summary[k] for k in ("totalMaps", "composedMaps")}, indent=2))


if __name__ == "__main__":
    main()
