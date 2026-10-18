using System.Collections;
using Fire3D.Unity.Gameplay.Controls;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>Running sideways with no look input swings the camera round behind the runner; a look drag stops the follow.</summary>
    public class CameraFollowTests
    {
        /// <summary>A thumb that keeps a world direction: as the view swings, the stick is re-aimed, like a player would.</summary>
        private sealed class Stick : IGameplayInputSource
        {
            public Vector3 World;
            public Vector2 Look;
            public float Yaw;

            public GameplayInputFrame Current
            {
                get
                {
                    var local = Quaternion.Euler(0f, -Yaw, 0f) * World;
                    return new GameplayInputFrame { Move = new Vector2(local.x, local.z), LookDegrees = Look };
                }
            }
        }

        [UnityTest, Timeout(120000)]
        public IEnumerator CameraFollowsTheRunningDirection()
        {
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            session.enabled = false;
            var player = session.Player;
            var rig = player.CameraRig;
            var stick = new Stick();
            player.InputSource = stick;
            rig.InputSource = stick;
            player.Teleport(new Vector3(12f, 0.05f, 11f), 0f); // meeting room, open floor
            rig.SnapBehind(0f);
            yield return null;

            // Run towards +X while the camera looks along +Z: the view should swing round to yaw 90.
            stick.World = Vector3.right;
            var until = Time.time + 2.5f;
            while (Time.time < until)
            {
                stick.Yaw = rig.Yaw;
                yield return null;
            }

            stick.World = Vector3.zero;
            Assert.Less(Mathf.Abs(Mathf.DeltaAngle(rig.Yaw, 90f)), 20f, $"camera followed the run; yaw {rig.Yaw:F0}");

            // Looking while running: the player's drag wins, no auto-follow.
            player.Teleport(new Vector3(12f, 0.05f, 11f), 0f);
            rig.SnapBehind(0f);
            yield return null;
            stick.World = Vector3.right;
            stick.Look = new Vector2(-0.01f, 0f); // a constant tiny drag
            until = Time.time + 1.5f;
            while (Time.time < until)
            {
                stick.Yaw = rig.Yaw;
                yield return null;
            }

            stick.World = Vector3.zero;
            stick.Look = Vector2.zero;
            Assert.Less(Mathf.Abs(Mathf.DeltaAngle(rig.Yaw, 0f)), 25f, $"look drag suppressed follow; yaw {rig.Yaw:F0}");
        }
    }
}
