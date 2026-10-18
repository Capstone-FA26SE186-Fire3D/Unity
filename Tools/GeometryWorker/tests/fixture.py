"""Builds a small two-storey IFC4 file in MILLIMETRES with known geometry for the converter tests.

Layout (IFC world metres, Z up): site/building at origin; L1 at z=0, L2 at z=3.0.
- L1: wall W1 from (0,0) to (10,0), 3 m high, 0.2 m thick; slab 10 x 6 at z=0 (0.2 thick, top at 0); exterior door D1 in W1
  at x=4..5 flagged Pset_DoorCommon.IsExternal; space R1 (0..10, 0..6, 0..3) named "Lobby"; extinguisher proxy E1 at (8,5).
- L2: wall W2 from (0,6) to (10,6); slab at z=3.0; space R2 unnamed; stair S1 (proxy box) under L1 from (0,1) to (2,4).
"""
from __future__ import annotations

import numpy as np
import ifcopenshell
import ifcopenshell.api.aggregate as aggregate
import ifcopenshell.api.context as context
import ifcopenshell.api.geometry as geometry
import ifcopenshell.api.pset as pset
import ifcopenshell.api.root as root
import ifcopenshell.api.spatial as spatial
import ifcopenshell.api.unit as unit


def _place(f, product, x=0.0, y=0.0, z=0.0, rotate_z_deg=0.0):
    matrix = np.eye(4)
    angle = np.radians(rotate_z_deg)
    matrix[:2, :2] = [[np.cos(angle), -np.sin(angle)], [np.sin(angle), np.cos(angle)]]
    matrix[:3, 3] = [x, y, z]
    geometry.edit_object_placement(f, product=product, matrix=matrix, is_si=True)


def _box(f, body, x_size, y_size, z_size):
    """Extruded rectangle (local origin at the min corner) built from the slab helper."""
    return geometry.add_slab_representation(f, context=body, depth=z_size, polyline=[(0, 0), (x_size, 0), (x_size, y_size), (0, y_size)])


def build(path: str) -> dict:
    f = ifcopenshell.file(schema="IFC4")
    project = root.create_entity(f, ifc_class="IfcProject", name="FET3D fixture")
    length = unit.add_si_unit(f, unit_type="LENGTHUNIT", prefix="MILLI")
    unit.assign_unit(f, units=[length])
    model = context.add_context(f, context_type="Model")
    body = context.add_context(f, context_type="Model", context_identifier="Body", target_view="MODEL_VIEW", parent=model)

    site = root.create_entity(f, ifc_class="IfcSite", name="Site")
    building = root.create_entity(f, ifc_class="IfcBuilding", name="Building")
    l1 = root.create_entity(f, ifc_class="IfcBuildingStorey", name="Tầng 1")
    l2 = root.create_entity(f, ifc_class="IfcBuildingStorey", name="Tầng 2")
    aggregate.assign_object(f, products=[site], relating_object=project)
    aggregate.assign_object(f, products=[building], relating_object=site)
    aggregate.assign_object(f, products=[l1, l2], relating_object=building)
    for product in (site, building):
        _place(f, product)
    _place(f, l1, z=0.0)
    _place(f, l2, z=3.0)
    l1.Elevation, l2.Elevation = 0.0, 3000.0

    def element(ifc_class, name, storey, rep, x=0.0, y=0.0, z=0.0, rot=0.0, **attrs):
        entity = root.create_entity(f, ifc_class=ifc_class, name=name)
        for key, value in attrs.items():
            setattr(entity, key, value)
        geometry.assign_representation(f, product=entity, representation=rep)
        _place(f, entity, x, y, z, rot)
        if ifc_class == "IfcSpace":
            aggregate.assign_object(f, products=[entity], relating_object=storey)
        else:
            spatial.assign_container(f, products=[entity], relating_structure=storey)
        return entity

    w1 = element("IfcWall", "W1", l1, _box(f, body, 10.0, 0.2, 3.0), 0, 0, 0)
    w2 = element("IfcWall", "W2", l2, _box(f, body, 10.0, 0.2, 3.0), 0, 6.0, 3.0)
    s1 = element("IfcSlab", "Floor L1", l1, _box(f, body, 10.0, 6.0, 0.2), 0, 0, -0.2)
    s2 = element("IfcSlab", "Floor L2", l2, _box(f, body, 10.0, 6.0, 0.2), 0, 0, 2.8)
    d1 = element("IfcDoor", "D1", l1, _box(f, body, 1.0, 0.1, 2.1), 4.0, 0.05, 0.0)
    door_pset = pset.add_pset(f, product=d1, name="Pset_DoorCommon")
    pset.edit_pset(f, pset=door_pset, properties={"IsExternal": True})
    e1 = element("IfcBuildingElementProxy", "Fire extinguisher 6kg", l1, _box(f, body, 0.2, 0.2, 0.6), 8.0, 5.0, 0.0)
    st = element("IfcStair", "Stair", l1, _box(f, body, 2.0, 3.0, 3.0), 0.0, 1.0, 0.0)
    r1 = element("IfcSpace", "Lobby", l1, _box(f, body, 10.0, 6.0, 3.0), 0, 0, 0)
    r2 = element("IfcSpace", None, l2, _box(f, body, 10.0, 6.0, 3.0), 0, 0, 3.0)
    f.write(path)
    return {name: entity.GlobalId for name, entity in {
        "W1": w1, "W2": w2, "S1": s1, "S2": s2, "D1": d1, "E1": e1, "ST": st, "R1": r1, "R2": r2, "L1": l1, "L2": l2,
    }.items()}
