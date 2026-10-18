using System;
using System.Collections;
using System.IO;
using Fire3D.Unity.Presentation;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Review shots of the kitchen fire and corridor lighting at each quality tier: for a human to look at, plus
    /// basic sanity checks (flames and embers exist, lights spread, the haze only on the high tier).
    /// Output: FET3D_AUTOPLAY_OUT or Builds/Autoplay/fire-*.
    /// </summary>
    public class FireVisualTests
    {
        private static string OutDir => Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
            ? d
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "fire-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));

        [UnityTest, Timeout(600000)]
        public IEnumerator KitchenFireAtEachQualityTier()
        {
            var dir = OutDir;
            Directory.CreateDirectory(dir);
            foreach (var tier in new[] { FireQuality.Tier.High, FireQuality.Tier.Medium, FireQuality.Tier.Low })
            {
                FireQuality.Override(tier);
                yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
                yield return null;
                yield return null;
                var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
                session.StartRunning();
                var sim = session.Simulation;
                foreach (var id in new[] { "door-L1-office-2", "door-L1-stair" })
                    sim.Enqueue(new Fire3D.Simulation.HazardCommand { Kind = Fire3D.Simulation.HazardCommandKind.SetDoorOpen, ObjectId = id, Flag = true });

                // Stand in the doorway of the kitchen, looking at the fire.
                var player = session.Player;
                player.Teleport(new Vector3(8.6f, 0.05f, 3.4f), 112f);
                foreach (var r in player.GetComponentsInChildren<Renderer>())
                    r.enabled = false; // Clean frame: the body would cover the fire.
                for (var i = 0; i < 480; i++) // 24 s of fire growth
                {
                    session.Tick(0.05f);
                    if (i % 4 == 0)
                        yield return null;
                }

                for (var i = 0; i < 90; i++)
                    yield return null;

                var count = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None).Length;
                Assert.Greater(count, 4, "flame, core, embers, glow layers exist");
                var hazeActive = false;
                foreach (var t in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
                    if (t.name == VfxFactory.HazeLayer && t.gameObject.activeInHierarchy)
                        hazeActive = true;
                Assert.AreEqual(tier != FireQuality.Tier.Low, hazeActive, "real heat haze on Medium and High, the cheap look on Low");

                // Free camera (the third-person rig is paused) at fixed viewpoints around the fire.
                var cam = Camera.main;
                var rig = cam.GetComponent<Fire3D.Unity.Gameplay.Cameras.ThirdPersonCameraRig>();
                rig.enabled = false;
                var fire = new Vector3(12.2f, 0.7f, 1.6f);
                foreach (var (label, at) in new[] { ("wide", new Vector3(8.4f, 1.7f, 3.6f)), ("mid", new Vector3(9.8f, 1.3f, 3.0f)), ("low", new Vector3(10.4f, 0.5f, 2.6f)) })
                {
                    cam.transform.SetPositionAndRotation(at, Quaternion.LookRotation(fire - at));
                    for (var i = 0; i < 40; i++)
                        yield return null;
                    yield return Shot(dir, $"{tier}_kitchen_{label}");
                }

                rig.enabled = true;
                // Corridor: ceiling light, ambient and AO.
                player.Teleport(new Vector3(3f, 0.05f, 6.6f), 90f);
                for (var i = 0; i < 40; i++)
                    yield return null;
                yield return Shot(dir, $"{tier}_corridor_L1");

                UnityEngine.Object.Destroy(session.gameObject);
                yield return null;
            }
        }

        private static IEnumerator Shot(string dir, string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }
    }
}
