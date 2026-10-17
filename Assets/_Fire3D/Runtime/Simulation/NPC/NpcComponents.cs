using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace Fire3D.Simulation
{
    public enum NpcMode : byte
    {
        Idle = 0,
        Evacuating = 1,
        Panicking = 2,
        Following = 3,
        Injured = 4,
        Unconscious = 5,
        Evacuated = 6,
    }

    public enum NpcBehaviour : byte
    {
        Calm = 0,
        Panic = 1,
    }

    /// <summary>Simulated occupant. Movement and state live here; the view is a pooled GameObject.</summary>
    public struct NpcAgent : IComponentData
    {
        public FixedString64Bytes ObjectId;
        public float3 Position;
        public float3 Velocity;
        public float Yaw;
        public NpcMode Mode;
        public NpcBehaviour Behaviour;
        public bool MobilityImpaired;
        /// <summary>The player called this NPC; a panicking NPC calms down and waits.</summary>
        public bool Called;
        public bool Following;
        public float Health;
        public float SmokeInhaled;
        public float NextReplanAt;
        public float ReplanInterval;
        public uint RandomKey;
    }

    public struct NpcPathPoint : IBufferElementData
    {
        public float3 Position;
    }

    public struct NpcPathCursor : IComponentData
    {
        public int Index;
        public int GoalCell;
    }

    internal static class NpcCommands
    {
        public static void Apply(EntityManager em, HazardCommand cmd)
        {
            var query = em.CreateEntityQuery(ComponentType.ReadWrite<NpcAgent>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            foreach (var e in entities)
            {
                var npc = em.GetComponentData<NpcAgent>(e);
                if (npc.ObjectId != cmd.ObjectId)
                    continue;
                if (npc.Mode == NpcMode.Unconscious || npc.Mode == NpcMode.Evacuated)
                    return;
                if (cmd.Kind == HazardCommandKind.CallNpc)
                {
                    npc.Called = true;
                    if (npc.Mode == NpcMode.Panicking || npc.Mode == NpcMode.Idle)
                        npc.Mode = NpcMode.Idle;
                }
                else
                {
                    npc.Called = true;
                    npc.Following = true;
                    npc.Mode = NpcMode.Following;
                }

                npc.NextReplanAt = 0f;
                em.SetComponentData(e, npc);
                return;
            }
        }
    }
}
