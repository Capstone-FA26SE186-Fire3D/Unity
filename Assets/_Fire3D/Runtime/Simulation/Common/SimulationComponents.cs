using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Fire3D.Simulation
{
    /// <summary>Managed singleton that gives systems access to the hazard field.</summary>
    public sealed class HazardGridRef : IComponentData
    {
        public HazardGrid Grid;
        public HazardSettings Settings;
        public NavigationWeights Weights;
    }

    public struct SimulationClock : IComponentData
    {
        public long Tick;
        public float TickSeconds;
        public uint Seed;

        public float Time => Tick * TickSeconds;
    }

    /// <summary>Building-wide alarm state; raised by call points, detectors or the scenario timeline.</summary>
    public struct AlarmState : IComponentData
    {
        public bool Raised;
        public float RaisedAt;
        /// <summary>Scenario can disable the detector network (faulty system).</summary>
        public bool DetectorsDisabled;
    }

    /// <summary>Player pose pushed by gameplay before each tick.</summary>
    public struct PlayerState : IComponentData
    {
        public float3 Position;
        public bool Present;
    }

    public enum SimulationEventKind : byte
    {
        SprinklerActivated = 1,
        DetectorTriggered = 2,
        FireIgnited = 3,
        NpcEvacuated = 4,
        NpcCollapsed = 5,
        AlarmRaised = 6,
    }

    /// <summary>Simulation output drained by gameplay each frame (telemetry, HUD, audio).</summary>
    public struct SimulationEvent : IBufferElementData
    {
        public SimulationEventKind Kind;
        public FixedString64Bytes ObjectId;
        public float Time;
    }

    public enum HazardCommandKind : byte
    {
        SetDoorOpen = 1,
        Extinguish = 2,
        RaiseAlarm = 3,
        CallNpc = 4,
        LeadNpc = 5,
    }

    /// <summary>Gameplay → simulation commands, applied at the start of the next tick.</summary>
    public struct HazardCommand : IBufferElementData
    {
        public HazardCommandKind Kind;
        public FixedString64Bytes ObjectId;
        public float3 Position;
        public float3 Direction;
        public float Radius;
        public float Strength;
        public bool Flag;
    }

    /// <summary>Exit goal cell list (singleton buffer).</summary>
    public struct ExitCell : IBufferElementData
    {
        public int Cell;
    }
}
