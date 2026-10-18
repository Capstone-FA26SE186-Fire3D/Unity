using System;
using System.Collections;
using System.IO;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Unity.Bootstrap;
using Fire3D.Unity.Development;
using Fire3D.Unity.Gameplay.Interactions;
using Fire3D.Unity.Session;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// A package built by the worker from a real IFC (Tools/GeometryWorker → BuildWorker), launched exactly like Mobile does
    /// (Bootstrap scene, Fire3DApp.Launch with manifestPath), then played by the bot to the exit. Needs FET3D_TEST_PACKAGE
    /// = path of the package manifest.json (or that path in Builds/IfcTestPackage.txt); skipped otherwise. Shots/report go to FET3D_AUTOPLAY_OUT or Builds/Autoplay/ifc-*.
    /// </summary>
    public class IfcPackageTests
    {
        [UnityTest, Timeout(900000)]
        public IEnumerator IfcPackageLaunchesLikeMobileAndTheBotEscapes()
        {
            // An Editor that is already open cannot see new environment variables, so Builds/IfcTestPackage.txt also works.
            var manifest = Environment.GetEnvironmentVariable("FET3D_TEST_PACKAGE");
            var pointer = Path.Combine(Application.dataPath, "..", "Builds", "IfcTestPackage.txt");
            if (string.IsNullOrEmpty(manifest) && File.Exists(pointer))
                manifest = File.ReadAllText(pointer).Trim();
            if (string.IsNullOrEmpty(manifest) || !File.Exists(manifest))
                Assert.Ignore("Set FET3D_TEST_PACKAGE (or Builds/IfcTestPackage.txt) to the manifest.json of a package built from an IFC.");

            yield return SceneManager.LoadSceneAsync("Bootstrap", LoadSceneMode.Single);
            yield return null;
            var app = UnityEngine.Object.FindAnyObjectByType<Fire3DApp>();
            Assert.IsNotNull(app, "Bootstrap scene has the Fire3DBridge receiver");

            app.Launch(new JObject
            {
                ["protocolVersion"] = LaunchRequest.CurrentProtocol, ["sessionId"] = Guid.NewGuid().ToString(), ["kind"] = "Training",
                ["mode"] = TrainingMode.Guided, ["runtimeVersion"] = RuntimeCatalog.RuntimeVersion, ["manifestPath"] = Path.GetFullPath(manifest),
                ["resumeFromSequence"] = 0, ["locale"] = "vi",
            }.ToString());

            TrainingSession session = null;
            var deadline = Time.realtimeSinceStartup + 180f;
            while (session == null && Time.realtimeSinceStartup < deadline)
            {
                session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
                yield return null;
            }
            Assert.IsNotNull(session, "the package loaded and a session started (see the Error bridge message in the log otherwise)");
            yield return null;

            // The player must stand on a floor, not inside walls or furniture.
            var player = session.Player.transform.position;
            var blockers = Physics.OverlapCapsule(player + Vector3.up * 0.4f, player + Vector3.up * 1.4f, 0.25f, ~0, QueryTriggerInteraction.Ignore)
                .Where(c => !c.transform.IsChildOf(session.Player.transform) && c is not CharacterController).Select(c => c.name).ToArray();
            Assert.IsEmpty(blockers, $"spawn at {player} overlaps level geometry");
            Assert.IsTrue(Physics.Raycast(player + Vector3.up * 0.5f, Vector3.down, 1.5f, ~0, QueryTriggerInteraction.Ignore), $"no floor under the spawn at {player}");
            Assert.Greater(UnityEngine.Object.FindObjectsByType<DoorView>().Length, 0, "IFC doors became swinging doors");

            var outDir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT");
            if (string.IsNullOrEmpty(outDir))
                outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "ifc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            var pilot = AutoPilot.Attach(session, outDir, quitWhenDone: false, timeoutSeconds: 400f);
            while (!pilot.Finished)
                yield return null;

            Debug.Log("[Fire3D autoplay] IFC report\n" + pilot.Report);
            var outcome = pilot.Report["outcome"];
            Assert.IsTrue((bool)outcome["reachedExit"], "bot reached the exit; notes: " + pilot.Report["notes"]);
            Assert.IsFalse((bool)outcome["collapsed"]);
            Assert.AreEqual(SessionPhase.Ended, session.Phase);

            UnityEngine.Object.Destroy(pilot.gameObject);
            UnityEngine.Object.Destroy(app.gameObject);
            yield return null;
        }
    }
}
