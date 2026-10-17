using System;
using System.IO;
using Fire3D.Unity.Content;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Fire3D.Unity.Editor.Development
{
    /// <summary>
    /// Renders review images of the development scene (normal and outage lighting) without entering Play Mode.
    /// Batch: -executeMethod Fire3D.Unity.Editor.Development.SceneSnapshots.Capture -fet3dOut &lt;dir&gt;
    /// </summary>
    public static class SceneSnapshots
    {
        private static readonly (string name, Vector3 pos, Vector3 look)[] Shots =
        {
            ("corridor_L2", new Vector3(2.2f, 4.85f, 7.0f), new Vector3(14f, 4.3f, 7.0f)),
            ("office_C", new Vector3(7.4f, 4.9f, 5.4f), new Vector3(2.5f, 3.6f, 1.2f)),
            ("stair", new Vector3(18.6f, 1.6f, 4.8f), new Vector3(23f, 2.2f, 1.2f)),
            ("lobby_L1", new Vector3(19.0f, 1.65f, 9.0f), new Vector3(21f, 1.2f, 14f)),
        };

        [MenuItem("Fire3D/Development/Capture Snapshots")]
        public static void CaptureMenu() => Capture(Path.Combine(Application.dataPath, "../Builds/Snapshots"));

        public static void Capture()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "-fet3dOut");
            Capture(i >= 0 && i + 1 < args.Length ? args[i + 1] : Path.Combine(Application.dataPath, "../Builds/Snapshots"));
        }

        public static void Capture(string outDir)
        {
            Directory.CreateDirectory(outDir);
            EditorSceneManager.OpenScene(DevelopmentSceneBuilder.ScenePath, OpenSceneMode.Single);
            var cam = UnityEngine.Object.FindAnyObjectByType<Camera>();
            cam.fieldOfView = 70f;
            cam.nearClipPlane = 0.05f;
            LightProbes.Tetrahedralize();
            // Dynamic probe check: a white sphere in the L2 corridor must be lit by baked probes like the player/NPCs.
            var probe = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            probe.transform.position = new Vector3(6f, 4.0f, 7f);
            probe.transform.localScale = Vector3.one * 0.7f;
            probe.GetComponent<Renderer>().sharedMaterial = PrototypeFactory.Lit(new Color(0.85f, 0.85f, 0.85f), 0.3f);
            RenderAll(cam, outDir, "normal");

            var switcher = UnityEngine.Object.FindAnyObjectByType<LightingScenarioSwitcher>();
            if (switcher != null && switcher.Has(LightingScenarioSwitcher.Outage))
            {
                switcher.Apply(LightingScenarioSwitcher.Outage);
                foreach (var m in UnityEngine.Object.FindObjectsByType<LightGroupMember>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (!m.TryGetComponent<Light>(out _))
                        m.gameObject.SetActive(m.Group != LightGroup.Normal);
                RenderAll(cam, outDir, "outage");
            }

            Debug.Log($"[Fire3D] Snapshots written to {outDir}");
        }

        private static void RenderAll(Camera cam, string outDir, string suffix)
        {
            foreach (var (name, pos, look) in Shots)
            {
                cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos));
                var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                File.WriteAllBytes(Path.Combine(outDir, $"{name}_{suffix}.png"), tex.EncodeToPNG());
                cam.targetTexture = null;
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
