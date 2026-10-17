using System.Collections.Generic;
using System.IO;
using Fire3D.Unity.Content;
using UnityEditor;
using UnityEngine;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Builds URP Lit materials from the curated CC0 texture sets in Art/MaterialLibrary/Source and records them in
    /// the MaterialLibrary asset. All library assets go into the shared "fet3d-materials" AssetBundle.
    /// </summary>
    public static class MaterialLibraryBuilder
    {
        public const string Root = "Assets/_Fire3D/Art/MaterialLibrary";
        public const string SourceDir = Root + "/Source";
        public const string MaterialsDir = Root + "/Materials";
        public const string LibraryAsset = Root + "/MaterialLibrary.asset";
        public const string BundleName = ContentLoader.MaterialLibraryBundle;

        /// <summary>Repeat size in meters per key (Docs ifc_ingestion_visual_interaction.md 5.5).</summary>
        public static readonly Dictionary<string, float> Repeat = new()
        {
            ["floor.tile"] = 1.2f, ["floor.wood"] = 2f, ["floor.carpet"] = 1f, ["ceiling.tile"] = 1.2f,
            ["wall.plaster"] = 2f, ["wall.brick"] = 1.5f, ["wall.tile"] = 1f, ["concrete"] = 2f,
            ["wood"] = 1f, ["stone"] = 1.5f, ["metal.painted"] = 1f, ["metal.brushed"] = 0.5f,
        };

        [MenuItem("Fire3D/Content/Build Material Library")]
        public static MaterialLibrary Build()
        {
            Directory.CreateDirectory(MaterialsDir);
            var library = AssetDatabase.LoadAssetAtPath<MaterialLibrary>(LibraryAsset);
            if (library == null)
            {
                library = ScriptableObject.CreateInstance<MaterialLibrary>();
                AssetDatabase.CreateAsset(library, LibraryAsset);
            }

            library.Entries.Clear();
            foreach (var dir in Directory.GetDirectories(SourceDir))
            {
                var key = Path.GetFileName(dir);
                var mat = BuildMaterial(key, dir.Replace('\\', '/'));
                library.Entries.Add(new MaterialLibrary.Entry { Key = key, Material = mat, RepeatMeters = Repeat.TryGetValue(key, out var r) ? r : 1f });
            }

            library.Entries.Add(new MaterialLibrary.Entry { Key = "glass.clear", Material = Glass("glass.clear", 0.18f), RepeatMeters = 1f });
            library.Entries.Add(new MaterialLibrary.Entry { Key = "glass.frosted", Material = Glass("glass.frosted", 0.55f), RepeatMeters = 1f });
            library.Entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            EditorUtility.SetDirty(library);

            AssignBundle(Root, BundleName);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[Fire3D] Material library: {library.Entries.Count} entries.");
            return library;
        }

        private static Material BuildMaterial(string key, string dir)
        {
            var color = ConfigureTexture($"{dir}/Color.jpg", TextureImporterType.Default, true);
            var normal = ConfigureTexture($"{dir}/NormalGL.jpg", TextureImporterType.NormalMap, false);
            var rough = ConfigureTexture($"{dir}/Roughness.jpg", TextureImporterType.Default, false, readable: true);
            // Painted metal is a dielectric paint layer: ignore the metalness map so it does not render black indoors.
            var metal = key == "metal.painted" ? null : ConfigureTexture($"{dir}/Metalness.jpg", TextureImporterType.Default, false, readable: true);
            var ao = ConfigureTexture($"{dir}/AmbientOcclusion.jpg", TextureImporterType.Default, false);
            var mask = BuildMetallicSmoothness(dir, rough, metal);

            var path = $"{MaterialsDir}/{key}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }

            var tiling = 1f / (Repeat.TryGetValue(key, out var r) ? r : 1f);
            mat.SetTexture("_BaseMap", color);
            mat.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
            mat.SetColor("_BaseColor", Color.white);
            if (normal != null)
            {
                mat.SetTexture("_BumpMap", normal);
                mat.SetFloat("_BumpScale", 1f);
                mat.EnableKeyword("_NORMALMAP");
            }

            if (mask != null)
            {
                mat.SetTexture("_MetallicGlossMap", mask);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                mat.SetFloat("_Smoothness", 1f);
                mat.SetFloat("_Metallic", 1f);
            }

            if (ao != null)
            {
                mat.SetTexture("_OcclusionMap", ao);
                mat.SetFloat("_OcclusionStrength", 1f);
                mat.EnableKeyword("_OCCLUSIONMAP");
            }

            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Material Glass(string key, float alpha)
        {
            var path = $"{MaterialsDir}/{key}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(mat, path);
            }

            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 0f);
            mat.SetColor("_BaseColor", new Color(0.75f, 0.85f, 0.9f, alpha));
            mat.SetFloat("_Smoothness", 0.95f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetInt("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        private static Texture2D ConfigureTexture(string path, TextureImporterType type, bool srgb, bool readable = false)
        {
            if (!File.Exists(path))
                return null;
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            var dirty = importer.textureType != type || importer.sRGBTexture != srgb || importer.isReadable != readable || importer.maxTextureSize != 1024;
            importer.textureType = type;
            importer.sRGBTexture = srgb;
            importer.isReadable = readable;
            importer.maxTextureSize = 1024;
            importer.mipmapEnabled = true;
            importer.anisoLevel = 4;
            importer.wrapMode = TextureWrapMode.Repeat;
            var android = importer.GetPlatformTextureSettings("Android");
            if (!android.overridden || android.format != TextureImporterFormat.ASTC_6x6)
            {
                android.overridden = true;
                android.maxTextureSize = 1024;
                android.format = TextureImporterFormat.ASTC_6x6;
                importer.SetPlatformTextureSettings(android);
                dirty = true;
            }

            if (dirty)
                importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>URP metallic workflow: R = metallic, A = smoothness (1 - roughness).</summary>
        private static Texture2D BuildMetallicSmoothness(string dir, Texture2D rough, Texture2D metal)
        {
            if (rough == null)
                return null;
            var path = $"{dir}/MetallicSmoothness.png";
            var w = rough.width;
            var h = rough.height;
            var r = rough.GetPixels();
            var m = metal != null && metal.width == w && metal.height == h ? metal.GetPixels() : null;
            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var px = new Color[r.Length];
            for (var i = 0; i < r.Length; i++)
            {
                var metallic = m != null ? m[i].r : 0f;
                px[i] = new Color(metallic, metallic, metallic, 1f - r[i].r);
            }

            outTex.SetPixels(px);
            outTex.Apply();
            File.WriteAllBytes(path, outTex.EncodeToPNG());
            Object.DestroyImmediate(outTex);
            AssetDatabase.ImportAsset(path);
            return ConfigureTexture(path, TextureImporterType.Default, false);
        }

        public static void AssignBundle(string folder, string bundle)
        {
            foreach (var guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(p) || p.EndsWith(".md"))
                    continue;
                var importer = AssetImporter.GetAtPath(p);
                if (importer != null && importer.assetBundleName != bundle)
                    importer.assetBundleName = bundle;
            }
        }
    }
}
