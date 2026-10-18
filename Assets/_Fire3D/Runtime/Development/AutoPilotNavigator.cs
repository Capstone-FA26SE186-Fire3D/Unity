using System;
using System.Collections.Generic;
using System.Linq;
using Fire3D.Simulation;
using Fire3D.Unity.Content;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Fire3D.Unity.Development
{
    /// <summary>
    /// Route planning for <see cref="AutoPilot"/>. The hazard grid links floors through every stair cell, but a
    /// character can only use a flight from its ends, so each floor change is replaced by
    /// entry → top/bottom of the flight → entry on the other floor, measured from the stair geometry.
    /// </summary>
    internal sealed class AutoPilotNavigator : IDisposable
    {
        private const float EntrySearchMeters = 2.5f;

        internal struct StairLink
        {
            public Bounds Bounds;
            public Vector3 Top;
            public Vector3 Bottom;
            public Vector3 TopEntry;
            public Vector3 BottomEntry;
        }

        internal sealed class Route
        {
            public readonly List<Vector3> Points = new();

            /// <summary>False when the route ends at a floor change and must be planned again from there.</summary>
            public bool ReachesGoal;
        }

        private readonly HazardGrid _grid;
        private readonly Transform _ignore;
        private NativeArray<byte> _floorOnlyFlags;
        private readonly List<StairLink> _stairs = new();

        public IReadOnlyList<StairLink> Stairs => _stairs;
        public Fire3D.Simulation.GridLayout Layout => _grid.Layout;

        public AutoPilotNavigator(HazardGrid grid, Transform ignore)
        {
            _grid = grid;
            _ignore = ignore;
            _floorOnlyFlags = new NativeArray<byte>(grid.Flags.Length, Allocator.Persistent);
            for (var i = 0; i < grid.Flags.Length; i++)
            {
                var f = (CellFlags)grid.Flags[i];
                if ((f & CellFlags.Stair) != 0)
                    f &= ~CellFlags.Walkable;
                _floorOnlyFlags[i] = (byte)f;
            }

            foreach (var v in UnityEngine.Object.FindObjectsByType<StairVolume>(FindObjectsSortMode.None))
                if (TryMeasure(v.WorldBounds, v.Flight, out var link))
                    _stairs.Add(link);
        }

        public void Dispose()
        {
            if (_floorOnlyFlags.IsCreated)
                _floorOnlyFlags.Dispose();
        }

        public int FloorOf(Vector3 world) => _grid.Layout.FloorAt(world.y);

        /// <summary>Plans from <paramref name="from"/> to <paramref name="goal"/>; null when there is no route.</summary>
        public Route Plan(Vector3 from, Vector3 goal)
        {
            var layout = _grid.Layout;
            var start = layout.CellAtClamped(from);
            var goalCell = NearestWalkable(goal, _grid.Flags, floorOnly: false);
            if (goalCell < 0)
                return null;

            var full = new NativeList<int>(Allocator.Temp);
            try
            {
                if (!GridPathfinder.FindPath(View(_grid.Flags), start, goalCell, full))
                    return null;

                var transition = -1;
                for (var i = 1; i < full.Length; i++)
                {
                    if (layout.Decode(full[i]).y != layout.Decode(full[i - 1]).y)
                    {
                        transition = i;
                        break;
                    }
                }

                var route = new Route();
                if (transition < 0)
                {
                    route.ReachesGoal = true;
                    var flatGoal = NearestWalkable(goal, _floorOnlyFlags, floorOnly: true);
                    if (!AppendFloorPath(route, from, flatGoal))
                        AppendCells(route, full, 0, full.Length);
                    route.Points.Add(goal);
                    return route;
                }

                var at = (Vector3)layout.CellCenter(full[transition - 1]);
                var down = layout.Decode(full[transition]).y < layout.Decode(full[transition - 1]).y;
                if (!TryStairAt(at, out var stair))
                {
                    // Unmeasured shaft: follow the grid and hope the geometry allows it.
                    route.ReachesGoal = true;
                    AppendCells(route, full, 0, full.Length);
                    return route;
                }

                var entry = down ? stair.TopEntry : stair.BottomEntry;
                var entryCell = NearestWalkable(entry, _floorOnlyFlags, floorOnly: true);
                if (!AppendFloorPath(route, from, entryCell))
                    return null;
                route.Points.Add(entry);
                route.Points.Add(down ? stair.Top : stair.Bottom);
                route.Points.Add(down ? stair.Bottom : stair.Top);
                route.Points.Add(down ? stair.BottomEntry : stair.TopEntry);
                route.ReachesGoal = false;
                return route;
            }
            finally
            {
                full.Dispose();
            }
        }

        private bool AppendFloorPath(Route route, Vector3 from, int goalCell)
        {
            if (goalCell < 0)
                return false;
            var start = NearestWalkable(from, _floorOnlyFlags, floorOnly: true);
            if (start < 0)
                return false;
            var path = new NativeList<int>(Allocator.Temp);
            try
            {
                if (!GridPathfinder.FindPath(View(_floorOnlyFlags), start, goalCell, path))
                    return false;
                AppendCells(route, path, 1, path.Length);
                return true;
            }
            finally
            {
                path.Dispose();
            }
        }

        private void AppendCells(Route route, NativeList<int> cells, int begin, int end)
        {
            for (var i = begin; i < end; i++)
                route.Points.Add(_grid.Layout.CellCenter(cells[i]));
        }

        private GridNavView View(NativeArray<byte> flags) => new()
        {
            Layout = _grid.Layout, Weights = NavigationWeights.Default, AmbientC = HazardSettings.Default.AmbientC,
            Flags = flags, Fire = _grid.Fire, Smoke = _grid.Smoke, Heat = _grid.Heat,
        };

        /// <summary>Closest walkable cell on the floor of <paramref name="world"/>, searched outwards in rings.</summary>
        private int NearestWalkable(Vector3 world, NativeArray<byte> flags, bool floorOnly)
        {
            var layout = _grid.Layout;
            var floor = layout.FloorAt(world.y);
            var cx = (int)math.floor((world.x - layout.Origin.x) / layout.CellSize);
            var cz = (int)math.floor((world.z - layout.Origin.z) / layout.CellSize);
            var rings = (int)math.ceil(EntrySearchMeters / layout.CellSize);
            var best = -1;
            var bestD = float.MaxValue;
            for (var r = 0; r <= rings && best < 0; r++)
            {
                for (var dz = -r; dz <= r; dz++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (math.max(math.abs(dx), math.abs(dz)) != r || !layout.InBounds(floor, cx + dx, cz + dz))
                        continue;
                    var i = layout.Index(floor, cx + dx, cz + dz);
                    var f = (CellFlags)flags[i];
                    if ((f & CellFlags.Walkable) == 0 || (f & CellFlags.Wall) != 0 || (floorOnly && (f & CellFlags.Stair) != 0))
                        continue;
                    var c = layout.CellCenter(i);
                    var d = math.distancesq(new float2(c.x, c.z), new float2(world.x, world.z));
                    if (d < bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }
            }

            return best;
        }

        private bool TryStairAt(Vector3 world, out StairLink link)
        {
            var bestD = float.MaxValue;
            link = default;
            foreach (var s in _stairs)
            {
                var b = s.Bounds;
                var d = Vector2.Distance(new Vector2(world.x, world.z), new Vector2(Mathf.Clamp(world.x, b.min.x, b.max.x), Mathf.Clamp(world.z, b.min.z, b.max.z)));
                if (d < bestD)
                {
                    bestD = d;
                    link = s;
                }
            }

            return bestD < 1f;
        }

        /// <summary>
        /// Samples the walking surface along the long axis of the volume; the high end is the top of the flight.
        /// </summary>
        private bool TryMeasure(Bounds b, Transform flight, out StairLink link)
        {
            link = default;
            var volume = b;
            // The volume is padded around the flight; sampling its padded ends would miss the steps, so measure the flight.
            var colliders = flight != null ? flight.GetComponentsInChildren<Collider>() : System.Array.Empty<Collider>();
            if (colliders.Length > 0)
            {
                b = colliders[0].bounds;
                foreach (var part in colliders)
                    b.Encapsulate(part.bounds);
            }

            var alongX = b.size.x >= b.size.z;
            var length = alongX ? b.size.x : b.size.z;
            var inset = colliders.Length > 0 ? 0.25f : 0.2f;
            Vector3 At(float t) => alongX
                ? new Vector3(Mathf.Lerp(b.min.x + inset, b.max.x - inset, t), b.max.y + 0.3f, b.center.z)
                : new Vector3(b.center.x, b.max.y + 0.3f, Mathf.Lerp(b.min.z + inset, b.max.z - inset, t));

            if (!Ground(At(0f), b.size.y + 1f, out var a, flight) || !Ground(At(1f), b.size.y + 1f, out var c, flight))
                return false;
            if (Mathf.Abs(a.y - c.y) < 1f || length < 1f)
                return false;

            var top = a.y > c.y ? a : c;
            var bottom = a.y > c.y ? c : a;
            link = new StairLink
            {
                Bounds = volume,
                Top = top,
                Bottom = bottom,
                TopEntry = EntryNear(top, b),
                BottomEntry = EntryNear(bottom, b),
            };
            return true;
        }

        /// <summary>Closest floor cell (outside the flight) that can be walked to in a straight line from the end of the flight.</summary>
        private Vector3 EntryNear(Vector3 end, Bounds stair)
        {
            var layout = _grid.Layout;
            var floor = layout.FloorAt(end.y);
            var cx = (int)math.floor((end.x - layout.Origin.x) / layout.CellSize);
            var cz = (int)math.floor((end.z - layout.Origin.z) / layout.CellSize);
            var rings = (int)math.ceil(EntrySearchMeters / layout.CellSize);
            var candidates = new List<(float d, Vector3 p)>();
            for (var dz = -rings; dz <= rings; dz++)
            for (var dx = -rings; dx <= rings; dx++)
            {
                if (!layout.InBounds(floor, cx + dx, cz + dz))
                    continue;
                var i = layout.Index(floor, cx + dx, cz + dz);
                var f = (CellFlags)_floorOnlyFlags[i];
                if ((f & CellFlags.Walkable) == 0 || (f & CellFlags.Wall) != 0)
                    continue;
                var c = (Vector3)layout.CellCenter(i);
                c.y = end.y;
                candidates.Add((Vector2.Distance(new Vector2(c.x, c.z), new Vector2(end.x, end.z)), c));
            }

            foreach (var (_, p) in candidates.OrderBy(c => c.d))
                if (Clear(end + Vector3.up * 0.6f, p + Vector3.up * 0.6f))
                    return p;
            return end;
        }

        private bool Clear(Vector3 a, Vector3 b)
        {
            foreach (var hit in Physics.RaycastAll(a, b - a, Vector3.Distance(a, b), ~0, QueryTriggerInteraction.Ignore))
                if (_ignore == null || !hit.transform.IsChildOf(_ignore))
                    return false;
            return true;
        }

        private bool Ground(Vector3 from, float distance, out Vector3 point, Transform only = null)
        {
            point = default;
            var best = float.MaxValue;
            foreach (var hit in Physics.RaycastAll(from, Vector3.down, distance, ~0, QueryTriggerInteraction.Ignore))
            {
                if (_ignore != null && hit.transform.IsChildOf(_ignore))
                    continue;
                if (only != null && !hit.transform.IsChildOf(only))
                    continue;
                if (hit.distance < best)
                {
                    best = hit.distance;
                    point = hit.point;
                }
            }

            return best < float.MaxValue;
        }
    }
}
