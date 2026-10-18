using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fire3D.Unity.Content;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Bakes the two lighting sets of a building (Docs 5.6): first the power-outage set (emergency lights and EXIT
    /// signs only), captured into <see cref="LightingScenarioData"/> assets, then the normal set which stays in the
    /// scenes. Light probes are required so the player and NPCs pick up baked light; reflection probes per space.
    /// </summary>
    public static class LightingBaker
    {
        public sealed class Options
        {
            public float TexelsPerUnit = 16f;
            public int MaxLightmapSize = 1024;
            public bool UseGpu;
            public float ProbeSpacing = 2f;

            // Sky/ground ambient (flat colour per scenario; the runtime applies it with the lightmaps).
            public Color NormalAmbient = new(0.24f, 0.26f, 0.30f);
            public Color OutageAmbient = new(0.035f, 0.035f, 0.045f);
        }

        public static LightingSettings EnsureLightingSettings(string folder, Options options)
        {
            Directory.CreateDirectory(folder);
            var path = $"{folder}/LightingSettings.lighting";
            var settings = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (settings == null)
            {
                settings = new LightingSettings { name = "FET3D Lighting" };
                AssetDatabase.CreateAsset(settings, path);
            }

            settings.bakedGI = true;
            settings.realtimeGI = false;
            settings.lightmapper = options.UseGpu ? LightingSettings.Lightmapper.ProgressiveGPU : LightingSettings.Lightmapper.ProgressiveCPU;
            settings.lightmapResolution = options.TexelsPerUnit;
            settings.lightmapMaxSize = options.MaxLightmapSize;
            settings.lightmapPadding = 4;
            settings.directionalityMode = LightmapsMode.CombinedDirectional;
            settings.ao = true;
            settings.aoMaxDistance = 1.2f;
            settings.aoExponentIndirect = 1f;
            settings.aoExponentDirect = 0.35f;
            settings.lightmapCompression = LightmapCompression.NormalQuality;
            settings.mixedBakeMode = MixedLightingMode.Subtractive;
            settings.directSampleCount = 64;
            settings.indirectSampleCount = 512;
            settings.environmentSampleCount = 256;
            settings.maxBounces = 4;
            settings.lightmapMaxSize = Mathf.Max(options.MaxLightmapSize, 2048);
            // Denoised bake: smooth gradients instead of grainy or blotchy lightmaps (the main reason baked rooms look hard).
            settings.filteringMode = LightingSettings.FilterMode.Advanced;
            settings.denoiserTypeDirect = LightingSettings.DenoiserType.OpenImage;
            settings.denoiserTypeIndirect = LightingSettings.DenoiserType.OpenImage;
            settings.denoiserTypeAO = LightingSettings.DenoiserType.OpenImage;
            settings.filterTypeDirect = LightingSettings.FilterType.None;
            settings.filterTypeIndirect = LightingSettings.FilterType.None;
            settings.filterTypeAO = LightingSettings.FilterType.None;
            EditorUtility.SetDirty(settings);
            return settings;
        }

        /// <summary>Marks building renderers static for GI and gives each a stable id for scenario swapping.</summary>
        public static void PrepareStatic(IEnumerable<Scene> scenes)
        {
            var id = 1;
            foreach (var scene in scenes)
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        // Small props use probes. Door leaves are baked too: they are closed most of the time and keep
                        // their baked shading when they swing.
                        if (r.GetComponentInParent<IgnoreForGrid>() != null)
                            continue;
                        // Moving parts (door leaf, handles) keep baked shading but must not be static-batched: batched
                        // meshes never move, so the leaf stayed visibly shut while its collider swung open. They must
                        // not occlude either, or an open door would still hide the room behind it.
                        var moving = IsMovingPart(r.transform);
                        GameObjectUtility.SetStaticEditorFlags(r.gameObject, moving
                            ? StaticEditorFlags.ContributeGI | StaticEditorFlags.ReflectionProbeStatic
                            : StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic
                              | StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.BatchingStatic);
                        r.receiveGI = ReceiveGI.Lightmaps;
                        if (!r.TryGetComponent<BakedRendererId>(out var bid))
                            bid = r.gameObject.AddComponent<BakedRendererId>();
                        bid.Id = id++;
                    }
                }
            }
        }

        /// <summary>True under a door's swinging leaf (anything below the DoorView's hinge/leaf transform).</summary>
        private static bool IsMovingPart(Transform t)
        {
            var door = t.GetComponentInParent<Fire3D.Unity.Gameplay.Interactions.DoorView>();
            if (door == null)
                return false;
            var leaf = new SerializedObject(door).FindProperty("leaf")?.objectReferenceValue as Transform;
            return leaf != null && (t == leaf || t.IsChildOf(leaf));
        }

        public static void SetLightsForScenario(IEnumerable<Scene> scenes, string scenario)
        {
            foreach (var scene in scenes)
            foreach (var root in scene.GetRootGameObjects())
            foreach (var m in root.GetComponentsInChildren<LightGroupMember>(true))
            {
                var on = scenario == LightingScenarioSwitcher.Outage ? m.Group != LightGroup.Normal : m.Group != LightGroup.Emergency;
                if (m.TryGetComponent<Light>(out var light))
                {
                    light.lightmapBakeType = LightmapBakeType.Baked;
                    light.enabled = on;
                }
                else
                {
                    m.gameObject.SetActive(on);
                }
            }
        }

        /// <summary>Light probes in a grid over every space (or the building bounds) at knee and head height.</summary>
        public static void PlaceProbes(Scene scene, Options options)
        {
            var spaces = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<SpaceVolume>(true)).ToList();
            var go = new GameObject("LightProbes");
            SceneManager.MoveGameObjectToScene(go, scene);
            var group = go.AddComponent<LightProbeGroup>();
            var positions = new List<Vector3>();
            foreach (var s in spaces)
            {
                var b = s.Bounds;
                for (var x = b.min.x + 0.4f; x <= b.max.x - 0.4f + 1e-3f; x += options.ProbeSpacing)
                for (var z = b.min.z + 0.4f; z <= b.max.z - 0.4f + 1e-3f; z += options.ProbeSpacing)
                {
                    positions.Add(new Vector3(x, b.min.y + 0.4f, z));
                    positions.Add(new Vector3(x, b.min.y + 1.7f, z));
                }

                var probe = new GameObject($"Reflection_{s.Category}_{s.GlobalId}");
                SceneManager.MoveGameObjectToScene(probe, scene);
                probe.transform.position = new Vector3(b.center.x, b.min.y + 1.5f, b.center.z);
                var rp = probe.AddComponent<ReflectionProbe>();
                rp.mode = UnityEngine.Rendering.ReflectionProbeMode.Baked;
                rp.size = b.size;
                rp.center = new Vector3(0f, b.center.y - probe.transform.position.y, 0f);
                rp.boxProjection = true;
                rp.resolution = 128;
                rp.hdr = true;
            }

            // Outside ring (exits, assembly area): without it, characters that leave the building take their light
            // from the nearest indoor probes and turn black against the daylight.
            if (spaces.Count > 0)
            {
                var all = spaces[0].Bounds;
                foreach (var s in spaces)
                    all.Encapsulate(s.Bounds);
                const float margin = 6f;
                var ground = all.min.y;
                for (var x = all.min.x - margin; x <= all.max.x + margin + 1e-3f; x += options.ProbeSpacing * 1.5f)
                for (var z = all.min.z - margin; z <= all.max.z + margin + 1e-3f; z += options.ProbeSpacing * 1.5f)
                {
                    if (x > all.min.x - 0.6f && x < all.max.x + 0.6f && z > all.min.z - 0.6f && z < all.max.z + 0.6f)
                        continue;
                    positions.Add(new Vector3(x, ground + 0.4f, z));
                    positions.Add(new Vector3(x, ground + 1.7f, z));
                }
            }

            group.probePositions = positions.ToArray();
        }

        /// <summary>
        /// Full two-scenario bake of the open scenes. Writes outage scenario data under <paramref name="outputFolder"/>
        /// and returns it per scene path.
        /// </summary>
        public static Dictionary<string, LightingScenarioData> BakeBuilding(IReadOnlyList<Scene> scenes, string outputFolder, Options options)
        {
            options ??= new Options();
            var settings = EnsureLightingSettings(outputFolder, options);
            foreach (var s in scenes)
            {
                SceneManager.SetActiveScene(s);
                Lightmapping.lightingSettings = settings;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            }

            PrepareStatic(scenes);
            var paths = scenes.Select(s => s.path).ToArray();

            // BakeMultipleScenes re-opens the scenes from disk: save every change first and re-acquire after.
            // 1) Outage set → captured into scenario assets.
            SetLightsForScenario(scenes, LightingScenarioSwitcher.Outage);
            SetAmbient(options.OutageAmbient);
            SaveAll(scenes);
            Lightmapping.BakeMultipleScenes(paths);
            var reopened = Reacquire(paths);
            // Keep asset paths: the next bake reloads scenes and unloads unreferenced assets.
            var outagePaths = new Dictionary<string, string>();
            foreach (var s in reopened)
                outagePaths[s.path] = AssetDatabase.GetAssetPath(Capture(s, LightingScenarioSwitcher.Outage, outputFolder));

            // 2) Normal set → stays in the scenes' own lighting data.
            SetLightsForScenario(reopened, LightingScenarioSwitcher.Normal);
            SetAmbient(options.NormalAmbient);
            SaveAll(reopened);
            Lightmapping.BakeMultipleScenes(paths);
            SaveAll(Reacquire(paths));
            AssetDatabase.SaveAssets();
            var outage = new Dictionary<string, LightingScenarioData>();
            foreach (var kv in outagePaths)
                outage[kv.Key] = AssetDatabase.LoadAssetAtPath<LightingScenarioData>(kv.Value);
            return outage;
        }

        private static void SetAmbient(Color c)
        {
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = c;
        }

        private static void SaveAll(IEnumerable<Scene> scenes)
        {
            foreach (var s in scenes)
            {
                EditorSceneManager.MarkSceneDirty(s);
                EditorSceneManager.SaveScene(s);
            }
        }

        /// <summary>Scene handles become invalid after a multi-scene bake; look them up again by path.</summary>
        public static List<Scene> Reacquire(IEnumerable<string> paths)
        {
            var list = new List<Scene>();
            var first = true;
            foreach (var p in paths)
            {
                var s = SceneManager.GetSceneByPath(p);
                if (!s.IsValid() || !s.isLoaded)
                    s = EditorSceneManager.OpenScene(p, first ? OpenSceneMode.Single : OpenSceneMode.Additive);
                list.Add(s);
                first = false;
            }

            return list;
        }

        private static LightingScenarioData Capture(Scene scene, string scenario, string outputFolder)
        {
            var folder = $"{outputFolder}/{scenario}_{Path.GetFileNameWithoutExtension(scene.path)}";
            if (AssetDatabase.IsValidFolder(folder))
                AssetDatabase.DeleteAsset(folder);
            Directory.CreateDirectory(folder);
            AssetDatabase.Refresh();

            var renderers = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<BakedRendererId>(true)).ToList();
            var used = new SortedSet<int>();
            foreach (var id in renderers)
                if (id.TryGetComponent<Renderer>(out var r) && r.lightmapIndex >= 0 && r.lightmapIndex < 0xFFFE)
                    used.Add(r.lightmapIndex);

            var remap = new Dictionary<int, int>();
            var color = new List<Texture2D>();
            var dir = new List<Texture2D>();
            foreach (var global in used)
            {
                var lm = LightmapSettings.lightmaps[global];
                remap[global] = color.Count;
                color.Add(CopyTexture(lm.lightmapColor, $"{folder}/Lightmap-{color.Count}_color"));
                dir.Add(lm.lightmapDir != null ? CopyTexture(lm.lightmapDir, $"{folder}/Lightmap-{dir.Count}_dir") : null);
            }

            var data = ScriptableObject.CreateInstance<LightingScenarioData>();
            data.Scenario = scenario;
            data.FloorId = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<BuildingRoot>(true)).FirstOrDefault()?.FloorId;
            data.LightmapColor = color.ToArray();
            data.LightmapDirection = dir.ToArray();
            data.Renderers = renderers.Where(r => r.TryGetComponent<Renderer>(out _)).Select(r =>
            {
                var rend = r.GetComponent<Renderer>();
                return new LightingScenarioData.RendererInfo
                {
                    BakedId = r.Id,
                    LightmapIndex = remap.TryGetValue(rend.lightmapIndex, out var local) ? local : -1,
                    ScaleOffset = rend.lightmapScaleOffset,
                };
            }).ToArray();
            data.AmbientColor = RenderSettings.ambientLight;
            AssetDatabase.CreateAsset(data, $"{folder}/{scenario}.asset");
            if (LightmapSettings.lightProbes != null)
            {
                var probes = Object.Instantiate(LightmapSettings.lightProbes);
                probes.name = $"{scenario}Probes";
                AssetDatabase.AddObjectToAsset(probes, data);
                data.Probes = probes;
            }

            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            return data;
        }

        private static Texture2D CopyTexture(Texture2D source, string pathWithoutExt)
        {
            if (source == null)
                return null;
            var src = AssetDatabase.GetAssetPath(source);
            var ext = Path.GetExtension(src);
            var dst = pathWithoutExt + ext;
            AssetDatabase.CopyAsset(src, dst);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(dst);
        }
    }
}
