"""IFC -> building.glb + fet3d.editor/1 preview metadata + fet3d.building/1 building metadata.

Frames (one canonical frame for every artifact, the one BE contracts/editor/v1 declares):
- GLB / metadata: metres, Y-up, right-handed. g = (x - ox, z - oz, -(y - oy)) from IFC world metres (Z-up).
- Unity converts on import (glTFast negates X); metadata stays in the GLB frame.
- Storeys are the scene's root nodes (id L1..Ln by elevation, name = floor id); every exported element hangs under its
  storey at absolute height and is named by its IFC GlobalId. Nothing is moved, rebuilt or invented: missing data is a QA
  issue, never a generated wall/floor/exit.
"""
from __future__ import annotations

import hashlib
import json
import multiprocessing
import os
from collections import defaultdict
from dataclasses import dataclass, field

import ifcopenshell
import ifcopenshell.geom
import ifcopenshell.util.element as element_util
import ifcopenshell.util.placement as placement_util
import ifcopenshell.util.shape as shape_util
import ifcopenshell.util.unit as unit_util
import numpy as np

from . import classify
from .glb import GlbBuilder, Primitive

WORKER_VERSION = "fet3d-geometry-worker-1"
TRIANGLE_BUDGET_PER_FLOOR = 150_000


@dataclass
class Issue:
    code: str
    severity: str  # Info | Warning | Error
    message: str
    ifc_global_ids: list[str] = field(default_factory=list)

    def to_json(self) -> dict:
        return {"code": self.code, "severity": self.severity, "message": self.message, "ifcGlobalIds": self.ifc_global_ids[:100]}


@dataclass
class ElementMesh:
    global_id: str
    ifc_class: str
    storey_index: int
    positions: np.ndarray  # (n,3) GLB frame, triangle soup
    library_keys: list[str]  # per triangle
    bbox_min: np.ndarray
    bbox_max: np.ndarray


