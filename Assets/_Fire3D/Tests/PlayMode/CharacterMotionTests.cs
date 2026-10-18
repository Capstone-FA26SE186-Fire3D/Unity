using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fire3D.Unity.Gameplay.Cameras;
using Fire3D.Unity.Gameplay.Controls;
using Fire3D.Unity.Presentation;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Drives the player with a scripted stick (walk, run, U-turn, crouch walk) at real frame rate and records a
    /// contact sheet from the game camera plus joint sanity numbers, so a broken retarget (twisted knees, head
    /// facing backwards, arms flung up) shows in the shots and fails the test instead of only being felt in Play.
    /// </summary>
    public class CharacterMotionTests
    {
        private sealed class Stick : IGameplayInputSource
        {
            public GameplayInputFrame Frame;
            public GameplayInputFrame Current => Frame;
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator PlayerMovesNaturally()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "motion-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            var player = session.Player;
            var body = player.GetComponentInChildren<CharacterAnimator>();
            Assert.IsNotNull(body, "player has no rigged body");
            var an = body.Animator;
            var rig = Camera.main.GetComponent<ThirdPersonCameraRig>();
            var stick = new Stick();
            player.InputSource = stick;
            rig.InputSource = stick;
            player.Teleport(new Vector3(21f, 0.05f, 6f), 0f);
            rig.Snap(0f, 14f);

            // Phases: (name, seconds, move, sprint). The stick is camera-relative; the camera follows the body.
            var phases = new (string name, float seconds, Vector2 move, bool sprint, int stance)[]
            {
                ("walk", 2.0f, Vector2.up, false, 0),
                ("run", 2.0f, Vector2.up, true, 0),
                ("uturn", 1.2f, Vector2.down, false, 0),
                ("crouchwalk", 2.0f, Vector2.up, false, 1),
            };
            var frames = new List<Texture2D>();
            var worst = new Dictionary<string, float>();
            foreach (var (name, seconds, move, sprint, stance) in phases)
            {
                if (stance == 1) player.SetCrouched(true); else player.SetCrouched(false);
                stick.Frame = new GameplayInputFrame { Move = move, Sprint = sprint };
                var t = 0f;
                var shotEvery = seconds / 6f;
                var nextShot = 0.35f;
                while (t < seconds)
                {
                    t += Time.deltaTime;
                    yield return null;
                    Check(an, player.transform, name, worst);
                    if (t >= nextShot && frames.Count < 24)
                    {
                        nextShot += shotEvery;
                        yield return new WaitForEndOfFrame();
                        var shot = ScreenCapture.CaptureScreenshotAsTexture();
                        File.WriteAllBytes(Path.Combine(dir, $"frame_{frames.Count:D2}_{name}.png"), shot.EncodeToPNG());
                        frames.Add(shot);
                    }
                }
            }

            stick.Frame = default;
            Sheet(frames, Path.Combine(dir, "motion_sheet.png"));
            foreach (var f in frames)
                UnityEngine.Object.Destroy(f);
            var report = string.Join(", ", worst.Select(kv => $"{kv.Key}={kv.Value:F0}"));
            Debug.Log("[motion] worst angles: " + report);
            File.WriteAllText(Path.Combine(dir, "motion.txt"), report);
            Assert.Less(worst["headYaw"], 60f, "head turns away from the chest: " + report);
            Assert.Less(worst["bodyYaw"], 60f, "torso faces away from the walking direction: " + report);
            Assert.Less(worst["kneeTwist"], 50f, "knee bends sideways: " + report);
            Assert.Less(worst["armRaise"], 100f, "arms fly up: " + report);
        }

        /// <summary>Angles a human body cannot do in a walk cycle, from joint positions only (bone axes are arbitrary).</summary>
        private static void Check(Animator an, Transform root, string phase, Dictionary<string, float> worst)
        {
            Transform B(HumanBodyBones b) => an.GetBoneTransform(b);
            // Torso facing: right across the shoulders × up.
            var across = B(HumanBodyBones.RightUpperArm).position - B(HumanBodyBones.LeftUpperArm).position;
            var torsoFwd = Flat(Vector3.Cross(across, Vector3.up));
            // Head facing: from the head bone to the eyes (Tripo rig has eye bones); fall back to the torso.
            var eyeL = B(HumanBodyBones.LeftEye);
            var eyeR = B(HumanBodyBones.RightEye);
            var headFwd = eyeL != null && eyeR != null ? Flat((eyeL.position + eyeR.position) * 0.5f - B(HumanBodyBones.Head).position) : torsoFwd;
            var headYaw = Vector3.Angle(torsoFwd, headFwd);
            var bodyYaw = Vector3.Angle(torsoFwd, Flat(root.forward));
            var kneeTwist = 0f;
            var armRaise = 0f;
            foreach (var (hip, knee, foot) in new[] { (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot) })
            {
                var thigh = B(knee).position - B(hip).position;
                var shin = B(foot).position - B(knee).position;
                // A bent knee folds backwards in the torso plane; a sideways fold is a twisted knee.
                // Only a clearly bent knee has a meaningful fold direction; near-straight legs are noise.
                var bend = Vector3.ProjectOnPlane(shin, thigh);
                if (Vector3.Angle(thigh, shin) > 25f && bend.magnitude > 0.12f)
                    kneeTwist = Mathf.Max(kneeTwist, Mathf.Asin(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(bend.normalized, Flat(across))))) * Mathf.Rad2Deg);
            }

            foreach (var (shoulder, hand) in new[] { (HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand), (HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand) })
                armRaise = Mathf.Max(armRaise, Vector3.Angle(Vector3.down, B(hand).position - B(shoulder).position));

            Keep(worst, "headYaw", headYaw);
            Keep(worst, "bodyYaw", bodyYaw);
            Keep(worst, "kneeTwist", kneeTwist);
            Keep(worst, "armRaise", armRaise);
            Keep(worst, phase + "_headYaw", headYaw);
            Keep(worst, phase + "_kneeTwist", kneeTwist);
            Keep(worst, phase + "_armRaise", armRaise);
        }

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z).normalized;

        private static void Keep(Dictionary<string, float> d, string k, float v) => d[k] = d.TryGetValue(k, out var o) ? Mathf.Max(o, v) : v;

        private static void Sheet(List<Texture2D> frames, string path)
        {
            if (frames.Count == 0)
                return;
            var w = frames[0].width / 3;
            var h = frames[0].height / 3;
            const int cols = 6;
            var rows = (frames.Count + cols - 1) / cols;
            var sheet = new Texture2D(w * cols, h * rows, TextureFormat.RGB24, false);
            var fill = new Color32[w * cols * h * rows];
            sheet.SetPixels32(fill);
            for (var i = 0; i < frames.Count; i++)
            {
                var f = frames[i];
                var px = new Color[w * h];
                for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    px[y * w + x] = f.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
                sheet.SetPixels((i % cols) * w, (rows - 1 - i / cols) * h, w, h, px);
            }

            sheet.Apply();
            File.WriteAllBytes(path, sheet.EncodeToPNG());
            UnityEngine.Object.Destroy(sheet);
        }
    }
}
