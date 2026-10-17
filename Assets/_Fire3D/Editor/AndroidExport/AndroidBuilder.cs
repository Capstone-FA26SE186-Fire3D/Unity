using System;
using System.IO;
using System.Linq;
using Fire3D.Unity.Content;
using Fire3D.Unity.Editor.ContentPipeline;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Fire3D.Unity.Editor.AndroidExport
{
    /// <summary>
    /// Android outputs:
    /// - Demo APK with the Bootstrap + Development scenes for device testing without Mobile.
    /// - Gradle export (Unity as a Library) that the React Native/Expo native module embeds.
    /// Both ship the shared material library bundle in StreamingAssets.
    /// Batch: -executeMethod Fire3D.Unity.Editor.AndroidExport.AndroidBuilder.BuildDemoApk [-fet3dOut &lt;path&gt;]
    /// </summary>
    public static class AndroidBuilder
    {
        public const string StreamingDir = "Assets/StreamingAssets";

        [MenuItem("Fire3D/Build/Material Library Bundle (Android)")]
        public static void BuildLibraryBundle() => BuildLibraryBundle(BuildTarget.Android);

        public static void BuildLibraryBundle(BuildTarget target)
        {
            MaterialLibraryBuilder.Build();
            var tmp = Path.Combine(Path.GetTempPath(), "fet3d_library_" + target);
            Directory.CreateDirectory(tmp);
            var assets = AssetDatabase.FindAssets("", new[] { MaterialLibraryBuilder.Root })
                .Select(AssetDatabase.GUIDToAssetPath).Where(p => !AssetDatabase.IsValidFolder(p) && !p.EndsWith(".md")).Distinct().ToArray();
            var builds = new[] { new AssetBundleBuild { assetBundleName = ContentLoader.MaterialLibraryBundle, assetNames = assets } };
            if (BuildPipeline.BuildAssetBundles(tmp, builds, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, target) == null)
                throw new InvalidOperationException("Material library bundle build failed.");
            Directory.CreateDirectory(StreamingDir);
            File.Copy(Path.Combine(tmp, ContentLoader.MaterialLibraryBundle), Path.Combine(StreamingDir, ContentLoader.MaterialLibraryBundle), true);
            AssetDatabase.Refresh();
        }

        [MenuItem("Fire3D/Build/Demo APK")]
        public static void BuildDemoApk()
        {
            var output = Arg("-fet3dOut", "Builds/Android/FET3D-demo.apk");
            Prepare();
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            EditorUserBuildSettings.buildAppBundle = false;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Development.DevelopmentSceneBuilder.ScenePath },
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            Finish(report, output);
        }

        [MenuItem("Fire3D/Build/Export Unity Library for Mobile")]
        public static void ExportForMobile()
        {
            var output = Arg("-fet3dOut", "Builds/AndroidLibrary");
            Prepare();
            EditorUserBuildSettings.exportAsGoogleAndroidProject = true;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { Development.DevelopmentSceneBuilder.BootstrapPath },
                locationPathName = output,
                target = BuildTarget.Android,
                options = BuildOptions.None,
            });
            EditorUserBuildSettings.exportAsGoogleAndroidProject = false;
            Finish(report, output);
        }

        private static void Prepare()
        {
            ProjectSetup.Apply();
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);
            BuildLibraryBundle(BuildTarget.Android);
        }

        private static void Finish(BuildReport report, string output)
        {
            var ok = report.summary.result == BuildResult.Succeeded;
            Debug.Log($"[Fire3D build] {report.summary.result} → {output} ({report.summary.totalSize / (1024 * 1024)} MB, {report.summary.totalTime})");
            if (Application.isBatchMode)
                EditorApplication.Exit(ok ? 0 : 1);
        }

        private static string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
    }
}
