using System;
using System.Collections;
using System.IO;
using System.Linq;
using Fire3D.Unity.Gameplay.Controls;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Walks down the stair from L2 the way a player does and checks the opening stays open: nothing without a
    /// collider (a ceiling or floor finish) may lie in the stairwell above the treads, the body must stay on the
    /// treads, and the camera must not touch geometry. Shots from the third-person camera at a few points of the descent.
    /// </summary>
    public class StairDescentTests
    {
        private sealed class Steer : IGameplayInputSource
        {
            public Vector3 World;
            public float Yaw;

            public GameplayInputFrame Current
            {
                get
                {
                    var local = Quaternion.Euler(0f, -Yaw, 0f) * World;
                    return new GameplayInputFrame { Move = new Vector2(local.x, local.z) };
                }
            }
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator StairwellIsOpenAndWalkable()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "stairs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            session.enabled = false;
            var player = session.Player;
            var rig = player.CameraRig;

            // No renderer without a collider inside the stairwell volume above the flight (x 18.6..23.2, z 0.4..2.3, y 0.5..3.1).
            var hole = new Bounds(new Vector3(20.9f, 1.8f, 1.35f), new Vector3(4.6f, 2.6f, 1.9f));
            var intruders = UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None)
                .Where(r => r.GetComponent<Collider>() == null && r.bounds.Intersects(hole) && !r.transform.IsChildOf(player.transform))
                .Where(r => r.bounds.min.y > 0.3f && r.bounds.max.y < 3.1f) // not the L1 floor or the L2 slab edge
                .Select(r => $"{r.name} {r.bounds.center:F1} size {r.bounds.size:F1}")
                .ToList();
            Assert.IsEmpty(intruders, "collider-less sheets inside the stairwell:\n" + string.Join("\n", intruders));

            var input = new Steer();
            player.InputSource = input;
            rig.InputSource = input;
            player.Teleport(new Vector3(21f, 3.25f, 3.3f), 180f); // L2, in front of the stair door
            rig.SnapBehind(180f);
            yield return null;
            // Open the stair door so the walk is not blocked.
            var door = Fire3D.Unity.Gameplay.Interactions.Interactable.All.First(i => i.ObjectId == "stair-door-l2");
            door.GetComponent<Fire3D.Unity.Gameplay.Interactions.DoorView>().SetOpen(true, instant: true);
            door.State = "open";

            var minFeetOverTread = float.MaxValue;
            var cameraHits = 0;
            var shots = new[] { 2.8f, 1.9f, 1.0f, 0.2f };
            var shotIndex = 0;
            var waypoints = new[] { new Vector3(22.6f, 3.2f, 1.35f), new Vector3(17.2f, 0f, 1.35f) };
            foreach (var wp in waypoints)
            {
                var until = Time.time + 12f;
                while (Time.time < until)
                {
                    var p = player.transform.position;
                    var to = wp - p;
                    to.y = 0f;
                    if (to.magnitude < 0.35f)
                        break;
                    input.Yaw = rig.Yaw;
                    input.World = to.normalized;
                    yield return null;

                    // Feet relative to the surface below (treads or landing), camera clearance, screenshots on the way down.
                    if (Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore)
                        && !hit.collider.transform.IsChildOf(player.transform))
                        minFeetOverTread = Mathf.Min(minFeetOverTread, p.y - hit.point.y);
                    if (Physics.CheckSphere(rig.transform.position, 0.1f, ~0, QueryTriggerInteraction.Ignore))
                        cameraHits++;
                    if (shotIndex < shots.Length && p.y <= shots[shotIndex] && p.x > 18.6f && p.x < 23.3f)
                    {
                        yield return new WaitForEndOfFrame();
                        var tex = ScreenCapture.CaptureScreenshotAsTexture();
                        File.WriteAllBytes(Path.Combine(dir, $"descent_{shotIndex:00}_y{p.y:F1}.png"), tex.EncodeToPNG());
                        UnityEngine.Object.Destroy(tex);
                        shotIndex++;
                    }
                }
            }

            input.World = Vector3.zero;
            Debug.Log($"[Fire3D stairs] end {player.transform.position:F2}, min feet over tread {minFeetOverTread:F3}, camera hits {cameraHits}, shots {shotIndex}");
            Assert.Less(player.transform.position.y, 0.3f, "reached the ground floor");
            Assert.GreaterOrEqual(minFeetOverTread, -0.08f, "feet sank into the treads");
            Assert.AreEqual(0, cameraHits, "camera touched geometry during the descent");
        }
    }
}
