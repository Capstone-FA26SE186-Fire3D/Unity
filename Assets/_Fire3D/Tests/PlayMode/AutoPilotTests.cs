using System;
using System.Collections;
using System.IO;
using Fire3D.Contracts;
using Fire3D.Unity.Bootstrap;
using Fire3D.Unity.Development;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Plays the whole dev scenario in real time with <see cref="AutoPilot"/> (≈1 minute). The report, frames.csv
    /// and screenshots go to FET3D_AUTOPLAY_OUT or Builds/Autoplay/playmode-*.
    /// </summary>
    public class AutoPilotTests
    {
        [UnityTest, Timeout(600000)]
        public IEnumerator BotFollowsTheProcedureAndEscapes()
        {
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            Assert.IsNotNull(session, "DevLauncher should start a session");

            var outDir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT");
            if (string.IsNullOrEmpty(outDir))
                outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "playmode-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            var pilot = AutoPilot.Attach(session, outDir, quitWhenDone: false, timeoutSeconds: 400f);
            while (!pilot.Finished)
                yield return null;

            Debug.Log("[Fire3D autoplay] report\n" + pilot.Report);
            var outcome = pilot.Report["outcome"];
            Assert.IsTrue((bool)outcome["reachedExit"], "bot reached an open exit; notes: " + pilot.Report["notes"]);
            Assert.IsFalse((bool)outcome["collapsed"]);
            foreach (var step in pilot.Report["steps"])
                Assert.IsTrue((bool)step["ok"], $"step {step["name"]} failed; notes: {pilot.Report["notes"]}");
            Assert.GreaterOrEqual(session.Log.Count(EventTypes.AlarmRaised), 1);
            Assert.GreaterOrEqual(session.Log.Count(EventTypes.ItemWetted), 1);
            Assert.AreEqual(SessionPhase.Ended, session.Phase);
            Assert.IsTrue(File.Exists(Path.Combine(outDir, "report.json")));

            UnityEngine.Object.Destroy(pilot.gameObject);
            UnityEngine.Object.Destroy(session.gameObject);
            var app = UnityEngine.Object.FindAnyObjectByType<Fire3DApp>();
            if (app != null)
                UnityEngine.Object.Destroy(app.gameObject);
            yield return null;
        }
    }
}
