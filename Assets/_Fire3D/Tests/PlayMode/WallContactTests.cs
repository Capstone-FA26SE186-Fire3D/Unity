using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Fire3D.Simulation;
using Fire3D.Unity.Gameplay.Controls;
using Fire3D.Unity.Gameplay.Player;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Pushes the player into walls all over the graybox at glancing angles: the capsule must never sink into
    /// geometry ("đi xuyên tường") and must keep sliding along the wall instead of stopping ("dính tường").
    /// </summary>
    public class WallContactTests
    {
        private const float MaxPenetration = 0.04f;
        private const float MinSlideRatio = 0.45f;

        [UnityTest, Timeout(900000)]
        public IEnumerator PlayerSlidesAlongWallsWithoutSinking()
        {
            TrainingSession session = null;
            PlayerMotor player = null;
            CharacterController cc = null;
            var input = new Steer();
            var restarts = 0;

            IEnumerator Restart()
            {
                yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
                yield return null;
                yield return null;
                session = Object.FindAnyObjectByType<TrainingSession>();
                session.StartRunning();
                player = session.Player;
                cc = player.GetComponent<CharacterController>();
                player.InputSource = input;
                player.CameraRig.InputSource = input;
            }

            yield return Restart();

            var grid = session.Simulation.Grid;
            var layout = grid.Layout;
            var probes = WallProbes(grid).ToList();
            Assert.Greater(probes.Count, 20, "enough wall probes");

            var worstPenetration = 0f;
            string worstAt = null;
            var stuck = new List<string>();
            foreach (var (cell, wallDir) in probes)
            {
                foreach (var side in new[] { -1f, 1f })
                {
                    if (player == null || session.Phase != SessionPhase.Running)
                    {
                        // An exit trigger or the fire ended the session: start a fresh one and carry on.
                        restarts++;
                        yield return Restart();
                    }

                    var start = (Vector3)layout.CellCenter(cell) + Vector3.up * 0.05f;
                    var tangent = Vector3.Cross(Vector3.up, wallDir) * side;
                    // 35° off the wall normal, i.e. mostly into the wall.
                    var push = (wallDir * Mathf.Cos(35f * Mathf.Deg2Rad) + tangent * Mathf.Sin(35f * Mathf.Deg2Rad)).normalized;
                    player.Teleport(start, Mathf.Atan2(push.x, push.z) * Mathf.Rad2Deg);
                    player.CameraRig.SnapBehind(Mathf.Atan2(push.x, push.z) * Mathf.Rad2Deg);
                    yield return null;

                    input.Yaw = player.CameraRig.Yaw;
                    input.World = push;
                    var contactAt = -1f;
                    var contactPos = Vector3.zero;
                    for (var f = 0; f < 50; f++)
                    {
                        yield return null;
                        var depth = Penetration(cc, player.transform);
                        if (depth > worstPenetration)
                        {
                            worstPenetration = depth;
                            worstAt = $"{start} push {push}";
                        }

                        if (contactAt < 0f && (cc.collisionFlags & CollisionFlags.Sides) != 0)
                        {
                            contactAt = Time.time;
                            contactPos = player.transform.position;
                        }
                    }

                    input.World = Vector3.zero;
                    if (contactAt < 0f)
                        continue; // Door gap or no wall reached: nothing to judge.
                    var slid = Vector3.Dot(player.transform.position - contactPos, tangent);
                    var seconds = Mathf.Max(0.05f, Time.time - contactAt);
                    var expected = 1.6f * Mathf.Sin(35f * Mathf.Deg2Rad) * seconds; // walk speed × tangential share
                    // A real obstacle along the wall (counter end, door jamb, corner) is allowed to stop the slide.
                    var blockedAhead = Physics.CapsuleCast(contactPos + Vector3.up * (cc.radius + 0.05f), contactPos + Vector3.up * (cc.height - cc.radius),
                        cc.radius * 0.9f, tangent, out var ahead, expected + 0.1f, ~0, QueryTriggerInteraction.Ignore) && !ahead.collider.transform.IsChildOf(player.transform);
                    if (seconds > 0.3f && slid < expected * MinSlideRatio && !blockedAhead)
                        stuck.Add($"{start:F1} side {side} slid {slid:F2} m of {expected:F2} m");
                }
            }

            Debug.Log($"[Fire3D walls] probes {probes.Count * 2}, restarts {restarts}, worst penetration {worstPenetration:F3} m at {worstAt}, stuck {stuck.Count}\n" + string.Join("\n", stuck.Take(40)));
            Assert.LessOrEqual(worstPenetration, MaxPenetration, $"capsule sank into geometry at {worstAt}");
            Assert.IsEmpty(stuck, "player stopped against walls instead of sliding:\n" + string.Join("\n", stuck.Take(20)));
        }

        /// <summary>Walkable cells whose neighbour in one axis direction is a wall, sampled sparsely.</summary>
        private static IEnumerable<(int cell, Vector3 dir)> WallProbes(HazardGrid grid)
        {
            var l = grid.Layout;
            var dirs = new[] { (1, 0), (-1, 0), (0, 1), (0, -1) };
            for (var f = 0; f < l.Floors; f++)
            for (var z = 1; z < l.SizeZ - 1; z += 5)
            for (var x = 1; x < l.SizeX - 1; x += 5)
            {
                var i = l.Index(f, x, z);
                var flags = (CellFlags)grid.Flags[i];
                if ((flags & CellFlags.Walkable) == 0 || (flags & (CellFlags.Wall | CellFlags.Stair | CellFlags.Door | CellFlags.Outside | CellFlags.Exit)) != 0)
                    continue;
                foreach (var (dx, dz) in dirs)
                {
                    var n = (CellFlags)grid.Flags[l.Index(f, x + dx, z + dz)];
                    if ((n & CellFlags.Wall) != 0)
                    {
                        yield return (i, new Vector3(dx, 0f, dz));
                        break;
                    }
                }
            }
        }

        private static float Penetration(CharacterController cc, Transform player)
        {
            var r = cc.radius;
            var p = player.position;
            var bottom = p + Vector3.up * r;
            var top = p + Vector3.up * (cc.height - r);
            var worst = 0f;
            var probe = new GameObject("PenetrationProbe").AddComponent<CapsuleCollider>();
            probe.radius = r;
            probe.height = cc.height;
            probe.center = cc.center;
            foreach (var c in Physics.OverlapCapsule(bottom, top, r, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c.transform.IsChildOf(player) || c == probe)
                    continue;
                if (Physics.ComputePenetration(probe, p, Quaternion.identity, c, c.transform.position, c.transform.rotation, out _, out var d))
                    worst = Mathf.Max(worst, d);
            }

            Object.Destroy(probe.gameObject);
            return worst;
        }

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
    }
}
