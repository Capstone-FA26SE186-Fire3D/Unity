using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Fire3D.Unity.Content;
using Fire3D.Unity.Editor.ContentPipeline;
using GLTFast;
using GLTFast.Export;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Fire3D.Unity.Editor.Development
{
    /// <summary>
    /// Exports the graybox building as the input package the build worker gets from the backend: one <c>building.glb</c>
    /// (top-level node per storey, IFC GlobalIds as node names, plain materials that carry only a name), plus
    /// <c>building_metadata.json</c> (<c>materials</c> mapped to <c>libraryKey</c>) and <c>scenario.json</c>.
    /// Used by Tools/PackageWorker/Test to run the whole worker loop without the IFC/Blender worker.
    /// <code>
    /// Unity.exe -batchmode -projectPath . -executeMethod Fire3D.Unity.Editor.Development.DevBuildingExporter.Export -fet3dOut &lt;dir&gt;
    /// </code>
    /// </summary>
    public static class DevBuildingExporter
    {
        [MenuItem("Fire3D/Development/Export Graybox as Worker Input")]
        public static void ExportMenu() => Run(Path.Combine(Application.dataPath, "../Builds/WorkerInput"));

        /// <summary>Batch entry. The glTFast writer awaits stream writes, so the editor loop must keep running until it finishes.</summary>
        public static void Export()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-fet3dOut");
            Run(i >= 0 && i + 1 < args.Length ? args[i + 1] : Path.Combine(Application.dataPath, "../Builds/WorkerInput"));
        }

        private static void Run(string outDir)
        {
            Task task;
            try
            {
                task = ExportAsync(outDir);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Fire3D export] FAILED: {ex}");
                if (Application.isBatchMode)
                    EditorApplication.Exit(1);
                return;
            }

            EditorApplication.update += Tick;

            void Tick()
            {
                if (!task.IsCompleted)
                    return;
                EditorApplication.update -= Tick;
                var code = 0;
                if (task.IsFaulted)
                {
                    Debug.LogError($"[Fire3D export] FAILED: {task.Exception?.GetBaseException()}");
                    code = 1;
                }

                if (Application.isBatchMode)
                    EditorApplication.Exit(code);
            }
        }

        public static async Task ExportAsync(string outDir)
        {
            Directory.CreateDirectory(outDir);
            if (!File.Exists(DevelopmentSceneBuilder.ScenePath))
                DevelopmentSceneBuilder.Build(bake: false);
            EditorSceneManager.OpenScene(DevelopmentSceneBuilder.ScenePath, OpenSceneMode.Single);

            var building = GameObject.Find("Building_L1_L2")
                           ?? throw new InvalidOperationException("Building_L1_L2 not found in the graybox scene.");
            var library = MaterialLibraryBuilder.Build();
            var materialNames = new Dictionary<string, string>(); // export material name -> libraryKey

            // Clone with plain materials so the GLB carries names only (no textures), like the IFC worker's output.
            var clone = UnityEngine.Object.Instantiate(building);
            clone.name = "Building";
            var plain = new Dictionary<string, Material>();
            try
            {
                foreach (var r in clone.GetComponentsInChildren<Renderer>(true))
                {
                    var mats = r.sharedMaterials;
                    for (var m = 0; m < mats.Length; m++)
                    {
                        if (mats[m] == null)
                            continue;
                        // Library materials export under a name the metadata maps back to a libraryKey; the rest (prototype
                        // colours such as EXIT signs) keep a plain name and stay unmapped, like unknown IFC materials.
                        var key = library.Entries.FirstOrDefault(e => e.Material == mats[m]).Key;
                        var exportName = key != null ? "FET3D_" + key.Replace('.', '_') : mats[m].name;
                        if (!plain.TryGetValue(exportName, out var p))
                        {
                            p = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = exportName };
                            p.SetColor("_BaseColor", mats[m].HasProperty("_BaseColor") ? mats[m].GetColor("_BaseColor") : Color.gray);
                            plain[exportName] = p;
                            if (key != null)
                                materialNames[exportName] = key;
                        }

                        mats[m] = p;
                    }

                    r.sharedMaterials = mats;
                }

                // Drop helpers that are not geometry (lights, probes, volumes stay out through the component mask).
                foreach (var t in clone.GetComponentsInChildren<Transform>(true).Where(t => t != clone.transform).ToList())
                {
                    if (t == null)
                        continue;
                    var n = t.name;
                    if (n.StartsWith("Space_") || n.StartsWith("StairVolume") || n.EndsWith("_Emergency") || n == "ExitGlow" || n.Contains("_Light_"))
                        UnityEngine.Object.DestroyImmediate(t.gameObject);
                }

                var export = new GameObjectExport(
                    new ExportSettings { Format = GltfFormat.Binary, ComponentMask = ComponentType.Mesh },
                    new GameObjectExportSettings { OnlyActiveInHierarchy = false },
                    deferAgent: new UninterruptedDeferAgent());
                // Children of the clone become the top-level nodes (L1, L2) the worker splits by storey.
                var storeyNodes = Enumerable.Range(0, clone.transform.childCount).Select(c => clone.transform.GetChild(c).gameObject).ToArray();
                if (!export.AddScene(storeyNodes, "Building"))
                    throw new InvalidOperationException("glTFast could not add the scene.");
                var glb = Path.Combine(outDir, "building.glb");
                if (!await export.SaveToFileAndDispose(glb))
                    throw new InvalidOperationException("glTFast export failed.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clone);
                foreach (var p in plain.Values)
                    UnityEngine.Object.DestroyImmediate(p);
            }

            var metadata = JObject.Parse(File.ReadAllText(DevelopmentSceneBuilder.BuildingPath));
            var materials = new JObject();
            foreach (var (name, key) in materialNames.OrderBy(kv => kv.Key))
                materials[name] = new JObject { ["libraryKey"] = key };
            metadata["materials"] = materials;
            File.WriteAllText(Path.Combine(outDir, "building_metadata.json"), metadata.ToString());
            File.Copy(DevelopmentSceneBuilder.ScenarioPath, Path.Combine(outDir, "scenario.json"), true);
            Debug.Log($"[Fire3D export] wrote {outDir} ({materialNames.Count} materials)");
        }
    }
}
