using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Unity.Content;
using Fire3D.Unity.Gameplay.Interactions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Unity build worker (Docs technology §4, ifc_ingestion 5.6–5.8). Runs in batchmode on the team server:
    /// <code>
    /// unity run &lt;project&gt; -- -executeMethod Fire3D.Unity.Editor.ContentPipeline.BuildWorker.Run
    ///   -fet3dInput &lt;dir&gt; -fet3dOutput &lt;dir&gt; [-fet3dTarget Android|StandaloneWindows64] [-fet3dGpu] [-fet3dJob &lt;id&gt;]
    /// </code>
    /// Input dir: <c>building_metadata.json</c> (fet3d.building/1), <c>scenario.json</c> (fet3d.editor/1) and GLB
    /// geometry (one per storey via <c>storeys[].chunk</c>, or a single <c>building.glb</c>). Node names are IFC
    /// GlobalIds; material names map to <c>libraryKey</c> through <c>materials</c>. Output: per-floor scene bundles,
    /// outage lighting bundles, the documents and <c>manifest.json</c> (fet3d.package/1) with SHA-256 per file.
    /// </summary>
    public static class BuildWorker
    {
        private const string WorkRoot = "Assets/_Fire3D/ContentWork";

        public static void Run()
        {
            var args = Environment.GetCommandLineArgs();
            string Arg(string name, string fallback = null)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            }

            var code = 0;
            try
            {
                var result = Build(new Request
                {
                    InputDir = Arg("-fet3dInput") ?? throw new ArgumentException("-fet3dInput is required"),
                    OutputDir = Arg("-fet3dOutput") ?? throw new ArgumentException("-fet3dOutput is required"),
                    JobId = Arg("-fet3dJob", DateTime.UtcNow.ToString("yyyyMMddHHmmss")),
                    Target = Enum.TryParse<BuildTarget>(Arg("-fet3dTarget", "Android"), out var t) ? t : BuildTarget.Android,
                    UseGpu = Array.IndexOf(args, "-fet3dGpu") >= 0,
                    Bake = Array.IndexOf(args, "-fet3dNoBake") < 0,
                });
                Debug.Log($"[Fire3D worker] OK {result.ManifestPath} ({result.Floors} floors, {result.Warnings.Count} warnings)");
                foreach (var w in result.Warnings)
                    Debug.LogWarning($"[Fire3D worker] {w}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Fire3D worker] FAILED: {ex}");
                code = 1;
            }

            if (Application.isBatchMode)
                EditorApplication.Exit(code);
        }

        public sealed class Request
        {
            public string InputDir;
            public string OutputDir;
            public string JobId;
            public BuildTarget Target = BuildTarget.Android;
            public bool UseGpu;
            public bool Bake = true;
        }

        public sealed class Result
        {
            public string ManifestPath;
            public int Floors;
            public readonly List<string> Warnings = new();
        }

        public static Result Build(Request req)
        {
            var result = new Result();
            var building = ContractJson.ReadBuilding(File.ReadAllText(Path.Combine(req.InputDir, "building_metadata.json")));
            var scenarioJson = File.ReadAllText(Path.Combine(req.InputDir, "scenario.json"));
            ContractJson.ReadScenario(scenarioJson); // Validate before spending time on import and bake.
            var library = MaterialLibraryBuilder.Build();

            var work = $"{WorkRoot}/{Sanitize(req.JobId)}";
            if (AssetDatabase.IsValidFolder(work))
                AssetDatabase.DeleteAsset(work);
            Directory.CreateDirectory(work);

            // 1) Import GLB geometry (glTFast ScriptedImporter).
            var floors = building.Storeys.OrderBy(s => s.Elevation).ToList();
            var chunks = new List<(StoreyInfo storey, string asset)>();
            foreach (var s in floors)
            {
                var src = !string.IsNullOrEmpty(s.Chunk) ? Path.Combine(req.InputDir, s.Chunk) : null;
                if (src != null && File.Exists(src))
                    chunks.Add((s, CopyIn(src, work)));
            }

            if (chunks.Count == 0)
            {
                var single = Path.Combine(req.InputDir, "building.glb");
                if (!File.Exists(single))
                    throw new FileNotFoundException("No storey chunks and no building.glb in input.", single);
                var first = floors.FirstOrDefault() ?? new StoreyInfo { Id = "L1", Elevation = 0f };
                chunks.Add((first, CopyIn(single, work)));
                if (floors.Count > 1)
                    result.Warnings.Add("Single building.glb: all storeys go into one scene bundle.");
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // 2) One scene per chunk with materials, colliders, doors, spaces, stairs, lights and probes.
            var scenes = new List<Scene>();
            var scenePaths = new List<(StoreyInfo storey, string path)>();
            var first_ = true;
            foreach (var (storey, asset) in chunks)
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, first_ ? NewSceneMode.Single : NewSceneMode.Additive);
                first_ = false;
                SceneManager.SetActiveScene(scene);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset)
                             ?? throw new InvalidOperationException($"glTFast did not import {asset}");
                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                root.name = $"Building_{storey.FloorId}";
                var br = root.AddComponent<BuildingRoot>();
                br.FloorId = storey.FloorId;
                br.Elevation = storey.Elevation;

                AssignMaterials(root, building, library, work, result);
                AddColliders(root);
                MarkElements(root, building, storey, result);
                AddSpacesAndLights(root, building, storey, chunks.Count == 1);
                var path = $"{work}/{Sanitize(storey.FloorId)}.unity";
                EditorSceneManager.SaveScene(scene, path);
                scenes.Add(scene);
                scenePaths.Add((storey, path));
            }

            // 3) Bake both lighting scenarios.
            var outage = new Dictionary<string, LightingScenarioData>();
            if (req.Bake)
            {
                foreach (var s in scenes)
                    LightingBaker.PlaceProbes(s, new LightingBaker.Options());
                outage = LightingBaker.BakeBuilding(scenes, $"{work}/Lighting", new LightingBaker.Options { UseGpu = req.UseGpu });
            }

            // 4) AssetBundles: library entry keeps material references pointing at the app's shared bundle.
            var builds = new List<AssetBundleBuild>
            {
                new() { assetBundleName = ContentLoader.MaterialLibraryBundle, assetNames = LibraryAssets() },
            };
            var manifest = new ContentManifest
            {
                SchemaVersion = ContentManifest.Schema,
                RuntimeVersion = RuntimeCatalog.RuntimeVersion,
                BuildTarget = req.Target.ToString(),
                UnityVersion = Application.unityVersion,
                MaterialLibraryVersion = library.Version,
                RevisionId = building.RevisionId,
                Metadata = "building_metadata.json",
                Scenario = "scenario.json",
                LightingScenarios = req.Bake ? new List<string> { LightingScenarioSwitcher.Normal, LightingScenarioSwitcher.Outage } : new List<string>(),
            };
            foreach (var (storey, path) in scenePaths)
            {
                var floorBundle = $"floor_{Sanitize(storey.FloorId).ToLowerInvariant()}";
                builds.Add(new AssetBundleBuild { assetBundleName = floorBundle, assetNames = new[] { path } });
                string lightingBundle = null;
                if (outage.TryGetValue(path, out var data) && data != null)
                {
                    lightingBundle = floorBundle + "_lighting";
                    var folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(data))!.Replace('\\', '/');
                    builds.Add(new AssetBundleBuild
                    {
                        assetBundleName = lightingBundle,
                        assetNames = AssetDatabase.FindAssets("", new[] { folder }).Select(AssetDatabase.GUIDToAssetPath).Where(p => !AssetDatabase.IsValidFolder(p)).ToArray(),
                    });
                }

                manifest.Floors.Add(new ManifestFloor
                {
                    FloorId = storey.FloorId, Bundle = floorBundle, LightingBundle = lightingBundle,
                    Scene = Path.GetFileNameWithoutExtension(path), Elevation = storey.Elevation,
                });
            }

            var bundleOut = Path.Combine(Path.GetFullPath(work), "..", "_bundles_" + Sanitize(req.JobId));
            Directory.CreateDirectory(bundleOut);
            var abm = BuildPipeline.BuildAssetBundles(bundleOut, builds.ToArray(),
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, req.Target)
                ?? throw new InvalidOperationException("BuildAssetBundles failed.");

            // 5) Output package.
            Directory.CreateDirectory(req.OutputDir);
            foreach (var b in abm.GetAllAssetBundles().Where(n => n != ContentLoader.MaterialLibraryBundle))
                File.Copy(Path.Combine(bundleOut, b), Path.Combine(req.OutputDir, b), true);
            File.Copy(Path.Combine(req.InputDir, "building_metadata.json"), Path.Combine(req.OutputDir, "building_metadata.json"), true);
            File.WriteAllText(Path.Combine(req.OutputDir, "scenario.json"), scenarioJson);
            foreach (var file in Directory.GetFiles(req.OutputDir).Where(f => Path.GetFileName(f) != "manifest.json").OrderBy(f => f))
            {
                manifest.Files.Add(new ManifestFile
                {
                    Path = Path.GetFileName(file), Sha256 = ContentLoader.Sha256(file), Size = new FileInfo(file).Length,
                });
            }

            result.ManifestPath = Path.Combine(req.OutputDir, "manifest.json");
            File.WriteAllText(result.ManifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented, ContractJson.Settings));
            result.Floors = manifest.Floors.Count;
            return result;
        }

        private static string CopyIn(string src, string work)
        {
            var dst = $"{work}/{Path.GetFileName(src)}";
            File.Copy(src, dst, true);
            return dst;
        }

        private static string[] LibraryAssets() =>
            AssetDatabase.FindAssets("", new[] { MaterialLibraryBuilder.Root })
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => !AssetDatabase.IsValidFolder(p) && !p.EndsWith(".md"))
                .Distinct()
                .ToArray();

        private static void AssignMaterials(GameObject root, BuildingMetadata building, MaterialLibrary library, string work, Result result)
        {
            var tinted = new Dictionary<string, Material>();
            var unmapped = new HashSet<string>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var name = mats[i] != null ? mats[i].name.Replace(" (Instance)", "") : "";
                    if (!building.Materials.TryGetValue(name, out var map) || string.IsNullOrEmpty(map.LibraryKey))
                    {
                        unmapped.Add(name);
                        continue;
                    }

                    var baseMat = library.Get(map.LibraryKey);
                    if (baseMat == null)
                    {
                        unmapped.Add(name);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(map.Tint) && ColorUtility.TryParseHtmlString(map.Tint, out var tint))
                    {
                        var key = $"{map.LibraryKey}_{map.Tint}";
                        if (!tinted.TryGetValue(key, out var variant))
                        {
                            variant = new Material(baseMat) { name = key };
                            variant.SetColor("_BaseColor", tint);
                            AssetDatabase.CreateAsset(variant, $"{work}/{Sanitize(key)}.mat");
                            tinted[key] = variant;
                        }

                        mats[i] = variant;
                    }
                    else
                    {
                        mats[i] = baseMat;
                    }
                }

                r.sharedMaterials = mats;
            }

            foreach (var u in unmapped.Where(u => !string.IsNullOrEmpty(u)))
                result.Warnings.Add($"IFC_UNMAPPED_MATERIAL: '{u}' keeps its glTF material.");
        }

        private static void AddColliders(GameObject root)
        {
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<Collider>() != null)
                    continue;
                var mc = mf.gameObject.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
            }
        }

        private static void MarkElements(GameObject root, BuildingMetadata building, StoreyInfo storey, Result result)
        {
            var nodes = root.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            foreach (var p in building.Portals.Where(p => p.Type == "door" && p.IfcGlobalId != null))
            {
                if (!nodes.TryGetValue(p.IfcGlobalId, out var node))
                    continue;
                MakeDoor(node, p.IfcGlobalId);
                if (p.FireExit == true || p.ExitInferred == true)
                {
                    var b = BoundsOf(node.gameObject);
                    LightRig.ExitSign(new Vector3(b.center.x, b.max.y + 0.25f, b.center.z), node.rotation, root.transform);
                }
            }

            foreach (var it in building.Interactables.Where(i => i.IfcGlobalId != null))
            {
                if (nodes.TryGetValue(it.IfcGlobalId, out var node) && node.GetComponent<IfcElement>() == null)
                {
                    var e = node.gameObject.AddComponent<IfcElement>();
                    e.GlobalId = it.IfcGlobalId;
                    e.IfcClass = it.IfcClass;
                    node.gameObject.AddComponent<IgnoreForGrid>();
                }
            }

            foreach (var t in nodes.Values.Where(t => t.name.IndexOf("stair", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var b = BoundsOf(t.gameObject);
                if (b.size.sqrMagnitude < 0.5f)
                    continue;
                var vol = new GameObject($"StairVolume_{t.name}").AddComponent<StairVolume>();
                vol.transform.SetParent(root.transform, true);
                vol.transform.position = new Vector3(b.center.x, b.min.y - 0.1f, b.center.z);
                vol.Size = new Vector3(b.size.x + 0.4f, b.size.y + 0.4f, b.size.z + 0.4f);
            }
        }

        /// <summary>Wraps an IFC door node so it can swing: hinge on one end of the door's long horizontal axis.</summary>
        private static void MakeDoor(Transform node, string globalId)
        {
            var b = BoundsOf(node.gameObject);
            var alongX = b.size.x >= b.size.z;
            var rootGo = new GameObject(globalId);
            rootGo.transform.SetParent(node.parent, false);
            rootGo.transform.position = new Vector3(b.center.x, b.min.y, b.center.z);
            rootGo.transform.rotation = alongX ? Quaternion.identity : Quaternion.Euler(0f, 90f, 0f);
            var width = alongX ? b.size.x : b.size.z;
            var hinge = new GameObject("Hinge").transform;
            hinge.SetParent(rootGo.transform, false);
            hinge.localPosition = new Vector3(-width * 0.5f, 0f, 0f);
            node.SetParent(hinge, true);
            node.name = "Leaf";
            foreach (var c in node.GetComponentsInChildren<Collider>(true))
                UnityEngine.Object.DestroyImmediate(c);
            var box = node.gameObject.AddComponent<BoxCollider>();
            var local = node.InverseTransformPoint(b.center);
            box.center = local;
            box.size = node.InverseTransformVector(b.size);
            box.size = new Vector3(Mathf.Abs(box.size.x), Mathf.Abs(box.size.y), Mathf.Abs(box.size.z));
            var element = rootGo.AddComponent<IfcElement>();
            element.GlobalId = globalId;
            element.IfcClass = "IfcDoor";
            var view = rootGo.AddComponent<DoorView>();
            view.Bind(hinge, box);
            view.SetOpen(false, instant: true);
        }

        private static void AddSpacesAndLights(GameObject root, BuildingMetadata building, StoreyInfo storey, bool allStoreys)
        {
            foreach (var s in building.Spaces)
            {
                if (!allStoreys && s.Storey != storey.FloorId && s.Storey != storey.IfcGlobalId)
                    continue;
                if (s.Bbox is not { Length: 2 } || s.Bbox[0].Length < 3 || s.Bbox[1].Length < 3)
                    continue;
                var min = new Vector3(s.Bbox[0][0], s.Bbox[0][1], s.Bbox[0][2]);
                var max = new Vector3(s.Bbox[1][0], s.Bbox[1][1], s.Bbox[1][2]);
                var bounds = new Bounds((min + max) * 0.5f, max - min);
                var go = new GameObject($"Space_{s.IfcGlobalId}");
                go.transform.SetParent(root.transform, false);
                var vol = go.AddComponent<SpaceVolume>();
                vol.GlobalId = s.IfcGlobalId;
                vol.Category = s.Category ?? "room";
                vol.Bounds = bounds;
                if (vol.Category != "outdoor")
                    LightRig.Room(s.LongName ?? s.Name ?? s.IfcGlobalId, vol.Category, bounds, max.y, root.transform);
            }
        }

        private static Bounds BoundsOf(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0)
                return new Bounds(go.transform.position, Vector3.one);
            var b = rs[0].bounds;
            foreach (var r in rs)
                b.Encapsulate(r.bounds);
            return b;
        }

        private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_').ToArray());
    }
}
