using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Fire3D.Contracts;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Fire3D.Unity.Content
{
    /// <summary>
    /// Loaded package: parsed documents and everything the launch put in memory. Filled while loading, so a launch that
    /// fails or is cancelled halfway can still release exactly what it took.
    /// </summary>
    public sealed class LoadedContent
    {
        public ContentManifest Manifest;
        public BuildingMetadata Building;
        public ScenarioState Scenario;
        public readonly List<AssetBundle> Bundles = new();
        public readonly List<Scene> Scenes = new();
        public readonly List<LightingScenarioData> Lighting = new();
    }

    /// <summary>
    /// Loads a package produced by the Unity build worker: verifies file hashes against the manifest (Mobile has
    /// verified the download already; this guards against local tampering or corruption), loads the shared
    /// material library bundle shipped in StreamingAssets, then each floor scene bundle additively, and registers
    /// the baked lighting scenarios. On failure or cancellation it releases what this launch loaded before reporting,
    /// so the next launch starts clean; the shared material library stays loaded for the app's lifetime.
    /// </summary>
    public static class ContentLoader
    {
        public const string MaterialLibraryBundle = "fet3d-materials";
        public const string Cancelled = "LAUNCH_CANCELLED";

        private static AssetBundle _library;

        /// <param name="content">Receives each loaded piece as soon as Unity hands it over.</param>
        /// <param name="cancelled">Polled between steps. A Unity load that is already running cannot be stopped, so it
        /// finishes first and is then released.</param>
        public static IEnumerator Load(string manifestPath, LoadedContent content, Action<string, string> fail, Func<bool> cancelled = null)
        {
            string errorCode = null, errorMessage = null;
            yield return LoadSteps(manifestPath, content, cancelled ?? (() => false), (code, message) =>
            {
                errorCode = code;
                errorMessage = message;
            });
            if (errorCode == null)
                yield break;

            yield return Unload(content);
            fail(errorCode, errorMessage);
        }

        private static IEnumerator LoadSteps(string manifestPath, LoadedContent content, Func<bool> cancelled, Action<string, string> fail)
        {
            if (cancelled())
            {
                fail(Cancelled, "Launch cancelled before the package was read.");
                yield break;
            }

            try
            {
                content.Manifest = ContractJson.ReadManifest(File.ReadAllText(manifestPath));
            }
            catch (Exception ex)
            {
                fail(ex is ContractException ce ? ce.Code : "MANIFEST_UNREADABLE", ex.Message);
                yield break;
            }

            var root = Path.GetDirectoryName(manifestPath) ?? ".";
            var manifest = content.Manifest;
            // The package names the oldest runtime that can play it (minRuntimeVersion, fet3d.package/1); older packages only
            // carry runtimeVersion. Same major, package not newer than the app.
            var required = manifest.MinRuntimeVersion ?? manifest.RuntimeVersion;
            if (!RuntimeCatalog.IsCompatible(required))
            {
                fail("RUNTIME_INCOMPATIBLE", $"Package needs runtime {required}; app runtime is {RuntimeCatalog.RuntimeVersion}.");
                yield break;
            }

            foreach (var file in manifest.Files)
            {
                var path = Path.Combine(root, file.Path);
                if (!File.Exists(path))
                {
                    fail("PACKAGE_FILE_MISSING", file.Path);
                    yield break;
                }

                if (!string.Equals(Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    fail("PACKAGE_HASH_MISMATCH", file.Path);
                    yield break;
                }

                yield return null;
                if (cancelled())
                {
                    fail(Cancelled, "Launch cancelled while verifying the package.");
                    yield break;
                }
            }

            try
            {
                content.Building = ContractJson.ReadBuilding(File.ReadAllText(Path.Combine(root, manifest.Metadata)));
                content.Scenario = ContractJson.ReadScenario(File.ReadAllText(Path.Combine(root, manifest.Scenario)));
            }
            catch (Exception ex)
            {
                fail(ex is ContractException ce ? ce.Code : "CONTENT_UNREADABLE", ex.Message);
                yield break;
            }

            if (_library == null)
            {
                var libraryRequest = AssetBundle.LoadFromFileAsync(Path.Combine(Application.streamingAssetsPath, MaterialLibraryBundle));
                yield return libraryRequest;
                _library = libraryRequest.assetBundle;
                if (_library == null)
                {
                    fail("MATERIAL_LIBRARY_MISSING", "Shared material library bundle not found in the app.");
                    yield break;
                }
            }

            var switcher = Switcher(true);
            foreach (var floor in manifest.Floors)
            {
                if (cancelled())
                {
                    fail(Cancelled, "Launch cancelled while loading floors.");
                    yield break;
                }

                var request = AssetBundle.LoadFromFileAsync(Path.Combine(root, floor.Bundle));
                yield return request;
                var bundle = request.assetBundle;
                if (bundle == null)
                {
                    fail("BUNDLE_LOAD_FAILED", floor.Bundle);
                    yield break;
                }

                content.Bundles.Add(bundle);
                if (!string.IsNullOrEmpty(floor.LightingBundle))
                {
                    var lightingRequest = AssetBundle.LoadFromFileAsync(Path.Combine(root, floor.LightingBundle));
                    yield return lightingRequest;
                    if (lightingRequest.assetBundle != null)
                    {
                        content.Bundles.Add(lightingRequest.assetBundle);
                        foreach (var data in lightingRequest.assetBundle.LoadAllAssets<LightingScenarioData>())
                        {
                            switcher.Register(data);
                            content.Lighting.Add(data);
                        }
                    }
                }

                if (cancelled())
                {
                    fail(Cancelled, "Launch cancelled while loading floors.");
                    yield break;
                }

                var sceneName = floor.Scene;
                if (string.IsNullOrEmpty(sceneName) && bundle.isStreamedSceneAssetBundle && bundle.GetAllScenePaths().Length > 0)
                    sceneName = Path.GetFileNameWithoutExtension(bundle.GetAllScenePaths()[0]);
                AsyncOperation load = null;
                if (!string.IsNullOrEmpty(sceneName))
                {
                    try
                    {
                        load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogException(ex);
                    }
                }

                if (load == null)
                {
                    fail("SCENE_LOAD_FAILED", $"{floor.Bundle}: scene '{sceneName}' not found in the bundle.");
                    yield break;
                }

                yield return load;
                var scene = SceneManager.GetSceneByName(sceneName);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    fail("SCENE_LOAD_FAILED", $"{floor.Bundle}: scene '{sceneName}' did not load.");
                    yield break;
                }

                content.Scenes.Add(scene);
            }

            if (cancelled())
                fail(Cancelled, "Launch cancelled after the package loaded.");
        }

        /// <summary>Releases what one launch loaded: its scenes, bundles and lighting registrations. Safe to call twice.</summary>
        public static IEnumerator Unload(LoadedContent content)
        {
            if (content == null)
                yield break;
            var switcher = Switcher(false);
            if (switcher != null)
            {
                foreach (var data in content.Lighting)
                    switcher.Unregister(data);
            }

            content.Lighting.Clear();
            foreach (var scene in content.Scenes.ToArray())
            {
                if (scene.IsValid() && scene.isLoaded)
                    yield return SceneManager.UnloadSceneAsync(scene);
            }

            content.Scenes.Clear();
            foreach (var b in content.Bundles)
            {
                if (b != null)
                    b.Unload(true);
            }

            content.Bundles.Clear();
        }

        private static LightingScenarioSwitcher Switcher(bool create)
        {
            var switcher = UnityEngine.Object.FindAnyObjectByType<LightingScenarioSwitcher>();
            if (switcher == null && create)
                switcher = new GameObject("LightingScenarios").AddComponent<LightingScenarioSwitcher>();
            return switcher;
        }

        public static string Sha256(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            var hash = sha.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }
}
