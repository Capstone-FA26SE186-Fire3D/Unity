"""Splits the Sketchfab "Firebell & MCP" pack (CC BY, yvesjotres) into its two loose parts with Blender (no GUI).

Run:  blender-launcher.exe --background --factory-startup --python Tools/CallPointModel/split_call_point.py -- <pack.glb> <out dir>
Writes <out dir>/FireBell.glb (round bell) and <out dir>/CallPointButton.glb (square push button), each centred on its
own origin, so EquipmentPrefabBuilder can stack them the Vietnamese way: bell above, push button below.
"""
import sys
from pathlib import Path

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
SRC, OUT = Path(args[0]), Path(args[1])

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=str(SRC))
mesh = next(o for o in bpy.context.scene.objects if o.type == "MESH")
for o in bpy.context.view_layer.objects:
    o.select_set(False)
bpy.context.view_layer.objects.active = mesh
mesh.select_set(True)
# Bake parent transforms so the parts keep their real size, then split by loose parts.
bpy.ops.object.parent_clear(type="CLEAR_KEEP_TRANSFORM")
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
bpy.ops.object.mode_set(mode="EDIT")
bpy.ops.mesh.select_all(action="SELECT")
bpy.ops.mesh.separate(type="LOOSE")
bpy.ops.object.mode_set(mode="OBJECT")
parts = [o for o in bpy.context.scene.objects if o.type == "MESH"]

# Loose parts may be many small pieces: group them by which half of the pack they sit in (bell and button are
# side by side along the longest axis).
for o in parts:
    o.select_set(True)
bpy.context.view_layer.objects.active = parts[0]
xs = [(o, sum((o.matrix_world @ v.co).x for v in o.data.vertices) / max(1, len(o.data.vertices))) for o in parts]
mid = (min(x for _, x in xs) + max(x for _, x in xs)) / 2


def export(group, name):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    for o in group:
        o.select_set(True)
    bpy.context.view_layer.objects.active = group[0]
    if len(group) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.name = name
    bpy.ops.object.origin_set(type="ORIGIN_GEOMETRY", center="BOUNDS")
    obj.location = (0, 0, 0)
    OUT.mkdir(parents=True, exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=str(OUT / f"{name}.glb"), export_format="GLB", use_selection=True, export_yup=True)
    d = obj.dimensions
    print(f"[callpoint] {name}.glb: {len(obj.data.polygons)} faces, {d.x:.3f} x {d.y:.3f} x {d.z:.3f}")
    return obj


left = [o for o, x in xs if x < mid]
right = [o for o, x in xs if x >= mid]
# The bell is the larger part (round, deeper); the push button is the smaller square box.
size = lambda g: sum(len(o.data.vertices) for o in g)
bell, button = (left, right) if size(left) >= size(right) else (right, left)
export(bell, "FireBell")
export(button, "CallPointButton")
