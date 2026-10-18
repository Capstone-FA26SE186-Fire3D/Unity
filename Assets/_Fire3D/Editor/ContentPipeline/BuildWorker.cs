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
    ///   [-fet3dNoBake] [-fet3dResult &lt;file&gt;]
    /// </code>
    /// <c>-fet3dResult</c> writes a JSON summary (<see cref="Summary"/>) the worker agent (Tools/PackageWorker) reads after
    /// the Editor exits; the manifest is checked with <see cref="PackageManifestFactory.Validate"/> before success is reported.
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
            var summary = new Summary();
            var resultPath = Arg("-fet3dResult");
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
                summary.Ok = true;
                summary.ManifestPath = result.ManifestPath;
                summary.Floors = result.Floors;
                summary.Warnings.AddRange(result.Warnings);
                Debug.Log($"[Fire3D worker] OK {result.ManifestPath} ({result.Floors} floors, {result.Warnings.Count} warnings)");
                foreach (var w in result.Warnings)
                    Debug.LogWarning($"[Fire3D worker] {w}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Fire3D worker] FAILED: {ex}");
                summary.Error = ex.Message;
                if (ex is ManifestInvalidException mi)
                    summary.Issues.AddRange(mi.Issues);
                code = 1;
            }

            if (!string.IsNullOrEmpty(resultPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(resultPath))!);
                    File.WriteAllText(resultPath, JsonConvert.SerializeObject(summary, Formatting.Indented, ContractJson.Settings));
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[Fire3D worker] cannot write result file {resultPath}: {ex.Message}");
                    code = 1;
                }
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

            /// <summary>Clone imported meshes with lightmap UVs and clean normals/tangents (default). Off only to compare.</summary>
            public bool PrepareMeshes = true;
        }

        public sealed class Result
        {
            public string ManifestPath;
            public int Floors;
            public readonly List<string> Warnings = new();
        }

        /// <summary>Machine-readable outcome written to <c>-fet3dResult</c>.</summary>
        public sealed class Summary
        {
            [JsonProperty("ok")] public bool Ok;
            [JsonProperty("manifestPath")] public string ManifestPath;
            [JsonProperty("floors")] public int Floors;
            [JsonProperty("warnings")] public List<string> Warnings = new();
            /// <summary>Manifest validation problems ("path: message"), reported to the backend as issues.</summary>
            [JsonProperty("issues")] public List<string> Issues = new();
            [JsonProperty("error")] public string Error;
        }

        public sealed class ManifestInvalidException : Exception
        {
            public readonly List<string> Issues;
            public ManifestInvalidException(List<string> issues) : base("manifest.json failed validation: " + string.Join("; ", issues)) => Issues = issues;
        }

        public static Result Build(Request req)
        {
            var result = new Result();
            var building = ContractJson.ReadBuilding(File.ReadAllText(Path.Combine(req.InputDir, "building_metadata.json")));
            var scenarioJson = File.ReadAllText(Path.Combine(req.InputDir, "scenario.json"));
            var scenario = ContractJson.ReadScenario(scenarioJson); // Validate before spending time on import and bake.
            var library = MaterialLibraryBuilder.Build();

            var work = $"{WorkRoot}/{Sanitize(req.JobId)}";
            if (AssetDatabase.IsValidFolder(work))
                AssetDatabase.DeleteAsset(work);
            Directory.CreateDirectory(work);

            // 1) Import GLB geometry (glTFast ScriptedImporter).
            var floors = building.Storeys.OrderBy(s => s.Elevation).ToList();
            // (storey, imported asset, name of the top-level node to keep when one GLB holds several storeys)
            var chunks = new List<(StoreyInfo storey, string asset, string keepNode)>();
            foreach (var s in floors)
            {
                var src = !string.IsNullOrEmpty(s.Chunk) ? Path.Combine(req.InputDir, s.Chunk) : null;
                if (src != null && File.Exists(src))
                    chunks.Add((s, CopyIn(src, work), null));
            }

            if (chunks.Count == 0)
            {
                var single = Path.Combine(req.InputDir, "building.glb");
                if (!File.Exists(single))
                    throw new FileNotFoundException("No storey chunks and no building.glb in input.", single);
                var asset = CopyIn(single, work);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset)
                             ?? throw new InvalidOperationException($"glTFast did not import {asset}");
                var split = floors.Count > 1 ? StoreyNodes(prefab.transform, floors) : null;
                if (split != null)
                {
                    foreach (var s in floors)
                        chunks.Add((s, asset, split[s]));
                }
                else
                {
                    var first = floors.FirstOrDefault() ?? new StoreyInfo { Id = "L1", Elevation = 0f };
                    chunks.Add((first, asset, null));
                    if (floors.Count > 1)
                        result.Warnings.Add("Single building.glb without one top-level node per storey: all storeys go into one scene bundle.");
                }
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            // 2) One scene per chunk with materials, colliders, doors, spaces, stairs, lights and probes.
            var scenes = new List<Scene>();
            var scenePaths = new List<(StoreyInfo storey, string path)>();
            var first_ = true;
            foreach (var (storey, asset, keepNode) in chunks)
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, first_ ? NewSceneMode.Single : NewSceneMode.Additive);
                first_ = false;
                SceneManager.SetActiveScene(scene);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(asset)
                             ?? throw new InvalidOperationException($"glTFast did not import {asset}");
                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                if (keepNode != null)
                    KeepOnly(root.transform, keepNode);
                root.name = $"Building_{storey.FloorId}";
                var br = root.AddComponent<BuildingRoot>();
                br.FloorId = storey.FloorId;
                br.Elevation = storey.Elevation;

                AssignMaterials(root, building, library, work, result);
                if (req.PrepareMeshes)
                    PrepareMeshes(root, $"{work}/Meshes_{Sanitize(storey.FloorId)}", result);
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
            var manifest = PackageManifestFactory.Create(scenario, building, req.JobId, req.Target.ToString(), Application.unityVersion,
                library.Version, req.Bake);
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
                    Id = storey.FloorId, Bundle = floorBundle, LightingBundle = lightingBundle,
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
            PackageManifestFactory.AddFiles(manifest, req.OutputDir);

            var issues = PackageManifestFactory.Validate(manifest);
            if (issues.Count > 0)
                throw new ManifestInvalidException(issues);

            result.ManifestPath = Path.Combine(req.OutputDir, ContentManifest.FileName);
            File.WriteAllText(result.ManifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented, ContractJson.Settings));
            result.Floors = manifest.Floors.Count;
            return result;
        }

        /// <summary>
        /// One GLB for the whole building: when every storey has a top-level node named after its id, name or IFC GlobalId,
        /// each storey gets its own scene bundle. Returns null when the nodes are not there.
        /// </summary>
        private static Dictionary<StoreyInfo, string> StoreyNodes(Transform prefabRoot, List<StoreyInfo> floors)
        {
            var children = new HashSet<string>(StringComparer.Ordinal);
            foreach (Transform child in prefabRoot)
                children.Add(child.name);
            var map = new Dictionary<StoreyInfo, string>();
            foreach (var s in floors)
            {
                var node = new[] { s.Id, s.Name, s.IfcGlobalId }.FirstOrDefault(n => n != null && children.Contains(n));
                if (node == null || map.ContainsValue(node))
                    return null;
                map[s] = node;
            }

            return map;
        }

        private static void KeepOnly(Transform root, string keepNode)
        {
            for (var i = root.childCount - 1; i >= 0; i--)
            {
                var child = root.GetChild(i);
                if (child.name != keepNode)
                    UnityEngine.Object.DestroyImmediate(child.gameObject);
            }
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

        /// <summary>
        /// GLB meshes from the IFC pipeline carry one UV set for textures. Baked lighting needs a second, non-overlapping
        /// set (UV2): without it the bake reuses UV1, where tiled faces overlap, and walls come out blotchy and
        /// streaked. Each distinct mesh is cloned once (imported assets are read-only), unwrapped, given tangents for
        /// the normal maps and saved next to the scene so the AssetBundle carries it.
        /// </summary>
        private static void PrepareMeshes(GameObject root, string folder, Result result)
        {
            Directory.CreateDirectory(folder);
            var clones = new Dictionary<Mesh, Mesh>();
            var failed = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                var src = mf.sharedMesh;
                if (src == null)
                    continue;
                if (!clones.TryGetValue(src, out var mesh))
                {
                    mesh = UnityEngine.Object.Instantiate(src);
                    mesh.name = src.name;
                    if (mesh.normals == null || mesh.normals.Length != mesh.vertexCount)
                        mesh.RecalculateNormals();
                    if (mesh.uv2 == null || mesh.uv2.Length == 0)
                    {
                        try
                        {
                            Unwrapping.GenerateSecondaryUVSet(mesh, new UnwrapParam
                            {
                                hardAngle = 88f, angleError = 0.08f, areaError = 0.15f, packMargin = 4f / 1024f,
                            });
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Debug.LogWarning($"[Fire3D worker] lightmap UVs failed for '{src.name}': {ex.Message}");
                        }
                    }

                    if (mesh.uv != null && mesh.uv.Length == mesh.vertexCount)
                        mesh.RecalculateTangents();
                    AssetDatabase.CreateAsset(mesh, $"{folder}/{clones.Count:D4}_{Sanitize(src.name)}.asset");
                    clones[src] = mesh;
                }

                mf.sharedMesh = mesh;
            }

            if (failed > 0)
                result.Warnings.Add($"IFC_LIGHTMAP_UV_FAILED: {failed} mesh(es) keep a single UV set; their baked lighting may look streaked.");
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

            // Stairs: IFC nodes are named by GlobalId, so the worker lists IfcStair/IfcRamp as portals of type "stair";
            // hand-made inputs may still name the node "stair".
            var stairIds = new HashSet<string>(building.Portals.Where(p => p.Type == "stair" && p.IfcGlobalId != null).Select(p => p.IfcGlobalId));
            foreach (var t in nodes.Values.Where(t => stairIds.Contains(t.name) || t.name.IndexOf("stair", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                var b = BoundsOf(t.gameObject);
                if (b.size.sqrMagnitude < 0.5f)
                    continue;
                var vol = new GameObject($"StairVolume_{t.name}").AddComponent<StairVolume>();
                vol.transform.SetParent(root.transform, true);
                vol.transform.position = new Vector3(b.center.x, b.min.y - 0.1f, b.center.z);
                vol.Size = new Vector3(b.size.x + 0.4f, b.size.y + 0.4f, b.size.z + 0.4f);
                vol.Flight = t;
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
                var bounds = ContractFrame.ToUnityBounds(new Vector3(s.Bbox[0][0], s.Bbox[0][1], s.Bbox[0][2]), new Vector3(s.Bbox[1][0], s.Bbox[1][1], s.Bbox[1][2]));
                var max = bounds.max;
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
