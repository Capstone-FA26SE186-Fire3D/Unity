using System;
using System.Collections;
using System.IO;
using System.Linq;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>Third person vs first person at the same spots (corridor, kitchen fire): framing, body hidden in FP, body facing the view.</summary>
    public class ViewModeVisualTests
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator FirstPersonHidesBodyAndFacesTheView()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "views-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            var sim = session.Simulation;
            foreach (var id in new[] { "door-L1-office-2", "door-L1-stair" })
                sim.Enqueue(new Fire3D.Simulation.HazardCommand { Kind = Fire3D.Simulation.HazardCommandKind.SetDoorOpen, ObjectId = id, Flag = true });
            for (var i = 0; i < 500; i++)
                sim.Advance(0.05f);
            session.enabled = false;
            var player = session.Player;
            var rig = player.CameraRig;
            var renderers = player.GetComponentsInChildren<Renderer>().Where(r => r.name != "Hand").ToArray();

            foreach (var (label, pos, yaw) in new[] { ("corridor", new Vector3(4.5f, 0.05f, 6.6f), 90f), ("kitchen", new Vector3(10.2f, 0.05f, 3.6f), 100f) })
            foreach (var fp in new[] { false, true })
            {
                rig.FirstPerson = fp;
                player.Teleport(pos, yaw);
                rig.Snap(yaw, fp ? 4f : 14f);
                var until = Time.time + 0.8f; // the view blend takes 0.25 s
                while (Time.time < until)
                {
                    player.Step(Time.deltaTime);
                    yield return null;
                }

                if (fp)
                {
                    Assert.IsTrue(renderers.All(r => r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly), "body hidden in first person");
                    Assert.Less(Vector3.Distance(rig.transform.position, player.transform.position + Vector3.up * 1.62f), 0.15f, "camera at the eyes");
                    Assert.Less(Mathf.Abs(Mathf.DeltaAngle(player.transform.eulerAngles.y, rig.Yaw)), 3f, "body faces the view");
                }
                else
                {
                    Assert.IsTrue(renderers.All(r => r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.On), "body visible in third person");
                    Assert.Greater(rig.CurrentDistance, 1.5f, "third-person camera behind the player");
                }

                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(dir, $"{label}_{(fp ? "first" : "third")}.png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }

            rig.FirstPerson = false;
        }
    }
}
