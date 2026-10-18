using UnityEngine;

namespace Fire3D.Contracts
{
    /// <summary>
    /// Positions in <c>fet3d.editor/1</c> scenarios and <c>fet3d.building/1</c> metadata use the preview GLB frame
    /// (metres, Y-up, right-handed), the frame the web editor places objects in. Unity is left-handed and glTFast imports a
    /// GLB by negating X, so every contract coordinate goes through the same mirror to land on the imported geometry.
    /// </summary>
    public static class ContractFrame
    {
        public static Vector3 ToUnity(float x, float y, float z) => new(-x, y, z);

        public static Vector3 ToUnity(Vector3 contract) => new(-contract.x, contract.y, contract.z);

        public static Vector3 FromUnity(Vector3 unity) => new(-unity.x, unity.y, unity.z);

        /// <summary>Mirroring across X keeps the rotation angle but flips the Y and Z rotation axes.</summary>
        public static Quaternion ToUnity(Quaternion contract) => new(contract.x, -contract.y, -contract.z, contract.w);

        public static Quaternion FromUnity(Quaternion unity) => new(unity.x, -unity.y, -unity.z, unity.w);

        /// <summary>Axis-aligned box given as contract min/max corners.</summary>
        public static Bounds ToUnityBounds(Vector3 contractMin, Vector3 contractMax)
        {
            var a = ToUnity(contractMin);
            var b = ToUnity(contractMax);
            var bounds = new Bounds(a, Vector3.zero);
            bounds.Encapsulate(b);
            return bounds;
        }
    }
}
