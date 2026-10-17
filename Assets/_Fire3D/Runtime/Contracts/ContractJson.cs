using System;
using System.Globalization;
using Newtonsoft.Json;

namespace Fire3D.Contracts
{
    /// <summary>Shared JSON settings so every contract is read and written the same way.</summary>
    public static class ContractJson
    {
        public static readonly JsonSerializerSettings Settings = new()
        {
            NullValueHandling = NullValueHandling.Ignore,
            MissingMemberHandling = MissingMemberHandling.Ignore,
            Culture = CultureInfo.InvariantCulture,
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
        };

        public static T Read<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ContractException("CONTRACT_EMPTY", $"Empty {typeof(T).Name} document.");
            try
            {
                var value = JsonConvert.DeserializeObject<T>(json, Settings);
                if (value == null)
                    throw new ContractException("CONTRACT_INVALID", $"{typeof(T).Name} document is null.");
                return value;
            }
            catch (JsonException ex)
            {
                throw new ContractException("CONTRACT_INVALID", $"{typeof(T).Name}: {ex.Message}");
            }
        }

        public static string Write(object value) => JsonConvert.SerializeObject(value, Formatting.None, Settings);

        public static ScenarioState ReadScenario(string json)
        {
            var scenario = Read<ScenarioState>(json);
            if (scenario.SchemaVersion != ScenarioState.Schema)
                throw new ContractException("SCENARIO_SCHEMA_UNSUPPORTED", $"Expected {ScenarioState.Schema}, got '{scenario.SchemaVersion}'.");
            foreach (var obj in scenario.Objects)
            {
                if (string.IsNullOrEmpty(obj.Id) || string.IsNullOrEmpty(obj.Kind) || obj.Placement == null)
                    throw new ContractException("SCENARIO_OBJECT_INVALID", $"Object '{obj.Id}' needs id, kind and placement.");
            }

            return scenario;
        }

        public static BuildingMetadata ReadBuilding(string json)
        {
            var building = Read<BuildingMetadata>(json);
            if (building.SchemaVersion != BuildingMetadata.Schema)
                throw new ContractException("BUILDING_SCHEMA_UNSUPPORTED", $"Expected {BuildingMetadata.Schema}, got '{building.SchemaVersion}'.");
            if (!string.IsNullOrEmpty(building.Units) && building.Units != "m")
                throw new ContractException("BUILDING_UNITS_UNSUPPORTED", $"Units must be meters, got '{building.Units}'.");
            return building;
        }

        public static LaunchRequest ReadLaunch(string json)
        {
            var launch = Read<LaunchRequest>(json);
            if (launch.ProtocolVersion != LaunchRequest.CurrentProtocol)
                throw new ContractException("PROTOCOL_UNSUPPORTED", $"Protocol '{launch.ProtocolVersion}' is not {LaunchRequest.CurrentProtocol}.");
            if (string.IsNullOrEmpty(launch.SessionId))
                throw new ContractException("LAUNCH_INVALID", "sessionId is required.");
            if (!TrainingMode.IsKnown(launch.Mode))
                throw new ContractException("MODE_UNSUPPORTED", $"Unknown mode '{launch.Mode}'.");
            if (string.IsNullOrEmpty(launch.ManifestPath))
                throw new ContractException("LAUNCH_INVALID", "manifestPath is required.");
            return launch;
        }

        public static ContentManifest ReadManifest(string json)
        {
            var manifest = Read<ContentManifest>(json);
            if (manifest.SchemaVersion != ContentManifest.Schema)
                throw new ContractException("MANIFEST_SCHEMA_UNSUPPORTED", $"Expected {ContentManifest.Schema}, got '{manifest.SchemaVersion}'.");
            if (manifest.Floors.Count == 0)
                throw new ContractException("MANIFEST_INVALID", "Manifest has no floors.");
            return manifest;
        }

        public static string UtcNow() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Contract violation with a stable code that is reported to Mobile.</summary>
    public sealed class ContractException : Exception
    {
        public string Code { get; }

        public ContractException(string code, string message) : base(message)
        {
            Code = code;
        }
    }
}
