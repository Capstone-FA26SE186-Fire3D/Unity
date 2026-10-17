using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fire3D.Contracts
{
    /// <summary>
    /// Scenario snapshot in the <c>fet3d.editor/1</c> format (BE contracts/editor/v1/scenario-state.schema.json).
    /// Unity only reads the parts it simulates; the rubric stays on the server and is never evaluated here.
    /// </summary>
    public sealed class ScenarioState
    {
        public const string Schema = "fet3d.editor/1";

        [JsonProperty("schemaVersion")] public string SchemaVersion;
        [JsonProperty("name")] public string Name;
        [JsonProperty("runtimeVersion")] public string RuntimeVersion;
        [JsonProperty("timeLimitSeconds")] public int? TimeLimitSeconds;
        [JsonProperty("randomSeed")] public long? RandomSeed;
        [JsonProperty("replanIntervalSeconds")] public int? ReplanIntervalSeconds;
        [JsonProperty("modePolicy")] public ModePolicy ModePolicy;
        [JsonProperty("learningObjectives")] public List<string> LearningObjectives = new();
        [JsonProperty("learnerInstructions")] public string LearnerInstructions;
        [JsonProperty("objects")] public List<ScenarioObject> Objects = new();
    }

    public sealed class ModePolicy
    {
        [JsonProperty("allowedModes")] public List<string> AllowedModes = new();
    }

    public static class ScenarioObjectKind
    {
        public const string Spawn = "Spawn";
        public const string Hazard = "Hazard";
        public const string Goal = "Goal";
        public const string Npc = "Npc";
        public const string BlockedElement = "BlockedElement";
        public const string Equipment = "Equipment";
    }

    public sealed class ScenarioObject
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("label")] public string Label;
        [JsonProperty("anchorId")] public string AnchorId;
        [JsonProperty("capability")] public CapabilityRef Capability;
        [JsonProperty("parameters")] public JObject Parameters;
        [JsonProperty("placement")] public Placement Placement;

        public string CapabilityId => Capability?.Id;

        public float GetFloat(string name, float fallback)
        {
            var token = Parameters?[name];
            return token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
                ? token.Value<float>()
                : fallback;
        }

        public bool TryGetFloat(string name, out float value)
        {
            var token = Parameters?[name];
            if (token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer))
            {
                value = token.Value<float>();
                return true;
            }

            value = 0f;
            return false;
        }

        public string GetString(string name, string fallback = null)
        {
            var token = Parameters?[name];
            return token != null && token.Type == JTokenType.String ? token.Value<string>() : fallback;
        }

        public bool GetBool(string name, bool fallback)
        {
            var token = Parameters?[name];
            return token != null && token.Type == JTokenType.Boolean ? token.Value<bool>() : fallback;
        }
    }

    public sealed class CapabilityRef
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("version")] public string Version;
    }

    public sealed class Placement
    {
        [JsonProperty("floorId")] public string FloorId;
        [JsonProperty("position")] public Vec3 Position;
        [JsonProperty("rotation")] public Quat Rotation;
    }

    public struct Vec3
    {
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("z")] public float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public struct Quat
    {
        [JsonProperty("x")] public float X;
        [JsonProperty("y")] public float Y;
        [JsonProperty("z")] public float Z;
        [JsonProperty("w")] public float W;
    }
}
