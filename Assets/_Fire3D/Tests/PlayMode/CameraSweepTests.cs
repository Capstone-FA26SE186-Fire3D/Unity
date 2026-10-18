using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Fire3D.Simulation;
using Fire3D.Unity.Gameplay.Cameras;
using Fire3D.Unity.Session;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Sweeps the player over the walkable cells of both floors (stairs included) and turns the camera through every
    /// yaw and three pitches: the camera must never end up touching level geometry, which shows the other side of a wall.
    /// </summary>
    public class CameraSweepTests
    {
        [UnityTest, Timeout(600000)]
        public IEnumerator CameraNeverTouchesGeometryAnywhere()
        {
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            session.enabled = false;
            var player = session.Player;
            var rig = Camera.main.GetComponent<ThirdPersonCameraRig>();
            var grid = session.Simulation.Grid;
            var layout = grid.Layout;
            var bad = new List<string>();
            var checkedPoses = 0;
            var buffer = new Collider[8];

            var spots = new List<(string label, Vector3 pos)>();
            for (var f = 0; f < layout.Floors; f++)
            for (var z = 1; z < layout.SizeZ - 1; z += 3)
            for (var x = 1; x < layout.SizeX - 1; x += 3)
            {
                var i = layout.Index(f, x, z);
                var flags = (CellFlags)grid.Flags[i];
                if ((flags & CellFlags.Walkable) == 0 || (flags & (CellFlags.Wall | CellFlags.Outside | CellFlags.Stair)) != 0)
                    continue;
                spots.Add(($"floor {f}", (Vector3)layout.CellCenter(i) + Vector3.up * 0.03f));
            }

            // The stair flight itself: stand on every tread (the grid links floors through the whole shaft).
            foreach (var z in new[] { 0.9f, 1.35f, 1.8f })
            for (var x = 18.9f; x <= 23.6f; x += 0.2f)
                if (Physics.Raycast(new Vector3(x, 4.0f, z), Vector3.down, out var tread, 4.5f, ~0, QueryTriggerInteraction.Ignore))
                    spots.Add(("stair", tread.point + Vector3.up * 0.03f));

            foreach (var (label, c) in spots)
            {
                player.Teleport(c, 0f);
                yield return null;
                foreach (var pitch in new[] { -20f, 18f, 65f })
                for (var yaw = 0f; yaw < 360f; yaw += 45f)
                {
                    rig.Snap(yaw, pitch);
                    for (var k = 0; k < 25; k++)
                        rig.Step(0.1f);
                    checkedPoses++;
                    var p = rig.transform.position;
                    var n = Physics.OverlapSphereNonAlloc(p, 0.1f, buffer, ~0, QueryTriggerInteraction.Ignore);
                    for (var j = 0; j < n; j++)
                    {
                        if (buffer[j].transform.IsChildOf(player.transform) || buffer[j] is CharacterController)
                            continue;
                        bad.Add($"{label} player {c:F1} yaw {yaw} pitch {pitch}: camera {p:F2} touches {buffer[j].name}");
                        break;
                    }
                }
            }

            Debug.Log($"[Fire3D camera] poses {checkedPoses}, touching geometry {bad.Count}\n" + string.Join("\n", bad.Take(25)));
            Assert.Greater(checkedPoses, 500, "swept enough poses");
            Assert.IsEmpty(bad, "camera touched geometry:\n" + string.Join("\n", bad.Take(15)));
        }
    }
}
