using System;
using System.Collections;
using System.IO;
using System.Linq;
using Fire3D.Unity.Content;
using Fire3D.Unity.Gameplay.Player;
using Fire3D.Unity.Presentation;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Rigged characters in the development building: the player in each stance and reaching with the IK hand, the
    /// scenario's NPC views, and all ten bodies side by side in different animation states (shots for a human to review).
    /// </summary>
    public class CharacterVisualTests
    {
        private string _dir;

        [UnityTest, Timeout(300000)]
        public IEnumerator CharactersAnimateInTheBuilding()
        {
            _dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "characters-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(_dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            var player = session.Player;
            var body = player.GetComponentInChildren<CharacterAnimator>();
            Assert.IsNotNull(body, "player has no rigged body (RuntimeAssets.PlayerPrefab)");
            Assert.IsTrue(body.Animator.isHuman, "player avatar is not humanoid");

            // 1) Player stances in the corridor, third-person camera.
            player.Teleport(new Vector3(21f, 0.05f, 10f), 0f);
            var rig = Camera.main.GetComponent<Fire3D.Unity.Gameplay.Cameras.ThirdPersonCameraRig>();
            foreach (var (name, stance) in new[] { ("stand", 0), ("crouch", 1), ("crawl", 2) })
            {
                if (stance == 0) player.SetCrouched(false);
                else if (stance == 1) player.SetCrouched(true);
                else player.SetCrawling(true);
                rig.Snap(150f, 18f);
                for (var i = 0; i < 40; i++)
                {
                    player.Step(0.02f);
                    rig.Step(0.02f);
                    yield return null;
                }

                Assert.AreEqual(stance, body.Stance, $"{name}: stance not passed to the animator");
                var hips = body.Animator.GetBoneTransform(HumanBodyBones.Hips).position.y - player.transform.position.y;
                Debug.Log($"[characters] {name}: hips {hips:F2} m above the feet");
                if (stance == 2)
                    Assert.Less(hips, 0.6f, "crawl pose keeps the hips high");
                yield return Capture($"player_{name}.png");
            }

            player.SetCrouched(false);

            // 2) Hand IK: reach a point in front of the chest.
            var gestures = player.GetComponent<PlayerGestures>();
            // Within arm's reach of the right shoulder, like a door handle or a wall-mounted button.
            var target = player.transform.position + player.transform.forward * 0.42f + player.transform.right * 0.18f + Vector3.up * 1.15f;
            var hand = body.Animator.GetBoneTransform(HumanBodyBones.RightHand);
            var reach = player.StartCoroutine(gestures.Reach(target, 0.3f, 1.2f, null));
            yield return new WaitForSeconds(0.6f);
            var miss = Vector3.Distance(hand.position, target);
            Debug.Log($"[characters] reach: hand {miss:F2} m from the target");
            Assert.Less(miss, 0.15f, "IK hand does not reach the target");
            rig.Snap(110f, 12f);
            for (var i = 0; i < 5; i++) { rig.Step(0.02f); yield return null; }
            yield return Capture("player_reach.png");
            yield return reach;

            // 3) The scenario's NPC views.
            // (NPC bodies are optional while the rigged NPC set is being remade; the capsule stand-in is used meanwhile.)
            var npcs = UnityEngine.Object.FindObjectsByType<CharacterAnimator>(FindObjectsSortMode.None).Where(c => c != body).ToArray();
            foreach (var npc in npcs)
                yield return Shoot(npc.transform.position, npc.transform.forward, $"npc_{npc.name}.png", 3.2f);

            // 4) Every rigged body in a row outside the building, each in a different state.
            var assets = Resources.FindObjectsOfTypeAll<RuntimeAssets>().First();
            var bodies = assets.PlayerVariants.Concat(assets.NpcVariants).Where(b => b != null).ToList();
            Assert.IsNotEmpty(bodies, "no rigged character variants");
            // With few bodies, repeat them so each one is seen in several states.
            while (bodies.Count < 10)
                bodies.Add(bodies[bodies.Count % Math.Max(1, assets.PlayerVariants.Count + assets.NpcVariants.Count)]);
            var bounds = new Bounds(player.transform.position, Vector3.one);
            foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
                bounds.Encapsulate(r.bounds);
            var origin = new Vector3(bounds.center.x, 0f, bounds.min.z - 4f);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.position = origin + Vector3.down * 0.001f;
            ground.transform.localScale = new Vector3(2f, 1f, 1f);
            var states = new (float speed, int stance, bool down)[] { (0, 0, false), (1.6f, 0, false), (3.3f, 0, false), (0, 1, false), (0.85f, 1, false), (0, 2, false), (0.5f, 2, false), (1.6f, 0, false), (0, 0, true), (3.3f, 0, false) };
            var spawned = new GameObject[bodies.Count];
            for (var i = 0; i < bodies.Count; i++)
            {
                var go = UnityEngine.Object.Instantiate(bodies[i], origin + Vector3.right * ((i - 4.5f) * 1.0f), Quaternion.Euler(0f, 160f, 0f));
                var c = go.GetComponent<CharacterAnimator>();
                c.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                (c.Speed, c.Stance, c.Down) = states[i];
                spawned[i] = go;
            }

            yield return new WaitForSeconds(1.2f);
            yield return Shoot(origin, Vector3.back, "lineup_all10.png", 7.5f, 1600, 640, 1.0f);

            foreach (var go in spawned)
                UnityEngine.Object.Destroy(go);
            UnityEngine.Object.Destroy(ground);
        }

        private IEnumerator Capture(string file)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(_dir, file), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }

        /// <summary>Renders from a temporary camera placed <paramref name="distance"/> in front of a point.</summary>
        private IEnumerator Shoot(Vector3 at, Vector3 facing, string file, float distance, int w = 720, int h = 900, float height = 1.0f)
        {
            yield return new WaitForEndOfFrame();
            var go = new GameObject("ShotCam");
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = 38f;
            var main = Camera.main;
            if (main != null)
                cam.CopyFrom(main);
            cam.fieldOfView = 38f;
            var f = facing; f.y = 0f; f = f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            go.transform.position = at + f * distance + Vector3.up * (height + 0.4f);
            go.transform.LookAt(at + Vector3.up * height);
            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(_dir, file), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            cam.targetTexture = null;
            rt.Release();
            UnityEngine.Object.Destroy(go);
        }
    }
}