def _sha256(path: str) -> str:
    digest = hashlib.sha256()
    with open(path, "rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _text(value) -> str | None:
    if value is None:
        return None
    text = str(value).strip()
    return text or None


def _predefined(entity) -> str | None:
    value = getattr(entity, "PredefinedType", None)
    if value in (None, "NOTDEFINED", "USERDEFINED"):
        type_object = element_util.get_type(entity)
        value = getattr(type_object, "PredefinedType", None) if type_object else None
    return None if value in (None, "NOTDEFINED", "USERDEFINED") else str(value)


def _material_names(entity) -> list[str]:
    material = element_util.get_material(entity, should_skip_usage=True)
    if material is None:
        return []
    if material.is_a("IfcMaterialLayerSet"):
        layers = [layer.Material.Name for layer in material.MaterialLayers if layer.Material]
        finish = [name for name in (layers[:1] + layers[-1:] + layers) if name and not classify.HIDDEN_LAYER.search(name)]
        return finish or layers
    if material.is_a("IfcMaterialConstituentSet"):
        return [c.Material.Name for c in material.MaterialConstituents or [] if c.Material]
    if material.is_a("IfcMaterialList"):
        return [m.Name for m in material.Materials]
    if material.is_a("IfcMaterialProfileSet"):
        return [p.Material.Name for p in material.MaterialProfiles or [] if p.Material]
    name = getattr(material, "Name", None)
    return [name] if name else []


def _storey_of(entity, storey_ids: set[int]) -> int | None:
    """Walks containment/aggregation/nesting up to the IfcBuildingStorey (spaces and parts included)."""
    seen = set()
    current = entity
    while current is not None and current.id() not in seen:
        seen.add(current.id())
        if current.id() in storey_ids:
            return current.id()
        parent = element_util.get_container(current) or element_util.get_aggregate(current) or element_util.get_nest(current)
        current = parent
    return None


def _pset(entity, pset: str, prop: str):
    return (element_util.get_psets(entity).get(pset) or {}).get(prop)


class Converter:
    def __init__(self, ifc_path: str, revision_id: str | None = None):
        self.ifc_path = ifc_path
        self.revision_id = revision_id
        self.model = ifcopenshell.open(ifc_path)
        self.issues: list[Issue] = []
        self.scale = unit_util.calculate_unit_scale(self.model)
        self.exit_hints: list[str] = []

    # ---------- frames ----------
    def _to_glb(self, points: np.ndarray) -> np.ndarray:
        out = np.empty_like(points)
        out[:, 0] = points[:, 0] - self.origin[0]
        out[:, 1] = points[:, 2] - self.origin[2]
        out[:, 2] = -(points[:, 1] - self.origin[1])
        return out

    def coordinate_transform(self) -> list[float]:
        """Column-major IFC model coordinates (file units) -> GLB metres."""
        s = self.scale
        ox, oy, oz = self.origin
        return [s, 0, 0, 0, 0, 0, -s, 0, 0, s, 0, 0, -ox, -oz, oy, 1]

    # ---------- extraction ----------
    def _settings(self):
        settings = ifcopenshell.geom.settings()
        settings.set("use-world-coords", True)
        settings.set("weld-vertices", False)
        settings.set("apply-default-materials", True)
        settings.set("use-material-names", True)
        return settings

    def _collect_storeys(self):
        storeys = []
        for storey in self.model.by_type("IfcBuildingStorey"):
            matrix = placement_util.get_local_placement(storey.ObjectPlacement) if storey.ObjectPlacement else None
            z = float(matrix[2][3]) * self.scale if matrix is not None else None
            if z is None and storey.Elevation is not None:
                z = float(storey.Elevation) * self.scale
            storeys.append({"entity": storey, "z": z if z is not None else 0.0})
        storeys.sort(key=lambda item: item["z"])
        return storeys

    def _iterate_shapes(self, products):
        settings = self._settings()
        iterator = ifcopenshell.geom.iterator(settings, self.model, max(1, multiprocessing.cpu_count() - 1), include=products)
        if not iterator.initialize():
            return
        while True:
            yield iterator.get()
            if not iterator.next():
                break

    def _shape_arrays(self, shape):
        geometry = shape.geometry
        verts = shape_util.get_vertices(geometry)
        faces = shape_util.get_faces(geometry)
        names = []
        for material in geometry.materials:
            names.append(getattr(material, "name", None) or "")
        material_ids = np.asarray(geometry.material_ids, dtype=np.int64) if len(geometry.material_ids) else np.zeros(len(faces), dtype=np.int64)
        return np.asarray(verts, dtype=np.float64), np.asarray(faces, dtype=np.int64), names, material_ids

    def run(self, out_dir: str) -> dict:
        os.makedirs(out_dir, exist_ok=True)
        model = self.model
        schema = model.schema
        storeys = self._collect_storeys()
        storey_ids = {item["entity"].id() for item in storeys}

        products = [p for p in model.by_type("IfcProduct") if not any(p.is_a(c) for c in classify.SKIP_CLASSES)]
        spaces = model.by_type("IfcSpace")
        doors = model.by_type("IfcDoor")

        if not doors and not spaces:
            self.issues.append(Issue("IFC_NOT_ARCHITECTURAL", "Error", "Không có IfcDoor và IfcSpace: tệp có thể chỉ là kết cấu/MEP."))
        if not storeys:
            self.issues.append(Issue("IFC_NO_STOREY", "Error", "Tệp không khai báo IfcBuildingStorey; không chia được tầng."))

        masses = [p for p in products if p.is_a("IfcBuildingElementProxy") and classify.MASS.search(f"{p.Name or ''} {p.ObjectType or ''}")]
        if masses:
            self.issues.append(Issue("IFC_DEFAULT_MASS_PRESENT", "Info", f"Bỏ {len(masses)} khối massing khỏi hình học.", [m.GlobalId for m in masses]))
        mass_ids = {m.id() for m in masses}
        products = [p for p in products if p.id() not in mass_ids]

        raw: list[tuple] = []  # (entity, verts(n,3) IFC world m, faces, style names, material ids)
        seen = set()
        for shape in self._iterate_shapes(products):
            entity = model.by_id(shape.id)
            verts, faces, names, material_ids = self._shape_arrays(shape)
            if len(faces):
                raw.append((entity, verts, faces, names, material_ids))
                seen.add(entity.id())

        without_body = [p for p in products if p.id() not in seen and getattr(p, "Representation", None) is not None]
        blocking = [p for p in without_body if any(p.is_a(c) for c in classify.STRUCTURAL_CLASSES)]
        if blocking:
            self.issues.append(Issue("IFC_NO_BODY_GEOMETRY", "Error", f"{len(blocking)} tường/sàn/cửa/cầu thang không dựng được hình học Body.", [p.GlobalId for p in blocking]))
        minor = [p for p in without_body if p not in blocking and p.Representation and any(r.RepresentationIdentifier == "Body" for r in p.Representation.Representations)]
        if minor:
            self.issues.append(Issue("IFC_NO_BODY_GEOMETRY", "Warning", f"{len(minor)} phần tử phụ không có hình học Body.", [p.GlobalId for p in minor]))

        if not raw:
            raise ConversionError("IFC_NO_GEOMETRY", "Không dựng được hình học nào từ tệp IFC.")

        # Origin: horizontal centre of the building shell, keep authored elevations unless they are far from zero.
        all_points = np.concatenate([item[1] for item in raw])
        lo, hi = all_points.min(axis=0), all_points.max(axis=0)
        ground = storeys[0]["z"] if storeys else float(lo[2])
        substantial = [s for s in storeys if abs(s["z"]) < 5] or storeys
        oz = 0.0 if abs(ground) < 50 else (substantial[0]["z"] if substantial else float(lo[2]))
        self.origin = (float((lo[0] + hi[0]) / 2), float((lo[1] + hi[1]) / 2), oz)

        floors = []
        for index, item in enumerate(storeys):
            entity = item["entity"]
            floors.append({
                "id": f"L{index + 1}", "name": _text(entity.Name) or _text(entity.LongName) or f"Tầng {index + 1}",
                "ifcGlobalId": entity.GlobalId, "elevation": round(item["z"] - oz, 4), "entityId": entity.id(),
            })
        if not floors:
            floors.append({"id": "L1", "name": "Tầng 1", "ifcGlobalId": None, "elevation": round(float(lo[2]) - oz, 4), "entityId": None})
        floor_by_entity = {f["entityId"]: i for i, f in enumerate(floors) if f["entityId"] is not None}

        def nearest_floor(min_y: float) -> int:
            below = [i for i, f in enumerate(floors) if f["elevation"] <= min_y + 0.3]
            return below[-1] if below else 0

        unmapped_names: dict[str, set[str]] = defaultdict(set)
        material_names: dict[str, set[str]] = defaultdict(set)
        elements: list[ElementMesh] = []
        for entity, verts, faces, names, material_ids in raw:
            ifc_class = entity.is_a()
            predefined = _predefined(entity)
            element_names = _material_names(entity)
            base_key, mapped = classify.library_key_for(element_names, ifc_class, predefined)
            if not mapped and element_names:
                unmapped_names[base_key].update(n for n in element_names if n)
            per_style = []
            for style_name in names:
                key, style_mapped = classify.library_key_for([style_name], ifc_class, predefined) if style_name else (base_key, False)
                per_style.append(key if style_mapped else base_key)
                if style_name:
                    material_names[per_style[-1]].add(style_name)
            for name in element_names:
                material_names[base_key].add(name)
            keys = [per_style[m] if 0 <= m < len(per_style) else base_key for m in material_ids.tolist()]

            soup = self._to_glb(verts[faces.reshape(-1)])
            storey_entity = _storey_of(entity, storey_ids)
            bbox_min, bbox_max = soup.min(axis=0), soup.max(axis=0)
            floor_index = floor_by_entity.get(storey_entity) if storey_entity is not None else None
            if floor_index is None:
                floor_index = nearest_floor(float(bbox_min[1]))
            elements.append(ElementMesh(entity.GlobalId, ifc_class, floor_index, soup, keys, bbox_min, bbox_max))

        if unmapped_names:
            self.issues.append(Issue("IFC_UNMAPPED_MATERIAL", "Info",
                                     "Tên vật liệu chưa có trong từ điển, dùng vật liệu mặc định theo loại: " + ", ".join(sorted({n for v in unmapped_names.values() for n in v}))[:900]))

        glb_path = os.path.join(out_dir, "building.glb")
        semantic = self._write_glb(glb_path, floors, elements)

        space_records = self._spaces(spaces, storey_ids, floor_by_entity, floors)
        portals = self._portals(doors, elements, space_records, floors)
        portals += self._stairs(elements, floors)
        interactables = self._interactables(model, storey_ids, floor_by_entity, floors, elements)

        for f in floors:
            f.pop("entityId", None)

        self._qa(spaces, space_records, portals, interactables, elements, floors)

        editor_metadata = {
            "schemaVersion": "fet3d.editor/1", "units": "m", "upAxis": "Y", "handedness": "right",
            "coordinateTransform": self.coordinate_transform(),
            "floors": [{k: v for k, v in {
                "id": f["id"], "name": f["name"][:255], "elevationMeters": f["elevation"], "ifcGlobalId": f["ifcGlobalId"],
                "transform": [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, f["elevation"], 0, 1],
            }.items() if v is not None} for f in floors],
            "semanticMapping": semantic,
        }

        source_hash = _sha256(self.ifc_path)
        heights = [round(floors[i + 1]["elevation"] - floors[i]["elevation"], 4) for i in range(len(floors) - 1)]
        building = {
            "schemaVersion": "fet3d.building/1",
            "sources": [{"role": "architecture", "sha256": source_hash, "schema": schema, "app": self._application()}],
            "units": "m",
            "storeys": [{k: v for k, v in {
                "id": f["id"], "name": f["name"], "ifcGlobalId": f["ifcGlobalId"], "elevation": f["elevation"],
                "height": heights[i] if i < len(heights) and heights[i] > 0 else None,
            }.items() if v is not None} for i, f in enumerate(floors)],
            "spaces": [{k: v for k, v in s.items() if not k.startswith("_") and v is not None} for s in space_records],
            "portals": portals,
            "interactables": interactables,
            "materials": {f"FET3D_{key.replace('.', '_')}": {"libraryKey": key, "ifcNames": sorted(material_names.get(key, set()))[:50]}
                          for key in sorted({k for e in elements for k in e.library_keys})},
            "qaIssues": [issue.to_json() for issue in self.issues],
        }
        if self.revision_id:
            building["revisionId"] = self.revision_id

        with open(os.path.join(out_dir, "preview_metadata.json"), "w", encoding="utf-8") as handle:
            json.dump(editor_metadata, handle, ensure_ascii=False, indent=1)
        with open(os.path.join(out_dir, "building_metadata.json"), "w", encoding="utf-8") as handle:
            json.dump(building, handle, ensure_ascii=False, indent=1)

        outcome = "Failed" if any(i.severity in ("Error", "Critical") for i in self.issues) else "Passed"
        summary = {
            "ok": True, "outcome": outcome, "workerVersion": WORKER_VERSION, "schema": schema, "sourceSha256": source_hash,
            "glb": {"path": "building.glb", "sha256": _sha256(glb_path), "sizeBytes": os.path.getsize(glb_path)},
            "floors": len(floors), "elements": len(elements), "triangles": int(sum(len(e.positions) // 3 for e in elements)),
            "spaces": len(space_records), "portals": len(portals), "interactables": len(interactables),
            "issues": [issue.to_json() for issue in self.issues],
        }
        with open(os.path.join(out_dir, "result.json"), "w", encoding="utf-8") as handle:
            json.dump(summary, handle, ensure_ascii=False, indent=1)
        return summary

    def _application(self) -> str | None:
        apps = self.model.by_type("IfcApplication")
        if not apps:
            return None
        app = apps[0]
        return " ".join(x for x in (_text(app.ApplicationFullName), _text(app.Version)) if x) or None

    # ---------- GLB ----------
    def _write_glb(self, path: str, floors: list[dict], elements: list[ElementMesh]) -> list[dict]:
        builder = GlbBuilder()
        materials = {}

        def material_index(key: str) -> int:
            if key not in materials:
                colour = classify.PREVIEW_COLOURS.get(key, (0.8, 0.8, 0.8, 1.0))
                metallic = 0.6 if key.startswith("metal") else 0.0
                materials[key] = builder.material(f"FET3D_{key.replace('.', '_')}", colour, roughness=0.15 if key.startswith("glass") else 0.85, metallic=metallic)
            return materials[key]

        children = defaultdict(list)
        semantic = []
        triangles_per_floor = defaultdict(int)
        heavy = defaultdict(list)
        for element in elements:
            tri = element.positions.reshape(-1, 3, 3)
            normals = np.cross(tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0])
            lengths = np.linalg.norm(normals, axis=1)
            keep = lengths > 1e-12
            if not keep.any():
                continue
            tri, normals = tri[keep], normals[keep] / lengths[keep][:, None]
            keys = np.asarray(element.library_keys, dtype=object)[keep]
            primitives = []
            for key in sorted(set(keys.tolist())):
                mask = keys == key
                t, n = tri[mask], normals[mask]
                positions = t.reshape(-1, 3).astype(np.float32)
                vertex_normals = np.repeat(n, 3, axis=0).astype(np.float32)
                primitives.append(Primitive(positions, vertex_normals, _box_uv(positions, vertex_normals), np.arange(len(positions), dtype=np.uint32), material_index(key)))
            mesh = builder.mesh(element.global_id, primitives)
            node = builder.node(element.global_id, mesh=mesh)
            children[element.storey_index].append(node)
            count = int(tri.shape[0])
            triangles_per_floor[element.storey_index] += count
            heavy[element.storey_index].append((count, element.global_id))
            semantic.append({"ifcGlobalId": element.global_id, "nodeId": element.global_id, "floorId": floors[element.storey_index]["id"], "semanticType": element.ifc_class})

        roots = [builder.node(floor["id"], children=children.get(index, [])) for index, floor in enumerate(floors)]
        builder.write(path, roots, WORKER_VERSION)

        for index, count in triangles_per_floor.items():
            if count > TRIANGLE_BUDGET_PER_FLOOR:
                top = [gid for _, gid in sorted(heavy[index], reverse=True)[:20]]
                self.issues.append(Issue("IFC_TRIANGLE_BUDGET_EXCEEDED", "Warning",
                                         f"Tầng {floors[index]['id']} có {count:,} tam giác (ngân sách {TRIANGLE_BUDGET_PER_FLOOR:,}).", top))
        return semantic

    # ---------- metadata ----------
    def _spaces(self, spaces, storey_ids, floor_by_entity, floors) -> list[dict]:
        records = []
        if not spaces:
            return records
        for shape in self._iterate_shapes(list(spaces)):
            entity = self.model.by_id(shape.id)
            verts, faces, _, _ = self._shape_arrays(shape)
            if not len(faces):
                continue
            points = self._to_glb(verts)
            storey = _storey_of(entity, storey_ids)
            floor_index = floor_by_entity.get(storey, 0) if storey is not None else 0
            name, long_name = _text(entity.Name), _text(entity.LongName)
            records.append({
                "ifcGlobalId": entity.GlobalId, "name": name, "longName": long_name,
                "category": classify.space_category(f"{long_name or ''} {name or ''}"),
                "storey": floors[floor_index]["id"],
                "bbox": [[round(float(v), 4) for v in points.min(axis=0)], [round(float(v), 4) for v in points.max(axis=0)]],
            })
        return records

    def _portals(self, doors, elements, spaces, floors) -> list[dict]:
        by_gid = {e.global_id: e for e in elements}
        boundaries = defaultdict(set)
        for rel in self.model.by_type("IfcRelSpaceBoundary"):
            building_element = getattr(rel, "RelatedBuildingElement", None)
            space = getattr(rel, "RelatingSpace", None)
            if building_element is not None and space is not None and building_element.is_a("IfcDoor"):
                boundaries[building_element.GlobalId].add(space.GlobalId)
        portals = []
        for door in doors:
            fire_exit = _pset(door, "Pset_DoorCommon", "FireExit")
            external = _pset(door, "Pset_DoorCommon", "IsExternal")
            rating = _fire_rating(_pset(door, "Pset_DoorCommon", "FireRating"))
            linked = sorted(boundaries.get(door.GlobalId, set()))
            mesh = by_gid.get(door.GlobalId)
            sides = _spaces_beside(mesh, spaces) if mesh is not None else []
            if not linked:
                linked = sorted({s for s in sides if s})
            # Exit hint for the QA issue only (room on one side, nothing on the other, lowest floor). It is never written as
            # exitInferred: Unity would hang exit signs on it, and only the OrganizationUser may confirm an exit.
            if fire_exit is not True and external is not True and mesh is not None and mesh.storey_index == 0:
                if len(sides) == 2 and (sides[0] is None) != (sides[1] is None):
                    self.exit_hints.append(door.GlobalId)
            portal = {"ifcGlobalId": door.GlobalId, "type": "door", "fireExit": True if fire_exit is True else (False if fire_exit is False else None),
                      "external": external if isinstance(external, bool) else None, "fireRating": rating, "spaces": linked}
            portals.append({k: v for k, v in portal.items() if v is not None})
        return portals

    def _stairs(self, elements, floors) -> list[dict]:
        out = []
        for element in elements:
            if element.ifc_class in classify.STAIR_CLASSES:
                out.append({"ifcGlobalId": element.global_id, "type": "stair", "ifcClass": element.ifc_class, "floor": floors[element.storey_index]["id"]})
        return out

    def _interactables(self, model, storey_ids, floor_by_entity, floors, elements) -> list[dict]:
        by_gid = {e.global_id: e for e in elements}
        out = []
        candidates = []
        for ifc_class in classify.EQUIPMENT_CLASSES:
            try:
                candidates.extend(model.by_type(ifc_class))
            except RuntimeError:
                continue  # class absent from this schema
        seen = set()
        for entity in candidates:
            if entity.id() in seen:
                continue
            seen.add(entity.id())
            type_object = element_util.get_type(entity)
            text = " ".join(x for x in (_text(entity.Name), _text(getattr(entity, "ObjectType", None)), _text(getattr(type_object, "Name", None)) if type_object else None) if x)
            hit = classify.equipment_for(entity.is_a(), _predefined(entity), text)
            if not hit:
                continue
            key, confidence = hit
            mesh = by_gid.get(entity.GlobalId)
            if mesh is None:
                continue
            out.append(_candidate(entity.GlobalId, key, "ifc", entity.is_a(), confidence, mesh, floors))
        for door in model.by_type("IfcDoor"):
            mesh = by_gid.get(door.GlobalId)
            if mesh is None:
                continue
            fire_exit = _pset(door, "Pset_DoorCommon", "FireExit")
            external = _pset(door, "Pset_DoorCommon", "IsExternal")
            rating = _fire_rating(_pset(door, "Pset_DoorCommon", "FireRating"))
            if fire_exit is True or external is True:
                out.append(_candidate(door.GlobalId, "door.exit", "ifc", door.is_a(), 0.92 if fire_exit is True else 0.8, mesh, floors))
            elif rating:
                out.append(_candidate(door.GlobalId, "door.fire", "ifc", door.is_a(), 0.85, mesh, floors))
            else:
                out.append(_candidate(door.GlobalId, "door.standard", "ifc", door.is_a(), 0.9, mesh, floors))
        return out[:5000]

    def _qa(self, spaces, space_records, portals, interactables, elements, floors) -> None:
        if not spaces and self.model.by_type("IfcDoor"):
            self.issues.append(Issue("IFC_NO_SPACES", "Error", "Không có IfcSpace: bật xuất phòng/không gian khi export IFC."))
        unnamed = [s["ifcGlobalId"] for s in space_records if not s.get("name") and not s.get("longName")]
        if unnamed:
            self.issues.append(Issue("IFC_SPACE_UNNAMED", "Warning", f"{len(unnamed)} phòng không có tên.", unnamed))
        flagged_exit = [p for p in portals if p.get("type") == "door" and (p.get("fireExit") is True or p.get("external") is True)]
        if not flagged_exit:
            hints = self.exit_hints
            self.issues.append(Issue("IFC_EXIT_UNKNOWN", "Warning",
                                     "IFC không đánh dấu cửa thoát (Pset_DoorCommon.FireExit/IsExternal). Chọn một cửa có sẵn làm cửa thoát trong màn hình thiết bị.", hints))
        keys = {i["catalogKey"] for i in interactables}
        if not keys & classify.FIRE_KEYS:
            self.issues.append(Issue("IFC_NO_FIRE_EQUIPMENT", "Warning", "Không tìm thấy thiết bị báo/chữa cháy trong IFC."))
        if not keys & classify.WATER_KEYS:
            self.issues.append(Issue("IFC_NO_WATER_SOURCE", "Warning", "Không tìm thấy nguồn nước (bồn rửa, vòi sen…) trong IFC."))
        low = [i["ifcGlobalId"] for i in interactables if i["confidence"] < 0.9 and not i["catalogKey"].startswith("door.")]
        if low:
            self.issues.append(Issue("IFC_LOW_CONFIDENCE_DEVICE", "Warning", f"{len(low)} thiết bị nhận diện theo tên, cần xác nhận.", low))
        slabs = [e for e in elements if e.ifc_class in ("IfcSlab", "IfcCovering")]
        no_floor = []
        for space in space_records:
            (x0, y0, z0), (x1, y1, z1) = space["bbox"]
            cx, cz = (x0 + x1) / 2, (z0 + z1) / 2
            if not any(s.bbox_min[0] <= cx <= s.bbox_max[0] and s.bbox_min[2] <= cz <= s.bbox_max[2] and abs(s.bbox_max[1] - y0) < 0.35 for s in slabs):
                no_floor.append(space["ifcGlobalId"])
        if no_floor:
            self.issues.append(Issue("IFC_NO_FLOOR_UNDER_SPACE", "Warning", f"{len(no_floor)} phòng không có sàn bên dưới (không tự sinh sàn).", no_floor))
        if len(floors) > 1 and not any(e.ifc_class in classify.STAIR_CLASSES for e in elements):
            self.issues.append(Issue("IFC_NO_STAIR", "Warning", "Công trình nhiều tầng nhưng không có IfcStair/IfcRamp."))


class ConversionError(Exception):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def _box_uv(positions: np.ndarray, normals: np.ndarray) -> np.ndarray:
    """World-space box projection in metres so library textures tile at their real size."""
    axis = np.argmax(np.abs(normals), axis=1)
    uv = np.empty((len(positions), 2), dtype=np.float32)
    x, y, z = positions[:, 0], positions[:, 1], positions[:, 2]
    uv[axis == 0] = np.stack([z[axis == 0], y[axis == 0]], axis=1)
    uv[axis == 1] = np.stack([x[axis == 1], z[axis == 1]], axis=1)
    uv[axis == 2] = np.stack([x[axis == 2], y[axis == 2]], axis=1)
    return uv


def _spaces_beside(mesh: ElementMesh, spaces: list[dict]) -> list[str | None]:
    """Samples both sides of a door across its thin horizontal axis; returns [room or None, room or None]."""
    size = mesh.bbox_max - mesh.bbox_min
    centre = (mesh.bbox_max + mesh.bbox_min) / 2
    thin = 0 if size[0] < size[2] else 2
    found: list[str | None] = []
    for sign in (-1, 1):
        probe = centre.copy()
        probe[thin] += sign * (size[thin] / 2 + 0.5)
        probe[1] = mesh.bbox_min[1] + 1.0
        hit = None
        for space in spaces:
            (x0, y0, z0), (x1, y1, z1) = space["bbox"]
            if x0 <= probe[0] <= x1 and y0 <= probe[1] <= y1 and z0 <= probe[2] <= z1:
                hit = space["ifcGlobalId"]
                break
        found.append(hit)
    return found


def _fire_rating(value) -> str | None:
    """Keeps real ratings (EI30, 60 min, T30…) and drops placeholder text some exporters write."""
    text = _text(value)
    return text if text and any(ch.isdigit() for ch in text) else None


def _candidate(global_id: str, key: str, source: str, ifc_class: str, confidence: float, mesh: ElementMesh, floors: list[dict]) -> dict:
    floor = floors[mesh.storey_index]
    size = mesh.bbox_max - mesh.bbox_min
    centre = (mesh.bbox_max + mesh.bbox_min) / 2
    position = [round(float(centre[0]), 3), round(float(mesh.bbox_min[1] - floor["elevation"]), 3), round(float(centre[2]), 3)]
    rotation = 0.0 if size[0] >= size[2] else 90.0
    item = {
        "id": f"ifc:{global_id}"[:128].replace("$", "_"), "ifcGlobalId": global_id, "catalogKey": key, "source": source,
        "ifcClass": ifc_class, "confidence": round(confidence, 2), "floorId": floor["id"], "position": position, "rotationY": rotation,
    }
    dims = [max(round(float(v), 3), 0.01) for v in (size[0], size[2], size[1])]
    item["bbox"] = dims
    return item


def convert(ifc_path: str, out_dir: str, revision_id: str | None = None) -> dict:
    return Converter(ifc_path, revision_id).run(out_dir)
