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
    /// <summary>Loaded package: parsed documents and the floor scenes now in memory.</summary>
    public sealed class LoadedContent
    {
        public ContentManifest Manifest;
        public BuildingMetadata Building;
        public ScenarioState Scenario;
        public readonly List<AssetBundle> Bundles = new();
        public readonly List<Scene> Scenes = new();
    }

    /// <summary>
    /// Loads a package produced by the Unity build worker: verifies file hashes against the manifest (Mobile has
    /// verified the download already; this guards against local tampering or corruption), loads the shared
    /// material library bundle shipped in StreamingAssets, then each floor scene bundle additively, and registers
    /// the baked lighting scenarios.
    /// </summary>
    public static class ContentLoader
    {
        public const string MaterialLibraryBundle = "fet3d-materials";

        private static AssetBundle _library;

        public static IEnumerator Load(string manifestPath, Action<LoadedContent> done, Action<string, string> fail)
        {
            LoadedContent content;
            try
            {
                content = new LoadedContent { Manifest = ContractJson.ReadManifest(File.ReadAllText(manifestPath)) };
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

            var switcher = UnityEngine.Object.FindAnyObjectByType<LightingScenarioSwitcher>();
            if (switcher == null)
                switcher = new GameObject("LightingScenarios").AddComponent<LightingScenarioSwitcher>();
            foreach (var floor in manifest.Floors)
            {
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
                            switcher.Register(data);
                    }
                }

                var sceneName = floor.Scene;
                if (string.IsNullOrEmpty(sceneName) && bundle.isStreamedSceneAssetBundle && bundle.GetAllScenePaths().Length > 0)
                    sceneName = Path.GetFileNameWithoutExtension(bundle.GetAllScenePaths()[0]);
                var load = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                yield return load;
                content.Scenes.Add(SceneManager.GetSceneByName(sceneName));
            }

            done(content);
        }

        public static IEnumerator Unload(LoadedContent content)
        {
            if (content == null)
                yield break;
            foreach (var scene in content.Scenes)
            {
                if (scene.isLoaded)
                    yield return SceneManager.UnloadSceneAsync(scene);
            }

            foreach (var b in content.Bundles)
                b.Unload(true);
            content.Bundles.Clear();
            content.Scenes.Clear();
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
