using System;
using System.Collections;
using System.IO;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Unity.Content;
using Fire3D.Unity.Gameplay.Interactions;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Equipment art (Prefabs/Equipment) and sounds: every catalog object uses a real model rather than a graybox,
    /// every sound is assigned, and shots of each object where the development scenario placed it plus a line-up of
    /// all ten for a human to review.
    /// </summary>
    public class EquipmentVisualTests
    {
        private static readonly string[] Catalog =
        {
            Capabilities.ExtinguisherPortable, Capabilities.AlarmCallPoint, Capabilities.AlarmDetector, Capabilities.Sprinkler,
            Capabilities.WaterSink, Capabilities.WaterBath, Capabilities.WaterShower, Capabilities.ItemTowel,
            Capabilities.BaitBelongings, Capabilities.Elevator,
        };

        private string _dir;

        [Test]
        public void CatalogUsesEquipmentPrefabsAndSounds()
        {
            var assets = LoadAssets();
            foreach (var cap in Catalog)
            {
                var prefab = assets.PrefabFor(cap);
                Assert.IsNotNull(prefab, $"{cap}: no prefab");
                Assert.IsNull(prefab.transform.Find("Marker"), $"{cap}: still the graybox stand-in");
                Assert.Greater(prefab.GetComponentsInChildren<Renderer>().Length, 0, $"{cap}: nothing to draw");
            }

            var s = assets.Sounds;
            Assert.IsNotNull(assets.AlarmClip, "alarm");
            foreach (var (name, clip) in new (string, AudioClip)[]
                     {
                         ("fire", s.FireLoop), ("door open", s.DoorOpen), ("door close", s.DoorClose), ("door touch", s.DoorTouch),
                         ("pin", s.ExtinguisherPin), ("spray", s.ExtinguisherSpray), ("sprinkler", s.SprinklerSpray), ("tap", s.TapRunning),
                         ("cough", s.Cough), ("elevator", s.ElevatorDing), ("power cut", s.PowerCut), ("success", s.UiSuccess), ("error", s.UiError),
                     })
                Assert.IsNotNull(clip, $"sound '{name}' not assigned");
            Assert.GreaterOrEqual(s.Footsteps.Count, 2, "footsteps");
            Assert.GreaterOrEqual(s.Crawl.Count, 2, "crawl");
            Assert.GreaterOrEqual(s.DoorOpenWood.Count, 2, "wooden door open");
            Assert.GreaterOrEqual(s.DoorCloseWood.Count, 2, "wooden door close");
            Assert.IsNotNull(s.CallPointPress, "call point press");
            Assert.IsNotNull(s.FireLarge, "large fire");
            Assert.IsNotNull(s.RoomTone, "room tone");
            Assert.IsNotNull(s.Sparks, "sparks");
            Assert.GreaterOrEqual(s.Panting.Count, 1, "panting");
            Assert.IsNotNull(assets.Sky, "sky");
            foreach (var key in new[] { "floor.epoxy", "floor.vinyl", "floor.terrazzo", "floor.rubber", "wall.tile.white", "wall.paint", "metal.stainless" })
                Assert.IsTrue(assets.Materials.Entries.Exists(e => e.Key == key && e.Material != null), $"material library has no {key}");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator EquipmentLooksRightInTheBuilding()
        {
            _dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "equipment-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(_dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            yield return new WaitForSeconds(0.5f);

            // 1) In place, from where a trainee would stand.
            foreach (var it in UnityEngine.Object.FindObjectsByType<Interactable>(FindObjectsSortMode.None))
            {
                if (!Catalog.Contains(it.CapabilityId))
                    continue;
                var ceiling = it.CapabilityId is Capabilities.AlarmDetector or Capabilities.Sprinkler;
                var b = BoundsOf(it.gameObject);
                var target = b.center;
                var distance = Mathf.Clamp(b.size.magnitude * 1.3f, 0.6f, 2.2f);
                // Wall objects face -Z locally: prefer looking at that side.
                var prefer = -it.transform.forward;
                var eye = FreeView(target, prefer, distance, ceiling ? -0.6f : 0.35f);
                yield return ShootFrom(eye, target, $"inplace_{it.CapabilityId}.png");
                if (it.CapabilityId == Capabilities.Elevator)
                {
                    // Straight on from across the corridor, wide lens.
                    var front = -it.transform.forward;
                    yield return ShootFrom(target + front * 1.4f + Vector3.up * 0.2f, target, "front_transport.elevator.png", fov: 75f);
                }
            }

            // 2) All ten in a row on a plain floor outside the building.
            var assets = LoadAssets();
            var origin = new Vector3(-30f, 0f, -30f);
            // Outside the baked building there is no light: add a sun, flat ambient and a reflection probe for metals.
            var sun = new GameObject("TestSun").AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.3f;
            sun.shadows = LightShadows.Soft;
            sun.transform.rotation = Quaternion.Euler(40f, 200f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.5f);
            var probe = new GameObject("TestProbe").AddComponent<ReflectionProbe>();
            probe.transform.position = origin + Vector3.up * 1.5f;
            probe.size = new Vector3(30f, 10f, 30f);
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            probe.RenderProbe();
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.transform.position = origin;
            ground.transform.localScale = new Vector3(2f, 1f, 1f);
            var x = -6.5f;
            foreach (var cap in Catalog)
            {
                var prefab = assets.PrefabFor(cap);
                var go = UnityEngine.Object.Instantiate(prefab, origin + new Vector3(x, 0f, 0f), Quaternion.identity);
                // Lift ceiling devices to eye level so they are visible in the row.
                if (cap is Capabilities.AlarmDetector or Capabilities.Sprinkler)
                {
                    go.transform.position += Vector3.up * 1.2f;
                    go.transform.localScale = Vector3.one * 4f;
                }

                var b = go.GetComponentsInChildren<Renderer>().Select(r => r.bounds).Aggregate((a, c) => { a.Encapsulate(c); return a; });
                Debug.Log($"[equipment] {cap}: size {b.size.x:F2} x {b.size.y:F2} x {b.size.z:F2} m, " +
                          $"{go.GetComponentsInChildren<MeshFilter>().Sum(m => m.sharedMesh != null ? m.sharedMesh.triangles.Length / 3 : 0)} triangles");
                x += 1.45f;
            }

            yield return new WaitForSeconds(0.3f);
            yield return null;
            yield return ShootFrom(origin + new Vector3(0f, 1.9f, -8.5f), origin + Vector3.up * 0.8f, "lineup_all.png", 1800, 640);
        }

        private static RuntimeAssets LoadAssets()
        {
            var assets = Resources.FindObjectsOfTypeAll<RuntimeAssets>().FirstOrDefault();
#if UNITY_EDITOR
            if (assets == null)
                assets = UnityEditor.AssetDatabase.LoadAssetAtPath<RuntimeAssets>("Assets/_Fire3D/Settings/RuntimeAssets.asset");
#endif
            return assets ?? throw new AssertionException("RuntimeAssets not found");
        }

        private static Bounds BoundsOf(GameObject go)
        {
            var renderers = go.GetComponentsInChildren<Renderer>();
            var b = renderers.Length > 0 ? renderers[0].bounds : new Bounds(go.transform.position, Vector3.one * 0.3f);
            foreach (var r in renderers)
                b.Encapsulate(r.bounds);
            return b;
        }

        /// <summary>
        /// A camera spot <paramref name="distance"/> from the target that walls do not block: tries sixteen headings,
        /// keeps the one with the most free room, favouring <paramref name="prefer"/>.
        /// </summary>
        private static Vector3 FreeView(Vector3 target, Vector3 prefer, float distance, float lift)
        {
            prefer.y = 0f;
            prefer = prefer.sqrMagnitude > 1e-4f ? prefer.normalized : Vector3.back;
            var best = target + prefer * distance;
            var bestScore = float.MinValue;
            for (var k = 0; k < 16; k++)
            {
                var dir = Quaternion.Euler(0f, k * 22.5f, 0f) * prefer;
                var to = (dir * distance + Vector3.up * lift).normalized;
                var free = Physics.Raycast(target, to, out var hit, distance + 0.3f) ? hit.distance - 0.2f : distance;
                var score = Mathf.Min(free, distance) + Vector3.Dot(dir, prefer) * 0.4f;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = target + to * Mathf.Max(0.35f, Mathf.Min(free, distance));
                }
            }

            return best;
        }

        /// <summary>Renders by hand into a texture: no WaitForEndOfFrame, which never fires in batchmode.</summary>
        private IEnumerator ShootFrom(Vector3 eye, Vector3 target, string file, int w = 900, int h = 700, float fov = 45f)
        {
            yield return null;
            var go = new GameObject("ShotCam");
            var cam = go.AddComponent<Camera>();
            var main = Camera.main;
            if (main != null)
                cam.CopyFrom(main);
            cam.fieldOfView = fov;
            cam.nearClipPlane = 0.03f;
            go.transform.position = eye;
            go.transform.LookAt(target);
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
