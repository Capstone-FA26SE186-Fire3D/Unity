using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>Stable id the build worker assigns to each lightmapped renderer so scenarios can be re-applied.</summary>
    [DisallowMultipleComponent]
    public sealed class BakedRendererId : MonoBehaviour
    {
        public int Id;
    }
}
