using Unity.Collections;
using Unity.Mathematics;

namespace Fire3D.Simulation
{
    /// <summary>Read-only view of the grid for path queries inside Burst jobs.</summary>
    public struct GridNavView
    {
        public GridLayout Layout;
        public NavigationWeights Weights;
        public float AmbientC;
        [ReadOnly] public NativeArray<byte> Flags;
        [ReadOnly] public NativeArray<float> Fire;
        [ReadOnly] public NativeArray<float> Smoke;
        [ReadOnly] public NativeArray<float> Heat;

        public bool IsWalkable(int i)
        {
            var f = (CellFlags)Flags[i];
            return (f & CellFlags.Walkable) != 0 && (f & CellFlags.Wall) == 0;
        }

        public bool IsExit(int i) => ((CellFlags)Flags[i] & CellFlags.Exit) != 0;

        /// <summary>Hazard exposure proxy in 0..1 used by the cost function.</summary>
        public float Hazard(int i) =>
            math.saturate(Fire[i] * 1.5f + Smoke[i] * 0.6f + math.max(0f, Heat[i] - AmbientC - 35f) / 300f);
    }

    /// <summary>
    /// Risk-aware A* on the hazard grid: cost(edge) = distance + hazard·wh + portal·wp + blocked·wb
    /// (Docs technology section 7; congestion is not modelled yet).
    /// </summary>
    public static class GridPathfinder
    {
        private struct Node
        {
            public float F;
            public int Cell;
        }

        /// <summary>
        /// Finds a path from <paramref name="start"/> to <paramref name="goal"/>, or to the nearest exit cell when
        /// <paramref name="goal"/> is -1. Writes cells start→goal into <paramref name="path"/>.
        /// </summary>
        public static bool FindPath(in GridNavView view, int start, int goal, NativeList<int> path, int maxExpanded = 200000)
        {
            path.Clear();
            var n = view.Layout.CellCount;
            if (start < 0 || start >= n)
                return false;
            var multiGoal = goal < 0;
            if (!multiGoal && (goal >= n || !view.IsWalkable(goal)))
                return false;

            var g = new NativeArray<float>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var from = new NativeArray<int>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            var closed = new NativeArray<bool>(n, Allocator.Temp);
            for (var i = 0; i < n; i++)
            {
                g[i] = float.MaxValue;
                from[i] = -1;
            }

            var heap = new NativeList<Node>(256, Allocator.Temp);
            g[start] = 0f;
            Push(heap, new Node { F = Heuristic(view, start, goal), Cell = start });

            var found = -1;
            var expanded = 0;
            var layout = view.Layout;
            var step = layout.CellSize;
            var diagonal = step * 1.41421356f;

            while (heap.Length > 0 && expanded < maxExpanded)
            {
                var current = Pop(heap).Cell;
                if (closed[current])
                    continue;
                closed[current] = true;
                expanded++;

                if (multiGoal ? view.IsExit(current) : current == goal)
                {
                    found = current;
                    break;
                }

                var c = layout.Decode(current);
                for (var dz = -1; dz <= 1; dz++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0)
                        continue;
                    if (!layout.InBounds(c.y, c.x + dx, c.z + dz))
                        continue;
                    var next = layout.Index(c.y, c.x + dx, c.z + dz);
                    if (!view.IsWalkable(next) || closed[next])
                        continue;
                    if (dx != 0 && dz != 0)
                    {
                        // No corner cutting through walls.
                        if (!view.IsWalkable(layout.Index(c.y, c.x + dx, c.z)) || !view.IsWalkable(layout.Index(c.y, c.x, c.z + dz)))
                            continue;
                    }

                    Relax(view, g, from, heap, current, next, dx != 0 && dz != 0 ? diagonal : step, goal);
                }

                var flags = (CellFlags)view.Flags[current];
                if ((flags & CellFlags.Stair) != 0)
                {
                    for (var df = -1; df <= 1; df += 2)
                    {
                        if (!layout.InBounds(c.y + df, c.x, c.z))
                            continue;
                        var next = layout.Index(c.y + df, c.x, c.z);
                        if (((CellFlags)view.Flags[next] & CellFlags.Stair) == 0 || !view.IsWalkable(next) || closed[next])
                            continue;
                        Relax(view, g, from, heap, current, next, step * 2f, goal);
                    }
                }
            }

            var ok = found >= 0;
            if (ok)
            {
                var cell = found;
                while (cell >= 0)
                {
                    path.Add(cell);
                    cell = from[cell];
                }

                // Reverse to start→goal.
                for (int a = 0, b = path.Length - 1; a < b; a++, b--)
                    (path[a], path[b]) = (path[b], path[a]);
            }

            g.Dispose();
            from.Dispose();
            closed.Dispose();
            heap.Dispose();
            return ok;
        }

        private static void Relax(in GridNavView view, NativeArray<float> g, NativeArray<int> from, NativeList<Node> heap,
            int current, int next, float distance, int goal)
        {
            var flags = (CellFlags)view.Flags[next];
            var hazard = view.Hazard(next);
            var cost = distance + hazard * view.Weights.Hazard * distance;
            if ((flags & CellFlags.Door) != 0)
                cost += view.Weights.Portal;
            if ((flags & CellFlags.DoorClosed) != 0)
                cost += view.Weights.Portal * 4f;
            if (view.Fire[next] > 0.35f)
                cost += view.Weights.Blocked;

            var tentative = g[current] + cost;
            if (tentative >= g[next])
                return;
            g[next] = tentative;
            from[next] = current;
            Push(heap, new Node { F = tentative + Heuristic(view, next, goal), Cell = next });
        }

        private static float Heuristic(in GridNavView view, int a, int goal)
        {
            if (goal < 0)
                return 0f; // Dijkstra for nearest-exit queries.
            var ca = view.Layout.Decode(a);
            var cb = view.Layout.Decode(goal);
            var dx = math.abs(ca.x - cb.x);
            var dz = math.abs(ca.z - cb.z);
            var df = math.abs(ca.y - cb.y);
            var octile = (dx + dz) + (1.41421356f - 2f) * math.min(dx, dz);
            return (octile + df * 2f) * view.Layout.CellSize;
        }

        private static void Push(NativeList<Node> heap, Node node)
        {
            heap.Add(node);
            var i = heap.Length - 1;
            while (i > 0)
            {
                var parent = (i - 1) / 2;
                if (heap[parent].F <= heap[i].F)
                    break;
                (heap[parent], heap[i]) = (heap[i], heap[parent]);
                i = parent;
            }
        }

        private static Node Pop(NativeList<Node> heap)
        {
            var top = heap[0];
            var last = heap.Length - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            var i = 0;
            while (true)
            {
                var l = i * 2 + 1;
                var r = l + 1;
                var smallest = i;
                if (l < heap.Length && heap[l].F < heap[smallest].F)
                    smallest = l;
                if (r < heap.Length && heap[r].F < heap[smallest].F)
                    smallest = r;
                if (smallest == i)
                    break;
                (heap[smallest], heap[i]) = (heap[i], heap[smallest]);
                i = smallest;
            }

            return top;
        }
    }
}
