using System.Collections.Generic;
using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>
    /// Applies a baked lighting scenario across all loaded floor scenes by rebuilding the global lightmap array
    /// and re-pointing every <see cref="BakedRendererId"/> renderer. Light probes come from the first floor that
    /// has them (the worker bakes probes for the whole building into each scenario).
    /// </summary>
    public sealed class LightingScenarioSwitcher : MonoBehaviour
    {
        public const string Normal = "Normal";
        public const string Outage = "Outage";

        [SerializeField] private List<LightingScenarioData> _scenarios = new();

        public string Active { get; private set; }

        public void Register(LightingScenarioData data)
        {
            if (data != null && !_scenarios.Contains(data))
                _scenarios.Add(data);
        }

        public bool Has(string scenario) => _scenarios.Exists(s => s.Scenario == scenario);

        public void Apply(string scenario)
        {
            var sets = _scenarios.FindAll(s => s.Scenario == scenario);
            if (sets.Count == 0)
                return;

            var lightmaps = new List<LightmapData>();
            var placement = new Dictionary<int, (int index, Vector4 scaleOffset)>();
            LightProbes probes = null;
            Color? ambient = null;
            foreach (var set in sets)
            {
                var offset = lightmaps.Count;
                for (var i = 0; i < set.LightmapColor.Length; i++)
                {
                    lightmaps.Add(new LightmapData
                    {
                        lightmapColor = set.LightmapColor[i],
                        lightmapDir = i < set.LightmapDirection.Length ? set.LightmapDirection[i] : null,
                    });
                }

                foreach (var r in set.Renderers)
                    placement[r.BakedId] = (r.LightmapIndex < 0 ? -1 : r.LightmapIndex + offset, r.ScaleOffset);
                if (probes == null)
                    probes = set.Probes;
                ambient ??= set.AmbientColor;
            }

            LightmapSettings.lightmaps = lightmaps.ToArray();
            foreach (var id in FindObjectsByType<BakedRendererId>(FindObjectsSortMode.None))
            {
                if (!placement.TryGetValue(id.Id, out var p) || !id.TryGetComponent<Renderer>(out var renderer))
                    continue;
                renderer.lightmapIndex = p.index;
                renderer.lightmapScaleOffset = p.scaleOffset;
            }

            if (probes != null)
                LightmapSettings.lightProbes = probes;
            if (ambient.HasValue)
                RenderSettings.ambientLight = ambient.Value;
            Active = scenario;
        }
    }
}
