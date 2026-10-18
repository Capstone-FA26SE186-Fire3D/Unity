using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fire3D.Unity.Content;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Fire3D.Unity.Editor.Development
{
    /// <summary>
    /// Review images of whatever scenes the build worker produced: opens them (first one single, the rest additive so
    /// all floors and their baked lighting load together) and renders one eye-height view per space, looking along
    /// the room's long axis from one end. Used to judge IFC import quality without building bundles.
    /// </summary>
    public static class SpaceSnapshots
    {
        public static string[] Capture(IReadOnlyList<string> scenePaths, string outDir, int maxPerFloor = 6, bool openOutageFirst = false)
        {
            Directory.CreateDirectory(outDir);
            for (var i = 0; i < scenePaths.Count; i++)
                EditorSceneManager.OpenScene(scenePaths[i], i == 0 ? OpenSceneMode.Single : OpenSceneMode.Additive);
            LightProbes.Tetrahedralize();

            var camGo = new GameObject("SnapshotCamera") { hideFlags = HideFlags.HideAndDontSave };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 75f;
            cam.nearClipPlane = 0.05f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.45f, 0.5f, 0.56f);
            var written = new List<string>();
            try
            {
                foreach (var group in Object.FindObjectsByType<SpaceVolume>(FindObjectsSortMode.None)
                             .GroupBy(s => s.GetComponentInParent<BuildingRoot>()?.FloorId ?? "?"))
                {
                    var n = 0;
                    foreach (var space in group.OrderByDescending(s => s.Bounds.size.x * s.Bounds.size.z).Take(maxPerFloor))
                    {
                        var b = space.Bounds;
                        var alongX = b.size.x >= b.size.z;
                        var axis = alongX ? Vector3.right : Vector3.forward;
                        var half = (alongX ? b.size.x : b.size.z) * 0.5f;
                        var eye = new Vector3(b.center.x, b.min.y + 1.6f, b.center.z) - axis * Mathf.Max(0.3f, half - 0.5f);
                        var look = eye + axis * 4f - Vector3.up * 0.25f;
                        cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(look - eye));
                        var file = Path.Combine(outDir, $"{group.Key}_{n++:00}_{Sanitize(space.Category)}.png");
                        Render(cam, file);
                        written.Add(file);
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(camGo);
            }

            return written.ToArray();
        }

        private static void Render(Camera cam, string file)
        {
            var rt = new RenderTexture(1280, 720, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            File.WriteAllBytes(file, tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }

        private static string Sanitize(string s) => new((s ?? "space").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    }
}
