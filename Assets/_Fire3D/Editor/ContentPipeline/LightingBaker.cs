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
            public float TexelsPerUnit = 12f;
            public int MaxLightmapSize = 1024;
            public bool UseGpu;
            public float ProbeSpacing = 2f;
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
            settings.aoMaxDistance = 0.6f;
            settings.lightmapCompression = LightmapCompression.NormalQuality;
            settings.mixedBakeMode = MixedLightingMode.Subtractive;
            settings.directSampleCount = 32;
            settings.indirectSampleCount = 256;
            settings.environmentSampleCount = 128;
            settings.maxBounces = 3;
            settings.filteringMode = LightingSettings.FilterMode.Auto;
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
                        if (r.GetComponentInParent<IgnoreForGrid>() != null || r.GetComponentInParent<Gameplay.Interactions.DoorView>() != null
                            && r.transform != r.GetComponentInParent<Gameplay.Interactions.DoorView>().transform)
                            continue; // Door leaves move; they use probes.
                        GameObjectUtility.SetStaticEditorFlags(r.gameObject,
                            StaticEditorFlags.ContributeGI | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic
                            | StaticEditorFlags.ReflectionProbeStatic | StaticEditorFlags.BatchingStatic);
                        r.receiveGI = ReceiveGI.Lightmaps;
                        if (!r.TryGetComponent<BakedRendererId>(out var bid))
                            bid = r.gameObject.AddComponent<BakedRendererId>();
                        bid.Id = id++;
                    }
                }
            }
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
                RenderSettings.ambientLight = new Color(0.03f, 0.03f, 0.035f);
            }

            PrepareStatic(scenes);
            var paths = scenes.Select(s => s.path).ToArray();
            foreach (var s in scenes)
                EditorSceneManager.SaveScene(s);

            // 1) Outage set → captured.
            SetLightsForScenario(scenes, LightingScenarioSwitcher.Outage);
            Lightmapping.BakeMultipleScenes(paths);
            var outage = new Dictionary<string, LightingScenarioData>();
            foreach (var s in scenes)
                outage[s.path] = Capture(s, LightingScenarioSwitcher.Outage, outputFolder);

            // 2) Normal set → stays in the scenes' own lighting data.
            SetLightsForScenario(scenes, LightingScenarioSwitcher.Normal);
            Lightmapping.BakeMultipleScenes(paths);
            foreach (var s in scenes)
                EditorSceneManager.SaveScene(s);
            AssetDatabase.SaveAssets();
            return outage;
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
