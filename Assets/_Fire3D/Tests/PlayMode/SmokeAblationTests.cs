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
    /// <summary>
    /// Diagnostic, not a pass/fail gate: the same corridor smoke shot with one ingredient changed per frame, to see which
    /// one makes the smoke read as flat cards. Output under Builds/Autoplay/ablation-*.
    /// </summary>
    public class SmokeAblationTests
    {
        // Diagnostic tool, run on its own (it reads the live hazard state after a long simulation, which other
        // tests' quality overrides and ordering can disturb): unity command run_tests --filter SmokeAblationTests --include_explicit true
        [UnityTest, Explicit, Timeout(600000)]
        public IEnumerator CorridorSmokeIngredientByIngredient()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "ablation-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            FireQuality.Override(FireQuality.Tier.High);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            session.enabled = false;
            var sim = session.Simulation;
            foreach (var id in new[] { "door-L1-office-2", "door-L1-stair", "door-L2-stair" })
                sim.Enqueue(new HazardCommand { Kind = HazardCommandKind.SetDoorOpen, ObjectId = id, Flag = true });
            for (var i = 0; i < 1400; i++)
            {
                sim.Advance(0.05f);
                if (i % 8 == 0)
                    yield return null;
            }

            var player = session.Player;
            foreach (var r in player.GetComponentsInChildren<Renderer>())
                r.enabled = false;
            var cam = Camera.main;
            var rig = cam.GetComponent<Fire3D.Unity.Gameplay.Cameras.ThirdPersonCameraRig>();
            rig.enabled = false;
            // Eye height at the west end of the corridor looking east down it (the user's viewpoint).
            player.transform.position = new Vector3(3.5f, 0.05f, 6.9f);
            cam.transform.SetPositionAndRotation(new Vector3(3.5f, 1.6f, 6.9f), Quaternion.LookRotation(Vector3.right));
            for (var i = 0; i < 60; i++)
                yield return null;

            Material[] Smokes() => UnityEngine.Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsSortMode.None)
                .Where(r => r.gameObject.name.StartsWith("Smoke")).Select(r => r.material).ToArray();

            yield return Shot(dir, "00_baseline");

            foreach (var m in Smokes()) m.SetFloat("_SoftDistance", 0.05f);
            yield return Shot(dir, "01_softfade_off");
            foreach (var m in Smokes()) m.SetFloat("_SoftDistance", 2f);

            foreach (var m in Smokes()) { m.SetFloat("_LightGain", 0f); m.SetFloat("_Ambient", 0.6f); }
            yield return Shot(dir, "02_no_dynamic_lights");
            foreach (var m in Smokes()) { m.SetFloat("_LightGain", 1.6f); m.SetFloat("_Ambient", 0.9f); }

            // Opaque alpha: shows the raw sprite silhouettes and how they tile.
            foreach (var m in Smokes()) m.SetColor("_BaseColor", new Color(1f, 1f, 1f, 3f));
            yield return Shot(dir, "03_alpha_x3");
            foreach (var m in Smokes()) m.SetColor("_BaseColor", Color.white);

            // Camera near fade off.
            foreach (var m in Smokes()) m.SetVector("_CameraFade", new Vector4(0f, 0.01f, 0f, 0f));
            yield return Shot(dir, "04_nearfade_off");
            foreach (var m in Smokes()) m.SetVector("_CameraFade", new Vector4(0.3f, 1.6f, 0f, 0f));

            // Only one emitter alive: a single column, to judge the sprite itself.
            var systems = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None).Where(p => p.gameObject.name.StartsWith("Smoke")).ToList();
            foreach (var p in systems.Skip(1))
                p.gameObject.SetActive(false);
            yield return Shot(dir, "05_single_emitter");
            foreach (var p in systems)
                p.gameObject.SetActive(true);

            // From the side and from above, to see whether the quads are leaning or coplanar.
            cam.transform.SetPositionAndRotation(new Vector3(9f, 1.6f, 9.5f), Quaternion.LookRotation(new Vector3(0.3f, -0.1f, -1f)));
            yield return Shot(dir, "06_side_view");
            cam.transform.SetPositionAndRotation(new Vector3(9f, 2.9f, 6.9f), Quaternion.LookRotation(new Vector3(1f, -0.9f, 0f)));
            yield return Shot(dir, "07_top_view");
            // Kitchen doorway from the corridor: smoke should pour through the opening, not stop at the frame.
            cam.transform.SetPositionAndRotation(new Vector3(11f, 1.5f, 7.6f), Quaternion.LookRotation(new Vector3(0f, 0.15f, -1f)));
            yield return Shot(dir, "08_doorway");

            var grid = sim.Grid;
            var stats = string.Join(" ", systems.Where(p => p.particleCount > 0).Select(p => $"[{p.transform.position.x:F1},{p.transform.position.y:F1},{p.transform.position.z:F1} s={p.transform.localScale.x:F2} n={p.particleCount}]"));
            Debug.Log($"[Fire3D ablation] emitters {systems.Count(p => p.particleCount > 0)} {stats}");
            Assert.Greater(systems.Count(p => p.particleCount > 0), 0);
        }

        private static IEnumerator Shot(string dir, string name)
        {
            for (var i = 0; i < 12; i++)
                yield return null;
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }
    }
}
