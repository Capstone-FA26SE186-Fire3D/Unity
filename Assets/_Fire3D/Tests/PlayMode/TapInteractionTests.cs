using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Unity.Gameplay.Interactions;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Object actions are tiles on the world prompt: one tap runs them. Dipping a towel fills its ring by itself and
    /// fails if the player walks away first; staying until the ring closes wets the towel. Run windowed for the
    /// screenshots (batchmode never reaches end of frame, so captures are skipped there).
    /// </summary>
    public class TapInteractionTests
    {
        private string _dir;

        [UnityTest, Timeout(180000)]
        public IEnumerator TapToActAndDipFailsWhenWalkingAway()
        {
            _dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "tap-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(_dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            var player = session.Player;
            var interaction = session.Interaction;
            var messages = new List<(string text, MessageTone tone)>();
            GameContext.Current.Message += (t, tone) => messages.Add((t, tone));

            var all = UnityEngine.Object.FindObjectsByType<Interactable>(FindObjectsSortMode.None);
            var towel = all.First(i => i.CapabilityId == Capabilities.ItemTowel);
            var sink = all.First(i => Capabilities.IsWater(i.CapabilityId));

            // 1) Pick up the towel with one tap on its tile.
            yield return StandBefore(player, towel, 0.75f);
            Assert.AreEqual(towel, interaction.Target, "towel not targeted");
            yield return Capture("01_towel_tile.png");
            interaction.Tap(SlotOf(interaction, "pick_up"));
            yield return null;
            Assert.AreEqual(towel, interaction.Held, "one tap on the tile did not pick the towel up");

            // 2) Tap "dip", walk away half way: fails, towel stays dry.
            yield return StandBefore(player, sink, 0.8f);
            Assert.AreEqual(sink, interaction.Target, "sink not targeted");
            var dip = SlotOf(interaction, "wet_item");
            interaction.Tap(dip);
            yield return new WaitForSeconds(0.9f);
            Assert.Greater(interaction.HoldProgress(dip), 0.2f, "the ring does not fill by itself after one tap");
            yield return Capture("02_dip_ring.png");
            player.Teleport(player.transform.position - player.transform.forward * 1.5f, player.transform.eulerAngles.y);
            yield return null;
            yield return null;
            Assert.AreNotEqual("wet", towel.State, "walking away still wet the towel");
            Assert.IsTrue(messages.Any(m => m.tone == MessageTone.Danger && m.text.Contains("chưa xong")), "no failure message");
            Assert.AreEqual(-1, interaction.RunningTap);
            yield return Capture("03_dip_failed.png");

            // 3) Dip again and stay: wet.
            yield return StandBefore(player, sink, 0.8f);
            interaction.Tap(SlotOf(interaction, "wet_item"));
            var until = Time.time + 4f;
            while (towel.State != "wet" && Time.time < until)
                yield return null;
            Assert.AreEqual("wet", towel.State, "staying until the ring closes did not wet the towel");
            yield return Capture("04_dip_done.png");

            // 4) The held towel hangs from the hand (draped mesh), darker now that it is wet.
            Assert.IsTrue(towel.transform.Find("Draped")?.gameObject.activeSelf ?? false, "held towel is not shown draped");
            Assert.IsFalse(towel.transform.Find("Folded")?.gameObject.activeSelf ?? true, "held towel still shows folded");
            yield return null;
            var hand = towel.transform.Find("Draped").position;
            yield return Shoot(hand + player.transform.right * 0.8f + player.transform.forward * 0.6f, hand + Vector3.down * 0.22f, "05_towel_in_hand.png");
        }

        private static int SlotOf(InteractionController interaction, string id)
        {
            var actions = interaction.Actions;
            for (var i = 0; i < actions.Count; i++)
                if (actions[i].Id == id)
                    return i;
            Assert.Fail($"action {id} not offered ({string.Join(",", actions.Select(a => a.Id))})");
            return -1;
        }

        /// <summary>Puts the player <paramref name="distance"/> in front of the object, facing it.</summary>
        private static IEnumerator StandBefore(Gameplay.Player.PlayerMotor player, Interactable target, float distance)
        {
            var at = target.transform.position;
            var from = player.transform.position - at;
            from.y = 0f;
            // Prefer the object's front (its -Z) when there is room; otherwise come from where the player is.
            var dir = -target.transform.forward;
            dir.y = 0f;
            if (Physics.Raycast(at + Vector3.up * 0.5f, dir.normalized, distance + 0.3f))
                dir = from;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : Vector3.back;
            var pos = new Vector3(at.x, at.y, at.z) + dir * distance;
            // Stand on the floor under that spot (the object may sit on a table).
            var floor = Physics.Raycast(pos + Vector3.up * 1.2f, Vector3.down, out var hit, 4f) ? hit.point.y : at.y;
            player.Teleport(new Vector3(pos.x, floor + 0.05f, pos.z), Quaternion.LookRotation(-dir).eulerAngles.y);
            for (var i = 0; i < 5; i++)
                yield return null;
        }

        /// <summary>Close-up through a temporary camera (no HUD); works in batchmode too.</summary>
        private IEnumerator Shoot(Vector3 eye, Vector3 target, string file)
        {
            yield return null;
            var go = new GameObject("ShotCam");
            var cam = go.AddComponent<Camera>();
            if (Camera.main != null)
                cam.CopyFrom(Camera.main);
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.02f;
            go.transform.position = eye;
            go.transform.LookAt(target);
            var rt = new RenderTexture(800, 800, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(800, 800, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 800, 800), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(_dir, file), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
            cam.targetTexture = null;
            rt.Release();
            UnityEngine.Object.Destroy(go);
        }

        private IEnumerator Capture(string file)
        {
            if (Application.isBatchMode)
                yield break;
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(_dir, file), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }
    }
}
