using System;
using System.Collections;
using System.IO;
using Fire3D.Unity.Content;
using Fire3D.Unity.Presentation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// The smoke prefab alone, outside the building on open ground with a lit side: shows whether its sprites,
    /// flipbook and six-way lighting look like billows or like squares, without walls or soft-particle fades in the way.
    /// </summary>
    public class SmokeStudioTests
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator SmokePuffsAndColumnOnOpenGround()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "studio-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            var assets = UnityEngine.Object.FindAnyObjectByType<Fire3D.Unity.Bootstrap.Fire3DApp>().Assets;
            Assert.IsNotNull(assets.SmokePrefab, "Smoke prefab is assigned");

            var session = UnityEngine.Object.FindAnyObjectByType<Fire3D.Unity.Session.TrainingSession>();
            session.enabled = false;
            foreach (var r in session.Player.GetComponentsInChildren<Renderer>())
                r.enabled = false;
            var rig = Camera.main.GetComponent<Fire3D.Unity.Gameplay.Cameras.ThirdPersonCameraRig>();
            rig.enabled = false;
            foreach (var h in UnityEngine.Object.FindObjectsByType<Fire3D.Unity.Presentation.UI.TrainingHud>(FindObjectsSortMode.None))
                h.gameObject.SetActive(false);

            var origin = new Vector3(40f, 0.2f, -12f); // beyond the pavement
            var column = new GameObject("StudioColumn").transform;
            column.position = origin;
            for (var i = 0; i < 6; i++)
            {
                var ps = UnityEngine.Object.Instantiate(assets.SmokePrefab, column);
                ps.transform.localPosition = new Vector3(UnityEngine.Random.Range(-0.4f, 0.4f), 1.5f + i * 0.2f, UnityEngine.Random.Range(-0.4f, 0.4f));
                ps.Play(true);
            }

            var warm = new GameObject("StudioFire").AddComponent<Light>();
            warm.type = LightType.Point;
            warm.color = new Color(1f, 0.5f, 0.15f);
            warm.intensity = 8f;
            warm.range = 12f;
            warm.transform.position = origin + new Vector3(-2.5f, 1.5f, 1.5f);

            var cam = Camera.main;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.35f, 0.4f, 0.48f);
            cam.transform.SetPositionAndRotation(origin + new Vector3(0f, 3.2f, 11f), Quaternion.LookRotation(new Vector3(0f, 3.4f, 0f) - new Vector3(0f, 3.2f, 11f)));
            for (var i = 0; i < 360; i++)
                yield return null;
            yield return Shot(dir, "column");
            cam.transform.SetPositionAndRotation(origin + new Vector3(0f, 3.2f, 5f), Quaternion.LookRotation(new Vector3(0f, 3.4f, 0f) - new Vector3(0f, 3.2f, 5f)));
            for (var i = 0; i < 20; i++)
                yield return null;
            yield return Shot(dir, "column_near");
        }

        private static IEnumerator Shot(string dir, string name)
        {
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);
        }
    }
}
