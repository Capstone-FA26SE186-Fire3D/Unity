using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>Stair or open shaft volume: grid cells inside connect floors vertically.</summary>
    public sealed class StairVolume : MonoBehaviour
    {
        public Vector3 Size = new(2f, 3.2f, 4f);

        /// <summary>
        /// The flight geometry itself (IFC stair node). Real buildings have the upper slab over the foot of the flight, so
        /// tools measuring the flight must only count hits on this object; null for hand-built volumes.
        /// </summary>
        public Transform Flight;
        public Bounds WorldBounds => new(transform.position + Vector3.up * Size.y * 0.5f, Size);

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.25f);
            Gizmos.DrawCube(WorldBounds.center, WorldBounds.size);
        }
    }
}
