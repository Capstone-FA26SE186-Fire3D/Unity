using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Unity.Content;
using UnityEditor;
using UnityEngine;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Builds the equipment prefabs from the free art in Art/Equipment (see its CREDITS.md) and the gameplay sounds in
    /// Art/Audio, and puts them into RuntimeAssets. Pivots follow <see cref="PrototypeFactory"/>: floor items stand on
    /// the pivot, wall items hang on the wall above a floor pivot facing -Z, ceiling items hang from 4 cm above it.
    /// Run after <see cref="RuntimeAssetsBuilder.Build"/>; that builder keeps these prefabs because they live outside
    /// Prefabs/Catalog.
    /// </summary>
    public static class EquipmentPrefabBuilder
    {
        private const string Art = "Assets/_Fire3D/Art";
        private const string EquipmentArt = Art + "/Equipment";
        private const string MaterialDir = EquipmentArt + "/Materials";
        private const string PrefabDir = "Assets/_Fire3D/Prefabs/Equipment";
        private const string AudioDir = Art + "/Audio";

        /// <summary>Ceiling devices: the scenario places them 4 cm under the ceiling (see the development scenario).</summary>
        private const float CeilingGap = 0.04f;

        [MenuItem("Fire3D/Content/Build Equipment Prefabs")]
        public static void Build()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(MaterialDir);
            ImportTextures();
            CutLoudest($"{AudioDir}/Source/ElectricSparks.mp3", "Sparks", 2.5f, 1);
            CutLoudest($"{AudioDir}/Source/CrawlWoodFloor.mp3", "Crawl", 1.4f, 3);
            ImportAudio();
            RenderSetup.EnsureDecals();

            var assets = AssetDatabase.LoadAssetAtPath<RuntimeAssets>(RuntimeAssetsBuilder.AssetPath);
            if (assets == null)
                assets = RuntimeAssetsBuilder.Build();

            var built = new Dictionary<string, GameObject>
            {
                [Capabilities.ExtinguisherPortable] = Save(Extinguisher()),
                [Capabilities.AlarmCallPoint] = Save(CallPoint()),
                [Capabilities.AlarmDetector] = Save(Detector()),
                [Capabilities.Sprinkler] = Save(Sprinkler()),
                [Capabilities.WaterSink] = Save(Fixture(Capabilities.WaterSink, "Bathroom/bathroomSink.glb", height: 0.9f)),
                [Capabilities.WaterBath] = Save(Fixture(Capabilities.WaterBath, "Bathroom/bathtub.glb", length: 1.6f)),
                [Capabilities.WaterShower] = Save(Fixture(Capabilities.WaterShower, "Bathroom/shower.glb", height: 2.1f)),
                [Capabilities.ItemTowel] = Save(Towel()),
                [Capabilities.BaitBelongings] = Save(Belongings()),
                [Capabilities.BaitBelongings + ":backpack"] = Save(Bait("backpack", "Sketchfab/Backpack.glb", height: 0.45f), "bait.belongings.backpack"),
                [Capabilities.BaitBelongings + ":phone"] = Save(Bait("phone", "Sketchfab/Smartphone.glb", length: 0.155f, flat: true), "bait.belongings.phone"),
                [Capabilities.Elevator] = Save(Elevator()),
            };

            for (var i = 0; i < assets.Interactables.Count; i++)
            {
                var entry = assets.Interactables[i];
                if (built.Remove(entry.Capability, out var prefab))
                    assets.Interactables[i] = new RuntimeAssets.CapabilityPrefab { Capability = entry.Capability, Prefab = prefab };
            }

            foreach (var kv in built)
                assets.Interactables.Add(new RuntimeAssets.CapabilityPrefab { Capability = kv.Key, Prefab = kv.Value });

            AssignSounds(assets);
            assets.Sky = SkyMaterial("urban_street_01_2k.hdr", "StreetDay", 0.85f);
            assets.SkyNight = SkyMaterial("modern_buildings_night_2k.hdr", "StreetNight", 1.1f);
            assets.ScorchDecal = ScorchMaterial();
            assets.SprinklerPrefab = SaveParticles(Fire3D.Unity.Presentation.VfxFactory.Droplets(null, Vfx("Droplet")), $"{PrefabDir}/SprinklerSpray.prefab");
            assets.SprayPrefab = SaveParticles(Fire3D.Unity.Presentation.VfxFactory.Powder(null, Vfx("Powder")), $"{PrefabDir}/ExtinguisherPowder.prefab");
            EditorUtility.SetDirty(assets);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Fire3D] Equipment prefabs built in {PrefabDir}");
        }

        // ---- Models -------------------------------------------------------------------------------------------------

        private static GameObject Extinguisher()
        {
            // Poly Haven korean_fire_extinguisher_01 (CC0), real-world scale.
            var root = new GameObject(Capabilities.ExtinguisherPortable);
            var model = Model("Extinguisher/korean_fire_extinguisher_01_1k.gltf", root.transform);
            // The model sits in a floor stand; a trainee picks up the extinguisher only.
            RemoveFloorStand(model, $"{EquipmentArt}/Extinguisher/korean_fire_extinguisher_01_nostand.asset");
            StandOnPivot(root.transform, model);
            AddBoxCollider(root);
            return root;
        }

        private static GameObject CallPoint()
        {
            // Sketchfab "Firebell & MCP" by yvesjotres (CC BY), split into its bell and push button by
            // Tools/CallPointModel/split_call_point.py. Mounted as one unit the way Vietnamese buildings do it: the
            // 8.5 cm push button centred 1.4 m above the floor, the 15 cm bell right above it, the sign on top. Front
            // towards -Z, the wall behind at z = 0.
            var root = new GameObject(Capabilities.AlarmCallPoint);
            var t = root.transform;
            // The split button looks at the wall: turn its glass window to the room.
            var button = WallPart(t, "Sketchfab/CallPointButton.glb", 0.085f, 1.4f, flip: true);
            button.name = "Button";
            var bell = WallPart(t, "Sketchfab/FireBell.glb", 0.15f, 1.4f + 0.0425f + 0.04f + 0.075f);
            bell.name = "Bell";
            AddBoxCollider(root);
            Sign(t, "CallPoint", new Vector3(0f, 1.8f, -0.005f), 0.3f);
            return root;
        }

        /// <summary>
        /// A wall-mounted part on its own parent: largest extent <paramref name="size"/> m, thin axis along Z, centred
        /// at <paramref name="centreY"/>, back face on the wall (z = 0).
        /// </summary>
        private static Transform WallPart(Transform root, string model, float size, float centreY, bool flip = false)
        {
            var holder = new GameObject("Part").transform;
            holder.SetParent(root, false);
            var m = Model(model, holder);
            Fit(holder, m, size);
            FaceFront(holder, m);
            if (flip)
                m.localRotation = Quaternion.Euler(0f, 180f, 0f) * m.localRotation;
            var b = Bounds(holder);
            m.localPosition += new Vector3(-b.center.x, centreY - b.center.y, -b.max.z);
            return holder;
        }

        private static GameObject Detector()
        {
            // Sketchfab "Simple smoke detector" by sousinho (CC BY): 12 cm disc hanging under the ceiling gap.
            var root = new GameObject(Capabilities.AlarmDetector);
            var model = Model("Sketchfab/SmokeDetector.glb", root.transform);
            HangFromCeiling(root.transform, model, 0.12f);
            return root;
        }

        private static GameObject Sprinkler()
        {
            // Sketchfab "Fire alarm system Sprinkler" by tasha.kosaykina (CC BY) ships a sprinkler head and a red
            // break-glass box; only the head is kept (its material name says "sprinkler"), 9 cm tall, pendent.
            var root = new GameObject(Capabilities.Sprinkler);
            var model = Model("Sketchfab/SprinklerPack.glb", root.transform);
            foreach (var r in model.GetComponentsInChildren<Renderer>())
                if (r.sharedMaterial == null || !r.sharedMaterial.name.ToLowerInvariant().Contains("sprinkler"))
                    Object.DestroyImmediate(r.gameObject);
            HangFromCeiling(root.transform, model, 0f, height: 0.09f);
            return root;
        }

        private static GameObject Fixture(string capability, string model, float height = 0f, float length = 0f)
        {
            // Kenney Furniture Kit (CC0), scaled to real size and re-skinned with ceramic/chrome so it sits with the PBR building.
            var root = new GameObject(capability);
            var m = Model(model, root.transform);
            var b = Bounds(root.transform);
            var scale = height > 0f ? height / b.size.y : length / Mathf.Max(b.size.x, b.size.z);
            m.localScale *= scale;
            StandOnPivot(root.transform, m);
            Reskin(root);
            AddBoxCollider(root);
            return root;
        }

        private static GameObject Towel()
        {
            // Blender meshes from Tools/TowelModel/build_towel.py: folded on a surface, cloth-simulated drape in the hand
            // (origin at the grip). TowelView swaps them and darkens the towel once wet.
            var root = new GameObject(Capabilities.ItemTowel);
            var cloth = TexturedMat("Towel", "Towel", 0.05f, tiling: 1f);
            var folded = Model("Towel/TowelFolded.glb", root.transform);
            folded.name = "Folded";
            StandOnPivot(root.transform, folded);
            var draped = Model("Towel/TowelDraped.glb", root.transform);
            draped.name = "Draped";
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                r.sharedMaterials = Enumerable.Repeat(cloth, r.sharedMaterials.Length).ToArray();
            draped.gameObject.SetActive(false);
            var b = Bounds(root.transform);
            var box = root.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size;
            root.AddComponent<Fire3D.Unity.Gameplay.Interactions.TowelView>().Bind(folded.gameObject, draped.gameObject);
            return root;
        }

        private static GameObject Belongings()
        {
            // Poly Haven classic_laptop (CC0): the valuable a trainee is tempted to go back for. Its glTF is larger than a
            // real laptop, so fit the width to 36 cm.
            var root = new GameObject(Capabilities.BaitBelongings);
            var model = Model("Laptop/classic_laptop_1k.gltf", root.transform);
            var b = Bounds(root.transform);
            model.localScale *= 0.36f / Mathf.Max(b.size.x, b.size.z);
            StandOnPivot(root.transform, model);
            AddBoxCollider(root);
            return root;
        }

        /// <summary>Another belongings bait (Sketchfab, CC BY): a backpack standing up or a phone lying flat.</summary>
        private static GameObject Bait(string variant, string model, float height = 0f, float length = 0f, bool flat = false)
        {
            var root = new GameObject($"{Capabilities.BaitBelongings}.{variant}");
            var m = Model(model, root.transform);
            if (flat)
                LayFlat(root.transform, m);
            var b = Bounds(root.transform);
            m.localScale *= height > 0f ? height / b.size.y : length / Mathf.Max(b.size.x, b.size.z);
            StandOnPivot(root.transform, m);
            AddBoxCollider(root);
            return root;
        }

        private static GameObject Elevator()
        {
            var root = new GameObject(Capabilities.Elevator);
            var t = root.transform;
            // Sketchfab "Simple Elevator with Animation" by lhaegy.fu (CC BY): stainless door leaves, frame, floor
            // indicator and call buttons; the model's walls, cabin, mirror and floor are dropped. Scaled to a 2.25 m
            // frame, standing on the floor, back face at z = 0, front towards -Z.
            var doors = Model("Sketchfab/ElevatorModern.glb", t);
            // The model's own call buttons sit on the wrong side and a second plate floats above the frame: drop
            // them and fit one up/down panel to the right of the doors below.
            Strip(doors, "Wall", "Cage", "Mirror", "Floor", "Plane", "CallingButtons");
            FaceFront(t, doors);
            var db = Bounds(t);
            doors.localScale *= 2.25f / db.size.y;
            db = Bounds(t);
            doors.localPosition += new Vector3(-db.center.x, -db.min.y, -db.max.z);
            db = Bounds(t);
            var frame = Mat("ElevatorFrame", new Color(0.32f, 0.33f, 0.35f), 0.55f, metallic: 0.8f);
            Part(PrimitiveType.Cube, "Sill", t, new Vector3(0f, 0.005f, -0.06f), new Vector3(db.size.x * 0.8f, 0.01f, 0.1f), frame);
            // Call panel: brushed plate with up/down buttons, 1.1 m high, 20 cm right of the frame.
            var panelX = db.max.x + 0.2f;
            var steel = AssetDatabase.LoadAssetAtPath<Material>($"{MaterialLibraryBuilder.MaterialsDir}/metal.stainless.mat");
            Part(PrimitiveType.Cube, "CallPanel", t, new Vector3(panelX, 1.1f, -0.008f), new Vector3(0.09f, 0.2f, 0.012f), steel);
            var lit = Mat("ButtonLit", new Color(0.95f, 0.9f, 0.7f), 0.7f, emission: new Color(1.2f, 1.05f, 0.7f));
            foreach (var y in new[] { 1.15f, 1.05f })
            {
                var button = Part(PrimitiveType.Cylinder, "CallButton", t, new Vector3(panelX, y, -0.016f), new Vector3(0.032f, 0.004f, 0.032f), lit);
                button.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            }

            Sign(t, "NoLift", new Vector3(panelX + 0.05f, 1.55f, -0.02f), 0.36f);
            var box = root.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, db.size.y * 0.5f, db.center.z);
            box.size = new Vector3(db.size.x, db.size.y, Mathf.Max(0.12f, db.size.z));
            return root;
        }

        // ---- Helpers ------------------------------------------------------------------------------------------------

        private static Transform Model(string relativePath, Transform parent)
        {
            var path = $"{EquipmentArt}/{relativePath}";
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (source == null)
                throw new FileNotFoundException($"Model not imported: {path}");
            var go = Object.Instantiate(source, parent, false);
            go.name = "Model";
            foreach (var c in go.GetComponentsInChildren<Collider>())
                Object.DestroyImmediate(c);
            return go.transform;
        }

        /// <summary>
        /// Drops the lowest loose part of the model's mesh when it is a low, separate block (a floor stand); the result
        /// is saved as a mesh asset. Vertices are welded by position first because glTF splits them at UV seams.
        /// </summary>
        private static void RemoveFloorStand(Transform model, string meshPath)
        {
            var filter = model.GetComponentInChildren<MeshFilter>();
            var source = filter != null ? filter.sharedMesh : null;
            if (source == null)
                return;
            var verts = source.vertices;
            var parent = new int[verts.Length];
            for (var i = 0; i < parent.Length; i++)
                parent[i] = i;
            int Find(int i)
            {
                while (parent[i] != i)
                    i = parent[i] = parent[parent[i]];
                return i;
            }

            var weld = new Dictionary<Vector3Int, int>();
            for (var i = 0; i < verts.Length; i++)
            {
                var key = Vector3Int.RoundToInt(verts[i] * 10000f);
                if (weld.TryGetValue(key, out var other))
                    parent[Find(i)] = Find(other);
                else
                    weld[key] = i;
            }

            for (var sub = 0; sub < source.subMeshCount; sub++)
            {
                var tris = source.GetTriangles(sub);
                for (var t = 0; t < tris.Length; t += 3)
                {
                    parent[Find(tris[t + 1])] = Find(tris[t]);
                    parent[Find(tris[t + 2])] = Find(tris[t]);
                }
            }

            // Per part: lowest/highest point and vertex count.
            var parts = new Dictionary<int, (float min, float max, int count)>();
            for (var i = 0; i < verts.Length; i++)
            {
                var r = Find(i);
                var p = parts.TryGetValue(r, out var v) ? v : (float.MaxValue, float.MinValue, 0);
                parts[r] = (Mathf.Min(p.Item1, verts[i].y), Mathf.Max(p.Item2, verts[i].y), p.Item3 + 1);
            }

            var height = source.bounds.size.y;
            var stand = -1;
            var lowest = float.MaxValue;
            foreach (var kv in parts)
                if (kv.Value.max < source.bounds.min.y + height * 0.25f && kv.Value.count < verts.Length / 2 && kv.Value.min < lowest)
                    (stand, lowest) = (kv.Key, kv.Value.min);
            if (stand < 0)
            {
                Debug.LogWarning($"[Fire3D] {model.name}: no separate floor stand found; kept the whole mesh");
                return;
            }

            var mesh = Object.Instantiate(source);
            mesh.name = Path.GetFileNameWithoutExtension(meshPath);
            for (var sub = 0; sub < mesh.subMeshCount; sub++)
            {
                var tris = source.GetTriangles(sub);
                var kept = new List<int>(tris.Length);
                for (var t = 0; t < tris.Length; t += 3)
                    if (Find(tris[t]) != stand)
                        kept.AddRange(new[] { tris[t], tris[t + 1], tris[t + 2] });
                mesh.SetTriangles(kept, sub);
            }

            mesh.RecalculateBounds();
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (existing != null)
            {
                EditorUtility.CopySerialized(mesh, existing);
                mesh = existing;
            }
            else
            {
                AssetDatabase.CreateAsset(mesh, meshPath);
            }

            filter.sharedMesh = mesh;
            Debug.Log($"[Fire3D] {model.name}: removed the floor stand ({parts[stand].count} of {verts.Length} vertices)");
        }

        private static Bounds Bounds(Transform root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            var b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(root.position, Vector3.zero);
            foreach (var r in renderers)
                b.Encapsulate(r.bounds);
            b.center -= root.position;
            return b;
        }

        /// <summary>Centres the model over the pivot and sets its lowest point on the floor.</summary>
        private static void StandOnPivot(Transform root, Transform model)
        {
            var b = Bounds(root);
            model.localPosition += new Vector3(-b.center.x, -b.min.y, -b.center.z);
        }

        /// <summary>Scales the model so its largest extent is <paramref name="size"/> metres.</summary>
        private static void Fit(Transform root, Transform model, float size)
        {
            var b = Bounds(root);
            model.localScale *= size / Mathf.Max(b.size.x, b.size.y, b.size.z);
        }

        /// <summary>
        /// Wall items face -Z in prefabs. Sketchfab models arrive in every orientation: the thinnest axis is the
        /// depth, so turn that axis to Z; a model whose face looks at +Z is flipped by the visual check afterwards.
        /// </summary>
        private static void FaceFront(Transform root, Transform model)
        {
            var b = Bounds(root);
            if (b.size.x < b.size.z && b.size.x < b.size.y)
                model.localRotation = Quaternion.Euler(0f, 90f, 0f) * model.localRotation;
            else if (b.size.y < b.size.x && b.size.y < b.size.z)
                model.localRotation = Quaternion.Euler(-90f, 0f, 0f) * model.localRotation;
        }

        /// <summary>Lays a slab-like model on its largest face (a phone on a table).</summary>
        private static void LayFlat(Transform root, Transform model)
        {
            var b = Bounds(root);
            if (b.size.y > b.size.x || b.size.y > b.size.z)
                model.localRotation = Quaternion.Euler(90f, 0f, 0f) * model.localRotation;
            b = Bounds(root);
            if (b.size.y > b.size.x || b.size.y > b.size.z)
                model.localRotation = Quaternion.Euler(0f, 0f, 90f) * model.localRotation;
        }

        /// <summary>Ceiling devices: disc down (flat axis vertical), top at the ceiling gap, scaled by diameter or height.</summary>
        private static void HangFromCeiling(Transform root, Transform model, float diameter, float height = 0f)
        {
            var b = Bounds(root);
            if (height <= 0f)
            {
                // The flat axis of a disc is its thinnest one; make it vertical.
                if (b.size.x < b.size.y && b.size.x < b.size.z)
                    model.localRotation = Quaternion.Euler(0f, 0f, 90f) * model.localRotation;
                else if (b.size.z < b.size.y && b.size.z < b.size.x)
                    model.localRotation = Quaternion.Euler(90f, 0f, 0f) * model.localRotation;
            }
            else if (b.size.y < b.size.x || b.size.y < b.size.z)
            {
                // A pendent head is taller than wide.
                model.localRotation = (b.size.x > b.size.z ? Quaternion.Euler(0f, 0f, 90f) : Quaternion.Euler(90f, 0f, 0f)) * model.localRotation;
            }

            b = Bounds(root);
            model.localScale *= height > 0f ? height / b.size.y : diameter / Mathf.Max(b.size.x, b.size.z);
            b = Bounds(root);
            model.localPosition += new Vector3(-b.center.x, CeilingGap - b.max.y, -b.center.z);
            AddBoxCollider(root.gameObject);
        }

        /// <summary>Removes every node whose name contains one of <paramref name="names"/> (walls, cabins shipped with a model).</summary>
        private static void Strip(Transform model, params string[] names)
        {
            foreach (var t in model.GetComponentsInChildren<Transform>(true).ToArray())
            {
                if (t == null || t == model)
                    continue;
                foreach (var n in names)
                    if (t.name.Contains(n))
                    {
                        Object.DestroyImmediate(t.gameObject);
                        break;
                    }
            }
        }

        private static Texture2D Vfx(string name) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art}/VFX/{name}.png");

        private static ParticleSystem SaveParticles(ParticleSystem ps, string path)
        {
            var go = ps.gameObject;
            RuntimeAssetsBuilder.PersistMaterials(go);
            PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            return AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<ParticleSystem>();
        }

        /// <summary>Burn mark decal (Kenney particle pack scorch, CC0) on the URP decal shader.</summary>
        private static Material ScorchMaterial()
        {
            var shader = Shader.Find("Shader Graphs/Decal");
            var tex = Vfx("Scorch");
            if (shader == null || tex == null)
                return null;
            const string path = Art + "/VFX/Scorch.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(shader) { name = "Scorch" };
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetTexture("Base_Map", tex);
            mat.SetFloat("Normal_Blend", 0f);
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static void AddBoxCollider(GameObject root)
        {
            var b = Bounds(root.transform);
            var box = root.AddComponent<BoxCollider>();
            box.center = b.center;
            box.size = b.size;
        }

        private static GameObject Part(PrimitiveType type, string name, Transform parent, Vector3 pos, Vector3 scale, Material mat)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        /// <summary>A flat sign plate on a wall, facing -Z, <paramref name="width"/> wide at 2:1.</summary>
        private static void Sign(Transform parent, string texture, Vector3 pos, float width)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{Art}/Signs/{texture}.png");
            var mat = Mat($"Sign_{texture}", Color.white, 0.25f);
            mat.SetTexture("_BaseMap", tex);
            mat.mainTexture = tex;
            EditorUtility.SetDirty(mat);
            var go = Part(PrimitiveType.Quad, "Sign", parent, pos, new Vector3(width, width * 0.5f, 1f), mat);
            go.transform.localRotation = Quaternion.identity; // a Quad faces -Z
        }

        /// <summary>Kenney's material names say what a part is: glass stays see-through, metal becomes chrome, white ceramic.</summary>
        private static void Reskin(GameObject root)
        {
            var ceramic = Mat("Ceramic", new Color(0.95f, 0.95f, 0.94f), 0.88f);
            var chrome = Mat("Chrome", new Color(0.85f, 0.86f, 0.88f), 0.85f, metallic: 1f);
            var glass = Mat("ShowerGlass", new Color(0.82f, 0.9f, 0.92f, 0.22f), 0.95f);
            foreach (var r in root.GetComponentsInChildren<Renderer>())
            {
                var mats = r.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var name = mats[i] != null ? mats[i].name.ToLowerInvariant() : "";
                    mats[i] = name.Contains("glass") ? glass : name.Contains("metal") ? chrome : ceramic;
                }

                r.sharedMaterials = mats;
            }
        }

        /// <summary>A URP Lit material saved under Art/Equipment/Materials (updated in place on rebuild).</summary>
        private static Material Mat(string name, Color color, float smoothness, float metallic = 0f, Color? emission = null)
        {
            var path = $"{MaterialDir}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = name };
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", smoothness);
            mat.SetFloat("_Metallic", metallic);
            if (color.a < 1f)
            {
                mat.SetFloat("_Surface", 1f);
                mat.SetFloat("_Blend", 0f);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetFloat("_ZWrite", 0f);
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            }

            if (emission.HasValue)
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", emission.Value);
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material TexturedMat(string name, string texturePrefix, float smoothness, float metallic = 0f, float tiling = 1f)
        {
            var mat = Mat(name, Color.white, smoothness, metallic);
            var color = AssetDatabase.LoadAssetAtPath<Texture2D>($"{EquipmentArt}/Textures/{texturePrefix}_Color.jpg");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>($"{EquipmentArt}/Textures/{texturePrefix}_NormalGL.jpg");
            mat.SetTexture("_BaseMap", color);
            mat.mainTexture = color;
            mat.SetTextureScale("_BaseMap", Vector2.one * tiling);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.EnableKeyword("_NORMALMAP");
            }

            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static GameObject Save(GameObject instance, string fileName = null)
        {
            var path = $"{PrefabDir}/{fileName ?? instance.name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            return prefab;
        }

        private static void ImportTextures()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { $"{EquipmentArt}/Textures", $"{Art}/Signs", $"{Art}/VFX" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is not TextureImporter importer)
                    continue;
                if (path.Contains("/VFX/") && !(path.EndsWith("/Scorch.png") || path.EndsWith("/Powder.png") || path.EndsWith("/Droplet.png")))
                    continue;
                var normal = path.Contains("_NormalGL");
                var sign = path.Contains("/Signs/") || path.Contains("/VFX/");
                var changed = importer.textureType != (normal ? TextureImporterType.NormalMap : TextureImporterType.Default)
                              || importer.maxTextureSize != (sign ? 512 : 1024);
                if (!changed)
                    continue;
                importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.maxTextureSize = sign ? 512 : 1024;
                if (sign)
                    importer.wrapMode = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }
        }

        /// <summary>A Poly Haven HDRI (CC0) as a panoramic sky, seen only through windows.</summary>
        private static Material SkyMaterial(string file, string name, float exposure)
        {
            var hdr = $"{Art}/Sky/{file}";
            if (AssetImporter.GetAtPath(hdr) is TextureImporter importer && (importer.maxTextureSize != 2048 || importer.mipmapEnabled))
            {
                importer.maxTextureSize = 2048;
                importer.mipmapEnabled = false;
                importer.wrapModeU = TextureWrapMode.Repeat;
                importer.wrapModeV = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }

            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(hdr);
            if (texture == null)
                return null;
            var path = $"{Art}/Sky/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Skybox/Panoramic"));
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetTexture("_MainTex", texture);
            mat.SetFloat("_Exposure", exposure);
            mat.SetFloat("_Mapping", 1f); // latitude-longitude
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>
        /// Cuts the <paramref name="count"/> loudest, non-overlapping <paramref name="seconds"/> of a recording into
        /// Art/Audio/<paramref name="name"/>.wav (or name0..N-1, mono, 5 ms fades). Skipped when the source is not in
        /// the project: the source file is removed after the first cut.
        /// </summary>
        private static void CutLoudest(string sourcePath, string name, float seconds, int count)
        {
            if (AssetImporter.GetAtPath(sourcePath) is not AudioImporter importer)
                return;
            var settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();
            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(sourcePath);
            clip.LoadAudioData();
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            var mono = new float[clip.samples];
            for (var i = 0; i < mono.Length; i++)
            {
                var sum = 0f;
                for (var c = 0; c < clip.channels; c++)
                    sum += data[i * clip.channels + c];
                mono[i] = sum / clip.channels;
            }

            // Sliding window of squared samples; the window with the most energy wins, then its span is masked and
            // the next one is taken.
            var window = Mathf.Min(mono.Length, Mathf.RoundToInt(seconds * clip.frequency));
            var prefix = new double[mono.Length + 1];
            for (var i = 0; i < mono.Length; i++)
                prefix[i + 1] = prefix[i] + mono[i] * mono[i];
            var taken = new List<int>();
            for (var n = 0; n < count; n++)
            {
                double best = -1;
                var bestStart = -1;
                for (var start = 0; start + window <= mono.Length; start++)
                {
                    var overlaps = false;
                    foreach (var t in taken)
                        if (start < t + window && t < start + window)
                            overlaps = true;
                    if (overlaps)
                        continue;
                    var energy = prefix[start + window] - prefix[start];
                    if (energy > best)
                        (best, bestStart) = (energy, start);
                }

                if (bestStart < 0)
                    break;
                taken.Add(bestStart);
            }

            taken.Sort();
            var fade = clip.frequency / 200;
            for (var n = 0; n < taken.Count; n++)
            {
                var bestStart = taken[n];
                var outPath = $"{AudioDir}/{name}{(count > 1 ? n.ToString() : "")}.wav";
                using (var stream = new FileStream(outPath, FileMode.Create))
                using (var w = new BinaryWriter(stream))
                {
                    w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                    w.Write(36 + window * 2);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                    w.Write(16);
                    w.Write((short)1);
                    w.Write((short)1);
                    w.Write(clip.frequency);
                    w.Write(clip.frequency * 2);
                    w.Write((short)2);
                    w.Write((short)16);
                    w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                    w.Write(window * 2);
                    for (var i = 0; i < window; i++)
                    {
                        var g = Mathf.Min(1f, Mathf.Min(i, window - 1 - i) / (float)fade);
                        w.Write((short)Mathf.Clamp(mono[bestStart + i] * g * 32767f, -32768f, 32767f));
                    }
                }

                AssetDatabase.ImportAsset(outPath);
                Debug.Log($"[Fire3D] {Path.GetFileName(outPath)}: {seconds:F1} s from {bestStart / (float)clip.frequency:F1} s of {Path.GetFileName(sourcePath)}");
            }

            AssetDatabase.DeleteAsset(sourcePath);
            var folder = Path.GetDirectoryName(sourcePath)!.Replace('\\', '/');
            if (AssetDatabase.FindAssets("", new[] { folder }).Length == 0)
                AssetDatabase.DeleteAsset(folder);
        }

        /// <summary>Mobile audio: Vorbis, mono for positional sounds, long recordings kept compressed in memory.</summary>
        private static void ImportAudio()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:AudioClip", new[] { AudioDir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetImporter.GetAtPath(path) is not AudioImporter importer)
                    continue;
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
                var settings = importer.defaultSampleSettings;
                settings.compressionFormat = AudioCompressionFormat.Vorbis;
                settings.quality = 0.45f;
                settings.loadType = clip != null && clip.length > 8f ? AudioClipLoadType.CompressedInMemory : AudioClipLoadType.DecompressOnLoad;
                importer.defaultSampleSettings = settings;
                importer.forceToMono = !Path.GetFileName(path).StartsWith("Ui");
                importer.SaveAndReimport();
            }
        }

        private static void AssignSounds(RuntimeAssets assets)
        {
            AudioClip Clip(string name)
            {
                foreach (var ext in new[] { "mp3", "ogg", "wav" })
                {
                    var c = AssetDatabase.LoadAssetAtPath<AudioClip>($"{AudioDir}/{name}.{ext}");
                    if (c != null)
                        return c;
                }

                Debug.LogWarning($"[Fire3D] Missing sound {name}");
                return null;
            }

            assets.AlarmClip = Clip("AlarmBell");
            var s = assets.Sounds ??= new RuntimeAssets.SoundSet();
            s.FireLoop = Clip("FireLoop");
            s.FireLarge = Clip("FireLarge");
            s.RoomTone = Clip("RoomTone");
            s.Sparks = Clip("Sparks");
            s.DoorOpen = Clip("DoorOpen");
            s.DoorClose = Clip("DoorClose");
            s.DoorTouch = Clip("DoorTouch");
            s.CallPointPress = Clip("CallPointPress");
            s.ExtinguisherPin = Clip("ExtinguisherPin");
            s.ExtinguisherSpray = Clip("ExtinguisherSpray");
            s.SprinklerSpray = Clip("SprinklerSpray");
            s.TapRunning = Clip("TapRunning");
            s.Cough = Clip("Cough");
            s.ElevatorDing = Clip("ElevatorDing");
            s.PowerCut = Clip("PowerCut");
            s.DetectorBeep = Clip("DetectorBeep");
            s.UiClick = Clip("UiClick");
            s.UiSuccess = Clip("UiSuccess");
            s.UiError = Clip("UiError");
            void Fill(List<AudioClip> list, string prefix, int count)
            {
                list.Clear();
                for (var i = 0; i < count; i++)
                    if (Clip($"{prefix}{i}") is { } c)
                        list.Add(c);
            }

            Fill(s.Footsteps, "Footstep", 5);
            Fill(s.Crawl, "Crawl", 3);
            Fill(s.Panting, "Panting", 2);
            Fill(s.DoorOpenWood, "DoorOpenWood", 8);
            Fill(s.DoorCloseWood, "DoorCloseWood", 10);
        }
    }
}
