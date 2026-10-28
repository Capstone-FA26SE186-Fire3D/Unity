using System.Collections.Generic;
using System.IO;
using Fire3D.Contracts;
using Fire3D.Unity.Bootstrap;
using Fire3D.Unity.Content;
using Fire3D.Unity.Editor.ContentPipeline;
using Fire3D.Unity.Gameplay.Interactions;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fire3D.Unity.Editor.Development
{
    /// <summary>
    /// Builds a two-floor graybox office (corridor, offices, kitchen, WC, stairwell, lobby exit) with library
    /// materials, real-size doors, normal/emergency lights and EXIT signs, plus matching fet3d.building/1 and
    /// fet3d.editor/1 fixtures. It is the local test bed for camera, interactions, hazards and the bake pipeline.
    /// </summary>
    public static class DevelopmentSceneBuilder
    {
        public const string SceneDir = "Assets/_Fire3D/Scenes/Development";
        public const string ScenePath = SceneDir + "/Development_Graybox.unity";
        public const string BootstrapPath = "Assets/_Fire3D/Scenes/Bootstrap/Bootstrap.unity";
        public const string ScenarioPath = SceneDir + "/dev-scenario.json";
        public const string BuildingPath = SceneDir + "/dev-building.json";

        private const float FloorHeight = 3.2f;
        private const float WallHeight = 3.0f;
        private const float Slab = 0.2f;
        private const float WallT = 0.2f;
        private const float DoorW = 0.95f;
        private const float DoorH = 2.1f;

        private static MaterialLibrary _lib;
        private static Transform _root;
        private static readonly List<JObject> Spaces = new();
        private static readonly List<JObject> Portals = new();

        [MenuItem("Fire3D/Development/Rebuild Graybox Scene")]
        public static void BuildMenu() => Build(bake: false);

        [MenuItem("Fire3D/Development/Rebuild Graybox Scene + Bake Lighting")]
        public static void BuildAndBakeMenu() => Build(bake: true);

        /// <summary>Batch entry: -executeMethod Fire3D.Unity.Editor.Development.DevelopmentSceneBuilder.BuildFromCommandLine</summary>
        public static void BuildFromCommandLine()
        {
            var bake = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-fet3dBake") >= 0;
            ProjectSetup.Apply();
            Build(bake);
        }

        public static void Build(bool bake)
        {
            _lib = MaterialLibraryBuilder.Build();
            var assets = RuntimeAssetsBuilder.Build();
            Spaces.Clear();
            Portals.Clear();

            Directory.CreateDirectory(SceneDir);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.16f, 0.16f, 0.18f);
            RenderSettings.skybox = null;

            _root = new GameObject("Building_L1_L2").transform;
            var br = _root.gameObject.AddComponent<BuildingRoot>();
            br.FloorId = "L1";
            br.Elevation = 0f;

            BuildGround();
            BuildFloor(0, ground: true);
            BuildFloor(1, ground: false);
            BuildStair();
            BuildRoof();

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.55f, 0.62f, 0.7f);
            camGo.AddComponent<AudioListener>();
            camGo.transform.position = new Vector3(4f, 5f, -4f);
            var urpCam = camGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            urpCam.renderPostProcessing = true;
            urpCam.dithering = true;
            CreatePostProcessing();

            var sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 0.7f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);
            sun.shadows = LightShadows.Soft;
            sun.gameObject.AddComponent<LightGroupMember>().Group = LightGroup.Normal;

            var appGo = new GameObject("Fire3DApp");
            var app = appGo.AddComponent<Fire3DApp>();
            app.Assets = assets;
            var dev = appGo.AddComponent<DevLauncher>();
            WriteFixtures(out var scenarioAsset, out var buildingAsset);
            dev.Scenario = scenarioAsset;
            dev.Building = buildingAsset;
            dev.Mode = TrainingMode.Guided;

            EditorSceneManager.SaveScene(scene, ScenePath);
            BuildBootstrapScene(assets);
            SetBuildScenes();

            if (bake)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                LightingBaker.PlaceProbes(scene, new LightingBaker.Options());
                var outage = LightingBaker.BakeBuilding(new[] { scene }, $"{SceneDir}/Lighting", new LightingBaker.Options());
                scene = LightingBaker.Reacquire(new[] { ScenePath })[0];
                var switcher = new GameObject("LightingScenarios").AddComponent<LightingScenarioSwitcher>();
                SceneManager.MoveGameObjectToScene(switcher.gameObject, scene);
                foreach (var data in outage.Values)
                    switcher.Register(data);
                EditorSceneManager.SaveScene(scene);
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"[Fire3D] Development scene built ({(bake ? "baked" : "realtime lights")}): {ScenePath}");
        }

        // ---------- Geometry ----------

        private static Material M(string key) => _lib.Get(key) ?? PrototypeFactory.Lit(Color.gray);

        private static GameObject Box(string name, Vector3 min, Vector3 max, string material, Transform parent = null, bool collider = true)
        {
            var size = max - min;
            return GrayboxMesh.Create(name, parent ?? _root, (min + max) * 0.5f, size, M(material), collider);
        }

        private static void BuildGround()
        {
            Box("Pavement", new Vector3(-6f, -0.25f, -6f), new Vector3(30f, -0.2f, 20f), "concrete");
            AddSpace("outdoor", "outdoor", new Bounds(new Vector3(12f, 0.5f, 7f), new Vector3(36f, 1f, 26f)), "L1", register: false);
        }

        private static void BuildRoof() => Box("Roof", new Vector3(-0.1f, 2f * FloorHeight - Slab, -0.1f), new Vector3(24.1f, 2f * FloorHeight, 14.1f), "concrete");

        private static void BuildFloor(int f, bool ground)
        {
            var e = f * FloorHeight;
            var floorId = f == 0 ? "L1" : "L2";
            var level = new GameObject(floorId).transform;
            level.SetParent(_root, false);

            // Slab (with the stair hole on the upper floor) and floor finishes per room.
            if (ground)
            {
                Box("Slab", new Vector3(-0.1f, e - Slab, -0.1f), new Vector3(24.1f, e, 14.1f), "concrete", level);
            }
            else
            {
                Box("Slab_W", new Vector3(-0.1f, e - Slab, -0.1f), new Vector3(18.5f, e, 14.1f), "concrete", level);
                Box("Slab_E_N", new Vector3(18.5f, e - Slab, 2.5f), new Vector3(24.1f, e, 14.1f), "concrete", level);
                // Top landing starts where the last tread ends (x = 23.25) so the flight is reachable from L2.
                Box("Slab_E_S", new Vector3(23.25f, e - Slab, -0.1f), new Vector3(24.1f, e, 2.5f), "concrete", level);
            }

            // Ceiling finish under the slab above. On the ground floor it stops at the stair hole: a full sheet here
            // read as a tiled floor across the opening when seen from the flight above, and people walked through it.
            if (ground)
            {
                Box("Ceiling_W", new Vector3(0.1f, e + WallHeight - 0.02f, 0.1f), new Vector3(18.5f, e + WallHeight, 13.9f), "ceiling.tile", level, collider: false);
                Box("Ceiling_E_N", new Vector3(18.5f, e + WallHeight - 0.02f, 2.5f), new Vector3(23.9f, e + WallHeight, 13.9f), "ceiling.tile", level, collider: false);
                Box("Ceiling_E_S", new Vector3(23.25f, e + WallHeight - 0.02f, 0.1f), new Vector3(23.9f, e + WallHeight, 2.5f), "ceiling.tile", level, collider: false);
            }
            else
            {
                Box("Ceiling", new Vector3(0.1f, e + WallHeight - 0.02f, 0.1f), new Vector3(23.9f, e + WallHeight, 13.9f), "ceiling.tile", level, collider: false);
            }

            // Exterior walls.
            var exitNorth = ground ? new[] { 21f } : System.Array.Empty<float>();
            var exitWest = ground ? new[] { 7f } : System.Array.Empty<float>();
            WallX($"{floorId}_South", 0f, 0f, 24f, e, level, System.Array.Empty<float>(), "wall.plaster");
            WallX($"{floorId}_North", 14f, 0f, 24f, e, level, exitNorth, "wall.plaster", doorWidth: 1.4f);
            WallZ($"{floorId}_West", 0f, 0f, 14f, e, level, exitWest, "wall.plaster");
            WallZ($"{floorId}_East", 24f, 0f, 14f, e, level, System.Array.Empty<float>(), "wall.plaster");

            // Corridor walls (z = 6 and z = 8) with room doors; the lobby/hall opens wide onto the corridor.
            WallX($"{floorId}_CorridorS", 6f, 0f, 24f, e, level, new[] { 4f, 11f, 16f, 21f }, "wall.plaster");
            WallX($"{floorId}_CorridorN_W", 8f, 0f, 18.6f, e, level, new[] { 5f, 14f }, "wall.plaster");
            WallX($"{floorId}_CorridorN_E", 8f, 23.4f, 24f, e, level, System.Array.Empty<float>(), "wall.plaster");

            // Partitions.
            WallZ($"{floorId}_P8", 8f, 0f, 6f, e, level, System.Array.Empty<float>(), "wall.plaster");
            WallZ($"{floorId}_P14", 14f, 0f, 6f, e, level, System.Array.Empty<float>(), "wall.tile");
            WallZ($"{floorId}_P18", 18f, 0f, 6f, e, level, System.Array.Empty<float>(), "wall.tile");
            WallZ($"{floorId}_P10", 10f, 8f, 14f, e, level, System.Array.Empty<float>(), "wall.plaster");
            WallZ($"{floorId}_P18N", 18f, 8f, 14f, e, level, System.Array.Empty<float>(), "wall.plaster");

            // Room finishes and spaces.
            var rooms = ground
                ? new[]
                {
                    ("Office A", "office", 0f, 0f, 8f, 6f, "floor.carpet"), ("Kitchen", "kitchen", 8f, 0f, 14f, 6f, "floor.tile"),
                    ("WC", "wc", 14f, 0f, 18f, 6f, "floor.tile"), ("Stair", "stair", 18f, 0f, 24f, 6f, "concrete"),
                    ("Corridor", "corridor", 0f, 6f, 24f, 8f, "floor.tile"), ("Office B", "office", 0f, 8f, 10f, 14f, "floor.carpet"),
                    ("Meeting", "office", 10f, 8f, 18f, 14f, "floor.wood"), ("Lobby", "lobby", 18f, 8f, 24f, 14f, "stone"),
                }
                : new[]
                {
                    ("Office C", "office", 0f, 0f, 8f, 6f, "floor.carpet"), ("Office D", "office", 8f, 0f, 14f, 6f, "floor.carpet"),
                    ("WC 2", "wc", 14f, 0f, 18f, 6f, "floor.tile"), ("Stair", "stair", 18f, 0f, 24f, 6f, "concrete"),
                    ("Corridor 2", "corridor", 0f, 6f, 24f, 8f, "floor.tile"), ("Office E", "office", 0f, 8f, 10f, 14f, "floor.carpet"),
                    ("Storage", "storage", 10f, 8f, 18f, 14f, "floor.wood"), ("Hall", "lobby", 18f, 8f, 24f, 14f, "stone"),
                };
            foreach (var (name, category, x0, z0, x1, z1, mat) in rooms)
            {
                if (!(category == "stair" && !ground))
                    Box($"{name}_Floor", new Vector3(x0 + 0.1f, e, z0 + 0.1f), new Vector3(x1 - 0.1f, e + 0.01f, z1 - 0.1f), mat, level, collider: false);
                AddSpace($"{floorId}-{name}", category, new Bounds(new Vector3((x0 + x1) * 0.5f, e + 1.5f, (z0 + z1) * 0.5f),
                    new Vector3(x1 - x0 - 0.2f, WallHeight, z1 - z0 - 0.2f)), floorId, name: name);
                // Ground-floor stair room: the flight's shaft is open above z < 2.5, so its ceiling light hangs over the
                // landing side only; a panel in the middle of the opening sat at head height for anyone on the stairs.
                var lightZ0 = ground && category == "stair" ? 2.5f : z0;
                RoomLights($"{floorId}_{name}", category, x0, lightZ0, x1, z1, e, level);
            }

            Furniture(f, e, level);

            // Doors.
            Door($"door-{floorId}-office-1", new Vector3(4f, e, 6f), false, level, ground ? "Office A" : "Office C");
            Door($"door-{floorId}-office-2", new Vector3(11f, e, 6f), false, level, ground ? "Kitchen" : "Office D");
            Door($"door-{floorId}-wc", new Vector3(16f, e, 6f), false, level, "WC");
            Door($"door-{floorId}-stair", new Vector3(21f, e, 6f), false, level, "Stair", fire: true);
            Door($"door-{floorId}-north-1", new Vector3(5f, e, 8f), false, level, ground ? "Office B" : "Office E");
            Door($"door-{floorId}-north-2", new Vector3(14f, e, 8f), false, level, ground ? "Meeting" : "Storage");
            if (ground)
            {
                Door("door-L1-exit-north", new Vector3(21f, e, 14f), false, level, "Lobby", width: 1.4f, exit: true);
                Door("door-L1-exit-west", new Vector3(0f, e, 7f), true, level, "Corridor", exit: true);
                // Faces look along local -Z: north sign towards the lobby (south), west sign into the corridor (east).
                ExitSign(new Vector3(21f, e + 2.45f, 13.85f), Quaternion.identity, level, LightRig.ExitSignKind.Final);
                ExitSign(new Vector3(0.15f, e + 2.45f, 7f), Quaternion.Euler(0f, -90f, 0f), level, LightRig.ExitSignKind.Final);
            }
            else
            {
                // Upper floors: the escape stair is behind the fire door; the sign faces the corridor (north).
                ExitSign(new Vector3(21f, e + 2.45f, 6.15f), Quaternion.Euler(0f, 180f, 0f), level, LightRig.ExitSignKind.StairDown);
            }
        }

        private static void WallX(string name, float z, float x0, float x1, float e, Transform parent, float[] doors, string mat, float doorWidth = DoorW)
        {
            var cursor = x0;
            foreach (var d in doors)
            {
                var w = doorWidth;
                if (d - w * 0.5f > cursor)
                    Box($"{name}_{cursor:0.#}", new Vector3(cursor, e, z - WallT * 0.5f), new Vector3(d - w * 0.5f, e + WallHeight, z + WallT * 0.5f), mat, parent);
                Box($"{name}_Lintel_{d:0.#}", new Vector3(d - w * 0.5f, e + DoorH, z - WallT * 0.5f), new Vector3(d + w * 0.5f, e + WallHeight, z + WallT * 0.5f), mat, parent);
                cursor = d + w * 0.5f;
            }

            if (x1 > cursor)
                Box($"{name}_{cursor:0.#}", new Vector3(cursor, e, z - WallT * 0.5f), new Vector3(x1, e + WallHeight, z + WallT * 0.5f), mat, parent);
            if (mat == "wall.plaster")
                Box($"{name}_Skirting", new Vector3(x0, e, z - WallT * 0.5f - 0.015f), new Vector3(x1, e + 0.08f, z + WallT * 0.5f + 0.015f), "wood", parent, collider: false);
        }

        private static void WallZ(string name, float x, float z0, float z1, float e, Transform parent, float[] doors, string mat)
        {
            var cursor = z0;
            foreach (var d in doors)
            {
                if (d - DoorW * 0.5f > cursor)
                    Box($"{name}_{cursor:0.#}", new Vector3(x - WallT * 0.5f, e, cursor), new Vector3(x + WallT * 0.5f, e + WallHeight, d - DoorW * 0.5f), mat, parent);
                Box($"{name}_Lintel_{d:0.#}", new Vector3(x - WallT * 0.5f, e + DoorH, d - DoorW * 0.5f), new Vector3(x + WallT * 0.5f, e + WallHeight, d + DoorW * 0.5f), mat, parent);
                cursor = d + DoorW * 0.5f;
            }

            if (z1 > cursor)
                Box($"{name}_{cursor:0.#}", new Vector3(x - WallT * 0.5f, e, cursor), new Vector3(x + WallT * 0.5f, e + WallHeight, z1), mat, parent);
        }

        private static void Door(string id, Vector3 pos, bool alongZ, Transform parent, string room, float width = DoorW, bool fire = false, bool exit = false)
        {
            var root = new GameObject(id).transform;
            root.SetParent(parent, false);
            root.localPosition = pos;
            root.localRotation = alongZ ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            var element = root.gameObject.AddComponent<IfcElement>();
            element.GlobalId = id;
            element.IfcClass = "IfcDoor";

            // Frame.
            GrayboxMesh.Create("Frame_L", root, new Vector3(-width * 0.5f + 0.03f, DoorH * 0.5f, 0f), new Vector3(0.06f, DoorH, WallT + 0.04f), M("metal.painted"), false);
            GrayboxMesh.Create("Frame_R", root, new Vector3(width * 0.5f - 0.03f, DoorH * 0.5f, 0f), new Vector3(0.06f, DoorH, WallT + 0.04f), M("metal.painted"), false);
            GrayboxMesh.Create("Frame_T", root, new Vector3(0f, DoorH - 0.03f, 0f), new Vector3(width, 0.06f, WallT + 0.04f), M("metal.painted"), false);

            // Leaf hinged on the left jamb.
            var hinge = new GameObject("Hinge").transform;
            hinge.SetParent(root, false);
            hinge.localPosition = new Vector3(-width * 0.5f + 0.06f, 0f, 0f);
            var leafW = width - 0.12f;
            var leaf = GrayboxMesh.Create("Leaf", hinge, new Vector3(leafW * 0.5f, (DoorH - 0.04f) * 0.5f, 0f), new Vector3(leafW, DoorH - 0.04f, 0.045f),
                M(fire || exit ? "metal.painted" : "wood"));
            // Lever handles on both faces at 1.0 m (leaf centre is at ~1.03 m).
            GrayboxMesh.Create("Handle", leaf.transform, new Vector3(leafW * 0.38f, -0.03f, 0.045f), new Vector3(0.13f, 0.025f, 0.03f), M("metal.brushed"), false);
            GrayboxMesh.Create("Handle_Back", leaf.transform, new Vector3(leafW * 0.38f, -0.03f, -0.045f), new Vector3(0.13f, 0.025f, 0.03f), M("metal.brushed"), false);
            var view = root.gameObject.AddComponent<DoorView>();
            view.Bind(hinge, leaf.GetComponent<Collider>());
            view.SetOpen(false, instant: true);

            Portals.Add(new JObject
            {
                ["ifcGlobalId"] = id, ["type"] = "door", ["fireExit"] = exit ? true : null, ["fireRating"] = fire ? "EI30" : null,
                ["spaces"] = new JArray(room), ["exitInferred"] = false,
            });
        }

        private static void BuildStair()
        {
            var stair = new GameObject("Stair_1").transform;
            stair.SetParent(_root, false);
            const int steps = 18;
            var rise = FloorHeight / steps;
            const float run = 0.25f;
            const float x0 = 18.75f;
            // Thin treads over a sloped stringer: solid stacked blocks would bury most lightmap texels.
            for (var i = 0; i < steps; i++)
            {
                var x = x0 + i * run;
                Box($"Step_{i:00}", new Vector3(x, i * rise, 0.4f), new Vector3(x + run, (i + 1) * rise, 2.3f), "concrete", stair);
            }

            var length = Mathf.Sqrt(steps * run * steps * run + FloorHeight * FloorHeight);
            var angle = Mathf.Atan2(FloorHeight, steps * run) * Mathf.Rad2Deg;
            var stringer = GrayboxMesh.Create("Stringer", stair, Vector3.zero, new Vector3(length, 0.16f, 1.9f), M("concrete"));
            stringer.transform.localPosition = new Vector3(x0 + steps * run * 0.5f, FloorHeight * 0.5f - 0.12f, 1.35f);
            stringer.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            var rail = GrayboxMesh.Create("Rail", stair, Vector3.zero, new Vector3(length, 0.05f, 0.05f), M("metal.brushed"), false);
            rail.transform.localPosition = new Vector3(x0 + steps * run * 0.5f, FloorHeight * 0.5f + 0.95f, 2.35f);
            rail.transform.localRotation = Quaternion.Euler(0f, 0f, angle);

            // Guard along the stair hole on L2. It stops short of the top landing: the opening between its end
            // and the east wall (inner face x = 23.9) must fit the 0.56 m player capsule with room to turn.
            Box("Guard_L2", new Vector3(18.5f, FloorHeight, 2.45f), new Vector3(22.5f, FloorHeight + 1.0f, 2.55f), "metal.painted", stair);
            var vol = new GameObject("StairVolume").AddComponent<StairVolume>();
            vol.transform.SetParent(stair, false);
            vol.transform.localPosition = new Vector3(21.2f, -0.1f, 1.35f);
            vol.Size = new Vector3(5.6f, FloorHeight + 0.4f, 2.4f);
            var el = stair.gameObject.AddComponent<IfcElement>();
            el.GlobalId = "stair-1";
            el.IfcClass = "IfcStair";
        }

        private static void Furniture(int f, float e, Transform level)
        {
            void Desk(float x, float z)
            {
                Box("Desk", new Vector3(x - 0.7f, e + 0.72f, z - 0.35f), new Vector3(x + 0.7f, e + 0.76f, z + 0.35f), "wood", level);
                Box("DeskLegs", new Vector3(x - 0.65f, e, z - 0.3f), new Vector3(x + 0.65f, e + 0.72f, z - 0.26f), "metal.painted", level);
                Box("Chair", new Vector3(x - 0.25f, e, z + 0.45f), new Vector3(x + 0.25f, e + 0.48f, z + 0.95f), "metal.painted", level);
            }

            if (f == 0)
            {
                Desk(2.5f, 2f); Desk(5.5f, 2f); Desk(2.5f, 11f); Desk(6.5f, 11f);
                Box("Counter", new Vector3(8.3f, e, 0.2f), new Vector3(13.7f, e + 0.9f, 0.85f), "stone", level);
                Box("Fridge", new Vector3(13f, e, 4.6f), new Vector3(13.75f, e + 1.8f, 5.6f), "metal.brushed", level);
                Box("MeetingTable", new Vector3(12f, e + 0.72f, 9.8f), new Vector3(16f, e + 0.76f, 12.2f), "wood", level);
                Box("Reception", new Vector3(19f, e, 11.5f), new Vector3(21.5f, e + 1.05f, 12.2f), "wood", level);
            }
            else
            {
                Desk(2.5f, 1.5f); Desk(5.5f, 1.5f); Desk(10.5f, 2f); Desk(3f, 11f); Desk(7f, 11f);
                Box("Shelf_1", new Vector3(10.3f, e, 13.3f), new Vector3(17.7f, e + 2f, 13.8f), "wood", level);
                Box("Shelf_2", new Vector3(10.3f, e, 9.6f), new Vector3(14f, e + 1.6f, 10.1f), "wood", level);
                Box("Sofa", new Vector3(19.5f, e, 12.6f), new Vector3(22.5f, e + 0.75f, 13.5f), "floor.carpet", level);
            }
        }

        private static void RoomLights(string name, string category, float x0, float z0, float x1, float z1, float e, Transform parent) =>
            LightRig.Room(name, category, new Bounds(new Vector3((x0 + x1) * 0.5f, e + WallHeight * 0.5f, (z0 + z1) * 0.5f), new Vector3(x1 - x0, WallHeight, z1 - z0)),
                e + WallHeight, parent);

        private static void ExitSign(Vector3 pos, Quaternion rot, Transform parent, LightRig.ExitSignKind kind) => LightRig.ExitSign(pos, rot, parent, kind);

        private static void AddSpace(string id, string category, Bounds bounds, string floorId, string name = null, bool register = true)
        {
            var go = new GameObject($"Space_{id}");
            go.transform.SetParent(_root, false);
            var s = go.AddComponent<SpaceVolume>();
            s.GlobalId = id;
            s.Category = category;
            s.Bounds = bounds;
            if (!register)
                return;
            Spaces.Add(new JObject
            {
                ["ifcGlobalId"] = id, ["name"] = name ?? id, ["longName"] = name ?? id, ["category"] = category, ["storey"] = floorId,
                // Contract (GLB) frame: X mirrored, so the min/max X corners swap.
                ["bbox"] = new JArray(new JArray(-bounds.max.x, bounds.min.y, bounds.min.z), new JArray(-bounds.min.x, bounds.max.y, bounds.max.z)),
            });
        }

        private static void CreatePostProcessing()
        {
            var go = new GameObject("PostProcessing");
            var volume = go.AddComponent<UnityEngine.Rendering.Volume>();
            volume.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
            var tone = profile.Add<UnityEngine.Rendering.Universal.Tonemapping>(true);
            tone.mode.Override(UnityEngine.Rendering.Universal.TonemappingMode.ACES);
            // Soft bloom: low threshold + wide scatter turns lamps, fire and exit signs into glows rather than hard blobs.
            var bloom = profile.Add<UnityEngine.Rendering.Universal.Bloom>(true);
            bloom.intensity.Override(0.55f);
            bloom.threshold.Override(0.85f);
            bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(1f, 0.9f, 0.8f));
            bloom.highQualityFiltering.Override(false);
            bloom.downscale.Override(UnityEngine.Rendering.Universal.BloomDownscaleMode.Half);
            var color = profile.Add<UnityEngine.Rendering.Universal.ColorAdjustments>(true);
            color.postExposure.Override(0.15f);
            color.contrast.Override(6f);
            color.saturation.Override(6f);
            var white = profile.Add<UnityEngine.Rendering.Universal.WhiteBalance>(true);
            white.temperature.Override(8f);
            // Warm the lights, cool the shadows: gives depth without extra lights.
            var smh = profile.Add<UnityEngine.Rendering.Universal.ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(new Vector4(0.92f, 0.96f, 1.08f, 0f));
            smh.highlights.Override(new Vector4(1.06f, 1.0f, 0.92f, 0f));
            var vignette = profile.Add<UnityEngine.Rendering.Universal.Vignette>(true);
            vignette.intensity.Override(0.2f);
            vignette.smoothness.Override(0.55f);
            Directory.CreateDirectory("Assets/_Fire3D/Settings");
            AssetDatabase.CreateAsset(profile, "Assets/_Fire3D/Settings/TrainingPostProcess.asset");
            volume.sharedProfile = profile;
        }

        // ---------- Fixtures ----------

        private static void WriteFixtures(out TextAsset scenario, out TextAsset building)
        {
            var b = new JObject
            {
                ["schemaVersion"] = BuildingMetadata.Schema,
                ["revisionId"] = "dev-graybox",
                ["units"] = "m",
                ["storeys"] = new JArray(
                    new JObject { ["id"] = "L1", ["name"] = "Tầng 1", ["elevation"] = 0.0, ["height"] = FloorHeight },
                    new JObject { ["id"] = "L2", ["name"] = "Tầng 2", ["elevation"] = FloorHeight, ["height"] = FloorHeight }),
                ["spaces"] = new JArray(Spaces),
                ["portals"] = new JArray(Portals),
                ["interactables"] = new JArray(),
                ["materials"] = new JObject(),
                ["qaIssues"] = new JArray(),
            };
            File.WriteAllText(BuildingPath, b.ToString());

            JObject Obj(string id, string kind, string floor, float x, float y, float z, float yaw = 0f, string cap = null, JObject p = null,
                string label = null, string anchor = null)
            {
                // Authored in Unity coordinates, written in the contract (GLB) frame like the web editor does.
                var q = ContractFrame.FromUnity(Quaternion.Euler(0f, yaw, 0f));
                var o = new JObject
                {
                    ["id"] = id, ["kind"] = kind,
                    ["placement"] = new JObject
                    {
                        ["floorId"] = floor, ["position"] = new JObject { ["x"] = -x, ["y"] = y, ["z"] = z },
                        ["rotation"] = new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w },
                    },
                };
                if (cap != null) o["capability"] = new JObject { ["id"] = cap, ["version"] = "1.0.0" };
                if (p != null) o["parameters"] = p;
                if (label != null) o["label"] = label;
                if (anchor != null) o["anchorId"] = anchor;
                return o;
            }

            var objects = new JArray
            {
                Obj("spawn-1", ScenarioObjectKind.Spawn, "L2", 4f, 0f, 3.2f, 0f, label: "Phòng làm việc C, tầng 2"),
                Obj("fire-kitchen", ScenarioObjectKind.Hazard, "L1", 12.2f, 0f, 1.6f, cap: Capabilities.FireSource,
                    p: new JObject { ["intensity"] = 7, ["activationSeconds"] = 8 }),
                Obj("outage", ScenarioObjectKind.Hazard, "L1", 0f, 0f, 0f, cap: Capabilities.PowerOutage, p: new JObject { ["atSeconds"] = 75 }),
                Obj("det-l1-corridor", ScenarioObjectKind.Equipment, "L1", 11f, 2.96f, 7f, cap: Capabilities.AlarmDetector, p: new JObject { ["smokeThreshold"] = 0.15 }),
                Obj("det-l2-corridor", ScenarioObjectKind.Equipment, "L2", 11f, 2.96f, 7f, cap: Capabilities.AlarmDetector, p: new JObject { ["smokeThreshold"] = 0.15 }),
                Obj("spr-kitchen", ScenarioObjectKind.Equipment, "L1", 11f, 2.96f, 3f, cap: Capabilities.Sprinkler, p: new JObject
                {
                    ["state"] = "Faulty", ["activationTemperatureC"] = 68, ["coverageRadiusMeters"] = 2.5, ["fireIntensityReduction"] = 0.7, ["activationDelaySeconds"] = 5,
                }),
                Obj("callpoint-l2", ScenarioObjectKind.Equipment, "L2", 19.6f, 0f, 6.12f, 180f, cap: Capabilities.AlarmCallPoint),
                Obj("ext-l2", ScenarioObjectKind.Equipment, "L2", 8.6f, 0f, 6.35f, cap: Capabilities.ExtinguisherPortable,
                    p: new JObject { ["agent"] = "CO2", ["dischargeSeconds"] = 12 }),
                Obj("towel-1", ScenarioObjectKind.Equipment, "L2", 5.6f, 0.77f, 1.4f, cap: Capabilities.ItemTowel,
                    p: new JObject { ["smokeIntakeMultiplierWhenCovered"] = 0.4, ["dryAfterSeconds"] = 150 }),
                Obj("sink-l2", ScenarioObjectKind.Equipment, "L2", 17.55f, 0f, 3f, -90f, cap: Capabilities.WaterSink),
                Obj("elevator-l2", ScenarioObjectKind.Equipment, "L2", 16.5f, 0f, 7.86f, 0f, cap: Capabilities.Elevator),
                Obj("bag-1", ScenarioObjectKind.Equipment, "L2", 6.4f, 0f, 4.6f, cap: Capabilities.BaitBelongings, label: "Laptop của bạn"),
                Obj("stair-door-l1", ScenarioObjectKind.Equipment, "L1", 21f, 0f, 6f, cap: Capabilities.DoorFire, anchor: "door-L1-stair"),
                Obj("stair-door-l2", ScenarioObjectKind.Equipment, "L2", 21f, 0f, 6f, cap: Capabilities.DoorFire, anchor: "door-L2-stair"),
                Obj("exit-door-north", ScenarioObjectKind.Equipment, "L1", 21f, 0f, 14f, cap: Capabilities.DoorExit, anchor: "door-L1-exit-north"),
                Obj("exit-door-west", ScenarioObjectKind.Equipment, "L1", 0f, 0f, 7f, cap: Capabilities.DoorExit, anchor: "door-L1-exit-west"),
                Obj("exit-north", ScenarioObjectKind.Goal, "L1", 21f, 0f, 15.2f, cap: Capabilities.ExitFinal, label: "Cửa chính"),
                Obj("exit-west", ScenarioObjectKind.Goal, "L1", -1.2f, 0f, 7f, cap: Capabilities.ExitFinal, label: "Cửa thoát hiểm phía tây", anchor: "door-L1-exit-west"),
                Obj("blocked-west", ScenarioObjectKind.BlockedElement, "L1", 0f, 0f, 7f, anchor: "door-L1-exit-west"),
                Obj("npc-panic", ScenarioObjectKind.Npc, "L2", 11f, 0f, 2.6f, cap: Capabilities.NpcGuide, p: new JObject { ["behaviour"] = "panic", ["mobilityImpaired"] = false }),
                Obj("npc-calm", ScenarioObjectKind.Npc, "L2", 5f, 0f, 11.5f, cap: Capabilities.NpcGuide, p: new JObject { ["behaviour"] = "calm", ["mobilityImpaired"] = true }),
            };

            var s = new JObject
            {
                ["schemaVersion"] = ScenarioState.Schema,
                ["name"] = "Cháy bếp tầng 1 — sơ tán từ tầng 2",
                ["runtimeVersion"] = RuntimeCatalog.RuntimeVersion,
                ["timeLimitSeconds"] = 300,
                ["randomSeed"] = 2026,
                ["replanIntervalSeconds"] = 3,
                ["modePolicy"] = new JObject { ["allowedModes"] = new JArray(TrainingMode.Learn, TrainingMode.Guided, TrainingMode.Assessment) },
                ["learningObjectives"] = new JArray(
                    "Báo cháy khi phát hiện khói",
                    "Không dùng thang máy, đi cầu thang bộ",
                    "Che mũi bằng khăn ướt khi đi qua khói",
                    "Giúp người khác cùng thoát ra",
                    "Thoát ra ngoài qua lối an toàn"),
                ["learnerInstructions"] = "Bạn đang làm việc ở phòng C tầng 2. Có mùi khét từ tầng dưới. Hãy xử lý tình huống và thoát ra ngoài an toàn.",
                ["rubric"] = new JObject
                {
                    ["schema_version"] = "1", ["pass_threshold"] = 0.7,
                    ["criteria"] = new JArray(new JObject
                    {
                        ["id"] = "reached-exit", ["metric"] = "reached_exit", ["mandatory"] = true, ["weight"] = 1, ["operator"] = "eq", ["threshold"] = 1,
                        ["description"] = "Đến được lối thoát.",
                    }),
                },
                ["objects"] = objects,
            };
            File.WriteAllText(ScenarioPath, s.ToString());
            AssetDatabase.ImportAsset(BuildingPath);
            AssetDatabase.ImportAsset(ScenarioPath);
            scenario = AssetDatabase.LoadAssetAtPath<TextAsset>(ScenarioPath);
            building = AssetDatabase.LoadAssetAtPath<TextAsset>(BuildingPath);
        }

        private static void BuildBootstrapScene(RuntimeAssets assets)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BootstrapPath)!);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var app = new GameObject(Fire3DApp.ReceiverName).AddComponent<Fire3DApp>();
            app.Assets = assets;
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.04f, 0.05f, 0.07f);
            camGo.AddComponent<AudioListener>();
            EditorSceneManager.SaveScene(scene, BootstrapPath);
        }

        private static void SetBuildScenes()
        {
            EditorBuildSettings.scenes = new[]
            {
                new EditorBuildSettingsScene(BootstrapPath, true),
                new EditorBuildSettingsScene(ScenePath, true),
            };
        }
    }
}
