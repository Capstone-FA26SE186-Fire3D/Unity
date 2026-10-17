using System.Collections.Generic;
using Fire3D.Contracts;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Fire3D.Unity.Gameplay.Interactions
{
    /// <summary>
    /// Scenario object the player can act on. Identity is the scenario object id (or IFC GlobalId for anchored
    /// elements) and behaviour comes from the capability in the runtime catalog, never from per-building scripts.
    /// </summary>
    public sealed class Interactable : MonoBehaviour
    {
        private static readonly List<Interactable> Registry = new();

        [SerializeField] private string objectId;
        [SerializeField] private string capabilityId;
        [SerializeField] private string state;
        [SerializeField] private Transform promptAnchor;

        public string ObjectId { get => objectId; set => objectId = value; }
        public string CapabilityId { get => capabilityId; set => capabilityId = value; }

        /// <summary>Object state: doors "open"/"closed", towels "dry"/"wet", extinguisher "sealed"/"armed"/"empty".</summary>
        public string State { get => state; set => state = value; }

        public JObject Parameters { get; set; } = new();
        public bool Held { get; set; }
        public bool Consumed { get; set; }
        public Vector3 PromptPosition => promptAnchor != null ? promptAnchor.position : transform.position + Vector3.up * 1.2f;
        public string DisplayName { get; set; }

        public static IReadOnlyList<Interactable> All => Registry;

        private void OnEnable() => Registry.Add(this);
        private void OnDisable() => Registry.Remove(this);

        public void Configure(string id, string capability, JObject parameters, string initialState = null)
        {
            objectId = id;
            capabilityId = capability;
            Parameters = parameters ?? new JObject();
            state = initialState ?? DefaultState(capability);
        }

        public static string DefaultState(string capability)
        {
            if (Capabilities.IsDoor(capability))
                return "closed";
            if (capability == Capabilities.ItemTowel)
                return "dry";
            if (capability == Capabilities.ExtinguisherPortable)
                return "sealed";
            return "idle";
        }

        public float GetFloat(string name, float fallback)
        {
            var t = Parameters?[name];
            return t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) ? t.Value<float>() : fallback;
        }

        public bool TryGetFloat(string name, out float value)
        {
            var t = Parameters?[name];
            if (t != null && (t.Type == JTokenType.Float || t.Type == JTokenType.Integer))
            {
                value = t.Value<float>();
                return true;
            }

            value = 0f;
            return false;
        }

        public string GetString(string name, string fallback = null)
        {
            var t = Parameters?[name];
            return t != null && t.Type == JTokenType.String ? t.Value<string>() : fallback;
        }
    }
}
