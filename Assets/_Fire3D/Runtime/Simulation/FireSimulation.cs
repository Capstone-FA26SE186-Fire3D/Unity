using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Fire3D.Simulation
{
    /// <summary>
    /// Owns the ECS world that runs the hazard field and NPCs at a fixed tick. Gameplay pushes commands and the
    /// player pose, steps the world, then reads samples and events. Tests drive it the same way.
    /// </summary>
    public sealed class FireSimulation : IDisposable
    {
        public World World { get; }
        public HazardGrid Grid { get; }
        public float TickSeconds { get; }

        private readonly EntityManager _em;
        private readonly Entity _singleton;
        private readonly FireSimulationGroup _group;
        private float _accumulator;

        public FireSimulation(GridLayout layout, uint seed, HazardSettings? settings = null, NavigationWeights? weights = null)
        {
            var s = settings ?? HazardSettings.Default;
            TickSeconds = s.TickSeconds;
            World = new World("Fire3D Simulation", WorldFlags.Simulation);
            _em = World.EntityManager;
            Grid = new HazardGrid(layout, s.AmbientC);

            _singleton = _em.CreateEntity();
            _em.SetName(_singleton, "SimulationSingleton");
            _em.AddComponentData(_singleton, new SimulationClock { Tick = 0, TickSeconds = s.TickSeconds, Seed = seed });
            _em.AddComponentData(_singleton, new AlarmState());
            _em.AddComponentData(_singleton, new PowerState());
            _em.AddComponentData(_singleton, new PlayerState());
            _em.AddComponentObject(_singleton, new HazardGridRef { Grid = Grid, Settings = s, Weights = weights ?? NavigationWeights.Default });
            _em.AddBuffer<SimulationEvent>(_singleton);
            _em.AddBuffer<HazardCommand>(_singleton);
            _em.AddBuffer<ExitCell>(_singleton);

            _group = World.CreateSystemManaged<FireSimulationGroup>();
            _group.AddSystemToUpdateList(World.CreateSystemManaged<SimulationClockSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<HazardCommandSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<TimelineSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<FireSourceSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<SprinklerSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<HazardStepSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<SmokeDetectorSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<NpcSenseSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<NpcPlanSystem>());
            _group.AddSystemToUpdateList(World.CreateSystemManaged<NpcMoveSystem>());
            _group.SortSystems();
        }

        public SimulationClock Clock => _em.GetComponentData<SimulationClock>(_singleton);
        public float Time => Clock.Time;
        public AlarmState Alarm => _em.GetComponentData<AlarmState>(_singleton);
        public PowerState Power => _em.GetComponentData<PowerState>(_singleton);

        /// <summary>Call once after flags and fuel are set: registers exit cells.</summary>
        public void FinalizeBuilding()
        {
            var exits = _em.GetBuffer<ExitCell>(_singleton);
            exits.Clear();
            for (var i = 0; i < Grid.CellCount; i++)
            {
                if ((Grid.GetFlags(i) & CellFlags.Exit) != 0)
                    exits.Add(new ExitCell { Cell = i });
            }
        }

        public void SetDetectorsDisabled(bool disabled)
        {
            var alarm = Alarm;
            alarm.DetectorsDisabled = disabled;
            _em.SetComponentData(_singleton, alarm);
        }

        public void SetPlayer(float3 position, bool present = true) =>
            _em.SetComponentData(_singleton, new PlayerState { Position = position, Present = present });

        public void Enqueue(HazardCommand command) => _em.GetBuffer<HazardCommand>(_singleton).Add(command);

        /// <summary>Advances whole ticks for the elapsed real time; returns the number of ticks run.</summary>
        public int Advance(float deltaSeconds, int maxTicks = 5)
        {
            _accumulator += deltaSeconds;
            var ticks = 0;
            while (_accumulator >= TickSeconds && ticks < maxTicks)
            {
                Step();
                _accumulator -= TickSeconds;
                ticks++;
            }

            if (ticks == maxTicks)
                _accumulator = 0f; // Drop backlog instead of spiralling on a slow frame.
            return ticks;
        }

        public void Step()
        {
            _group.Update();
            Grid.StepLayer(TickSeconds);
        }

        public void DrainEvents(List<SimulationEvent> into)
        {
            var buffer = _em.GetBuffer<SimulationEvent>(_singleton);
            for (var i = 0; i < buffer.Length; i++)
                into.Add(buffer[i]);
            buffer.Clear();
        }

        public Entity AddFireSource(string objectId, float3 position, float intensity, float activationSeconds)
        {
            var e = _em.CreateEntity();
            _em.AddComponentData(e, new FireSource
            {
                ObjectId = objectId,
                Cell = Grid.Layout.CellAtClamped(position),
                Intensity = intensity,
                ActivationTime = activationSeconds,
            });
            return e;
        }

        public Entity AddSmokeSource(string objectId, float3 position, float ratePerSecond, float activationSeconds)
        {
            var e = _em.CreateEntity();
            _em.AddComponentData(e, new SmokeSource
            {
                ObjectId = objectId,
                Cell = Grid.Layout.CellAtClamped(position),
                RatePerSecond = ratePerSecond,
                ActivationTime = activationSeconds,
            });
            return e;
        }

        public Entity AddSprinkler(string objectId, float3 position, SprinklerMode mode, float activationTemperatureC,
            float coverageRadiusMeters, float fireIntensityReduction, float activationDelaySeconds)
        {
            var e = _em.CreateEntity();
            _em.AddComponentData(e, new Sprinkler
            {
                ObjectId = objectId,
                Cell = Grid.Layout.CellAtClamped(position),
                Mode = mode,
                ActivationTemperatureC = activationTemperatureC,
                CoverageRadiusMeters = coverageRadiusMeters,
                FireIntensityReduction = fireIntensityReduction,
                ActivationDelaySeconds = activationDelaySeconds,
            });
            return e;
        }

        public Entity AddDetector(string objectId, float3 position, float smokeThreshold)
        {
            var e = _em.CreateEntity();
            _em.AddComponentData(e, new SmokeDetector
            {
                ObjectId = objectId,
                Cell = Grid.Layout.CellAtClamped(position),
                SmokeThreshold = smokeThreshold,
            });
            return e;
        }

        public Entity AddDoor(string objectId, IReadOnlyList<int> cells, bool open, bool blocked)
        {
            var e = _em.CreateEntity();
            _em.AddComponentData(e, new Door { ObjectId = objectId, Open = open, Blocked = blocked });
            var buffer = _em.AddBuffer<DoorCell>(e);
            foreach (var c in cells)
            {
                buffer.Add(new DoorCell { Cell = c });
                Grid.AddFlags(c, CellFlags.Door);
                if (!open)
                    Grid.AddFlags(c, CellFlags.DoorClosed);
            }

            return e;
        }

        public Entity AddTimeline(string objectId, TimelineKind kind, float atSeconds)
        {
            var e = _em.CreateEntity();
            _em.AddComponentData(e, new TimelineEntry { ObjectId = objectId, Kind = kind, AtSeconds = atSeconds });
            return e;
        }

        public Entity AddNpc(string objectId, float3 position, NpcBehaviour behaviour, bool mobilityImpaired, float replanIntervalSeconds)
        {
            var e = _em.CreateEntity();
            _em.AddComponentData(e, new NpcAgent
            {
                ObjectId = objectId,
                Position = position,
                Behaviour = behaviour,
                MobilityImpaired = mobilityImpaired,
                Health = 1f,
                ReplanInterval = math.max(0.5f, replanIntervalSeconds),
                RandomKey = math.hash(new FixedString64Bytes(objectId).GetHashCode() * new int2(7919, 104729)),
            });
            _em.AddComponentData(e, new NpcPathCursor());
            _em.AddBuffer<NpcPathPoint>(e);
            return e;
        }

        public void ForEachNpc(Action<NpcAgent> visit)
        {
            using var query = _em.CreateEntityQuery(ComponentType.ReadOnly<NpcAgent>());
            using var agents = query.ToComponentDataArray<NpcAgent>(Allocator.Temp);
            foreach (var a in agents)
                visit(a);
        }

        public bool TryGetNpc(string objectId, out NpcAgent agent)
        {
            FixedString64Bytes id = objectId;
            using var query = _em.CreateEntityQuery(ComponentType.ReadOnly<NpcAgent>());
            using var agents = query.ToComponentDataArray<NpcAgent>(Allocator.Temp);
            foreach (var a in agents)
            {
                if (a.ObjectId == id)
                {
                    agent = a;
                    return true;
                }
            }

            agent = default;
            return false;
        }

        public bool TryGetSprinkler(string objectId, out Sprinkler sprinkler)
        {
            FixedString64Bytes id = objectId;
            using var query = _em.CreateEntityQuery(ComponentType.ReadOnly<Sprinkler>());
            using var items = query.ToComponentDataArray<Sprinkler>(Allocator.Temp);
            foreach (var s in items)
            {
                if (s.ObjectId == id)
                {
                    sprinkler = s;
                    return true;
                }
            }

            sprinkler = default;
            return false;
        }

        public void Dispose()
        {
            if (World.IsCreated)
                World.Dispose();
            Grid.Dispose();
        }
    }
}
