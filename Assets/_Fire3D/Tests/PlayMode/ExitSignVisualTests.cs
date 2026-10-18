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
    /// <summary>Every exit sign as a person approaching it sees it: readable text, facing the people, soft glow instead of a hard patch.</summary>
    public class ExitSignVisualTests
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator ExitSignsFaceTheRoomAndShowText()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "signs-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            session.enabled = false;
            var player = session.Player;
            foreach (var r in player.GetComponentsInChildren<Renderer>())
                r.enabled = false;
            var cam = Camera.main;
            cam.GetComponent<Fire3D.Unity.Gameplay.Cameras.ThirdPersonCameraRig>().enabled = false;

            var signs = UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Where(t => t.name.StartsWith("ExitSign_")).ToList();
            Assert.GreaterOrEqual(signs.Count, 3, "final x2 and the upper-floor stair sign exist");
            var n = 0;
            foreach (var sign in signs.OrderBy(s => s.name).ThenBy(s => s.position.y))
            {
                // Stand 3 m in front of the face (it looks along local -Z), at eye height, looking at it.
                var front = -sign.forward;
                var eye = sign.position + front * 3f;
                eye.y = sign.position.y - 0.7f;
                cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(sign.position - eye));
                for (var i = 0; i < 30; i++)
                    yield return null;
                // The face must be turned towards the camera, not its back.
                var face = sign.Find("Face");
                Assert.IsNotNull(face, $"{sign.name} has a face");
                Assert.Greater(Vector3.Dot(face.forward * -1f, (eye - face.position).normalized), 0.5f, $"{sign.name} face points at the viewer");
                yield return new WaitForEndOfFrame();
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(dir, $"{n++:00}_{sign.name}_floorY{(int)sign.position.y}.png"), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
