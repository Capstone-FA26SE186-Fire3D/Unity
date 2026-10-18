using System;
using System.Collections;
using System.IO;
using Fire3D.Simulation;
using Fire3D.Unity.Gameplay.Player;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Why the game asks you to get low: standing in a smoke-filled corridor breathes the most, crouching less, crawling
    /// least (head under the layer). Also shoots the three stances for review.
    /// </summary>
    public class StanceSmokeTests
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator CrawlingBreathesLessThanCrouchingLessThanStanding()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "stance-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            var sim = session.Simulation;
            foreach (var id in new[] { "door-L1-office-2", "door-L1-stair" })
                sim.Enqueue(new HazardCommand { Kind = HazardCommandKind.SetDoorOpen, ObjectId = id, Flag = true });
            // Let the corridor fill until the layer bottom is between a crouched head (1.0 m) and a crawler's (0.5 m):
            // the moment the game wants the learner to go from crouching to crawling. Player safely far away meanwhile.
            var player = session.Player;
            player.Teleport(new Vector3(21f, 0.05f, 12f), 0f);
            var spot = new Vector3(9.5f, 0.05f, 6.9f); // corridor outside the kitchen
            var spotCell = sim.Grid.Layout.CellAtClamped(spot);
            var layerBottom = 99f;
            for (var i = 0; i < 4000; i++)
            {
                sim.Advance(0.05f);
                layerBottom = sim.Grid.LayerBottom(spotCell);
                if (layerBottom < 0.9f)
                    break;
                if (i % 10 == 0)
                    yield return null;
            }

            Debug.Log($"[Fire3D stance] corridor layer bottom at y={layerBottom:F2}");
            Assert.Less(layerBottom, 1.55f, "after 100 s the layer is below standing head height");
            Assert.Greater(layerBottom, 0.5f, "but a crawler's head is still under it");

            var pinnedLayer = sim.Grid.SmokeLayer[spotCell];
            var vitals = player.GetComponent<PlayerVitals>();
            var intake = new float[3];
            var names = new[] { "standing", "crouched", "crawling" };
            for (var stance = 0; stance < 3; stance++)
            {
                player.SetCrouched(stance >= 1);
                if (stance == 2)
                    player.SetCrawling(true);
                player.Teleport(spot, 90f);
                vitals.ResetVitals();
                // Breathe for 4 s of session time. Tick advances the simulation, so pin the layer around the spot to its
                // starting thickness each step: the three stances then face the same layer and compare equally.
                for (var i = 0; i < 40; i++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                        for (var dz = -1; dz <= 1; dz++)
                            sim.Grid.SmokeLayer[sim.Grid.Layout.CellAtClamped(spot + new Vector3(dx * 0.5f, 0f, dz * 0.5f))] = pinnedLayer;
                    session.Tick(0.1f);
                    if (i % 4 == 0)
                        yield return null;
                }

                intake[stance] = vitals.SmokeInhaled;
                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(dir, $"{stance}_{names[stance]}.png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }

            Debug.Log($"[Fire3D stance] inhaled standing {intake[0]:F2}, crouched {intake[1]:F2}, crawling {intake[2]:F2}");
            Assert.Greater(intake[0], 0.2f, "standing in the layer breathes smoke");
            Assert.Less(intake[1], intake[0] * 0.6f, "crouching clearly reduces intake");
            Assert.LessOrEqual(intake[2], intake[1] * 1.15f, "crawling never breathes more than crouching (measurement noise allowed)");
            Assert.Less(intake[2], intake[0] * 0.25f, "crawling keeps the head out of the layer");
            player.SetCrouched(false);
        }
    }
}
