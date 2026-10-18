using System.Collections.Generic;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Simulation;
using Fire3D.Unity.Gameplay.Interactions;
using Unity.Mathematics;
using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>Result of building the playable world from content + scenario.</summary>
    public sealed class BuiltWorld
    {
        public FireSimulation Simulation;
        public Vector3 SpawnPosition;
        public float SpawnYaw;
        public readonly List<Interactable> Interactables = new();
        public readonly List<ExitZone> Exits = new();
        public readonly List<string> Warnings = new();
        public readonly Dictionary<string, Vector3> SprinklerPositions = new();
        public Transform Root;
    }

    /// <summary>
    /// Builds the playable world from loaded building geometry, <c>fet3d.building/1</c> metadata and a
    /// <c>fet3d.editor/1</c> scenario: places catalog objects, rasterizes the hazard grid and creates simulation
    /// entities. Placement y is relative to the floor slab of <c>placement.floorId</c>.
    /// </summary>
    public sealed class WorldBuilder
    {
        private readonly RuntimeAssets _assets;
        private readonly ScenarioState _scenario;
        private readonly BuildingMetadata _building;
        private readonly string _mode;
        private readonly List<string> _floorIds = new();
        private readonly List<float> _floorElevations = new();
        private Dictionary<string, IfcElement> _elements;

        public WorldBuilder(RuntimeAssets assets, ScenarioState scenario, BuildingMetadata building, string mode)
        {
            _assets = assets;
            _scenario = scenario;
            _building = building ?? new BuildingMetadata { SchemaVersion = BuildingMetadata.Schema };
            _mode = mode;
        }

        public BuiltWorld Build()
        {
            var world = new BuiltWorld { Root = new GameObject("ScenarioObjects").transform };
            CollectFloors();
            _elements = Object.FindObjectsByType<IfcElement>(FindObjectsSortMode.None)
                .Where(e => !string.IsNullOrEmpty(e.GlobalId))
                .GroupBy(e => e.GlobalId)
                .ToDictionary(g => g.Key, g => g.First());

            var blockedAnchors = new HashSet<string>(_scenario.Objects
                .Where(o => o.Kind == ScenarioObjectKind.BlockedElement && !string.IsNullOrEmpty(o.AnchorId))
                .Select(o => o.AnchorId));

            // 1. Objects that shape the grid must exist before rasterizing: exits, doors, blockers.
            foreach (var obj in _scenario.Objects)
            {
                if (obj.Kind == ScenarioObjectKind.Goal)
                    world.Exits.Add(CreateExit(world, obj, blockedAnchors));
                else if (obj.Kind == ScenarioObjectKind.BlockedElement && (obj.AnchorId == null || !_elements.ContainsKey(obj.AnchorId)))
                    CreateBlocker(world, obj);
            }

            foreach (var door in Object.FindObjectsByType<DoorView>(FindObjectsSortMode.None))
                EnsureDoorInteractable(door);

            // 2. Hazard grid over the building.
            var bounds = BuildingBounds();
            var layout = GridRasterizer.LayoutFor(bounds, _floorElevations);
            var seed = (uint)((_scenario.RandomSeed ?? 1L) & 0xffffffff);
            world.Simulation = new FireSimulation(layout, seed == 0 ? 1u : seed);
            var grid = world.Simulation.Grid;
            Physics.SyncTransforms();
            GridRasterizer.Rasterize(grid);
            grid.FillFuel(0.55f);
            ApplySpaceFuel(grid);

            foreach (var door in Object.FindObjectsByType<DoorView>(FindObjectsSortMode.None))
            {
                var it = door.GetComponent<Interactable>();
                var id = it != null ? it.SimulationId : door.name;
                var blocked = blockedAnchors.Contains(id);
                var cells = GridRasterizer.CellsIn(layout, door.FrameBounds);
                world.Simulation.AddDoor(id, cells, door.Open, blocked);
                if (blocked && it != null)
                    it.State = "blocked";
            }

            // 3. Scenario objects.
            var learn = _mode == TrainingMode.Learn;
            foreach (var obj in _scenario.Objects)
            {
                switch (obj.Kind)
                {
                    case ScenarioObjectKind.Spawn:
                        if (world.SpawnPosition == Vector3.zero)
                        {
                            world.SpawnPosition = WorldPosition(obj.Placement);
                            world.SpawnYaw = WorldRotation(obj.Placement).eulerAngles.y;
                        }

                        break;
                    case ScenarioObjectKind.Equipment:
                        CreateEquipment(world, obj);
                        break;
                    case ScenarioObjectKind.Hazard when !learn:
                        CreateHazard(world, obj);
                        break;
                    case ScenarioObjectKind.Npc when !learn:
                        CreateNpc(world, obj);
                        break;
                }
            }

            // Scenario equipment may have reconfigured anchored doors; locked doors stay locked.
            foreach (var door in Object.FindObjectsByType<DoorView>(FindObjectsSortMode.None))
            {
                if (door.TryGetComponent<Interactable>(out var it) && (blockedAnchors.Contains(it.SimulationId) || blockedAnchors.Contains(it.ObjectId)))
                    it.State = "blocked";
            }

            world.Simulation.FinalizeBuilding();
            if (world.Exits.Count == 0)
                world.Warnings.Add("Scenario has no Goal exit.");
            return world;
        }

        private void CollectFloors()
        {
            foreach (var s in _building.Storeys.OrderBy(s => s.Elevation))
            {
                _floorIds.Add(s.FloorId);
                _floorElevations.Add(s.Elevation);
            }

            if (_floorElevations.Count > 0)
                return;
            foreach (var root in Object.FindObjectsByType<BuildingRoot>(FindObjectsSortMode.None).OrderBy(r => r.Elevation))
            {
                _floorIds.Add(root.FloorId);
                _floorElevations.Add(root.Elevation);
            }

            if (_floorElevations.Count == 0)
            {
                _floorIds.Add("L1");
                _floorElevations.Add(0f);
            }
        }

        private float ElevationOf(string floorId)
        {
            var i = _floorIds.IndexOf(floorId);
            if (i >= 0)
                return _floorElevations[i];
            // Fall back to "L<n>" numbering when ids differ between scenario and metadata.
            if (!string.IsNullOrEmpty(floorId) && floorId.Length > 1 && (floorId[0] == 'L' || floorId[0] == 'F')
                && int.TryParse(floorId.Substring(1), out var n) && n >= 1 && n <= _floorElevations.Count)
                return _floorElevations[n - 1];
            return _floorElevations.Count > 0 ? _floorElevations[0] : 0f;
        }

        private Vector3 WorldPosition(Placement p)
        {
            if (p == null)
                return Vector3.zero;
            // Contract x/z are building coordinates in the GLB frame, y is height above the floor slab.
            return ContractFrame.ToUnity(p.Position.X, ElevationOf(p.FloorId) + p.Position.Y, p.Position.Z);
        }

        private static Quaternion WorldRotation(Placement p)
        {
            if (p == null)
                return Quaternion.identity;
            var q = new Quaternion(p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W);
            return Mathf.Approximately(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w, 0f) ? Quaternion.identity : ContractFrame.ToUnity(q.normalized);
        }

        private Bounds BuildingBounds()
        {
            var has = false;
            var bounds = new Bounds();
            foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                if (r is ParticleSystemRenderer || r.GetComponentInParent<CharacterController>() != null)
                    continue;
                if (!has)
                {
                    bounds = r.bounds;
                    has = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            return has ? bounds : new Bounds(Vector3.zero, new Vector3(20f, 3f, 20f));
        }

        private void ApplySpaceFuel(HazardGrid grid)
        {
            foreach (var space in Object.FindObjectsByType<SpaceVolume>(FindObjectsSortMode.None))
            {
                var fuel = space.Category switch
                {
                    "kitchen" => 0.85f,
                    "office" => 0.65f,
                    "storage" => 0.9f,
                    "corridor" => 0.2f,
                    "stair" => 0f,
                    "wc" => 0.15f,
                    _ => 0.55f,
                };
                foreach (var i in GridRasterizer.CellsIn(grid.Layout, space.Bounds))
                {
                    if ((grid.GetFlags(i) & CellFlags.Walkable) != 0)
                        grid.Fuel[i] = fuel;
                }
            }
        }

        private static void EnsureDoorInteractable(DoorView door)
        {
            if (!door.TryGetComponent<Interactable>(out var it))
            {
                it = door.gameObject.AddComponent<Interactable>();
                var element = door.GetComponent<IfcElement>();
                it.Configure(element != null ? element.GlobalId : door.name, Capabilities.DoorStandard, null);
                it.ElementId = element != null ? element.GlobalId : door.name;
            }

            it.State = door.Open ? "open" : "closed";
            it.DisplayName ??= "cửa";
        }

        private ExitZone CreateExit(BuiltWorld world, ScenarioObject obj, HashSet<string> blocked)
        {
            var go = new GameObject($"Exit_{obj.Id}");
            go.transform.SetParent(world.Root, false);
            var zone = go.AddComponent<ExitZone>();
            if (obj.AnchorId != null && _elements.TryGetValue(obj.AnchorId, out var element))
            {
                var renderers = element.GetComponentsInChildren<Renderer>();
                var b = new Bounds(element.transform.position, Vector3.one);
                for (var i = 0; i < renderers.Length; i++)
                {
                    if (i == 0)
                        b = renderers[i].bounds;
                    else
                        b.Encapsulate(renderers[i].bounds);
                }

                go.transform.position = new Vector3(b.center.x, b.min.y, b.center.z);
                zone.Size = new Vector3(Mathf.Max(1.4f, b.size.x + 0.9f), 2.4f, Mathf.Max(1.4f, b.size.z + 0.9f));
            }
            else
            {
                go.transform.SetPositionAndRotation(WorldPosition(obj.Placement), WorldRotation(obj.Placement));
                zone.Size = new Vector3(1.6f, 2.4f, 1.6f);
            }

            zone.ObjectId = obj.Id;
            zone.CapabilityId = obj.CapabilityId ?? Capabilities.ExitFinal;
            zone.Blocked = blocked.Contains(obj.Id) || (obj.AnchorId != null && blocked.Contains(obj.AnchorId));
            return zone;
        }

        private void CreateBlocker(BuiltWorld world, ScenarioObject obj)
        {
            var go = PrototypeFactory.Box($"Blocked_{obj.Id}", world.Root, Vector3.zero, new Vector3(1.4f, 2.2f, 0.4f),
                PrototypeFactory.Lit(new Color(0.25f, 0.2f, 0.18f), 0.2f));
            go.transform.SetPositionAndRotation(WorldPosition(obj.Placement) + Vector3.up * 1.1f, WorldRotation(obj.Placement));
        }

        private Interactable Spawn(BuiltWorld world, ScenarioObject obj, string capability)
        {
            GameObject go;
            if (obj.AnchorId != null && _elements.TryGetValue(obj.AnchorId, out var element))
            {
                go = element.gameObject;
            }
            else
            {
                var prefab = _assets != null ? _assets.PrefabFor(capability) : null;
                go = prefab != null ? Object.Instantiate(prefab, world.Root) : PrototypeFactory.Create(capability);
                go.transform.SetParent(world.Root, true);
                go.transform.SetPositionAndRotation(WorldPosition(obj.Placement), WorldRotation(obj.Placement));
                go.name = $"{capability}_{obj.Id}";
            }

            if (!go.TryGetComponent<Interactable>(out var it))
                it = go.AddComponent<Interactable>();
            var elementId = obj.AnchorId != null && _elements.ContainsKey(obj.AnchorId) ? obj.AnchorId : it.ElementId;
            it.Configure(obj.Id, capability, obj.Parameters);
            it.ElementId = elementId;
            if (Capabilities.IsDoor(capability) && go.TryGetComponent<DoorView>(out var view))
                it.State = view.Open ? "open" : "closed";
            it.DisplayName = obj.Label ?? DefaultName(capability);
            world.Interactables.Add(it);
            return it;
        }

        private void CreateEquipment(BuiltWorld world, ScenarioObject obj)
        {
            var cap = obj.CapabilityId;
            if (!RuntimeCatalog.IsSupported(cap))
            {
                world.Warnings.Add($"Capability '{cap}' of {obj.Id} is not supported by runtime {RuntimeCatalog.RuntimeVersion}.");
                return;
            }

            var it = Spawn(world, obj, cap);
            var pos = it.transform.position;
            var sim = world.Simulation;

            if (cap == Capabilities.AlarmDetector)
            {
                if (obj.TryGetFloat("smokeThreshold", out var threshold))
                    sim.AddDetector(it.ObjectId, new float3(pos.x, ElevationOf(obj.Placement.FloorId), pos.z), threshold); // ceiling device → floor cell below
                else
                    world.Warnings.Add($"Detector {obj.Id} has no smokeThreshold; it will never trigger.");
            }
            else if (cap == Capabilities.Sprinkler)
            {
                if (obj.TryGetFloat("activationTemperatureC", out var t) && obj.TryGetFloat("coverageRadiusMeters", out var r)
                    && obj.TryGetFloat("fireIntensityReduction", out var red) && obj.TryGetFloat("activationDelaySeconds", out var delay))
                {
                    var mode = obj.GetString("state", "Active") switch
                    {
                        "Disabled" => SprinklerMode.Disabled,
                        "Faulty" => SprinklerMode.Faulty,
                        _ => SprinklerMode.Active,
                    };
                    // Sprinkler heads hang from the ceiling; the hazard cell is the floor cell below.
                    sim.AddSprinkler(it.ObjectId, new float3(pos.x, ElevationOf(obj.Placement.FloorId), pos.z), mode, t, r, red, delay);
                    world.SprinklerPositions[it.ObjectId] = pos;
                }
                else
                {
                    world.Warnings.Add($"Sprinkler {obj.Id} is missing required parameters; it is inactive.");
                }
            }
        }

        private void CreateHazard(BuiltWorld world, ScenarioObject obj)
        {
            var sim = world.Simulation;
            var pos = WorldPosition(obj.Placement);
            switch (obj.CapabilityId)
            {
                case Capabilities.FireSource:
                    sim.AddFireSource(obj.Id, pos, obj.GetFloat("intensity", 5f), obj.GetFloat("activationSeconds", 0f));
                    break;
                case Capabilities.SmokeSource:
                    sim.AddSmokeSource(obj.Id, pos, obj.GetFloat("ratePerSecond", 0.3f), obj.GetFloat("activationSeconds", 0f));
                    break;
                case Capabilities.PowerOutage:
                    sim.AddTimeline(obj.Id, TimelineKind.PowerOutage, obj.GetFloat("atSeconds", 0f));
                    break;
                case Capabilities.AlarmTimeline:
                    if (obj.GetBool("fails", false))
                        sim.SetDetectorsDisabled(true);
                    else
                        sim.AddTimeline(obj.Id, TimelineKind.Alarm, obj.GetFloat("atSeconds", 0f));
                    break;
                default:
                    world.Warnings.Add($"Hazard capability '{obj.CapabilityId}' of {obj.Id} is not supported.");
                    break;
            }
        }

        private void CreateNpc(BuiltWorld world, ScenarioObject obj)
        {
            var behaviour = obj.GetString("behaviour", "calm") == "panic" ? NpcBehaviour.Panic : NpcBehaviour.Calm;
            var impaired = obj.GetBool("mobilityImpaired", false);
            world.Simulation.AddNpc(obj.Id, WorldPosition(obj.Placement), behaviour, impaired,
                _scenario.ReplanIntervalSeconds ?? 3);
        }

        private static string DefaultName(string capability) => capability switch
        {
            Capabilities.ExtinguisherPortable => "bình chữa cháy",
            Capabilities.ItemTowel => "khăn",
            Capabilities.AlarmCallPoint => "nút báo cháy",
            Capabilities.BaitBelongings => "đồ cá nhân",
            Capabilities.Elevator => "thang máy",
            _ => capability,
        };
    }
}
