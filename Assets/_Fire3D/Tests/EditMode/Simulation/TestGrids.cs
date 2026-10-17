using Fire3D.Simulation;
using Unity.Mathematics;

namespace Fire3D.Unity.Tests.Simulation
{
    /// <summary>Small hand-made buildings for simulation tests (cell size 0.5 m).</summary>
    internal static class TestGrids
    {
        public const float Cell = 0.5f;

        public static GridLayout Layout(int sizeX, int sizeZ, int floors = 1)
        {
            var layout = new GridLayout { Origin = float3.zero, CellSize = Cell, SizeX = sizeX, SizeZ = sizeZ, Floors = floors };
            for (var f = 0; f < floors; f++)
                layout.FloorElevations.Add(f * 3f);
            return layout;
        }

        /// <summary>Walkable floor with a solid border wall on every floor.</summary>
        public static void Box(HazardGrid grid)
        {
            var l = grid.Layout;
            for (var f = 0; f < l.Floors; f++)
            for (var z = 0; z < l.SizeZ; z++)
            for (var x = 0; x < l.SizeX; x++)
            {
                var border = x == 0 || z == 0 || x == l.SizeX - 1 || z == l.SizeZ - 1;
                grid.SetFlags(l.Index(f, x, z), border ? CellFlags.Wall : CellFlags.Walkable);
            }
        }

        public static void WallColumn(HazardGrid grid, int x, int floor = 0)
        {
            var l = grid.Layout;
            for (var z = 0; z < l.SizeZ; z++)
                grid.SetFlags(l.Index(floor, x, z), CellFlags.Wall);
        }

        public static void Walkable(HazardGrid grid, int floor, int x, int z, CellFlags extra = CellFlags.None) =>
            grid.SetFlags(grid.Layout.Index(floor, x, z), CellFlags.Walkable | extra);

        public static float3 Center(HazardGrid grid, int floor, int x, int z) => grid.Layout.CellCenter(grid.Layout.Index(floor, x, z));

        /// <summary>
        /// 40×12 cell room split by a wall at x = 20 with a one-cell door at z = 6; exit column at x = 38.
        /// </summary>
        public static FireSimulation TwoRooms(uint seed, bool doorOpen, out int doorCell)
        {
            var sim = new FireSimulation(Layout(40, 12), seed);
            var g = sim.Grid;
            Box(g);
            WallColumn(g, 20);
            doorCell = g.Layout.Index(0, 20, 6);
            g.SetFlags(doorCell, CellFlags.Walkable);
            for (var z = 1; z < 11; z++)
                g.AddFlags(g.Layout.Index(0, 38, z), CellFlags.Exit);
            g.FillFuel(0.6f);
            sim.AddDoor("door-1", new[] { doorCell }, doorOpen, false);
            sim.FinalizeBuilding();
            return sim;
        }

        public static float SumSmoke(HazardGrid grid, int fromX, int toX)
        {
            var sum = 0f;
            var l = grid.Layout;
            for (var z = 0; z < l.SizeZ; z++)
            for (var x = fromX; x <= toX; x++)
                sum += grid.Smoke[l.Index(0, x, z)];
            return sum;
        }

        public static float SumFire(HazardGrid grid)
        {
            var sum = 0f;
            for (var i = 0; i < grid.CellCount; i++)
                sum += grid.Fire[i];
            return sum;
        }

        public static void Run(FireSimulation sim, float seconds)
        {
            var ticks = (int)math.round(seconds / sim.TickSeconds);
            for (var i = 0; i < ticks; i++)
                sim.Step();
        }
    }
}
