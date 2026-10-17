using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>Stair or open shaft volume: grid cells inside connect floors vertically.</summary>
    public sealed class StairVolume : MonoBehaviour
    {
        public Vector3 Size = new(2f, 3.2f, 4f);
        public Bounds WorldBounds => new(transform.position + Vector3.up * Size.y * 0.5f, Size);

        private void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.25f);
            Gizmos.DrawCube(WorldBounds.center, WorldBounds.size);
        }
    }
}
