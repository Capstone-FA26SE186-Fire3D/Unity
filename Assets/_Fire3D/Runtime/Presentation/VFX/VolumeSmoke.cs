using System.Collections.Generic;
using Fire3D.Simulation;
using Unity.Mathematics;
using UnityEngine;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// One particle system that draws the whole smoke field as a volume (the way CS2 draws its smoke): every frame it
    /// tops up puffs in the grid cells that hold smoke, in proportion to their density, so the cloud has the exact
    /// shape of the simulated field: it fills a room, squeezes through an open door, climbs a stairwell and thins out
    /// at its edges. Each puff drifts along the density gradient (dense → thin, i.e. through gaps), rises while the
    /// cell is hot, and shrinks to the free space around it so no sprite is ever wider than the gap it sits in.
    /// Puffs that end up in a cell that cleared fade out early. No emitters, no per-emitter balls.
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public sealed class VolumeSmoke : MonoBehaviour
    {
        [SerializeField] private float viewRadius = 22f;
        [SerializeField] private float densityThreshold = 0.03f;
        [SerializeField] private float puffsPerCubicMetrePerSecond = 4.5f;
        [SerializeField] private float puffLifeMin = 5f;
        [SerializeField] private float puffLifeMax = 8f;
        [SerializeField] private float puffSizeMin = 0.9f;
        [SerializeField] private float puffSizeMax = 1.6f;
        [SerializeField] private float driftSpeed = 0.45f;
        [SerializeField] private float riseSpeed = 0.35f;

        private ParticleSystem _ps;
        private FireSimulation _sim;
        private Transform _viewer;
        private ParticleSystem.Particle[] _buffer = new ParticleSystem.Particle[1024];
        private readonly List<(int cell, float density)> _cells = new(512);
        private float _carry;
        private int _maxParticles = 600;

        public int Alive => _ps != null ? _ps.particleCount : 0;

        public void Bind(FireSimulation simulation, Transform viewer, int maxParticles)
        {
            _sim = simulation;
            _viewer = viewer;
            _maxParticles = maxParticles;
            _ps = GetComponent<ParticleSystem>();
            var main = _ps.main;
            main.maxParticles = maxParticles;
            main.loop = false;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var emission = _ps.emission;
            emission.enabled = false;
            var shape = _ps.shape;
            shape.enabled = false;
            if (_buffer.Length < maxParticles)
                _buffer = new ParticleSystem.Particle[maxParticles];
            _ps.Play(true);
        }

        private void LateUpdate()
        {
            if (_sim == null || _viewer == null)
                return;
            CollectCells();
            Steer(Time.deltaTime);
            Spawn(Time.deltaTime);
        }

        /// <summary>Smoky cells on the viewer's floor within view radius, with their density.</summary>
        private void CollectCells()
        {
            _cells.Clear();
            var grid = _sim.Grid;
            var layout = grid.Layout;
            var viewer = (float3)_viewer.position;
            var floor = layout.FloorAt(viewer.y);
            var r = (int)math.ceil(viewRadius / layout.CellSize);
            var c = layout.Decode(layout.CellAtClamped(viewer));
            for (var dz = -r; dz <= r; dz++)
            for (var dx = -r; dx <= r; dx++)
            {
                if (!layout.InBounds(floor, c.x + dx, c.z + dz))
                    continue;
                var i = layout.Index(floor, c.x + dx, c.z + dz);
                var s = grid.Smoke[i];
                if (s > densityThreshold && ((CellFlags)grid.Flags[i] & CellFlags.Wall) == 0)
                    _cells.Add((i, s));
            }
        }

        /// <summary>
        /// Keep the particle count proportional to the smoke volume. Each cell gets puffs by density; a cell with
        /// nothing yet gets priority so the cloud's outline fills in quickly.
        /// </summary>
        private void Spawn(float dt)
        {
            if (_cells.Count == 0)
                return;
            var grid = _sim.Grid;
            var layout = grid.Layout;
            var cellVolume = layout.CellSize * layout.CellSize * 2.4f; // ~ room height of smoke layer
            var total = 0f;
            foreach (var (_, d) in _cells)
                total += Mathf.Sqrt(d);
            var wanted = total * cellVolume * puffsPerCubicMetrePerSecond * dt + _carry;
            var count = Mathf.FloorToInt(wanted);
            _carry = wanted - count;
            var room = _maxParticles - _ps.particleCount;
            count = Mathf.Min(count, room);
            if (count <= 0)
                return;

            var emit = new ParticleSystem.EmitParams();
            for (var k = 0; k < count; k++)
            {
                // Density-weighted pick.
                var pick = UnityEngine.Random.value * total;
                var acc = 0f;
                var cell = _cells[_cells.Count - 1].cell;
                var density = _cells[_cells.Count - 1].density;
                foreach (var (ci, d) in _cells)
                {
                    acc += Mathf.Sqrt(d);
                    if (acc >= pick)
                    {
                        cell = ci;
                        density = d;
                        break;
                    }
                }

                var centre = (Vector3)layout.CellCenter(cell);
                var half = layout.CellSize * 0.35f;
                // Smoke layer: hot smoke lives under the ceiling; thin smoke hangs lower and spreads.
                // The simulated layer: smoke hangs from the ceiling and the layer creeps down over time.
                var top = centre.y + HazardGrid.CeilingHeight - 0.35f;
                var bottom = Mathf.Max(centre.y + 0.3f, grid.LayerBottom(cell));
                var y = UnityEngine.Random.Range(Mathf.Min(bottom, top - 0.2f), top);
                // Fresh smoke is pale grey-white; it darkens to sooty black over about a minute.
                var age = Mathf.Clamp01(grid.SmokeAge[cell] / 60f);
                var tint = Color.Lerp(new Color(0.86f, 0.84f, 0.8f), new Color(0.22f, 0.2f, 0.19f), age * age * (3f - 2f * age));
                var pos = new Vector3(centre.x + UnityEngine.Random.Range(-half, half), y, centre.z + UnityEngine.Random.Range(-half, half));
                // Thin smoke spreads as larger, fainter wisps (a haze layer); dense smoke as tighter, darker billows.
                var thinness = 1f - Mathf.Clamp01(density * 2f);
                var size = UnityEngine.Random.Range(puffSizeMin, puffSizeMax) * (1f + 0.6f * thinness);
                // A puff is a billboard of diameter `size`: it must not reach into a wall or a clean cell. Shrink it
                // to twice the nearest wall distance, and skip it when the gap is too small for a readable puff.
                var gap = NearestWallDistance(pos) * 2f;
                if (gap < 0.45f)
                    continue;
                size = Mathf.Min(size, gap);
                emit.position = pos;
                emit.startSize = size;
                emit.startLifetime = UnityEngine.Random.Range(puffLifeMin, puffLifeMax);
                emit.rotation = UnityEngine.Random.Range(0f, 360f);
                emit.angularVelocity = UnityEngine.Random.Range(-8f, 8f);
                emit.startColor = new Color(tint.r, tint.g, tint.b, Mathf.Lerp(0.25f, 0.85f, Mathf.Clamp01(density)));
                emit.velocity = Vector3.zero;
                _ps.Emit(emit, 1);
            }
        }

        /// <summary>Drift every puff down the density gradient (dense → thin, through gaps) and up while its cell is hot.</summary>
        private void Steer(float dt)
        {
            var n = _ps.GetParticles(_buffer);
            if (n == 0)
                return;
            var grid = _sim.Grid;
            var layout = grid.Layout;
            var step = layout.CellSize;
            for (var k = 0; k < n; k++)
            {
                ref var p = ref _buffer[k];
                var pos = (float3)p.position;
                if (!layout.TryCellAt(pos, out var cell))
                {
                    p.remainingLifetime = Mathf.Min(p.remainingLifetime, 0.4f);
                    continue;
                }

                var here = grid.Smoke[cell];
                var flags = (CellFlags)grid.Flags[cell];
                if ((flags & CellFlags.Wall) != 0 || here < 0.015f)
                {
                    // Centre crossed into a wall or clean air (a puff on a corridor wall in front of a closed door is
                    // exactly this): kill it now; the spawner refills the field where the smoke actually is.
                    p.remainingLifetime = 0f;
                    continue;
                }

                if (here < densityThreshold * 0.5f)
                    p.remainingLifetime = Mathf.Min(p.remainingLifetime, 0.8f); // thinning out: dissolve soon

                var gx = Density(grid, layout, pos + new float3(step, 0, 0)) - Density(grid, layout, pos - new float3(step, 0, 0));
                var gz = Density(grid, layout, pos + new float3(0, 0, step)) - Density(grid, layout, pos - new float3(0, 0, step));
                var drift = new Vector3(-gx, 0f, -gz);
                if (drift.sqrMagnitude > 1e-6f)
                    drift = drift.normalized * driftSpeed * Mathf.Clamp01(drift.magnitude * 4f);
                var hot = Mathf.Clamp01((grid.Heat[cell] - 40f) / 200f);
                var layerBottom = grid.LayerBottom(cell);
                var ceiling = layerBottom + grid.SmokeLayer[cell];
                // Buoyancy inside the layer: rise while hot, settle gently to the layer bottom otherwise; never leave the layer.
                var vertical = p.position.y < layerBottom ? 0.5f : p.position.y > ceiling - 0.3f ? -0.1f : riseSpeed * (0.1f + hot);
                var target = drift + Vector3.up * vertical;
                p.velocity = Vector3.Lerp(p.velocity, target, 1f - Mathf.Exp(-1.5f * dt));

                // A puff only moves where the simulated smoke is: the field already stops at walls and closed doors
                // (and leaks through gaps), so a puff that would enter a clean cell stays put instead of crossing.
                var ahead = pos + (float3)(p.velocity * (dt * 4f + 0.15f));
                if (!layout.TryCellAt(ahead, out var aheadCell) || grid.Smoke[aheadCell] < densityThreshold * 0.5f
                    || ((CellFlags)grid.Flags[aheadCell] & CellFlags.Wall) != 0)
                {
                    p.velocity = new Vector3(0f, p.velocity.y, 0f);
                    // Pull back into the cell's centre so it cannot sit on a wall face.
                    var c = (Vector3)layout.CellCenter(cell);
                    var inward = new Vector3(c.x - p.position.x, 0f, c.z - p.position.z);
                    p.position += inward * Mathf.Min(1f, dt * 2f);
                }

                // Keep the puff out of the walls: its radius may never exceed the distance to the nearest wall.
                var cap = NearestWallDistance(p.position) * 2f;
                if (p.startSize > cap)
                    p.startSize = Mathf.Max(0.3f, cap);
            }

            _ps.SetParticles(_buffer, n);
        }

        private static float Density(HazardGrid grid, Fire3D.Simulation.GridLayout layout, float3 at)
        {
            if (!layout.TryCellAt(at, out var i))
                return 1f; // outside the grid counts as "solid": push away
            return ((CellFlags)grid.Flags[i] & CellFlags.Wall) != 0 ? 1f : grid.Smoke[i];
        }

        private static readonly Vector3[] RayDirs = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

        /// <summary>Distance from <paramref name="at"/> to the nearest wall along the grid axes (metres, capped at 3).</summary>
        private static float NearestWallDistance(Vector3 at)
        {
            var best = 3f;
            foreach (var dir in RayDirs)
            {
                if (Physics.Raycast(at, dir, out var hit, best, ~0, QueryTriggerInteraction.Ignore) && !(hit.collider is CharacterController))
                    best = hit.distance;
            }

            return best;
        }
    }
}
