using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Fire3D.Simulation
{
    /// <summary>Hazard state sampled at a point, used by gameplay, HUD and VFX.</summary>
    public struct HazardSample
    {
        public float Fire;
        public float Smoke;
        public float HeatC;
        public float Visibility;
        public CellFlags Flags;
        public bool Valid;
        /// <summary>World Y of the underside of the smoke layer (ceiling when clear).</summary>
        public float LayerBottomY;
        /// <summary>Seconds the cell has held smoke (0 = none).</summary>
        public float SmokeAgeSeconds;
    }

    /// <summary>
    /// Double-buffered hazard field. Owned by the simulation world; jobs read the current buffers and write the next.
    /// Main-thread readers must only call it after the simulation step completed (the step completes its jobs).
    /// </summary>
    public sealed class HazardGrid : IDisposable
    {
        public GridLayout Layout;

        public NativeArray<byte> Flags;
        public NativeArray<float> Fuel;
        public NativeArray<float> Fire;
        public NativeArray<float> Heat;
        public NativeArray<float> Smoke;

        /// <summary>Thickness of the smoke layer hanging from the ceiling (m). Hot smoke pools under the ceiling and the
        /// layer creeps down as more smoke arrives; it lifts again as the cell vents.</summary>
        public NativeArray<float> SmokeLayer;

        /// <summary>Seconds this cell has held smoke. Fresh smoke is pale (water vapour, early pyrolysis); it darkens
        /// with age as soot builds up.</summary>
        public NativeArray<float> SmokeAge;

        /// <summary>Clear height of a storey (slab to ceiling) used for the layer model.</summary>
        public const float CeilingHeight = 3.0f;
        public const float MaxLayerThickness = 2.6f;
        /// <summary>Suppression rate per second (sprinklers, extinguisher bursts).</summary>
        public NativeArray<float> Suppression;
        /// <summary>Wind vector per cell in cells/second (XZ).</summary>
        public NativeArray<float2> Wind;

        public NativeArray<float> FireNext;
        public NativeArray<float> HeatNext;
        public NativeArray<float> SmokeNext;
        public NativeArray<float> FuelNext;

        public HazardGrid(GridLayout layout, float ambientC)
        {
            Layout = layout;
            var n = layout.CellCount;
            Flags = new NativeArray<byte>(n, Allocator.Persistent);
            Fuel = new NativeArray<float>(n, Allocator.Persistent);
            Fire = new NativeArray<float>(n, Allocator.Persistent);
            Heat = new NativeArray<float>(n, Allocator.Persistent);
            Smoke = new NativeArray<float>(n, Allocator.Persistent);
            Suppression = new NativeArray<float>(n, Allocator.Persistent);
            Wind = new NativeArray<float2>(n, Allocator.Persistent);
            FireNext = new NativeArray<float>(n, Allocator.Persistent);
            HeatNext = new NativeArray<float>(n, Allocator.Persistent);
            SmokeNext = new NativeArray<float>(n, Allocator.Persistent);
            FuelNext = new NativeArray<float>(n, Allocator.Persistent);
            SmokeLayer = new NativeArray<float>(n, Allocator.Persistent);
            SmokeAge = new NativeArray<float>(n, Allocator.Persistent);
            for (var i = 0; i < n; i++)
                Heat[i] = ambientC;
        }

        /// <summary>
        /// Advances the layer model for one step (call after SwapBuffers). Thickness grows with the smoke present
        /// (a dense cell fills ~2.6 m in about 90 s) and shrinks when the cell clears; age counts up while smoke is
        /// present and resets when it is gone.
        /// </summary>
        public void StepLayer(float dt)
        {
            var n = CellCount;
            for (var i = 0; i < n; i++)
            {
                var s = Smoke[i];
                var layer = SmokeLayer[i];
                if (s > 0.02f)
                {
                    // Smoke keeps arriving at the ceiling while the cell is smoky, so the layer grows with the
                    // accumulated smoke (a dense cell fills the 2.6 m in roughly a minute and a half; a thin haze much
                    // more slowly), and never thins while smoke is still present.
                    layer = math.min(MaxLayerThickness, layer + dt * (0.012f + 0.035f * math.saturate(s * 2f)));
                    SmokeAge[i] += dt;
                }
                else
                {
                    layer = math.max(0f, layer - dt * 0.15f);
                    if (layer <= 0.01f)
                        SmokeAge[i] = 0f;
                }

                SmokeLayer[i] = layer;
            }
        }

        /// <summary>Bottom of the smoke layer above cell <paramref name="i"/> in world Y (ceiling when there is no smoke).</summary>
        public float LayerBottom(int i)
        {
            var c = Layout.Decode(i);
            return Layout.Origin.y + Layout.ElevationOf(c.y) + CeilingHeight - SmokeLayer[i];
        }

        public int CellCount => Layout.CellCount;

        public void SetFlags(int index, CellFlags flags) => Flags[index] = (byte)flags;
        public CellFlags GetFlags(int index) => (CellFlags)Flags[index];

        public void AddFlags(int index, CellFlags flags) => Flags[index] = (byte)(Flags[index] | (byte)flags);
        public void RemoveFlags(int index, CellFlags flags) => Flags[index] = (byte)(Flags[index] & ~(byte)flags);

        /// <summary>Default fuel for every walkable indoor cell; call after flags are rasterized.</summary>
        public void FillFuel(float fuel)
        {
            for (var i = 0; i < CellCount; i++)
            {
                var f = GetFlags(i);
                Fuel[i] = (f & CellFlags.Walkable) != 0 && (f & (CellFlags.Outside | CellFlags.Stair)) == 0 ? fuel : 0f;
            }
        }

        public void SwapBuffers()
        {
            (Fire, FireNext) = (FireNext, Fire);
            (Heat, HeatNext) = (HeatNext, Heat);
            (Smoke, SmokeNext) = (SmokeNext, Smoke);
            (Fuel, FuelNext) = (FuelNext, Fuel);
        }

        public HazardSample Sample(float3 world)
        {
            if (!Layout.TryCellAt(world, out var i))
                return new HazardSample { Visibility = 1f, HeatC = 0f };
            return SampleCell(i);
        }

        public HazardSample SampleCell(int i) => new()
        {
            Fire = Fire[i],
            Smoke = Smoke[i],
            HeatC = Heat[i],
            Visibility = math.saturate(1f - Smoke[i] * 1.15f),
            Flags = GetFlags(i),
            LayerBottomY = LayerBottom(i),
            SmokeAgeSeconds = SmokeAge[i],
            Valid = true,
        };

        /// <summary>Highest fire intensity within <paramref name="radius"/> meters on the same floor.</summary>
        public float MaxFireNear(float3 world, float radius)
        {
            if (!Layout.TryCellAt(world, out var center))
                return 0f;
            var c = Layout.Decode(center);
            var r = (int)math.ceil(radius / Layout.CellSize);
            var best = 0f;
            for (var dz = -r; dz <= r; dz++)
            for (var dx = -r; dx <= r; dx++)
            {
                if (!Layout.InBounds(c.y, c.x + dx, c.z + dz))
                    continue;
                best = math.max(best, Fire[Layout.Index(c.y, c.x + dx, c.z + dz)]);
            }

            return best;
        }

        public void Dispose()
        {
            if (Flags.IsCreated) Flags.Dispose();
            if (Fuel.IsCreated) Fuel.Dispose();
            if (Fire.IsCreated) Fire.Dispose();
            if (Heat.IsCreated) Heat.Dispose();
            if (Smoke.IsCreated) Smoke.Dispose();
            if (Suppression.IsCreated) Suppression.Dispose();
            if (Wind.IsCreated) Wind.Dispose();
            if (FireNext.IsCreated) FireNext.Dispose();
            if (HeatNext.IsCreated) HeatNext.Dispose();
            if (SmokeNext.IsCreated) SmokeNext.Dispose();
            if (FuelNext.IsCreated) FuelNext.Dispose();
            if (SmokeLayer.IsCreated) SmokeLayer.Dispose();
            if (SmokeAge.IsCreated) SmokeAge.Dispose();
        }
    }
}
