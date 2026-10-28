using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>Renderer features the content needs: decals for burn marks and wall stains.</summary>
    public static class RenderSetup
    {
        public const string RendererAsset = "Assets/_Fire3D/Settings/FET3D_URP_Renderer.asset";

        [MenuItem("Fire3D/Content/Enable Decals")]
        public static void EnsureDecals()
        {
            var data = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererAsset);
            if (data == null)
            {
                Debug.LogWarning($"[Fire3D] Renderer not found: {RendererAsset}");
                return;
            }

            if (data.rendererFeatures.Any(f => f is DecalRendererFeature))
                return;

            var feature = ScriptableObject.CreateInstance<DecalRendererFeature>();
            feature.name = "Decals";
            AssetDatabase.AddObjectToAsset(feature, data);
            AssetDatabase.SaveAssets();
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);

            var so = new SerializedObject(data);
            var list = so.FindProperty("m_RendererFeatures");
            list.arraySize++;
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
            var map = so.FindProperty("m_RendererFeatureMap");
            map.arraySize++;
            map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();
            Debug.Log("[Fire3D] Decal renderer feature added");
        }
    }
}
