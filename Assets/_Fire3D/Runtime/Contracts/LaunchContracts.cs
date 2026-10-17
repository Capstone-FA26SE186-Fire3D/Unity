using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Fire3D.Contracts
{
    public static class TrainingMode
    {
        public const string Learn = "Learn";
        public const string Guided = "Guided";
        public const string Assessment = "Assessment";

        public static bool IsKnown(string mode) => mode == Learn || mode == Guided || mode == Assessment;
    }

    /// <summary>
    /// Launch request sent by Mobile after the online start succeeded. Mobile already verified the grant,
    /// downloaded and hash-checked the package; Unity re-checks the manifest it loads but never decides access.
    /// </summary>
    public sealed class LaunchRequest
    {
        public const string CurrentProtocol = "1";

        [JsonProperty("protocolVersion")] public string ProtocolVersion;
        [JsonProperty("sessionId")] public string SessionId;
        [JsonProperty("kind")] public string Kind = "Training"; // Training | Playtest
        [JsonProperty("mode")] public string Mode;
        [JsonProperty("runtimeVersion")] public string RuntimeVersion;
        /// <summary>Absolute path of the package manifest on device storage.</summary>
        [JsonProperty("manifestPath")] public string ManifestPath;
        /// <summary>Highest event sequence already acknowledged by the backend when resuming; 0 for a new session.</summary>
        [JsonProperty("resumeFromSequence")] public long ResumeFromSequence;
        [JsonProperty("locale")] public string Locale = "vi";
    }

    /// <summary>Message sent from Unity to Mobile through the native bridge.</summary>
    public sealed class BridgeMessage
    {
        [JsonProperty("type")] public string Type; // Ready | Launched | Event | Completed | Error | Exited
        [JsonProperty("sessionId")] public string SessionId;
        [JsonProperty("payload")] public JToken Payload;
    }

    /// <summary>Telemetry event as accepted by POST /api/training/sessions/{id}/events:batch.</summary>
    public sealed class SessionEvent
    {
        /// <summary>Backend stores schemaVersion as a string (≤ 32 chars); eventId must be a GUID.</summary>
        public const string CurrentSchemaVersion = "1";

        [JsonProperty("eventId")] public string EventId;
        [JsonProperty("sequence")] public long Sequence;
        [JsonProperty("schemaVersion")] public string SchemaVersion = CurrentSchemaVersion;
        [JsonProperty("type")] public string Type;
        [JsonProperty("occurredAt")] public string OccurredAt;
        [JsonProperty("elapsedMs")] public long ElapsedMs;
        [JsonProperty("payload")] public JObject Payload = new();
    }

    public static class EndReason
    {
        public const string Finished = "Finished";
        public const string TimedOut = "TimedOut";
        public const string Abandoned = "Abandoned";
    }

    /// <summary>Completion summary Unity hands to Mobile; Mobile calls /complete with these values.</summary>
    public sealed class SessionCompletion
    {
        [JsonProperty("lastEventSequence")] public long LastEventSequence;
        [JsonProperty("endReason")] public string EndReason;
        [JsonProperty("elapsedMs")] public long ElapsedMs;
        [JsonProperty("reachedExit")] public bool ReachedExit;
        [JsonProperty("collapsed")] public bool Collapsed;
    }

    /// <summary>Package manifest written by the Unity build worker next to the bundles.</summary>
    public sealed class ContentManifest
    {
        public const string Schema = "fet3d.package/1";

        [JsonProperty("schemaVersion")] public string SchemaVersion;
        [JsonProperty("runtimeVersion")] public string RuntimeVersion;
        [JsonProperty("buildTarget")] public string BuildTarget;
        [JsonProperty("unityVersion")] public string UnityVersion;
        [JsonProperty("materialLibraryVersion")] public string MaterialLibraryVersion;
        [JsonProperty("buildingId")] public string BuildingId;
        [JsonProperty("revisionId")] public string RevisionId;
        [JsonProperty("lightingScenarios")] public List<string> LightingScenarios = new();
        [JsonProperty("floors")] public List<ManifestFloor> Floors = new();
        [JsonProperty("files")] public List<ManifestFile> Files = new();
        /// <summary>Relative path of building_metadata.json.</summary>
        [JsonProperty("metadata")] public string Metadata;
        /// <summary>Relative path of the scenario snapshot (fet3d.editor/1).</summary>
        [JsonProperty("scenario")] public string Scenario;
    }

    public sealed class ManifestFloor
    {
        [JsonProperty("floorId")] public string FloorId;
        [JsonProperty("bundle")] public string Bundle;
        [JsonProperty("scene")] public string Scene;
        [JsonProperty("elevation")] public float Elevation;
    }

    public sealed class ManifestFile
    {
        [JsonProperty("path")] public string Path;
        [JsonProperty("sha256")] public string Sha256;
        [JsonProperty("size")] public long Size;
    }
}
