using System;
using System.Collections;
using System.IO;
using System.Linq;
using Fire3D.Simulation;
using Fire3D.Unity.Presentation;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>Smoke as the player sees it: spread through the kitchen and corridor with doors open, shot from fixed free-camera views.</summary>
    public class SmokeVisualTests
    {
        [UnityTest, Timeout(600000)]
        public IEnumerator SmokeIsVisibleInKitchenAndCorridor()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "smoke-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            FireQuality.Override(FireQuality.Tier.High);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            var sim = session.Simulation;
            foreach (var id in new[] { "door-L1-office-2", "door-L1-stair", "door-L2-stair" })
                sim.Enqueue(new HazardCommand { Kind = HazardCommandKind.SetDoorOpen, ObjectId = id, Flag = true });

            var player = session.Player;
            player.Teleport(new Vector3(8.6f, 0.05f, 3.4f), 112f);
            foreach (var r in player.GetComponentsInChildren<Renderer>())
                r.enabled = false;
            for (var i = 0; i < 1200; i++) // 60 s of fire and smoke
            {
                sim.Advance(0.05f); // Simulation only: the player's vitals must not end the session before the shots.
                if (i % 4 == 0)
                    yield return null;
            }

            for (var i = 0; i < 90; i++)
                yield return null;

            var grid = sim.Grid;
            var maxSmoke = 0f;
            var cellsOver = 0;
            for (var i = 0; i < grid.Layout.CellCount; i++)
            {
                maxSmoke = Mathf.Max(maxSmoke, grid.Smoke[i]);
                if (grid.Smoke[i] > 0.18f)
                    cellsOver++;
            }

            var smokes = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None)
                .Where(p => p.gameObject.name.StartsWith("Smoke")).ToList();
            var live = smokes.Count(p => p.isEmitting);
            var particles = smokes.Sum(p => p.particleCount);
            Debug.Log($"[Fire3D smoke] max grid smoke {maxSmoke:F2}, cells>0.18: {cellsOver}, smoke emitters {smokes.Count} (emitting {live}), live particles {particles}, fog {RenderSettings.fogDensity:F3}");

            var cam = Camera.main;
            var rig = cam.GetComponent<Fire3D.Unity.Gameplay.Cameras.ThirdPersonCameraRig>();
            rig.enabled = false;
            foreach (var (label, pos, look) in new[]
                     {
                         ("kitchen", new Vector3(8.4f, 1.7f, 3.6f), new Vector3(12.2f, 2.2f, 1.6f)),
                         ("kitchen_ceiling", new Vector3(9.5f, 1.5f, 3.0f), new Vector3(12.2f, 2.8f, 2.0f)),
                         ("corridor", new Vector3(2.5f, 1.6f, 7.0f), new Vector3(14f, 2.0f, 7.0f)),
                         ("corridor_low", new Vector3(1.5f, 0.9f, 7.0f), new Vector3(14f, 2.4f, 7.0f)),
                         ("stair_L1", new Vector3(19.0f, 1.6f, 7.2f), new Vector3(22.5f, 2.2f, 1.5f)),
                     })
            {
                player.transform.position = pos;
                cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos));
                for (var i = 0; i < 40; i++)
                    yield return null;
                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(dir, $"smoke_{label}.png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }

            rig.enabled = true;
            Assert.Greater(maxSmoke, 0.2f, "the simulation produced smoke");
            Assert.Greater(live, 0, "smoke emitters are playing where the grid has smoke");
        }
    }
}
