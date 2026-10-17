using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Fire3D.Simulation
{
    /// <summary>Exposure, awareness and state transitions of NPCs (few entities: main thread is enough).</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(SmokeDetectorSystem))]
    public partial class NpcSenseSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var gridRef = EntityManager.GetComponentObject<HazardGridRef>(singleton);
            var grid = gridRef.Grid;
            var clock = EntityManager.GetComponentData<SimulationClock>(singleton);
            var alarm = EntityManager.GetComponentData<AlarmState>(singleton);
            var player = EntityManager.GetComponentData<PlayerState>(singleton);
            var dt = clock.TickSeconds;
            var ambient = gridRef.Settings.AmbientC;

            var events = new NativeList<SimulationEvent>(Allocator.Temp);
            foreach (var agentRef in SystemAPI.Query<RefRW<NpcAgent>>())
            {
                ref var npc = ref agentRef.ValueRW;
                if (npc.Mode == NpcMode.Unconscious || npc.Mode == NpcMode.Evacuated)
                    continue;

                var sample = grid.Sample(npc.Position);
                if (sample.Valid)
                {
                    npc.SmokeInhaled += sample.Smoke * dt;
                    var damage = math.max(0f, sample.HeatC - ambient - 34f) / 400f + sample.Smoke * 0.012f + sample.Fire * 0.25f;
                    npc.Health = math.max(0f, npc.Health - damage * dt);
                }

                if (npc.Health <= 0f)
                {
                    npc.Mode = NpcMode.Unconscious;
                    npc.Velocity = float3.zero;
                    events.Add(new SimulationEvent { Kind = SimulationEventKind.NpcCollapsed, ObjectId = npc.ObjectId, Time = clock.Time });
                    continue;
                }

                if (sample.Valid && (sample.Flags & CellFlags.Exit) != 0 && npc.Mode != NpcMode.Idle)
                {
                    npc.Mode = NpcMode.Evacuated;
                    npc.Velocity = float3.zero;
                    events.Add(new SimulationEvent { Kind = SimulationEventKind.NpcEvacuated, ObjectId = npc.ObjectId, Time = clock.Time });
                    continue;
                }

                if (npc.Following)
                {
                    npc.Mode = player.Present ? NpcMode.Following : NpcMode.Evacuating;
                    continue;
                }

                var aware = alarm.Raised || sample.Smoke > 0.12f || grid.MaxFireNear(npc.Position, 4f) > 0.15f;
                if (npc.Mode == NpcMode.Idle && aware && !npc.Called)
                    npc.Mode = npc.Behaviour == NpcBehaviour.Panic ? NpcMode.Panicking : NpcMode.Evacuating;
                else if (npc.Mode == NpcMode.Idle && aware && npc.Called)
                    npc.Mode = NpcMode.Evacuating;

                if (npc.Health < 0.4f && npc.Mode == NpcMode.Evacuating)
                    npc.Mode = NpcMode.Injured;
            }

            var buffer = EntityManager.GetBuffer<SimulationEvent>(singleton);
            foreach (var e in events)
                buffer.Add(e);
            events.Dispose();
        }
    }

    /// <summary>Replans NPC routes with the risk-aware A* in parallel Burst jobs.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(NpcSenseSystem))]
    public partial class NpcPlanSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var gridRef = EntityManager.GetComponentObject<HazardGridRef>(singleton);
            var grid = gridRef.Grid;
            var clock = EntityManager.GetComponentData<SimulationClock>(singleton);
            var player = EntityManager.GetComponentData<PlayerState>(singleton);

            var job = new NpcPlanJob
            {
                View = new GridNavView
                {
                    Layout = grid.Layout,
                    Weights = gridRef.Weights,
                    AmbientC = gridRef.Settings.AmbientC,
                    Flags = grid.Flags,
                    Fire = grid.Fire,
                    Smoke = grid.Smoke,
                    Heat = grid.Heat,
                },
                Time = clock.Time,
                Tick = clock.Tick,
                Seed = clock.Seed,
                PlayerCell = player.Present ? grid.Layout.CellAtClamped(player.Position) : -1,
            };
            Dependency = job.ScheduleParallel(Dependency);
            Dependency.Complete();
        }
    }

    [BurstCompile]
    public partial struct NpcPlanJob : IJobEntity
    {
        public GridNavView View;
        public float Time;
        public long Tick;
        public uint Seed;
        public int PlayerCell;

        private void Execute(ref NpcAgent npc, ref NpcPathCursor cursor, DynamicBuffer<NpcPathPoint> path)
        {
            if (npc.Mode == NpcMode.Unconscious || npc.Mode == NpcMode.Evacuated || npc.Mode == NpcMode.Idle)
            {
                path.Clear();
                return;
            }

            if (Time < npc.NextReplanAt && path.Length > 0)
                return;
            npc.NextReplanAt = Time + (npc.Mode == NpcMode.Following ? 1f : npc.ReplanInterval);

            var start = View.Layout.CellAtClamped(npc.Position);
            var goal = -1;
            if (npc.Mode == NpcMode.Following)
            {
                goal = PlayerCell;
                if (goal < 0 || !View.IsWalkable(goal))
                    return;
            }
            else if (npc.Mode == NpcMode.Panicking)
            {
                goal = PickWanderCell(start, npc.RandomKey);
                if (goal < 0)
                    return;
            }

            var cells = new NativeList<int>(64, Allocator.Temp);
            if (GridPathfinder.FindPath(View, start, goal, cells))
            {
                path.Clear();
                for (var i = 1; i < cells.Length; i++)
                    path.Add(new NpcPathPoint { Position = View.Layout.CellCenter(cells[i]) });
                cursor.Index = 0;
                cursor.GoalCell = cells[cells.Length - 1];
            }

            cells.Dispose();
        }

        private int PickWanderCell(int start, uint key)
        {
            var layout = View.Layout;
            var c = layout.Decode(start);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var r1 = DeterministicRandom.Value01(Seed ^ key, Tick, attempt * 2);
                var r2 = DeterministicRandom.Value01(Seed ^ key, Tick, attempt * 2 + 1);
                var dx = (int)math.round((r1 * 2f - 1f) * 12f);
                var dz = (int)math.round((r2 * 2f - 1f) * 12f);
                if (!layout.InBounds(c.y, c.x + dx, c.z + dz))
                    continue;
                var cell = layout.Index(c.y, c.x + dx, c.z + dz);
                if (View.IsWalkable(cell) && View.Fire[cell] < 0.05f)
                    return cell;
            }

            return -1;
        }
    }

    /// <summary>Moves NPCs along their path; speed depends on state, mobility and smoke.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(NpcPlanSystem))]
    public partial class NpcMoveSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var grid = EntityManager.GetComponentObject<HazardGridRef>(singleton).Grid;
            var clock = EntityManager.GetComponentData<SimulationClock>(singleton);
            var player = EntityManager.GetComponentData<PlayerState>(singleton);
            Dependency = new NpcMoveJob
            {
                Dt = clock.TickSeconds,
                Smoke = grid.Smoke,
                Layout = grid.Layout,
                PlayerPosition = player.Position,
                PlayerPresent = player.Present,
            }.ScheduleParallel(Dependency);
            Dependency.Complete();
        }
    }

    [BurstCompile]
    public partial struct NpcMoveJob : IJobEntity
    {
        public float Dt;
        public GridLayout Layout;
        [ReadOnly] public NativeArray<float> Smoke;
        public float3 PlayerPosition;
        public bool PlayerPresent;

        private void Execute(ref NpcAgent npc, ref NpcPathCursor cursor, DynamicBuffer<NpcPathPoint> path)
        {
            npc.Velocity = float3.zero;
            if (npc.Mode == NpcMode.Unconscious || npc.Mode == NpcMode.Evacuated || npc.Mode == NpcMode.Idle)
                return;
            if (npc.Mode == NpcMode.Following && PlayerPresent && math.distance(npc.Position.xz, PlayerPosition.xz) < 1.4f)
                return;
            if (cursor.Index >= path.Length)
                return;

            var speed = npc.Mode switch
            {
                NpcMode.Panicking => 2.1f,
                NpcMode.Injured => 0.7f,
                NpcMode.Following => 1.6f,
                _ => 1.35f,
            };
            if (npc.MobilityImpaired)
                speed = math.min(speed, 0.6f);
            var smoke = Layout.TryCellAt(npc.Position, out var cell) ? Smoke[cell] : 0f;
            speed *= 1f - 0.5f * smoke;

            var remaining = speed * Dt;
            while (remaining > 0f && cursor.Index < path.Length)
            {
                var target = path[cursor.Index].Position;
                var delta = target - npc.Position;
                var dist = math.length(delta);
                if (dist <= remaining)
                {
                    npc.Position = target;
                    remaining -= dist;
                    cursor.Index++;
                    continue;
                }

                var dir = delta / dist;
                npc.Position += dir * remaining;
                npc.Velocity = dir * speed;
                remaining = 0f;
            }

            if (math.lengthsq(npc.Velocity.xz) > 1e-4f)
                npc.Yaw = math.degrees(math.atan2(npc.Velocity.x, npc.Velocity.z));
        }
    }
}
