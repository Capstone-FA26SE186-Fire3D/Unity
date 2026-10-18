using System;
using System.Collections;
using System.IO;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>Standing and crouched from the third-person camera at a high and a low pitch (for a human to compare).</summary>
    public class CrouchLookTests
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator CrouchPoseKeepsFeetOnTheFloor()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "crouch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            session.enabled = false;
            var player = session.Player;
            player.Teleport(new Vector3(21f, 0.05f, 10f), 0f);
            var rig = Camera.main.GetComponent<Fire3D.Unity.Gameplay.Cameras.ThirdPersonCameraRig>();
            foreach (var crouch in new[] { false, true })
            foreach (var pitch in new[] { 18f, 60f })
            {
                player.SetCrouched(crouch);
                rig.Snap(0f, pitch);
                for (var i = 0; i < 45; i++)
                {
                    player.Step(0.02f);
                    rig.Step(0.02f);
                    yield return null;
                }

                // Lowest point of the body must not be under the floor: the feet of a rigged body (its skinned bounds are a
                // loose box around the hips), the renderer bounds of the capsule stand-in.
                var lowest = float.MaxValue;
                var animator = player.GetComponentInChildren<Animator>();
                if (animator != null && animator.isHuman)
                {
                    foreach (var bone in new[] { HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, HumanBodyBones.LeftToes, HumanBodyBones.RightToes, HumanBodyBones.Hips, HumanBodyBones.Head })
                    {
                        var t = animator.GetBoneTransform(bone);
                        if (t != null)
                            lowest = Mathf.Min(lowest, t.position.y);
                    }
                }
                else
                {
                    foreach (var r in player.GetComponentsInChildren<Renderer>())
                        lowest = Mathf.Min(lowest, r.bounds.min.y);
                }
                Assert.GreaterOrEqual(lowest, player.transform.position.y - 0.12f, $"crouch={crouch}: body sticks below the feet");
                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(dir, $"{(crouch ? "crouched" : "standing")}_pitch{(int)pitch}.png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
