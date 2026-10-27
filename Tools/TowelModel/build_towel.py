"""Builds the two towel meshes for item.towel with Blender (no GUI).

Run:  blender-launcher.exe --background --factory-startup --python Tools/TowelModel/build_towel.py
Writes Assets/_Fire3D/Art/Equipment/Towel/:
  TowelFolded.glb  folded in three on a surface: soft rounded layers, slight puff and lumps (34 x 24 cm, ~5 cm high)
  TowelDraped.glb  held in one hand: cloth simulation of a towel folded lengthwise hanging from the grip point,
                   origin at the grip so it hangs from the hand anchor
UVs are box-projected at 4 repeats per metre so the terry texture keeps its scale on both.
"""
import math
import random
from pathlib import Path

import bpy
import bmesh

OUT = Path(__file__).resolve().parents[2] / "Assets" / "_Fire3D" / "Art" / "Equipment" / "Towel"
REPEAT_PER_M = 4.0


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.unit_settings.system = "METRIC"
    return scene


def activate(obj):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)


def box_uv(obj):
    """Planar UVs from the dominant normal axis, in metres x repeat."""
    me = obj.data
    bm = bmesh.new()
    bm.from_mesh(me)
    uv = bm.loops.layers.uv.verify()
    for f in bm.faces:
        n = f.normal
        ax = max(range(3), key=lambda i: abs(n[i]))
        for loop in f.loops:
            co = obj.matrix_world @ loop.vert.co
            u, v = [(co.y, co.z), (co.x, co.z), (co.x, co.y)][ax]
            loop[uv].uv = (u * REPEAT_PER_M, v * REPEAT_PER_M)
    bm.to_mesh(me)
    bm.free()


def apply_all(obj):
    activate(obj)
    for m in list(obj.modifiers):
        bpy.ops.object.modifier_apply(modifier=m.name)


def material():
    mat = bpy.data.materials.new("Towel")
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (0.12, 0.2, 0.55, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.95
    return mat


def lumpy_layer(name, size, z, seed, inset=0.0):
    """One folded layer: a rounded slab, subdivided and lightly pushed around so it reads as soft cloth."""
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=(0, 0, z))
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = (size[0] - inset, size[1] - inset, size[2])
    activate(obj)
    bpy.ops.object.transform_apply(scale=True)
    bev = obj.modifiers.new("Bevel", "BEVEL")
    bev.width = size[2] * 0.48
    bev.segments = 3
    bev.limit_method = "NONE"
    sub = obj.modifiers.new("Subsurf", "SUBSURF")
    sub.levels = 1  # mobile budget: ~3k triangles per towel
    apply_all(obj)
    rng = random.Random(seed)
    phases = [rng.uniform(0, 6.28) for _ in range(4)]
    for v in obj.data.vertices:
        x, y, zz = v.co
        # Puff in the middle, a few soft lumps, edges slightly lower than the centre.
        puff = 1.0 - min(1.0, (abs(x) / (size[0] * 0.5)) ** 4 + (abs(y) / (size[1] * 0.5)) ** 4)
        lump = 0.0018 * math.sin(x * 38 + phases[0]) * math.sin(y * 31 + phases[1]) + 0.0012 * math.sin(x * 71 + y * 53 + phases[2])
        if zz > z:
            v.co.z += puff * size[2] * 0.35 + lump
        else:
            v.co.z -= lump * 0.3
        v.co.x += 0.0015 * math.sin(y * 45 + phases[3])
    return obj


def folded():
    reset()
    w, d, layer = 0.34, 0.24, 0.016
    parts = [lumpy_layer(f"Layer{i}", (w, d, layer), layer * 0.5 + i * (layer * 0.92), seed=i, inset=i * 0.006) for i in range(3)]
    # Rounded fold along one short edge, binding the layers.
    bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=layer * 1.45, depth=d - 0.004, location=(-w * 0.5 + layer * 0.9, 0, layer * 1.45),
                                        rotation=(math.radians(90), 0, 0))
    fold = bpy.context.active_object
    sub = fold.modifiers.new("Subsurf", "SUBSURF")
    sub.levels = 1
    apply_all(fold)
    parts.append(fold)
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    obj = bpy.context.active_object
    obj.name = "TowelFolded"
    bpy.ops.object.shade_smooth()
    box_uv(obj)
    obj.data.materials.append(material())
    return obj


def draped():
    scene = reset()
    # Towel folded lengthwise once (30 x 50 cm), gripped at the middle of its top edge.
    w, h = 0.30, 0.50
    bpy.ops.mesh.primitive_grid_add(x_subdivisions=24, y_subdivisions=40, size=1.0, location=(0, 0, 0))
    obj = bpy.context.active_object
    obj.name = "TowelDraped"
    obj.scale = (w, h, 1.0)
    activate(obj)
    bpy.ops.object.transform_apply(scale=True)
    # Lay it out horizontally, then let it fall around the grip: rotate so it starts hanging slightly forward.
    for v in obj.data.vertices:
        x, y, _ = v.co
        v.co = (x, 0.0, -(y + h * 0.5))  # top edge at z = 0, hanging down
        v.co.y = 0.02 * math.sin(x * 20)  # small initial ripple so the cloth folds instead of staying flat
    group = obj.vertex_groups.new(name="Grip")
    grip = [v.index for v in obj.data.vertices if abs(v.co.z) < 1e-4 and abs(v.co.x) < 0.035]
    group.add(grip, 1.0, "REPLACE")

    cloth = obj.modifiers.new("Cloth", "CLOTH")
    cs = cloth.settings
    cs.quality = 8
    cs.mass = 0.12
    cs.tension_stiffness = 8
    cs.compression_stiffness = 8
    cs.bending_stiffness = 0.08
    cs.air_damping = 1.5
    cs.vertex_group_mass = "Grip"
    cloth.collision_settings.use_self_collision = True
    cloth.collision_settings.self_distance_min = 0.004
    # Hand pinches the grip: pull those vertices together so the towel bunches at the top.
    for v in obj.data.vertices:
        if v.index in grip:
            v.co.x *= 0.35
    scene.frame_start, scene.frame_end = 1, 90
    for f in range(1, 91):
        scene.frame_set(f)
    activate(obj)
    bpy.ops.object.modifier_apply(modifier="Cloth")
    solid = obj.modifiers.new("Solidify", "SOLIDIFY")
    solid.thickness = 0.006
    solid.offset = 0.0
    apply_all(obj)
    bpy.ops.object.shade_smooth()
    box_uv(obj)
    obj.data.materials.append(material())
    return obj


def export(obj, name):
    OUT.mkdir(parents=True, exist_ok=True)
    activate(obj)
    bpy.ops.export_scene.gltf(filepath=str(OUT / f"{name}.glb"), export_format="GLB", use_selection=True, export_apply=True,
                              export_yup=True, export_materials="EXPORT")
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    dims = obj.dimensions
    print(f"[towel] {name}.glb: {tris} triangles, {dims.x:.2f} x {dims.y:.2f} x {dims.z:.2f} m")


export(folded(), "TowelFolded")
export(draped(), "TowelDraped")
