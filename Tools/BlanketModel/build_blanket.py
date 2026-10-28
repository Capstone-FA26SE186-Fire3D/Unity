"""Builds the batch-2 blanket meshes and a door wedge with Blender (no GUI), the same way as Tools/TowelModel.

Run:  blender-launcher.exe --background --factory-startup --python Tools/BlanketModel/build_blanket.py -- <out dir>
Writes <out dir>/:
  BlanketFolded.glb  folded in four on a bed or shelf: soft rounded layers (60 x 45 cm, ~9 cm high)
  BlanketDraped.glb  held over the head and shoulders: cloth simulation dropped over a head-and-shoulders proxy,
                     origin at the top of the head so it hangs from the head bone
  DoorWedge.glb      rubber door wedge (12 x 4 x 3 cm), origin at the thin end on the floor
UVs are box-projected at 3 repeats per metre so a wool texture keeps its scale.
"""
import math
import random
import sys
from pathlib import Path

import bpy
import bmesh

OUT = Path(sys.argv[sys.argv.index("--") + 1]) if "--" in sys.argv else Path(__file__).resolve().parents[2] / "artifacts" / "made"
REPEAT_PER_M = 3.0


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.context.scene.unit_settings.system = "METRIC"
    return bpy.context.scene


def activate(obj):
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)


def box_uv(obj):
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


def material(name, rgba, rough=0.95):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = rgba
    bsdf.inputs["Roughness"].default_value = rough
    return mat


def lumpy_layer(name, size, z, seed, inset=0.0):
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
    sub.levels = 1
    apply_all(obj)
    rng = random.Random(seed)
    phases = [rng.uniform(0, 6.28) for _ in range(4)]
    for v in obj.data.vertices:
        x, y, zz = v.co
        puff = 1.0 - min(1.0, (abs(x) / (size[0] * 0.5)) ** 4 + (abs(y) / (size[1] * 0.5)) ** 4)
        lump = 0.003 * math.sin(x * 24 + phases[0]) * math.sin(y * 19 + phases[1]) + 0.002 * math.sin(x * 47 + y * 33 + phases[2])
        if zz > z:
            v.co.z += puff * size[2] * 0.3 + lump
        else:
            v.co.z -= lump * 0.3
        v.co.x += 0.002 * math.sin(y * 31 + phases[3])
    return obj


def folded():
    reset()
    w, d, layer = 0.60, 0.45, 0.022
    parts = [lumpy_layer(f"Layer{i}", (w, d, layer), layer * 0.5 + i * (layer * 0.92), seed=i, inset=i * 0.008) for i in range(4)]
    bpy.ops.mesh.primitive_cylinder_add(vertices=16, radius=layer * 1.9, depth=d - 0.006, location=(-w * 0.5 + layer * 1.2, 0, layer * 1.9),
                                        rotation=(math.radians(90), 0, 0))
    fold = bpy.context.active_object
    fold.modifiers.new("Subsurf", "SUBSURF").levels = 1
    apply_all(fold)
    parts.append(fold)
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    obj = bpy.context.active_object
    obj.name = "BlanketFolded"
    bpy.ops.object.shade_smooth()
    box_uv(obj)
    obj.data.materials.append(material("Blanket", (0.45, 0.14, 0.12, 1.0)))
    return obj


def draped():
    """A 1.4 x 1.0 m blanket dropped over a head-and-shoulders proxy: what a trainee looks like under a wet blanket."""
    scene = reset()
    # Proxy body: head sphere + shoulder capsule, collision only, deleted before export.
    bpy.ops.mesh.primitive_uv_sphere_add(radius=0.11, location=(0, 0, 1.62), segments=24, ring_count=12)
    head = bpy.context.active_object
    bpy.ops.mesh.primitive_cylinder_add(vertices=24, radius=0.23, depth=0.26, location=(0, 0, 1.36), rotation=(math.radians(90), 0, 0))
    shoulders = bpy.context.active_object
    shoulders.scale = (1.0, 0.6, 1.0)
    for proxy in (head, shoulders):
        activate(proxy)
        bpy.ops.object.transform_apply(scale=True)
        proxy.modifiers.new("Collision", "COLLISION")

    w, h = 1.4, 1.0
    bpy.ops.mesh.primitive_grid_add(x_subdivisions=40, y_subdivisions=30, size=1.0, location=(0, 0, 1.78))
    obj = bpy.context.active_object
    obj.name = "BlanketDraped"
    obj.scale = (w, h, 1.0)
    activate(obj)
    bpy.ops.object.transform_apply(scale=True)
    for v in obj.data.vertices:
        v.co.z += 0.01 * math.sin(v.co.x * 9) * math.cos(v.co.y * 7)

    cloth = obj.modifiers.new("Cloth", "CLOTH")
    cs = cloth.settings
    cs.quality = 8
    cs.mass = 0.4
    cs.tension_stiffness = 12
    cs.compression_stiffness = 12
    cs.bending_stiffness = 0.3
    cs.air_damping = 1.2
    cloth.collision_settings.use_self_collision = True
    cloth.collision_settings.self_distance_min = 0.005
    cloth.collision_settings.distance_min = 0.008
    scene.frame_start, scene.frame_end = 1, 70
    for f in range(1, 71):
        scene.frame_set(f)
    activate(obj)
    bpy.ops.object.modifier_apply(modifier="Cloth")
    solid = obj.modifiers.new("Solidify", "SOLIDIFY")
    solid.thickness = 0.008
    solid.offset = 0.0
    apply_all(obj)
    # Origin at the top of the head so Unity can hang it from the head bone.
    top = max(v.co.z for v in obj.data.vertices)
    for v in obj.data.vertices:
        v.co.z -= top
    for proxy in (head, shoulders):
        bpy.data.objects.remove(proxy, do_unlink=True)
    bpy.ops.object.shade_smooth()
    box_uv(obj)
    obj.data.materials.append(material("Blanket", (0.45, 0.14, 0.12, 1.0)))
    return obj


def wedge():
    reset()
    bm = bmesh.new()
    L, W, H = 0.12, 0.04, 0.03
    pts = [(0, -W / 2, 0), (L, -W / 2, 0), (L, -W / 2, H), (0, -W / 2, 0.004),
           (0, W / 2, 0), (L, W / 2, 0), (L, W / 2, H), (0, W / 2, 0.004)]
    vs = [bm.verts.new(p) for p in pts]
    for idx in [(0, 1, 2, 3), (4, 7, 6, 5), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)]:
        bm.faces.new([vs[i] for i in idx])
    me = bpy.data.meshes.new("DoorWedge")
    bm.to_mesh(me)
    bm.free()
    obj = bpy.data.objects.new("DoorWedge", me)
    bpy.context.scene.collection.objects.link(obj)
    activate(obj)
    bev = obj.modifiers.new("Bevel", "BEVEL")
    bev.width = 0.003
    bev.segments = 2
    apply_all(obj)
    box_uv(obj)
    obj.data.materials.append(material("Rubber", (0.12, 0.12, 0.13, 1.0), rough=0.8))
    return obj


def export(obj, name):
    OUT.mkdir(parents=True, exist_ok=True)
    activate(obj)
    bpy.ops.export_scene.gltf(filepath=str(OUT / f"{name}.glb"), export_format="GLB", use_selection=True, export_apply=True,
                              export_yup=True, export_materials="EXPORT")
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    d = obj.dimensions
    print(f"[blanket] {name}.glb: {tris} triangles, {d.x:.2f} x {d.y:.2f} x {d.z:.2f} m")


export(folded(), "BlanketFolded")
export(draped(), "BlanketDraped")
export(wedge(), "DoorWedge")
