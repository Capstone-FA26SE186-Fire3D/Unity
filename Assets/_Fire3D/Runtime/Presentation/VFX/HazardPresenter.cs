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
        private int maxFires => FireQuality.MaxEmitters;
        private int maxSmoke => FireQuality.MaxSmoke;
        private int maxFireLights => FireQuality.MaxLights;

        /// <summary>Flames within 2.5 m of a light that make it a large fire (louder roar instead of crackle).</summary>
        [SerializeField] private int largeFireFlames = 4;
        [SerializeField] private float viewRadius = 22f;
        [SerializeField] private float refreshSeconds = 0.25f;
        [SerializeField] private Color clearFog = new(0.62f, 0.64f, 0.66f);
        [SerializeField] private Color smokeFog = new(0.18f, 0.17f, 0.16f);

        private readonly List<ParticleSystem> _fires = new();
        private VolumeSmoke _volumeSmoke;
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

            EnsureVolumeSmoke();
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
                var value = fire ? grid.Fire[i] : grid.Smoke[i];
                float target;
                if (fire)
                {
                    ps.transform.position = center;
                    // Hierarchy scaling drives size and emission of every layer (flame, core, embers, glow, haze).
                    target = Mathf.Lerp(0.55f, 1.4f, value);
                }
                else
                {
                    // Jitter inside the cell, deterministic per cell so an emitter does not drift between refreshes.
                    var jx = Mathf.PerlinNoise(i * 0.173f, 0.37f) - 0.5f;
                    var jz = Mathf.PerlinNoise(0.61f, i * 0.131f) - 0.5f;
                    var at = center + new Vector3(jx, 0f, jz) * grid.Layout.CellSize * 1.6f;
                    // Hot smoke collects under the ceiling: the denser the cell, the higher the emitter.
                    at.y += Mathf.Lerp(1.4f, 2.4f, Mathf.Clamp01(value));
                    ps.transform.position = at;
                    // Free width around the emitter: a sprite wider than the room gets sliced flat by the walls, so
                    // scale the whole system to the clearance (0.8 of the narrowest free span). 2.2 m = sprite size at scale 1.
                    var sizeCap = Mathf.Clamp(FreeSpan(at) * 0.8f / 2.2f, 0.45f, 1.25f);
                    target = Mathf.Min(Mathf.Lerp(0.75f, 1.25f, value), sizeCap);
                    // Thin smoke stays see-through so billows show; dense smoke builds up to near opaque.
                    var sm = ps.main;
                    sm.startColor = new Color(0.8f, 0.78f, 0.76f, Mathf.Lerp(0.5f, 1f, Mathf.Clamp01(value)));
                }
                var current = ps.transform.localScale.x;
                ps.transform.localScale = Vector3.one * Mathf.Lerp(current < 0.01f ? target : current, target, 0.35f);
                if (fire)
                    ps.transform.rotation = Quaternion.identity;
                SetHaze(ps, fire && k < FireQuality.MaxHazeEmitters);
                if (!ps.isEmitting)
                    ps.Play(true);
            }
        }

        /// <summary>
        /// The smoke field is drawn by one <see cref="VolumeSmoke"/> that spawns puffs straight into the smoky grid
        /// cells (its material, flipbook and vertex streams come from the smoke prefab), instead of balls around emitters.
        /// </summary>
        private void EnsureVolumeSmoke()
        {
            if (_volumeSmoke != null)
                return;
            var prefab = _assets?.SmokePrefab;
            var ps = prefab != null ? Instantiate(prefab, transform) : VfxFactory.Create(false, transform);
            ps.gameObject.name = "SmokeVolume";
            ps.gameObject.SetActive(true);
            ps.transform.localScale = Vector3.one;
            _volumeSmoke = ps.gameObject.AddComponent<VolumeSmoke>();
            _volumeSmoke.Bind(_sim, _viewer, FireQuality.MaxSmoke * 100); // Low 800, Medium 1400, High 2000 puffs
        }

        private static readonly Vector3[] SpanDirs = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

        /// <summary>Narrowest wall-to-wall distance through <paramref name="at"/> along the grid axes (meters, capped at 8).</summary>
        private static float FreeSpan(Vector3 at)
        {
            var best = 8f;
            for (var axis = 0; axis < 2; axis++)
            {
                var span = 0f;
                for (var side = 0; side < 2; side++)
                {
                    var hitWall = Physics.Raycast(at, SpanDirs[axis * 2 + side], out var hit, 4f, ~0, QueryTriggerInteraction.Ignore)
                                  && !(hit.collider is CharacterController);
                    span += hitWall ? hit.distance : 4f;
                }

                best = Mathf.Min(best, span);
            }

            return best;
        }

        private bool _anyHaze;

        private void SetHaze(ParticleSystem root, bool on)
        {
            var haze = root.transform.Find(VfxFactory.HazeLayer);
            if (haze == null || haze.gameObject.activeSelf == on)
                return;
            haze.gameObject.SetActive(on);
        }

        private ParticleSystem CreateEmitter(bool fire)
        {
            var prefab = fire ? _assets?.FirePrefab : _assets?.SmokePrefab;
            var ps = prefab != null ? Instantiate(prefab, transform) : VfxFactory.Create(fire, transform);
            ps.gameObject.SetActive(true);
            return ps;
        }

        private readonly List<int> _lightSource = new();
        private readonly List<float> _lightPhase = new();

        private void AssignLights()
        {
            while (_fireLights.Count < maxFireLights)
            {
                var go = new GameObject("FireLight");
                go.transform.SetParent(transform, false);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.52f, 0.18f);
                l.range = 8f;
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.ForcePixel;
                l.enabled = false;
                // The crackle follows the light: one source per cluster of flames.
                GameAudio.Loop(GameAudio.Sounds?.FireLoop, go, 0.8f, 16f);
                _fireLights.Add(l);
                _lightPhase.Add(UnityEngine.Random.value * 100f);
            }

            // One light per cluster of flames: the strongest emitters, at least 2.5 m apart, so the glow spreads over a
            // room instead of stacking on one spot.
            _lightSource.Clear();
            for (var f = 0; f < _fires.Count && _lightSource.Count < _fireLights.Count; f++)
            {
                if (!_fires[f].isEmitting)
                    continue;
                var p = _fires[f].transform.position;
                var apart = true;
                foreach (var other in _lightSource)
                    if ((_fires[other].transform.position - p).sqrMagnitude < 2.5f * 2.5f)
                        apart = false;
                if (apart)
                    _lightSource.Add(f);
            }

            var anyHaze = false;
            for (var k = 0; k < _fireLights.Count; k++)
            {
                var on = k < _lightSource.Count;
                _fireLights[k].enabled = on;
                if (_fireLights[k].TryGetComponent<AudioSource>(out var crackle))
                {
                    // A cluster of many flames roars; a small fire crackles.
                    var clip = on && ClusterSize(_lightSource[k]) >= largeFireFlames && GameAudio.Sounds?.FireLarge != null
                        ? GameAudio.Sounds.FireLarge
                        : GameAudio.Sounds?.FireLoop;
                    if (on && clip != null && crackle.clip != clip)
                    {
                        crackle.clip = clip;
                        crackle.time = UnityEngine.Random.Range(0f, clip.length * 0.9f);
                        crackle.Play();
                    }
                    else if (crackle.isPlaying != on)
                    {
                        if (on)
                            crackle.Play();
                        else
                            crackle.Stop();
                    }
                }
                if (on)
                    _fireLights[k].transform.position = _fires[_lightSource[k]].transform.position + Vector3.up * 0.9f;
            }

            for (var f = 0; f < _fires.Count && !anyHaze; f++)
            {
                var haze = _fires[f].transform.Find(VfxFactory.HazeLayer);
                anyHaze = haze != null && haze.gameObject.activeSelf && _fires[f].isEmitting;
            }

            if (anyHaze != _anyHaze)
            {
                _anyHaze = anyHaze;
                FireQuality.RequestOpaqueTexture(anyHaze);
            }
        }

        private int ClusterSize(int source)
        {
            var p = _fires[source].transform.position;
            var count = 0;
            foreach (var f in _fires)
                if (f.isEmitting && (f.transform.position - p).sqrMagnitude < 2.5f * 2.5f)
                    count++;
            return count;
        }

        private void FlickerLights()
        {
            var t = Time.time;
            for (var k = 0; k < _fireLights.Count; k++)
            {
                var l = _fireLights[k];
                if (!l.enabled)
                    continue;
                // Slow swell + quick shimmer, never a hard on/off: reads as a living fire on the walls.
                var ph = _lightPhase[k];
                var swell = Mathf.PerlinNoise(t * 1.3f, ph);
                var shimmer = Mathf.PerlinNoise(t * 9f, ph + 7.7f);
                var strength = _sim.Grid.Fire[_sim.Grid.Layout.CellAtClamped(l.transform.position)];
                var scale = Mathf.Lerp(0.8f, 1.4f, Mathf.Clamp01(strength));
                var target = (2.4f + swell * 2.2f + shimmer * 0.9f) * scale;
                l.intensity = Mathf.Lerp(l.intensity, target, 1f - Mathf.Exp(-14f * Time.deltaTime));
                l.range = 7f + swell * 1.6f;
                l.color = Color.Lerp(new Color(1f, 0.42f, 0.12f), new Color(1f, 0.64f, 0.26f), shimmer);
            }
        }

        private void OnDestroy()
        {
            FireQuality.RequestOpaqueTexture(false);
            RenderSettings.fogDensity = 0.002f;
        }
    }
}
