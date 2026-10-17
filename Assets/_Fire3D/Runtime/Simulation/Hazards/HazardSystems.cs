using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace Fire3D.Simulation
{
    /// <summary>All simulation systems run inside this group, stepped at a fixed tick by <see cref="FireSimulation"/>.</summary>
    [DisableAutoCreation]
    public partial class FireSimulationGroup : ComponentSystemGroup
    {
    }

    internal static class SimulationSingletons
    {
        public static void Emit(EntityManager em, Entity singleton, SimulationEventKind kind, FixedString64Bytes objectId, float time)
        {
            em.GetBuffer<SimulationEvent>(singleton).Add(new SimulationEvent { Kind = kind, ObjectId = objectId, Time = time });
        }

        public static void RaiseAlarm(EntityManager em, Entity singleton, float time, FixedString64Bytes source)
        {
            var alarm = em.GetComponentData<AlarmState>(singleton);
            if (alarm.Raised)
                return;
            alarm.Raised = true;
            alarm.RaisedAt = time;
            em.SetComponentData(singleton, alarm);
            Emit(em, singleton, SimulationEventKind.AlarmRaised, source, time);
        }
    }

    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup), OrderFirst = true)]
    public partial class SimulationClockSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var e = SystemAPI.GetSingletonEntity<SimulationClock>();
            var clock = EntityManager.GetComponentData<SimulationClock>(e);
            clock.Tick++;
            EntityManager.SetComponentData(e, clock);
        }
    }

    /// <summary>Applies gameplay commands (doors, extinguisher, alarm, NPC calls) at the tick boundary.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(SimulationClockSystem))]
    public partial class HazardCommandSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var gridRef = EntityManager.GetComponentObject<HazardGridRef>(singleton);
            var grid = gridRef.Grid;
            var clock = EntityManager.GetComponentData<SimulationClock>(singleton);

            // Suppression is rebuilt every tick: sprinklers re-apply theirs, extinguisher bursts last one tick.
            for (var i = 0; i < grid.CellCount; i++)
                grid.Suppression[i] = 0f;

            var commands = EntityManager.GetBuffer<HazardCommand>(singleton).ToNativeArray(Allocator.Temp);
            EntityManager.GetBuffer<HazardCommand>(singleton).Clear();

            foreach (var cmd in commands)
            {
                switch (cmd.Kind)
                {
                    case HazardCommandKind.SetDoorOpen:
                        SetDoor(grid, cmd.ObjectId, cmd.Flag);
                        break;
                    case HazardCommandKind.Extinguish:
                        ApplyExtinguisher(grid, cmd);
                        break;
                    case HazardCommandKind.RaiseAlarm:
                        SimulationSingletons.RaiseAlarm(EntityManager, singleton, clock.Time, cmd.ObjectId);
                        break;
                    case HazardCommandKind.CallNpc:
                    case HazardCommandKind.LeadNpc:
                        NpcCommands.Apply(EntityManager, cmd);
                        break;
                }
            }

            commands.Dispose();
        }

        private void SetDoor(HazardGrid grid, FixedString64Bytes id, bool open)
        {
            foreach (var (door, cells) in SystemAPI.Query<RefRW<Door>, DynamicBuffer<DoorCell>>())
            {
                if (door.ValueRO.ObjectId != id)
                    continue;
                if (open && door.ValueRO.Blocked)
                    return;
                door.ValueRW.Open = open;
                foreach (var c in cells)
                {
                    if (open)
                        grid.RemoveFlags(c.Cell, CellFlags.DoorClosed);
                    else
                        grid.AddFlags(c.Cell, CellFlags.DoorClosed);
                }

                return;
            }
        }

        private static void ApplyExtinguisher(HazardGrid grid, HazardCommand cmd)
        {
            var layout = grid.Layout;
            var dir = math.normalizesafe(new float3(cmd.Direction.x, 0f, cmd.Direction.z), new float3(0, 0, 1));
            var r = (int)math.ceil(cmd.Radius / layout.CellSize);
            if (!layout.TryCellAt(cmd.Position, out var origin))
                return;
            var o = layout.Decode(origin);
            for (var dz = -r; dz <= r; dz++)
            for (var dx = -r; dx <= r; dx++)
            {
                if (!layout.InBounds(o.y, o.x + dx, o.z + dz))
                    continue;
                var i = layout.Index(o.y, o.x + dx, o.z + dz);
                var to = layout.CellCenter(i) - new float3(cmd.Position.x, layout.CellCenter(i).y, cmd.Position.z);
                var dist = math.length(to);
                if (dist > cmd.Radius)
                    continue;
                // 35° half-angle cone in front of the nozzle; the cell under the nozzle always counts.
                if (dist > layout.CellSize && math.dot(to / dist, dir) < 0.82f)
                    continue;
                grid.Suppression[i] = math.max(grid.Suppression[i], cmd.Strength);
            }
        }
    }

    /// <summary>Fires scheduled scenario events: power outage, alarm sounding or failing.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(HazardCommandSystem))]
    public partial class TimelineSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var time = EntityManager.GetComponentData<SimulationClock>(singleton).Time;
            foreach (var entry in SystemAPI.Query<RefRW<TimelineEntry>>())
            {
                if (entry.ValueRO.Fired || time < entry.ValueRO.AtSeconds)
                    continue;
                entry.ValueRW.Fired = true;
                if (entry.ValueRO.Kind == TimelineKind.PowerOutage)
                {
                    EntityManager.SetComponentData(singleton, new PowerState { Outage = true, ChangedAt = time });
                }
                else if (entry.ValueRO.Kind == TimelineKind.Alarm)
                {
                    SimulationSingletons.RaiseAlarm(EntityManager, singleton, time, entry.ValueRO.ObjectId);
                }
            }
        }
    }

    /// <summary>Ignites fire sources at their activation time and feeds smoke sources.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(TimelineSystem))]
    public partial class FireSourceSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var grid = EntityManager.GetComponentObject<HazardGridRef>(singleton).Grid;
            var clock = EntityManager.GetComponentData<SimulationClock>(singleton);
            var time = clock.Time;

            foreach (var source in SystemAPI.Query<RefRW<FireSource>>())
            {
                if (source.ValueRO.Ignited || time < source.ValueRO.ActivationTime)
                    continue;
                source.ValueRW.Ignited = true;
                var intensity01 = math.saturate(source.ValueRO.Intensity / 10f);
                Ignite(grid, source.ValueRO.Cell, math.max(0.15f, intensity01), 0.45f + intensity01 * 0.5f, 1 + (int)(intensity01 * 3f));
                SimulationSingletons.Emit(EntityManager, singleton, SimulationEventKind.FireIgnited, source.ValueRO.ObjectId, time);
            }

            foreach (var source in SystemAPI.Query<RefRO<SmokeSource>>())
            {
                if (time < source.ValueRO.ActivationTime)
                    continue;
                var i = source.ValueRO.Cell;
                grid.Smoke[i] = math.saturate(grid.Smoke[i] + source.ValueRO.RatePerSecond * clock.TickSeconds);
            }
        }

        internal static void Ignite(HazardGrid grid, int cell, float fire, float fuel, int radiusCells)
        {
            var layout = grid.Layout;
            var c = layout.Decode(cell);
            for (var dz = -radiusCells; dz <= radiusCells; dz++)
            for (var dx = -radiusCells; dx <= radiusCells; dx++)
            {
                if (!layout.InBounds(c.y, c.x + dx, c.z + dz))
                    continue;
                var i = layout.Index(c.y, c.x + dx, c.z + dz);
                if ((grid.GetFlags(i) & CellFlags.Wall) != 0)
                    continue;
                grid.Fuel[i] = math.max(grid.Fuel[i], fuel);
            }

            grid.Fire[cell] = math.max(grid.Fire[cell], fire);
        }
    }

    /// <summary>Sprinklers: arm when their cell passes the activation temperature, then suppress fire in their radius.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(FireSourceSystem))]
    public partial class SprinklerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var grid = EntityManager.GetComponentObject<HazardGridRef>(singleton).Grid;
            var time = EntityManager.GetComponentData<SimulationClock>(singleton).Time;
            var layout = grid.Layout;

            foreach (var s in SystemAPI.Query<RefRW<Sprinkler>>())
            {
                ref var sp = ref s.ValueRW;
                if (sp.Mode == SprinklerMode.Disabled)
                    continue;

                if (!sp.ThresholdReached && grid.Heat[sp.Cell] >= sp.ActivationTemperatureC)
                {
                    sp.ThresholdReached = true;
                    sp.ThresholdReachedAt = time;
                }

                if (sp.ThresholdReached && !sp.Activated && time - sp.ThresholdReachedAt >= sp.ActivationDelaySeconds)
                {
                    sp.Activated = true;
                    // A faulty head opens but delivers no water; the event still tells the debrief it tried.
                    SimulationSingletons.Emit(EntityManager, singleton, SimulationEventKind.SprinklerActivated, sp.ObjectId, time);
                }

                if (!sp.Activated || sp.Mode == SprinklerMode.Faulty)
                    continue;

                var c = layout.Decode(sp.Cell);
                var r = (int)math.ceil(sp.CoverageRadiusMeters / layout.CellSize);
                var rate = sp.FireIntensityReduction * 2.5f;
                for (var dz = -r; dz <= r; dz++)
                for (var dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r * r || !layout.InBounds(c.y, c.x + dx, c.z + dz))
                        continue;
                    var i = layout.Index(c.y, c.x + dx, c.z + dz);
                    grid.Suppression[i] = math.max(grid.Suppression[i], rate);
                }
            }
        }
    }

    /// <summary>Advances fire, heat and smoke one tick with a Burst job over all cells.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(SprinklerSystem))]
    public partial class HazardStepSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var gridRef = EntityManager.GetComponentObject<HazardGridRef>(singleton);
            var grid = gridRef.Grid;
            var clock = EntityManager.GetComponentData<SimulationClock>(singleton);

            var settings = gridRef.Settings;
            var subSteps = math.max(1, settings.SubSteps);
            settings.TickSeconds = clock.TickSeconds / subSteps;
            for (var sub = 0; sub < subSteps; sub++)
            {
                var job = new HazardStepJob
                {
                    Layout = grid.Layout,
                    Settings = settings,
                    Seed = clock.Seed,
                    Tick = clock.Tick * subSteps + sub,
                    Flags = grid.Flags,
                    FireIn = grid.Fire,
                    HeatIn = grid.Heat,
                    SmokeIn = grid.Smoke,
                    FuelIn = grid.Fuel,
                    Suppression = grid.Suppression,
                    Wind = grid.Wind,
                    FireOut = grid.FireNext,
                    HeatOut = grid.HeatNext,
                    SmokeOut = grid.SmokeNext,
                    FuelOut = grid.FuelNext,
                };
                job.Schedule(grid.CellCount, 256, Dependency).Complete();
                grid.SwapBuffers();
            }
        }
    }

    [BurstCompile(FloatMode = FloatMode.Strict)]
    public struct HazardStepJob : IJobParallelFor
    {
        public GridLayout Layout;
        public HazardSettings Settings;
        public uint Seed;
        public long Tick;

        [ReadOnly] public NativeArray<byte> Flags;
        [ReadOnly] public NativeArray<float> FireIn;
        [ReadOnly] public NativeArray<float> HeatIn;
        [ReadOnly] public NativeArray<float> SmokeIn;
        [ReadOnly] public NativeArray<float> FuelIn;
        [ReadOnly] public NativeArray<float> Suppression;
        [ReadOnly] public NativeArray<float2> Wind;

        [WriteOnly] public NativeArray<float> FireOut;
        [WriteOnly] public NativeArray<float> HeatOut;
        [WriteOnly] public NativeArray<float> SmokeOut;
        [WriteOnly] public NativeArray<float> FuelOut;

        public void Execute(int i)
        {
            var flags = (CellFlags)Flags[i];
            var ambient = Settings.AmbientC;
            if ((flags & CellFlags.Wall) != 0)
            {
                FireOut[i] = 0f;
                HeatOut[i] = ambient;
                SmokeOut[i] = 0f;
                FuelOut[i] = 0f;
                return;
            }

            if ((flags & CellFlags.Outside) != 0)
            {
                FireOut[i] = 0f;
                HeatOut[i] = ambient;
                SmokeOut[i] = 0f;
                FuelOut[i] = 0f;
                return;
            }

            var dt = Settings.TickSeconds;
            var c = Layout.Decode(i);
            var h = HeatIn[i];
            var s = SmokeIn[i];
            var f = FireIn[i];
            var fuel = FuelIn[i];

            var heatLap = 0f;
            var smokeLap = 0f;
            var neighbourFire = 0f;
            Accumulate(c, 1, 0, flags, h, s, ref heatLap, ref smokeLap, ref neighbourFire);
            Accumulate(c, -1, 0, flags, h, s, ref heatLap, ref smokeLap, ref neighbourFire);
            Accumulate(c, 0, 1, flags, h, s, ref heatLap, ref smokeLap, ref neighbourFire);
            Accumulate(c, 0, -1, flags, h, s, ref heatLap, ref smokeLap, ref neighbourFire);

            // Upwind advection of smoke along the zone wind.
            var w = Wind[i];
            var advect = 0f;
            if (math.abs(w.x) > 1e-4f)
                advect += math.abs(w.x) * (SmokeAt(c, w.x > 0 ? -1 : 1, 0, flags, s) - s);
            if (math.abs(w.y) > 1e-4f)
                advect += math.abs(w.y) * (SmokeAt(c, 0, w.y > 0 ? -1 : 1, flags, s) - s);

            // Smoke and heat rise through stairs and shafts.
            var vertical = 0f;
            var verticalHeat = 0f;
            if ((flags & CellFlags.Stair) != 0)
            {
                if (c.y > 0)
                {
                    var below = Layout.Index(c.y - 1, c.x, c.z);
                    if (((CellFlags)Flags[below] & CellFlags.Stair) != 0)
                    {
                        vertical += SmokeIn[below];
                        verticalHeat += (HeatIn[below] - h) * 0.5f;
                    }
                }

                if (c.y < Layout.Floors - 1)
                {
                    var above = Layout.Index(c.y + 1, c.x, c.z);
                    if (((CellFlags)Flags[above] & CellFlags.Stair) != 0)
                        vertical -= s;
                }
            }

            var hNext = h + dt * (Settings.HeatDiffusion * heatLap
                                  + Settings.HeatCoolingPerSecond * (ambient - h)
                                  + f * Settings.FireHeatRelaxPerSecond * (Settings.MaxFireHeatC - h)
                                  + Settings.StairRisePerSecond * verticalHeat);
            HeatOut[i] = math.clamp(hNext, ambient - 5f, 1200f);

            var sNext = s + dt * (Settings.SmokeDiffusion * smokeLap
                                  + f * Settings.SmokeYieldPerSecond
                                  - Settings.SmokeVentPerSecond * s
                                  + Settings.WindAdvection * advect
                                  + Settings.StairRisePerSecond * vertical);
            SmokeOut[i] = math.saturate(sNext);

            var suppression = Suppression[i];
            if (f > 0f)
            {
                f += fuel > 0f
                    ? dt * Settings.FireGrowthPerSecond * (1f - f)
                    : -dt * Settings.FireDecayPerSecond;
                f -= dt * suppression;
                fuel = math.max(0f, fuel - dt * Settings.FuelBurnPerSecond * f);
                if (f < 0.01f)
                    f = 0f;
            }
            else if (fuel > 0.01f && suppression < 0.05f && neighbourFire > 0f && h >= Settings.IgnitionHeatC
                     && (flags & CellFlags.Walkable) != 0)
            {
                var p = 1f - math.exp(-Settings.SpreadRatePerSecond * neighbourFire * dt);
                if (DeterministicRandom.Value01(Seed, Tick, i) < p)
                    f = 0.05f;
            }

            FireOut[i] = math.saturate(f);
            FuelOut[i] = fuel;
        }

        private void Accumulate(int3 c, int dx, int dz, CellFlags self, float h, float s,
            ref float heatLap, ref float smokeLap, ref float neighbourFire)
        {
            var x = c.x + dx;
            var z = c.z + dz;
            if (!Layout.InBounds(c.y, x, z))
                return;
            var n = Layout.Index(c.y, x, z);
            var nf = (CellFlags)Flags[n];
            if ((nf & CellFlags.Wall) != 0)
                return;
            var leak = ((self | nf) & CellFlags.DoorClosed) != 0 ? Settings.DoorLeak : 1f;
            heatLap += (HeatIn[n] - h) * leak;
            smokeLap += (SmokeIn[n] - s) * leak;
            neighbourFire += FireIn[n] * (leak < 1f ? 0f : 1f);
        }

        private float SmokeAt(int3 c, int dx, int dz, CellFlags self, float fallback)
        {
            var x = c.x + dx;
            var z = c.z + dz;
            if (!Layout.InBounds(c.y, x, z))
                return fallback;
            var n = Layout.Index(c.y, x, z);
            var nf = (CellFlags)Flags[n];
            if ((nf & CellFlags.Wall) != 0 || ((self | nf) & CellFlags.DoorClosed) != 0)
                return fallback;
            return SmokeIn[n];
        }
    }

    /// <summary>Smoke detectors trigger the building alarm once local smoke passes their threshold.</summary>
    [DisableAutoCreation]
    [UpdateInGroup(typeof(FireSimulationGroup))]
    [UpdateAfter(typeof(HazardStepSystem))]
    public partial class SmokeDetectorSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var singleton = SystemAPI.GetSingletonEntity<SimulationClock>();
            var grid = EntityManager.GetComponentObject<HazardGridRef>(singleton).Grid;
            var time = EntityManager.GetComponentData<SimulationClock>(singleton).Time;
            var disabled = EntityManager.GetComponentData<AlarmState>(singleton).DetectorsDisabled;
            if (disabled)
                return;

            var triggered = new NativeList<FixedString64Bytes>(Allocator.Temp);
            foreach (var d in SystemAPI.Query<RefRW<SmokeDetector>>())
            {
                if (d.ValueRO.Triggered || grid.Smoke[d.ValueRO.Cell] < d.ValueRO.SmokeThreshold)
                    continue;
                d.ValueRW.Triggered = true;
                triggered.Add(d.ValueRO.ObjectId);
            }

            foreach (var id in triggered)
            {
                SimulationSingletons.Emit(EntityManager, singleton, SimulationEventKind.DetectorTriggered, id, time);
                SimulationSingletons.RaiseAlarm(EntityManager, singleton, time, id);
            }

            triggered.Dispose();
        }
    }
}
