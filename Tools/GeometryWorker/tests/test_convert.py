import json
import os

import jsonschema
import numpy as np
import pytest

from fet3d_geometry.convert import convert
from fet3d_geometry.glb import accessor_array, read_glb

from . import fixture

HERE = os.path.dirname(__file__)
REVISION = "6f1d2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b"


def _schema(name):
    with open(os.path.join(HERE, "schemas", name), encoding="utf-8") as handle:
        return json.load(handle)


@pytest.fixture(scope="module")
def converted(tmp_path_factory):
    work = tmp_path_factory.mktemp("convert")
    ifc = str(work / "fixture.ifc")
    ids = fixture.build(ifc)
    out = str(work / "out")
    summary = convert(ifc, out, REVISION)
    with open(os.path.join(out, "preview_metadata.json"), encoding="utf-8") as handle:
        editor = json.load(handle)
    with open(os.path.join(out, "building_metadata.json"), encoding="utf-8") as handle:
        building = json.load(handle)
    document, binary = read_glb(os.path.join(out, "building.glb"))
    return {"ids": ids, "summary": summary, "editor": editor, "building": building, "glb": document, "bin": binary}


def _node_positions(c, global_id):
    document = c["glb"]
    node = next(n for n in document["nodes"] if n["name"] == global_id)
    mesh = document["meshes"][node["mesh"]]
    return np.concatenate([accessor_array(document, c["bin"], p["attributes"]["POSITION"]) for p in mesh["primitives"]])


def test_metadata_matches_backend_schemas(converted):
    jsonschema.validate(converted["editor"], _schema("geometry-metadata.schema.json"))
    jsonschema.validate(converted["building"], _schema("building-metadata.schema.json"))


def test_storeys_are_root_nodes_with_elements_under_them(converted):
    document, ids = converted["glb"], converted["ids"]
    roots = [document["nodes"][i] for i in document["scenes"][0]["nodes"]]
    assert [n["name"] for n in roots] == ["L1", "L2"]
    l1_children = {document["nodes"][i]["name"] for i in roots[0]["children"]}
    l2_children = {document["nodes"][i]["name"] for i in roots[1]["children"]}
    assert {ids["W1"], ids["S1"], ids["D1"], ids["E1"], ids["ST"]} <= l1_children
    assert {ids["W2"], ids["S2"]} <= l2_children
    assert ids["R1"] not in l1_children  # spaces live in metadata only
    floors = converted["editor"]["floors"]
    assert [(f["id"], f["elevationMeters"], f["ifcGlobalId"]) for f in floors] == [("L1", 0.0, ids["L1"]), ("L2", 3.0, ids["L2"])]


def test_millimetre_model_becomes_metres_in_a_y_up_right_handed_frame(converted):
    # Origin = horizontal centre of the shell: x 0..10 -> 5, y 0..6.2 -> 3.1. GLB = (x-5, z, -(y-3.1)).
    w1 = _node_positions(converted, converted["ids"]["W1"])
    assert np.allclose(w1.min(axis=0), [-5.0, 0.0, 2.9], atol=1e-3)
    assert np.allclose(w1.max(axis=0), [5.0, 3.0, 3.1], atol=1e-3)
    w2 = _node_positions(converted, converted["ids"]["W2"])
    assert np.allclose(w2.min(axis=0), [-5.0, 3.0, -3.1], atol=1e-3)
    transform = np.array(converted["editor"]["coordinateTransform"]).reshape(4, 4).T  # column-major
    corner_mm = np.array([10000.0, 200.0, 3000.0, 1.0])  # W1 far top corner in IFC millimetres
    assert np.allclose(transform @ corner_mm, [5.0, 3.0, 2.9, 1.0], atol=1e-6)


def test_global_ids_are_kept_and_mapped(converted):
    mapping = {m["ifcGlobalId"]: m for m in converted["editor"]["semanticMapping"]}
    ids = converted["ids"]
    assert mapping[ids["D1"]]["semanticType"] == "IfcDoor" and mapping[ids["D1"]]["floorId"] == "L1"
    assert mapping[ids["W2"]]["floorId"] == "L2"
    assert all(m["nodeId"] == m["ifcGlobalId"] for m in mapping.values())


