using System;
using System.Collections;
using System.IO;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Unity.Bootstrap;
using Fire3D.Unity.Content;
using Fire3D.Unity.Integration;
using Fire3D.Unity.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Launch lifecycle against a real worker package, driven like Mobile drives it (Launch / Abort on the bridge receiver):
    /// a launch that fails halfway or is cancelled must leave no scene, bundle or lighting behind, report the error with the
    /// session id, announce Ready, never send Launched, and the next launch must play. Needs a package built for the Editor
    /// platform (FET3D_TEST_PACKAGE or Builds/IfcTestPackage.txt, e.g. from Tools/PackageWorker/Test/Run-LocalTest.ps1);
    /// skipped otherwise.
    /// </summary>
    public class LaunchLifecycleTests
    {
        private string _manifest;
        private string _scratch;
        private Fire3DApp _app;
        private Fire3DBridge _bridge;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            _manifest = Environment.GetEnvironmentVariable("FET3D_TEST_PACKAGE");
            var pointer = Path.Combine(Application.dataPath, "..", "Builds", "IfcTestPackage.txt");
            if (string.IsNullOrEmpty(_manifest) && File.Exists(pointer))
                _manifest = File.ReadAllText(pointer).Trim();
            if (string.IsNullOrEmpty(_manifest) || !File.Exists(_manifest))
                Assert.Ignore("Set FET3D_TEST_PACKAGE (or Builds/IfcTestPackage.txt) to the manifest.json of a package built for the Editor platform.");

            // Earlier tests may leave an app with a session or a loaded package; release it before destroying the app.
            foreach (var stale in UnityEngine.Object.FindObjectsByType<Fire3DApp>())
            {
                _app = stale;
                yield return StopAnything();
                UnityEngine.Object.Destroy(stale.gameObject);
            }

            foreach (var stale in UnityEngine.Object.FindObjectsByType<TrainingSession>())
                UnityEngine.Object.Destroy(stale.gameObject);
            yield return null;
            yield return SceneManager.LoadSceneAsync("Bootstrap", LoadSceneMode.Single);
            yield return null;
            _app = UnityEngine.Object.FindAnyObjectByType<Fire3DApp>();
            Assert.IsNotNull(_app, "Bootstrap scene has the Fire3DBridge receiver");
            _bridge = (Fire3DBridge)_app.Bridge;
            _bridge.Sent.Clear();
            _scratch = Path.Combine(Path.GetTempPath(), "fet3d-lifecycle-" + Guid.NewGuid().ToString("N"));
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            WorldBuilder.FaultForTests = null;
            if (_app != null)
            {
                _app.WorldBuilt = null;
                yield return StopAnything();
            }

            if (_scratch != null && Directory.Exists(_scratch))
                Directory.Delete(_scratch, true);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator SecondFloorFailureReleasesEverythingAndRetryPlays()
        {
            var broken = CopyPackage("broken");
            var manifest = JObject.Parse(File.ReadAllText(broken));
            var floors = (JArray)manifest["floors"];
            if (floors.Count < 2)
                Assert.Ignore("The test package has a single floor; a second-floor failure needs two.");

            // Garbage in place of the second floor bundle, with its hash updated so it passes verification and fails in Unity.
            var bundle = (string)floors[1]["bundle"];
            var bundlePath = Path.Combine(Path.GetDirectoryName(broken)!, bundle);
            File.WriteAllBytes(bundlePath, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            foreach (var file in (JArray)manifest["files"])
            {
                if ((string)file["path"] != bundle)
                    continue;
                file["sha256"] = ContentLoader.Sha256(bundlePath);
                file["size"] = 8;
            }

            File.WriteAllText(broken, manifest.ToString());

            // Unity logs its own error for the unreadable bundle on each attempt.
            var unreadable = new System.Text.RegularExpressions.Regex("AssetBundle .*" + System.Text.RegularExpressions.Regex.Escape(bundle));
            LogAssert.Expect(LogType.Error, unreadable);
            var sessionId = Launch(broken);
            yield return WaitIdle(180f);
            AssertFailedCleanly(sessionId, "BUNDLE_LOAD_FAILED", broken);

            // Same package again: the first floor bundle was released, so Unity does not refuse it as already loaded.
            LogAssert.Expect(LogType.Error, unreadable);
            sessionId = Launch(broken);
            yield return WaitIdle(180f);
            AssertFailedCleanly(sessionId, "BUNDLE_LOAD_FAILED", broken);

            yield return LaunchPlaysAndExits(_manifest);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator WorldBuildFailureReleasesThePackage()
        {
            _app.WorldBuilt = _ => throw new InvalidOperationException("simulated world build failure");
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("simulated world build failure"));
            var sessionId = Launch(_manifest);
            yield return WaitIdle(180f);
            AssertFailedCleanly(sessionId, "WORLD_BUILD_FAILED", _manifest);
            Assert.IsNull(GameObject.Find("ScenarioObjects"), "the half-built world was removed");

            _app.WorldBuilt = null;
            yield return LaunchPlaysAndExits(_manifest);
        }

        /// <summary>
        /// Build throws at each of its own points (before anything exists, after the root object, after the simulation):
        /// the half-built world is released, the package unloaded, the error reported and Ready sent, and the next launch plays.
        /// </summary>
        [UnityTest, Timeout(900000)]
        public IEnumerator WorldBuildFailureInsideBuildLeavesNothing()
        {
            foreach (var point in new[] { "start", "root", "simulation" })
            {
                var at = point;
                WorldBuilder.FaultForTests = p =>
                {
                    if (p == at)
                        throw new InvalidOperationException("simulated build failure at " + at);
                };
                LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("simulated build failure at " + at));
                var sessionId = Launch(_manifest);
                yield return WaitIdle(180f);
                AssertFailedCleanly(sessionId, "WORLD_BUILD_FAILED", _manifest);
                Assert.IsNull(GameObject.Find("ScenarioObjects"), $"no half-built world after a failure at '{at}'");
            }

            WorldBuilder.FaultForTests = null;
            yield return LaunchPlaysAndExits(_manifest);
        }

        /// <summary>Back pressed at each loading step: while verifying files, while floors load, and right as loading ends.</summary>
        [UnityTest, Timeout(900000)]
        public IEnumerator BackWhileLoadingCancelsAtEveryStep()
        {
            // While the manifest/hashes are read (first frames).
            var sessionId = Launch(_manifest);
            _app.Abort(null);
            yield return WaitIdle(180f);
            AssertFailedCleanly(sessionId, ContentLoader.Cancelled, _manifest);

            // While floor bundles and scenes load: as soon as the first package bundle is in memory.
            var names = PackageBundleNames(_manifest);
            sessionId = Launch(_manifest);
            var deadline = Time.realtimeSinceStartup + 120f;
            while (_app.State == Fire3DApp.AppState.Loading && !AssetBundle.GetAllLoadedAssetBundles().Any(b => names.Contains(b.name))
                   && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.AreEqual(Fire3DApp.AppState.Loading, _app.State, "still loading when Back is pressed");
            _app.Abort(null);
            _app.Abort(null);
            yield return WaitIdle(180f);
            AssertFailedCleanly(sessionId, ContentLoader.Cancelled, _manifest);

            // Right as loading ends: Back in the frame the last scene arrives.
            sessionId = Launch(_manifest);
            var floorCount = JObject.Parse(File.ReadAllText(_manifest))["floors"]!.Count();
            deadline = Time.realtimeSinceStartup + 180f;
            while (_app.State == Fire3DApp.AppState.Loading && SceneManager.sceneCount < 1 + floorCount && Time.realtimeSinceStartup < deadline)
                yield return null;
            var launchedBeforeBack = _app.State != Fire3DApp.AppState.Loading;
            if (launchedBeforeBack)
                yield return StopAnything();
            else
            {
                _app.Abort(null);
                yield return WaitIdle(180f);
            }

            if (!launchedBeforeBack)
                AssertFailedCleanly(sessionId, ContentLoader.Cancelled, _manifest);
            else
                Assert.AreEqual(1, _bridge.Sent.Count(m => m.Type == BridgeMessages.Exited && m.SessionId == sessionId), "a running session ends once");

            yield return LaunchPlaysAndExits(_manifest);
        }

        private string Launch(string manifest)
        {
            _bridge.Sent.Clear();
            var sessionId = Guid.NewGuid().ToString();
            _app.Launch(new JObject
            {
                ["protocolVersion"] = LaunchRequest.CurrentProtocol, ["sessionId"] = sessionId, ["kind"] = "Training",
                ["mode"] = TrainingMode.Guided, ["runtimeVersion"] = RuntimeCatalog.RuntimeVersion, ["manifestPath"] = Path.GetFullPath(manifest),
                ["resumeFromSequence"] = 0, ["locale"] = "vi",
            }.ToString());
            Assert.AreEqual(Fire3DApp.AppState.Loading, _app.State, "the launch was accepted");
            return sessionId;
        }

        /// <summary>Cancels a load, or ends a session and closes its result screen, then waits for Idle.</summary>
        private IEnumerator StopAnything()
        {
            for (var i = 0; i < 3 && _app != null && _app.State != Fire3DApp.AppState.Idle; i++)
            {
                _app.Abort(null);
                yield return WaitIdle(i == 0 ? 5f : 60f);
            }
        }

        private IEnumerator WaitIdle(float seconds)
        {
            var deadline = Time.realtimeSinceStartup + seconds;
            while (_app != null && _app.State != Fire3DApp.AppState.Idle && Time.realtimeSinceStartup < deadline)
                yield return null;
            // Destroyed objects and unloaded bundles settle at the end of the frame.
            yield return null;
            yield return null;
        }

        private void AssertFailedCleanly(string sessionId, string code, string manifest)
        {
            Assert.AreEqual(Fire3DApp.AppState.Idle, _app.State);
            var error = _bridge.Sent.LastOrDefault(m => m.Type == BridgeMessages.Error);
            Assert.IsNotNull(error, "an Error was reported");
            Assert.AreEqual(sessionId, error.SessionId, "the error names the session");
            Assert.AreEqual(code, (string)error.Payload?["code"], $"error payload {error.Payload}");
            Assert.AreEqual(BridgeMessages.Ready, _bridge.Sent.Last().Type, "Ready follows the error so Mobile may retry");
            Assert.IsFalse(_bridge.Sent.Any(m => m.Type is BridgeMessages.Launched or BridgeMessages.Completed or BridgeMessages.Exited),
                "no Launched or gameplay result for a launch that never started");
            Assert.IsNull(UnityEngine.Object.FindAnyObjectByType<TrainingSession>(), "no session was created");

            var names = PackageBundleNames(manifest);
            var leftover = AssetBundle.GetAllLoadedAssetBundles().Where(b => names.Contains(b.name)).Select(b => b.name).ToArray();
            Assert.IsEmpty(leftover, "package bundles were unloaded");
            Assert.AreEqual(1, SceneManager.sceneCount, "only the Bootstrap scene is loaded: " +
                string.Join(", ", Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name)));
            var switcher = UnityEngine.Object.FindAnyObjectByType<LightingScenarioSwitcher>();
            Assert.IsTrue(switcher == null || switcher.Count == 0, "lighting registrations were removed");
            Assert.IsTrue(AssetBundle.GetAllLoadedAssetBundles().Any(b => b.name == ContentLoader.MaterialLibraryBundle) ||
                          !AssetBundle.GetAllLoadedAssetBundles().Any(), "the shared material library stays loaded");
        }

        private IEnumerator LaunchPlaysAndExits(string manifest)
        {
            var sessionId = Launch(manifest);
            var deadline = Time.realtimeSinceStartup + 180f;
            while (_app.State == Fire3DApp.AppState.Loading && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.AreEqual(Fire3DApp.AppState.Running, _app.State,
                $"the next launch plays; bridge: {string.Join(" | ", _bridge.Sent.Select(m => $"{m.Type} {m.Payload}"))}");
            Assert.AreEqual(1, _bridge.Sent.Count(m => m.Type == BridgeMessages.Launched && m.SessionId == sessionId));

            // Back during the session: Abandoned, Completed once, result screen; Back again closes it (Exited once, Ready).
            _app.Abort(null);
            yield return null;
            Assert.AreEqual(Fire3DApp.AppState.Running, _app.State, "the result screen stays until closed");
            var completed = _bridge.Sent.Where(m => m.Type == BridgeMessages.Completed && m.SessionId == sessionId).ToArray();
            Assert.AreEqual(1, completed.Length, "Back after Launched ends the session once");
            Assert.AreEqual(EndReason.Abandoned, (string)completed[0].Payload?["endReason"]);
            _app.Abort(null);
            _app.Abort(null);
            yield return WaitIdle(60f);
            Assert.AreEqual(1, _bridge.Sent.Count(m => m.Type == BridgeMessages.Completed && m.SessionId == sessionId), "no second Completed");
            Assert.AreEqual(1, _bridge.Sent.Count(m => m.Type == BridgeMessages.Exited && m.SessionId == sessionId), "Exited once");
            Assert.AreEqual(BridgeMessages.Ready, _bridge.Sent.Last().Type);
            Assert.IsEmpty(AssetBundle.GetAllLoadedAssetBundles().Where(b => PackageBundleNames(manifest).Contains(b.name)), "package released after the session");
        }

        private string CopyPackage(string name)
        {
            var source = Path.GetDirectoryName(Path.GetFullPath(_manifest))!;
            var target = Path.Combine(_scratch, name);
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
            return Path.Combine(target, Path.GetFileName(_manifest));
        }

        private static string[] PackageBundleNames(string manifest)
        {
            var floors = (JArray)JObject.Parse(File.ReadAllText(manifest))["floors"];
            return floors.SelectMany(f => new[] { (string)f["bundle"], (string)f["lightingBundle"] })
                .Where(n => !string.IsNullOrEmpty(n)).Select(n => Path.GetFileName(n)).ToArray();
        }
    }
}
