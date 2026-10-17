using Unity.Mathematics;

namespace Fire3D.Simulation
{
    /// <summary>Stateless random numbers keyed by (seed, tick, cell) so results do not depend on job scheduling.</summary>
    public static class DeterministicRandom
    {
        public static float Value01(uint seed, long tick, int cell)
        {
            var h = math.hash(new uint4(seed, (uint)tick, (uint)(tick >> 32), (uint)cell));
            return (h >> 8) * (1f / 16777216f);
        }
    }
}
