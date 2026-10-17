using Unity.Collections;
using Unity.Entities;

namespace Fire3D.Simulation
{
    public struct FireSource : IComponentData
    {
        public FixedString64Bytes ObjectId;
        public int Cell;
        /// <summary>Scenario intensity 0..10 (fet3d.editor/1 fire.source).</summary>
        public float Intensity;
        public float ActivationTime;
        public bool Ignited;
    }

    public struct SmokeSource : IComponentData
    {
        public FixedString64Bytes ObjectId;
        public int Cell;
        public float RatePerSecond;
        public float ActivationTime;
    }

    public enum SprinklerMode : byte
    {
        Active = 0,
        Disabled = 1,
        Faulty = 2,
    }

    public struct Sprinkler : IComponentData
    {
        public FixedString64Bytes ObjectId;
        public int Cell;
        public SprinklerMode Mode;
        public float ActivationTemperatureC;
        public float CoverageRadiusMeters;
        /// <summary>0..1 share of fire intensity removed per second inside the coverage.</summary>
        public float FireIntensityReduction;
        public float ActivationDelaySeconds;
        public float ThresholdReachedAt;
        public bool ThresholdReached;
        public bool Activated;
    }

    public struct SmokeDetector : IComponentData
    {
        public FixedString64Bytes ObjectId;
        public int Cell;
        public float SmokeThreshold;
        public bool Triggered;
    }

    public struct Door : IComponentData
    {
        public FixedString64Bytes ObjectId;
        public bool Open;
        /// <summary>Locked or blocked by the scenario: cannot be opened.</summary>
        public bool Blocked;
    }

    public struct DoorCell : IBufferElementData
    {
        public int Cell;
    }

    public enum TimelineKind : byte
    {
        PowerOutage = 1,
        Alarm = 2,
    }

    /// <summary>Scheduled scenario event (power outage, alarm sounding or failing).</summary>
    public struct TimelineEntry : IComponentData
    {
        public FixedString64Bytes ObjectId;
        public TimelineKind Kind;
        public float AtSeconds;
        public bool Fired;
    }

    /// <summary>Building-wide lighting state toggled by the timeline; read by presentation.</summary>
    public struct PowerState : IComponentData
    {
        public bool Outage;
        public float ChangedAt;
    }
}
