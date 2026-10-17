using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>Light that belongs to the normal or emergency set; power outage swaps the sets.</summary>
    public sealed class LightGroupMember : MonoBehaviour
    {
        public LightGroup Group = LightGroup.Normal;
    }
}
