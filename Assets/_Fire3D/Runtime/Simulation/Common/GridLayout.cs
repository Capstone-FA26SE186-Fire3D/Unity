using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Fire3D.Simulation
{
    [Flags]
    public enum CellFlags : byte
    {
        None = 0,
        /// <summary>Floor present and nothing solid at body height: people can stand here.</summary>
        Walkable = 1 << 0,
        /// <summary>Solid wall or column: blocks movement, smoke, heat and fire.</summary>
        Wall = 1 << 1,
        /// <summary>Stair or open shaft: connects to the same cell on the floor above/below.</summary>
        Stair = 1 << 2,
        /// <summary>Exit goal cell.</summary>
        Exit = 1 << 3,
        /// <summary>Closed door: blocks movement; smoke and heat leak through slowly.</summary>
        DoorClosed = 1 << 4,
        /// <summary>Open air: smoke and heat vent away.</summary>
        Outside = 1 << 5,
        /// <summary>Door frame cell (open or closed); used for portal penalties.</summary>
        Door = 1 << 6,
    }

    /// <summary>Regular 2.5D grid: one XZ layer per floor, indexed floor-major.</summary>
    public struct GridLayout
    {
        public float3 Origin;
        public float CellSize;
        public int SizeX;
        public int SizeZ;
        public int Floors;
        /// <summary>Height of each floor slab above Origin.y; Floors entries.</summary>
        public FixedList128Bytes<float> FloorElevations;

        public int CellsPerFloor => SizeX * SizeZ;
        public int CellCount => SizeX * SizeZ * Floors;

        public int Index(int floor, int x, int z) => floor * SizeX * SizeZ + z * SizeX + x;

        public bool InBounds(int floor, int x, int z) =>
            floor >= 0 && floor < Floors && x >= 0 && x < SizeX && z >= 0 && z < SizeZ;

        public int3 Decode(int index)
        {
            var perFloor = SizeX * SizeZ;
            var floor = index / perFloor;
            var rest = index - floor * perFloor;
            var z = rest / SizeX;
            return new int3(rest - z * SizeX, floor, z);
        }

        public float ElevationOf(int floor) =>
            floor >= 0 && floor < FloorElevations.Length ? FloorElevations[floor] : floor * 3f;

        /// <summary>Floor whose slab is the highest one at or below <paramref name="y"/> (+0.5 m tolerance).</summary>
        public int FloorAt(float y)
        {
            var local = y - Origin.y;
            var best = 0;
            for (var f = 0; f < Floors; f++)
            {
                if (local + 0.5f >= ElevationOf(f))
                    best = f;
            }

            return best;
        }

        public bool TryCellAt(float3 world, out int index)
        {
            var floor = FloorAt(world.y);
            var x = (int)math.floor((world.x - Origin.x) / CellSize);
            var z = (int)math.floor((world.z - Origin.z) / CellSize);
            if (!InBounds(floor, x, z))
            {
                index = -1;
                return false;
            }

            index = Index(floor, x, z);
            return true;
        }

        public int CellAtClamped(float3 world)
        {
            var floor = FloorAt(world.y);
            var x = math.clamp((int)math.floor((world.x - Origin.x) / CellSize), 0, SizeX - 1);
            var z = math.clamp((int)math.floor((world.z - Origin.z) / CellSize), 0, SizeZ - 1);
            return Index(floor, x, z);
        }

        public float3 CellCenter(int index)
        {
            var c = Decode(index);
            return new float3(
                Origin.x + (c.x + 0.5f) * CellSize,
                Origin.y + ElevationOf(c.y),
                Origin.z + (c.z + 0.5f) * CellSize);
        }
    }
}
