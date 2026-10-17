using System.Collections.Generic;
using System.IO;
using Fire3D.Contracts;
using Fire3D.Unity.Content;
using Fire3D.Unity.Presentation;
using UnityEditor;
using UnityEngine;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Saves graybox catalog prefabs, player/NPC stand-ins and VFX as assets and fills the RuntimeAssets object.
    /// Replace a prefab with a library model (Tripo/curated) later without touching code.
    /// </summary>
    public static class RuntimeAssetsBuilder
    {
        public const string AssetPath = "Assets/_Fire3D/Settings/RuntimeAssets.asset";
        private const string PrefabDir = "Assets/_Fire3D/Prefabs/Catalog";
        private const string ProtoMatDir = "Assets/_Fire3D/Art/Prototype";

        private static readonly string[] Catalog =
        {
            Capabilities.AlarmCallPoint, Capabilities.AlarmDetector, Capabilities.ExtinguisherPortable, Capabilities.WaterSink,
            Capabilities.WaterBath, Capabilities.WaterShower, Capabilities.ItemTowel, Capabilities.Elevator, Capabilities.BaitBelongings,
            Capabilities.Sprinkler,
        };

        [MenuItem("Fire3D/Content/Build Runtime Assets")]
        public static RuntimeAssets Build()
        {
            Directory.CreateDirectory(PrefabDir);
            Directory.CreateDirectory(ProtoMatDir);
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath)!);

            var assets = AssetDatabase.LoadAssetAtPath<RuntimeAssets>(AssetPath);
            if (assets == null)
            {
                assets = ScriptableObject.CreateInstance<RuntimeAssets>();
                AssetDatabase.CreateAsset(assets, AssetPath);
            }

            var existing = new Dictionary<string, GameObject>();
            foreach (var p in assets.Interactables)
                if (p.Prefab != null)
                    existing[p.Capability] = p.Prefab;

            assets.Interactables.Clear();
            foreach (var cap in Catalog)
            {
                // Keep a prefab someone already replaced with a real model.
                var prefab = existing.TryGetValue(cap, out var kept) && !AssetDatabase.GetAssetPath(kept).StartsWith(PrefabDir)
                    ? kept
                    : SavePrefab(PrototypeFactory.Create(cap), $"{PrefabDir}/{cap}.prefab");
                assets.Interactables.Add(new RuntimeAssets.CapabilityPrefab { Capability = cap, Prefab = prefab });
            }

            if (assets.PlayerPrefab == null || AssetDatabase.GetAssetPath(assets.PlayerPrefab).StartsWith(PrefabDir))
                assets.PlayerPrefab = SavePrefab(PrototypeFactory.Person("PlayerVisual", new Color(0.95f, 0.45f, 0.1f)), $"{PrefabDir}/PlayerVisual.prefab");
            if (assets.NpcPrefab == null || AssetDatabase.GetAssetPath(assets.NpcPrefab).StartsWith(PrefabDir))
                assets.NpcPrefab = SavePrefab(PrototypeFactory.Person("NpcVisual", new Color(0.25f, 0.55f, 0.35f)), $"{PrefabDir}/NpcVisual.prefab");

            assets.FirePrefab = SaveVfx(true, $"{PrefabDir}/Flame.prefab");
            assets.SmokePrefab = SaveVfx(false, $"{PrefabDir}/Smoke.prefab");
            assets.Materials = AssetDatabase.LoadAssetAtPath<MaterialLibrary>(MaterialLibraryBuilder.LibraryAsset) ?? MaterialLibraryBuilder.Build();
            EditorUtility.SetDirty(assets);
            AssetDatabase.SaveAssets();
            return assets;
        }

        public static GameObject SavePrefab(GameObject instance, string path)
        {
            PersistMaterials(instance);
            var prefab = PrefabUtility.SaveAsPrefabAsset(instance, path);
            Object.DestroyImmediate(instance);
            return prefab;
        }

        /// <summary>Runtime-created materials are not assets; save each once so prefabs keep their references.</summary>
        public static void PersistMaterials(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mats = r.sharedMaterials;
                for (var i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null || AssetDatabase.Contains(m))
                        continue;
                    var path = $"{ProtoMatDir}/{m.name}.mat";
                    var saved = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (saved == null)
                    {
                        PersistTextures(m);
                        AssetDatabase.CreateAsset(m, path);
                        saved = m;
                    }

                    mats[i] = saved;
                }

                r.sharedMaterials = mats;
            }
        }

        private static void PersistTextures(Material m)
        {
            foreach (var name in m.GetTexturePropertyNames())
            {
                if (m.GetTexture(name) is Texture2D t && t != null && !AssetDatabase.Contains(t))
                {
                    var path = $"{ProtoMatDir}/{t.name}.png";
                    if (!File.Exists(path))
                    {
                        File.WriteAllBytes(path, t.EncodeToPNG());
                        AssetDatabase.ImportAsset(path);
                        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                        importer.alphaIsTransparency = true;
                        importer.wrapMode = TextureWrapMode.Clamp;
                        importer.SaveAndReimport();
                    }

                    m.SetTexture(name, AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                }
            }
        }

        private static ParticleSystem SaveVfx(bool fire, string path)
        {
            var root = new GameObject(fire ? "Flame" : "Smoke");
            var ps = VfxFactory.Create(fire, root.transform);
            ps.transform.SetParent(null);
            Object.DestroyImmediate(root);
            var go = ps.gameObject;
            PersistMaterials(go);
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab.GetComponent<ParticleSystem>();
        }
    }
}
