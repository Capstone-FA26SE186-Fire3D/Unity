using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>Root of a loaded building (one per floor scene). Used to compute bounds and find elements.</summary>
    public sealed class BuildingRoot : MonoBehaviour
    {
        public string FloorId;
        public float Elevation;
    }
}
