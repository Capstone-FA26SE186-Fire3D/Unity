using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>Links a scene object to its IFC element so scenario anchors (anchorId) can find it.</summary>
    public sealed class IfcElement : MonoBehaviour
    {
        public string GlobalId;
        public string IfcClass;
    }
}
