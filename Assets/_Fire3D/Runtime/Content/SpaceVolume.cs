using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>Space volume from IFC (category drives fuel load and guided hints).</summary>
    public sealed class SpaceVolume : MonoBehaviour
    {
        public string GlobalId;
        public string Category;
        public Bounds Bounds;
    }
}