def test_materials_are_library_keys_without_textures(converted):
    document, building = converted["glb"], converted["building"]
    names = {m["name"] for m in document["materials"]}
    assert names == set(building["materials"])
    assert all("baseColorTexture" not in m["pbrMetallicRoughness"] for m in document["materials"])
    assert {v["libraryKey"] for v in building["materials"].values()} <= {
        "ceiling.tile", "concrete", "floor.carpet", "floor.tile", "floor.wood", "metal.brushed", "metal.painted", "stone",
        "wall.brick", "wall.plaster", "wall.tile", "wood", "glass.clear", "glass.frosted"}
    prim = document["meshes"][0]["primitives"][0]
    assert {"POSITION", "NORMAL", "TEXCOORD_0"} <= set(prim["attributes"])


def test_building_metadata_spaces_portals_stairs_and_candidates(converted):
    building, ids = converted["building"], converted["ids"]
    spaces = {s["ifcGlobalId"]: s for s in building["spaces"]}
    assert spaces[ids["R1"]]["name"] == "Lobby" and spaces[ids["R1"]]["storey"] == "L1"
    assert np.allclose(spaces[ids["R1"]]["bbox"], [[-5, 0, -2.9], [5, 3, 3.1]], atol=1e-3)
    door = next(p for p in building["portals"] if p["ifcGlobalId"] == ids["D1"])
    assert door["type"] == "door" and door["external"] is True and "exitInferred" not in door
    assert any(p["type"] == "stair" and p["ifcGlobalId"] == ids["ST"] for p in building["portals"])
    candidates = {c["ifcGlobalId"]: c for c in building["interactables"]}
    assert candidates[ids["D1"]]["catalogKey"] == "door.exit"
    extinguisher = candidates[ids["E1"]]
    assert extinguisher["catalogKey"] == "extinguisher.portable" and extinguisher["confidence"] < 0.9
    assert np.allclose(extinguisher["position"], [3.1, 0.0, -2.0], atol=1e-3)  # floor frame, bottom centre


def test_qa_reports_without_inventing_geometry(converted):
    codes = {i["code"]: i for i in converted["building"]["qaIssues"]}
    assert "IFC_EXIT_UNKNOWN" not in codes  # the door is flagged external in the IFC
    assert converted["ids"]["R2"] in codes["IFC_SPACE_UNNAMED"]["ifcGlobalIds"]
    assert "IFC_NO_WATER_SOURCE" in codes and "IFC_LOW_CONFIDENCE_DEVICE" in codes
    assert converted["summary"]["outcome"] == "Passed"
    document = converted["glb"]
    triangle_count = sum(document["accessors"][p["indices"]]["count"] // 3 for m in document["meshes"] for p in m["primitives"])
    assert triangle_count == converted["summary"]["triangles"]


def test_coplanar_triangles_share_vertices(converted):
    # A box wall has 12 triangles but only 24 distinct (position, normal, uv) corners once welded.
    document, binary = converted["glb"], converted["bin"]
    node = next(n for n in document["nodes"] if n["name"] == converted["ids"]["W1"])
    primitive = document["meshes"][node["mesh"]]["primitives"][0]
    vertices = document["accessors"][primitive["attributes"]["POSITION"]]["count"]
    triangles = document["accessors"][primitive["indices"]]["count"] // 3
    assert triangles == 12 and vertices == 24


def test_exit_unknown_when_no_door_is_flagged(tmp_path):
    ifc = str(tmp_path / "fixture.ifc")
    fixture.build(ifc)
    import ifcopenshell
    model = ifcopenshell.open(ifc)
    for prop in model.by_type("IfcPropertySingleValue"):
        if prop.Name == "IsExternal":
            prop.NominalValue = model.create_entity("IfcBoolean", False)
    model.write(ifc)
    summary = convert(ifc, str(tmp_path / "out"))
    codes = {i["code"]: i for i in summary["issues"]}
    assert codes["IFC_EXIT_UNKNOWN"]["severity"] == "Warning"
    assert summary["outcome"] == "Passed"
