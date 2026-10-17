using System.Collections.Generic;
using Fire3D.Simulation;
using GridLayout = Fire3D.Simulation.GridLayout;
using Fire3D.Unity.Gameplay.Interactions;
using Unity.Mathematics;
using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>
    /// Turns loaded building colliders into hazard/navigation cells: floor present + nothing solid at body height
    /// means walkable; solid means wall. Doors, stairs and exits come from their marker components so the grid
    /// keeps IFC structure instead of guessing it.
    /// </summary>
    public static class GridRasterizer
    {
        public const float DefaultCellSize = 0.5f;

        public static GridLayout LayoutFor(Bounds bounds, IReadOnlyList<float> floorElevations, float cellSize = DefaultCellSize)
        {
            var layout = new GridLayout
            {
                Origin = new float3(bounds.min.x - 1f, 0f, bounds.min.z - 1f),
                CellSize = cellSize,
                SizeX = Mathf.CeilToInt((bounds.size.x + 2f) / cellSize),
                SizeZ = Mathf.CeilToInt((bounds.size.z + 2f) / cellSize),
                Floors = Mathf.Max(1, floorElevations.Count),
            };
            if (floorElevations.Count == 0)
                layout.FloorElevations.Add(0f);
            foreach (var e in floorElevations)
                layout.FloorElevations.Add(e);
            return layout;
        }

        public static void Rasterize(HazardGrid grid, int obstacleMask = ~0)
        {
            var layout = grid.Layout;
            var hits = new Collider[16];
            var cell = layout.CellSize;
            var radius = Mathf.Min(0.2f, cell * 0.4f);

            for (var f = 0; f < layout.Floors; f++)
            {
                var elev = layout.ElevationOf(f);
                for (var z = 0; z < layout.SizeZ; z++)
                for (var x = 0; x < layout.SizeX; x++)
                {
                    var i = layout.Index(f, x, z);
                    var c = new Vector3(layout.Origin.x + (x + 0.5f) * cell, elev, layout.Origin.z + (z + 0.5f) * cell);
                    var hasFloor = Physics.Raycast(c + Vector3.up * 1.0f, Vector3.down, out var floorHit, 1.45f, obstacleMask, QueryTriggerInteraction.Ignore)
                                   && floorHit.point.y >= elev - 0.35f && floorHit.point.y <= elev + 0.5f
                                   && !IsIgnored(floorHit.collider);
                    if (!hasFloor)
                    {
                        grid.SetFlags(i, CellFlags.Wall);
                        continue;
                    }

                    var floorY = floorHit.point.y;
                    var count = Physics.OverlapCapsuleNonAlloc(new Vector3(c.x, floorY + 0.35f + radius, c.z),
                        new Vector3(c.x, floorY + 1.55f, c.z), radius, hits, obstacleMask, QueryTriggerInteraction.Ignore);
                    var blocked = false;
                    for (var h = 0; h < count; h++)
                    {
                        if (!IsIgnored(hits[h]))
                        {
                            blocked = true;
                            break;
                        }
                    }

                    grid.SetFlags(i, blocked ? CellFlags.Wall : CellFlags.Walkable);
                }
            }

            foreach (var door in Object.FindObjectsByType<DoorView>(FindObjectsSortMode.None))
                MarkBounds(grid, door.FrameBounds, CellFlags.Walkable | CellFlags.Door, replace: true);
            foreach (var stair in Object.FindObjectsByType<StairVolume>(FindObjectsSortMode.None))
                MarkBounds(grid, stair.WorldBounds, CellFlags.Walkable | CellFlags.Stair, replace: true, allFloorsInside: true);
            foreach (var exit in ExitZone.All)
                MarkBounds(grid, exit.WorldBounds, exit.Blocked ? CellFlags.Walkable : CellFlags.Walkable | CellFlags.Exit | CellFlags.Outside, replace: true);
        }

        public static List<int> CellsIn(GridLayout layout, Bounds bounds, bool allFloorsInside = false)
        {
            var result = new List<int>();
            var minX = Mathf.FloorToInt((bounds.min.x - layout.Origin.x) / layout.CellSize);
            var maxX = Mathf.FloorToInt((bounds.max.x - layout.Origin.x) / layout.CellSize);
            var minZ = Mathf.FloorToInt((bounds.min.z - layout.Origin.z) / layout.CellSize);
            var maxZ = Mathf.FloorToInt((bounds.max.z - layout.Origin.z) / layout.CellSize);
            for (var f = 0; f < layout.Floors; f++)
            {
                var e = layout.ElevationOf(f) + layout.Origin.y;
                var onFloor = allFloorsInside
                    ? e >= bounds.min.y - 0.6f && e <= bounds.max.y + 0.1f
                    : layout.FloorAt(bounds.min.y + 0.05f) == f;
                if (!onFloor)
                    continue;
                for (var z = minZ; z <= maxZ; z++)
                for (var x = minX; x <= maxX; x++)
                {
                    if (layout.InBounds(f, x, z))
                        result.Add(layout.Index(f, x, z));
                }
            }

            return result;
        }

        private static void MarkBounds(HazardGrid grid, Bounds bounds, CellFlags flags, bool replace, bool allFloorsInside = false)
        {
            foreach (var i in CellsIn(grid.Layout, bounds, allFloorsInside))
            {
                if (replace)
                    grid.SetFlags(i, flags | (grid.GetFlags(i) & (CellFlags.Exit | CellFlags.Stair | CellFlags.Door)));
                else
                    grid.AddFlags(i, flags);
            }
        }

        /// <summary>Colliders that never count as walls: doors (handled as door cells), props, people, triggers.</summary>
        private static bool IsIgnored(Collider c)
        {
            if (c == null || c.isTrigger)
                return true;
            return c.GetComponentInParent<DoorView>() != null
                   || c.GetComponentInParent<Interactable>() != null
                   || c.GetComponentInParent<CharacterController>() != null
                   || c.GetComponentInParent<IgnoreForGrid>() != null;
        }
    }
}
