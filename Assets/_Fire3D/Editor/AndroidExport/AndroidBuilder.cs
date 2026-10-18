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

        /// <summary>
        /// Batch helper for test projects: the library bundle for the Editor's active target, so packages built for the same
        /// target render correctly in Editor play tests (Android shaders draw magenta in a Windows Editor).
        /// </summary>
        public static void BuildLibraryBundleForActiveTarget()
        {
            BuildLibraryBundle(EditorUserBuildSettings.activeBuildTarget);
            if (Application.isBatchMode)
                EditorApplication.Exit(0);
        }

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
            if (report.summary.result == BuildResult.Succeeded)
                PackageUnityLibrary(output);
            Finish(report, output);
        }

        /// <summary>
        /// Zips the exported <c>unityLibrary</c> module (build.gradle at the zip root, as Mobile's prepare-unity-library
        /// expects) and writes its SHA-256 next to it. Upload the zip to the S3 path pinned in Mobile's
        /// unity-artifact.lock.json ({version, s3Uri, sha256}).
        /// </summary>
        public static string PackageUnityLibrary(string exportRoot)
        {
            var module = Path.Combine(exportRoot, "unityLibrary");
            if (!File.Exists(Path.Combine(module, "build.gradle")))
            {
                Debug.LogError($"[Fire3D build] {module} has no build.gradle; nothing packaged.");
                return null;
            }
            var version = $"{Application.version}-{DateTime.UtcNow:yyyyMMddHHmm}";
            var zip = Path.GetFullPath(Path.Combine(exportRoot, "..", $"fet3d-unityLibrary-{version}.zip"));
            if (File.Exists(zip))
                File.Delete(zip);
            System.IO.Compression.ZipFile.CreateFromDirectory(module, zip, System.IO.Compression.CompressionLevel.Optimal, includeBaseDirectory: false);
            string sha;
            using (var stream = File.OpenRead(zip))
            using (var hash = System.Security.Cryptography.SHA256.Create())
                sha = string.Concat(hash.ComputeHash(stream).Select(b => b.ToString("x2")));
            File.WriteAllText(zip + ".json", new Newtonsoft.Json.Linq.JObject { ["version"] = version, ["sha256"] = sha, ["file"] = Path.GetFileName(zip) }.ToString());
            Debug.Log($"[Fire3D build] unityLibrary {version} → {zip} (sha256 {sha})");
            return zip;
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
