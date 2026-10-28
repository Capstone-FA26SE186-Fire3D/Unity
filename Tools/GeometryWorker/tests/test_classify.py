"""Material name rules (no IFC file needed): every rule key exists in the Unity material library."""
from pathlib import Path

from fet3d_geometry.classify import MATERIAL_RULES, NON_FLOOR_EQUIVALENT, PREVIEW_COLOURS, library_key_for

LIBRARY_SOURCE = Path(__file__).resolve().parents[3] / "Assets" / "_Fire3D" / "Art" / "MaterialLibrary" / "Source"


def test_named_finishes_map_to_their_library_keys():
    cases = [
        (["Sàn epoxy xám"], "IfcSlab", "floor.epoxy"),
        (["Epoxy concrete"], "IfcWall", "concrete"),
        (["Vinyl 2mm"], "IfcCovering", "floor.vinyl"),
        (["Sàn nhựa PVC"], "IfcSlab", "floor.vinyl"),
        (["Terrazzo"], "IfcSlab", "floor.terrazzo"),
        (["Đá mài"], "IfcWall", "stone"),
        (["Rubber stair tread"], "IfcStairFlight", "floor.rubber"),
        (["Wall tile white 30x60"], "IfcWall", "wall.tile.white"),
        (["White tile"], "IfcSlab", "floor.tile"),
        (["Fliesen"], "IfcWall", "wall.tile"),
        (["Sơn nước"], "IfcWall", "wall.paint"),
        (["Gypsum plaster"], "IfcWall", "wall.plaster"),
        (["Stainless steel"], "IfcRailing", "metal.stainless"),
        (["Aluminium"], "IfcMember", "metal.brushed"),
        (["Stahl RAL 7016"], "IfcMember", "metal.painted"),
    ]
    for names, ifc_class, expected in cases:
        key, by_name = library_key_for(names, ifc_class, None)
        assert (key, by_name) == (expected, True), f"{names} on {ifc_class}: {key}"


def test_every_key_has_a_texture_set_and_a_preview_colour():
    keys = {key for _, key in MATERIAL_RULES} | {v for v in NON_FLOOR_EQUIVALENT.values() if v}
    keys -= {"glass.clear", "glass.frosted"}  # built from a shader, no texture set
    for key in keys:
        assert (LIBRARY_SOURCE / key / "Color.jpg").exists(), f"no texture set for {key}"
        assert key in PREVIEW_COLOURS, f"no preview colour for {key}"


if __name__ == "__main__":
    test_named_finishes_map_to_their_library_keys()
    test_every_key_has_a_texture_set_and_a_preview_colour()
    print("classify tests passed")
