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
            for (var i = 0; i < n; i++)
                Heat[i] = ambientC;
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
        }
    }
}
