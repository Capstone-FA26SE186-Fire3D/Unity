using System.Collections.Generic;
using Fire3D.Simulation;
using Fire3D.Unity.Content;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// Draws the hazard field near the player: pooled flame and smoke emitters on burning/smoky cells, flickering
    /// fire light, and exponential fog driven by the smoke at the camera. Reads the grid after the simulation step;
    /// never writes simulation state.
    /// </summary>
    public sealed class HazardPresenter : MonoBehaviour
    {
        [SerializeField] private int maxFires = 40;
        [SerializeField] private int maxSmoke = 36;
        [SerializeField] private int maxFireLights = 6;
        [SerializeField] private float viewRadius = 22f;
        [SerializeField] private float refreshSeconds = 0.25f;
        [SerializeField] private Color clearFog = new(0.62f, 0.64f, 0.66f);
        [SerializeField] private Color smokeFog = new(0.18f, 0.17f, 0.16f);

        private readonly List<ParticleSystem> _fires = new();
        private readonly List<ParticleSystem> _smokes = new();
        private readonly List<Light> _fireLights = new();
        private readonly List<(float score, int cell)> _candidates = new();
        private FireSimulation _sim;
        private Transform _viewer;
        private RuntimeAssets _assets;
        private float _nextRefresh;
        private float _fogDensity;

        public void Bind(FireSimulation simulation, Transform viewer, RuntimeAssets assets)
        {
            _sim = simulation;
            _viewer = viewer;
            _assets = assets;
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = clearFog;
            RenderSettings.fogDensity = 0.002f;
        }

        private void LateUpdate()
        {
            if (_sim == null || _viewer == null)
                return;
            UpdateFog();
            if (Time.time >= _nextRefresh)
            {
                _nextRefresh = Time.time + refreshSeconds;
                RefreshEmitters();
            }

            FlickerLights();
        }

        private void UpdateFog()
        {
            var cam = UnityEngine.Camera.main != null ? UnityEngine.Camera.main.transform.position : _viewer.position;
            var atCamera = _sim.Grid.Sample(new float3(cam.x, _viewer.position.y + 0.2f, cam.z));
            var atPlayer = _sim.Grid.Sample(_viewer.position);
            var smoke = Mathf.Max(atCamera.Valid ? atCamera.Smoke : 0f, atPlayer.Valid ? atPlayer.Smoke : 0f);
            var target = Mathf.Lerp(0.002f, 0.32f, Mathf.Pow(smoke, 1.3f));
            _fogDensity = Mathf.Lerp(_fogDensity, target, 1f - Mathf.Exp(-3f * Time.deltaTime));
            RenderSettings.fogDensity = _fogDensity;
            RenderSettings.fogColor = Color.Lerp(clearFog, smokeFog, Mathf.Clamp01(smoke * 1.4f));
        }

        private void RefreshEmitters()
        {
            var grid = _sim.Grid;
            var layout = grid.Layout;
            var viewer = (float3)_viewer.position;
            var floor = layout.FloorAt(viewer.y);
            var r = (int)math.ceil(viewRadius / layout.CellSize);
            var c = layout.Decode(layout.CellAtClamped(viewer));

            _candidates.Clear();
            for (var dz = -r; dz <= r; dz += 2)
            for (var dx = -r; dx <= r; dx += 2)
            {
                if (!layout.InBounds(floor, c.x + dx, c.z + dz))
                    continue;
                var i = layout.Index(floor, c.x + dx, c.z + dz);
                if (grid.Fire[i] > 0.12f)
                    _candidates.Add((grid.Fire[i] * 10f - math.length(new float2(dx, dz)) * 0.05f, i));
            }

            _candidates.Sort((a, b) => b.score.CompareTo(a.score));
            Assign(_fires, maxFires, _candidates, true);

            _candidates.Clear();
            for (var dz = -r; dz <= r; dz += 4)
            for (var dx = -r; dx <= r; dx += 4)
            {
                if (!layout.InBounds(floor, c.x + dx, c.z + dz))
                    continue;
                var i = layout.Index(floor, c.x + dx, c.z + dz);
                if (grid.Smoke[i] > 0.18f && grid.Fire[i] < 0.5f)
                    _candidates.Add((grid.Smoke[i] - math.length(new float2(dx, dz)) * 0.01f, i));
            }

            _candidates.Sort((a, b) => b.score.CompareTo(a.score));
            Assign(_smokes, maxSmoke, _candidates, false);
            AssignLights();
        }

        private void Assign(List<ParticleSystem> pool, int max, List<(float score, int cell)> cells, bool fire)
        {
            var grid = _sim.Grid;
            var count = Mathf.Min(max, cells.Count);
            while (pool.Count < count)
                pool.Add(CreateEmitter(fire));

            for (var k = 0; k < pool.Count; k++)
            {
                var ps = pool[k];
                if (k >= count)
                {
                    if (ps.isEmitting)
                        ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    continue;
                }

                var i = cells[k].cell;
                var center = (Vector3)grid.Layout.CellCenter(i);
                ps.transform.position = fire ? center : center + Vector3.up * 1.6f;
                var emission = ps.emission;
                var value = fire ? grid.Fire[i] : grid.Smoke[i];
                emission.rateOverTimeMultiplier = fire ? Mathf.Lerp(8f, 45f, value) : Mathf.Lerp(2f, 10f, value);
                var main = ps.main;
                main.startSizeMultiplier = fire ? Mathf.Lerp(0.35f, 1.1f, value) : Mathf.Lerp(1.4f, 2.6f, value);
                if (!ps.isEmitting)
                    ps.Play(true);
            }
        }

        private ParticleSystem CreateEmitter(bool fire)
        {
            var prefab = fire ? _assets?.FirePrefab : _assets?.SmokePrefab;
            var ps = prefab != null ? Instantiate(prefab, transform) : VfxFactory.Create(fire, transform);
            ps.gameObject.SetActive(true);
            return ps;
        }

        private void AssignLights()
        {
            while (_fireLights.Count < maxFireLights)
            {
                var go = new GameObject("FireLight");
                go.transform.SetParent(transform, false);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.55f, 0.2f);
                l.range = 7f;
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.ForcePixel;
                l.enabled = false;
                _fireLights.Add(l);
            }

            var grid = _sim.Grid;
            for (var k = 0; k < _fireLights.Count; k++)
            {
                var on = k < _fires.Count && _fires[k].isEmitting && k * 6 < _fires.Count + 6;
                _fireLights[k].enabled = on;
                if (on)
                    _fireLights[k].transform.position = _fires[k * Mathf.Max(1, _fires.Count / maxFireLights)].transform.position + Vector3.up * 0.8f;
            }
        }

        private void FlickerLights()
        {
            var t = Time.time;
            for (var k = 0; k < _fireLights.Count; k++)
            {
                var l = _fireLights[k];
                if (!l.enabled)
                    continue;
                l.intensity = 2.2f + Mathf.PerlinNoise(t * 7f, k * 3.1f) * 2.4f;
            }
        }

        private void OnDestroy()
        {
            RenderSettings.fogDensity = 0.002f;
        }
    }

    /// <summary>Builds flame/smoke particle systems with URP particle materials when no prefab is provided.</summary>
    public static class VfxFactory
    {
        public static ParticleSystem Create(bool fire, Transform parent)
        {
            var go = new GameObject(fire ? "Flame" : "Smoke");
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = fire ? 80 : 40;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = fire ? 0.35f : 0.8f;
            shape.rotation = new Vector3(-90f, 0f, 0f);
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var size = ps.sizeOverLifetime;
            size.enabled = true;

            if (fire)
            {
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.45f, 0.9f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 1.8f);
                main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
                main.startColor = new Color(1f, 0.6f, 0.2f, 1f);
                main.gravityModifier = -0.15f;
                col.color = new ParticleSystem.MinMaxGradient(Gradient(
                    new Color(1f, 0.85f, 0.4f), new Color(1f, 0.45f, 0.08f), new Color(0.5f, 0.12f, 0.02f), 0.9f, 0f));
                size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.3f, 1f), new Keyframe(1f, 0.1f)));
            }
            else
            {
                main.startLifetime = new ParticleSystem.MinMaxCurve(3.5f, 6f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.1f, 0.35f);
                main.startSize = new ParticleSystem.MinMaxCurve(1.4f, 2.4f);
                main.startColor = new Color(0.22f, 0.21f, 0.2f, 0.55f);
                main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
                col.color = new ParticleSystem.MinMaxGradient(Gradient(
                    new Color(0.3f, 0.29f, 0.28f), new Color(0.2f, 0.19f, 0.18f), new Color(0.15f, 0.15f, 0.15f), 0.55f, 0f));
                size.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0f, 0.5f), new Keyframe(1f, 1.6f)));
            }

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sharedMaterial = ParticleMaterial(fire);
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return ps;
        }

        private static Material _fireMat;
        private static Material _smokeMat;

        public static Material ParticleMaterial(bool fire)
        {
            ref var cached = ref fire ? ref _fireMat : ref _smokeMat;
            if (cached != null)
                return cached;
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit") ?? Shader.Find("Particles/Standard Unlit");
            var m = new Material(shader) { name = fire ? "FlameParticles" : "SmokeParticles" };
            m.SetTexture("_BaseMap", SoftDot());
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", fire ? 2f : 0f); // Additive for flames, alpha for smoke.
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", fire ? (int)BlendMode.One : (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            cached = m;
            return m;
        }

        private static Texture2D _dot;

        private static Texture2D SoftDot()
        {
            if (_dot != null)
                return _dot;
            const int n = 64;
            _dot = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, name = "SoftDot" };
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var d = Vector2.Distance(new Vector2(x, y), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                var a = Mathf.Clamp01(1f - d);
                a = a * a * (3f - 2f * a);
                _dot.SetPixel(x, y, new Color(1f, 1f, 1f, a));
            }

            _dot.Apply();
            return _dot;
        }

        private static Gradient Gradient(Color a, Color b, Color c, float alphaStart, float alphaEnd)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(a, 0f), new GradientColorKey(b, 0.45f), new GradientColorKey(c, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(alphaStart, 0.12f), new GradientAlphaKey(alphaEnd, 1f) });
            return g;
        }
    }
}
