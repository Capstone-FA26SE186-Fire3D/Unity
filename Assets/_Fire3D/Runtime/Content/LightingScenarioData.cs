using System;
using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>
    /// One baked lighting set (normal or power outage) of one floor scene, produced by the Unity build worker:
    /// lightmaps, per-renderer lightmap placement and light probes. The runtime swaps sets without re-baking.
    /// </summary>
    public sealed class LightingScenarioData : ScriptableObject
    {
        [Serializable]
        public struct RendererInfo
        {
            public int BakedId;
            public int LightmapIndex;
            public Vector4 ScaleOffset;
        }

        public string Scenario;
        public string FloorId;
        public Texture2D[] LightmapColor = Array.Empty<Texture2D>();
        public Texture2D[] LightmapDirection = Array.Empty<Texture2D>();
        public RendererInfo[] Renderers = Array.Empty<RendererInfo>();
        public LightProbes Probes;
        public Color AmbientColor = new(0.2f, 0.2f, 0.22f);
    }
}
