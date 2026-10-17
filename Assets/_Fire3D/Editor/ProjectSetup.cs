using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fire3D.Unity.Editor
{
    /// <summary>One-time player settings for the Android training runtime. Safe to re-run.</summary>
    public static class ProjectSetup
    {
        public const string ApplicationId = "com.fet3d.training";

        [MenuItem("Fire3D/Setup/Apply Project Settings")]
        public static void Apply()
        {
            PlayerSettings.companyName = "FET3D";
            PlayerSettings.productName = "FET3D Training";
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.Android.optimizedFramePacing = true;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.Android, ManagedStrippingLevel.Low);
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.Android, Il2CppCodeGeneration.OptimizeSize);
            // Lightmaps and probes are part of the content; keep baked GI working on device.
            PlayerSettings.gpuSkinning = true;
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            ApplyRendering();
            AssetDatabase.SaveAssets();
            Debug.Log("[Fire3D] Project settings applied.");
        }

        /// <summary>
        /// Image quality on device: every quality level renders with the FET3D URP asset, 4× MSAA (cheap on tile-based
        /// mobile GPUs; edges of walls, furniture and stairs were stair-stepped without it), soft shadows over a
        /// 25 m indoor distance so the 2048 shadow map stays sharp, and anisotropic filtering for floors seen at
        /// grazing angles.
        /// </summary>
        public static void ApplyRendering()
        {
            var urp = GraphicsSettings.defaultRenderPipeline;
            if (urp == null)
            {
                Debug.LogWarning("[Fire3D] No default render pipeline asset; rendering settings skipped.");
                return;
            }

            var so = new SerializedObject(urp);
            Set(so, "m_MSAA", 4);
            Set(so, "m_RenderScale", 1f);
            Set(so, "m_SoftShadowsSupported", true);
            Set(so, "m_SoftShadowQuality", 2);
            Set(so, "m_ShadowDistance", 25f);
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(urp);

            var current = QualitySettings.GetQualityLevel();
            for (var i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = urp;
                QualitySettings.anisotropicFiltering = AnisotropicFiltering.Enable;
            }

            QualitySettings.SetQualityLevel(current, false);
        }

        private static void Set(SerializedObject so, string name, object value)
        {
            var p = so.FindProperty(name);
            if (p == null)
            {
                Debug.LogWarning($"[Fire3D] URP asset has no {name}");
                return;
            }

            switch (value)
            {
                case int v: p.intValue = v; break;
                case float v: p.floatValue = v; break;
                case bool v: p.boolValue = v; break;
            }
        }
    }
}
