"""Minimal glTF 2.0 binary writer: nodes, meshes (POSITION/NORMAL/TEXCOORD_0 + uint32 indices), untextured materials."""
from __future__ import annotations

import json
import struct
from dataclasses import dataclass, field

import numpy as np

_FLOAT = 5126
_UINT = 5125
_ARRAY_BUFFER = 34962
_ELEMENT_ARRAY_BUFFER = 34963


@dataclass
class Primitive:
    positions: np.ndarray  # (n, 3) float32
    normals: np.ndarray  # (n, 3) float32
    uvs: np.ndarray  # (n, 2) float32
    indices: np.ndarray  # (m,) uint32
    material: int


@dataclass
class GlbBuilder:
    nodes: list[dict] = field(default_factory=list)
    meshes: list[dict] = field(default_factory=list)
    materials: list[dict] = field(default_factory=list)
    accessors: list[dict] = field(default_factory=list)
    buffer_views: list[dict] = field(default_factory=list)
    _bin: bytearray = field(default_factory=bytearray)
    _material_index: dict[str, int] = field(default_factory=dict)

    def material(self, name: str, rgba: tuple[float, float, float, float], roughness: float = 0.85, metallic: float = 0.0) -> int:
        existing = self._material_index.get(name)
        if existing is not None:
            return existing
        material = {
            "name": name,
            "pbrMetallicRoughness": {"baseColorFactor": [round(c, 4) for c in rgba], "metallicFactor": metallic, "roughnessFactor": roughness},
            "doubleSided": False,
        }
        if rgba[3] < 0.999:
            material["alphaMode"] = "BLEND"
        self.materials.append(material)
        self._material_index[name] = len(self.materials) - 1
        return self._material_index[name]

    def _view(self, data: bytes, target: int) -> int:
        while len(self._bin) % 4:
            self._bin.append(0)
        self.buffer_views.append({"buffer": 0, "byteOffset": len(self._bin), "byteLength": len(data), "target": target})
        self._bin.extend(data)
        return len(self.buffer_views) - 1

    def _accessor(self, array: np.ndarray, kind: str, component: int, target: int, bounds: bool = False) -> int:
        view = self._view(array.tobytes(), target)
        accessor = {"bufferView": view, "componentType": component, "count": int(array.shape[0]), "type": kind}
        if bounds:
            accessor["min"] = [float(v) for v in array.min(axis=0)]
            accessor["max"] = [float(v) for v in array.max(axis=0)]
        self.accessors.append(accessor)
        return len(self.accessors) - 1

    def mesh(self, name: str, primitives: list[Primitive]) -> int:
        out = []
        for primitive in primitives:
            out.append({
                "attributes": {
                    "POSITION": self._accessor(np.ascontiguousarray(primitive.positions, dtype=np.float32), "VEC3", _FLOAT, _ARRAY_BUFFER, bounds=True),
                    "NORMAL": self._accessor(np.ascontiguousarray(primitive.normals, dtype=np.float32), "VEC3", _FLOAT, _ARRAY_BUFFER),
                    "TEXCOORD_0": self._accessor(np.ascontiguousarray(primitive.uvs, dtype=np.float32), "VEC2", _FLOAT, _ARRAY_BUFFER),
                },
                "indices": self._accessor(np.ascontiguousarray(primitive.indices, dtype=np.uint32), "SCALAR", _UINT, _ELEMENT_ARRAY_BUFFER),
                "material": primitive.material,
                "mode": 4,
            })
        self.meshes.append({"name": name, "primitives": out})
        return len(self.meshes) - 1

    def node(self, name: str, mesh: int | None = None, translation: tuple[float, float, float] | None = None,
             children: list[int] | None = None, extras: dict | None = None) -> int:
        node: dict = {"name": name}
        if mesh is not None:
            node["mesh"] = mesh
        if translation is not None and any(abs(v) > 0 for v in translation):
            node["translation"] = [float(v) for v in translation]
        if children:
            node["children"] = children
        if extras:
            node["extras"] = extras
        self.nodes.append(node)
        return len(self.nodes) - 1

    def write(self, path: str, scene_nodes: list[int], generator: str) -> None:
        while len(self._bin) % 4:
            self._bin.append(0)
        document = {
            "asset": {"version": "2.0", "generator": generator},
            "scene": 0,
            "scenes": [{"nodes": scene_nodes}],
            "nodes": self.nodes,
            "meshes": self.meshes,
            "materials": self.materials,
            "accessors": self.accessors,
            "bufferViews": self.buffer_views,
            "buffers": [{"byteLength": len(self._bin)}],
        }
        json_bytes = json.dumps(document, separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        json_bytes += b" " * ((4 - len(json_bytes) % 4) % 4)
        total = 12 + 8 + len(json_bytes) + 8 + len(self._bin)
        with open(path, "wb") as handle:
            handle.write(struct.pack("<4sII", b"glTF", 2, total))
            handle.write(struct.pack("<I4s", len(json_bytes), b"JSON"))
            handle.write(json_bytes)
            handle.write(struct.pack("<I4s", len(self._bin), b"BIN\x00"))
            handle.write(self._bin)


def read_glb(path: str) -> tuple[dict, bytes]:
    """Parses a GLB into (json, bin) — used by tests and the self-check."""
    with open(path, "rb") as handle:
        magic, version, total = struct.unpack("<4sII", handle.read(12))
        if magic != b"glTF" or version != 2:
            raise ValueError("not a glTF 2.0 binary")
        length, kind = struct.unpack("<I4s", handle.read(8))
        document = json.loads(handle.read(length))
        binary = b""
        if handle.tell() < total:
            length, kind = struct.unpack("<I4s", handle.read(8))
            binary = handle.read(length)
    return document, binary


def accessor_array(document: dict, binary: bytes, index: int) -> np.ndarray:
    accessor = document["accessors"][index]
    view = document["bufferViews"][accessor["bufferView"]]
    width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3}[accessor["type"]]
    dtype = np.float32 if accessor["componentType"] == _FLOAT else np.uint32
    data = np.frombuffer(binary, dtype=dtype, count=accessor["count"] * width, offset=view["byteOffset"])
    return data.reshape(-1, width) if width > 1 else data
