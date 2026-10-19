using System.Collections.Generic;

namespace Fire3D.Contracts
{
    /// <summary>Capability ids (= catalogKey) supported by this runtime. Mirrors BE runtime-catalog.batch1.json.</summary>
    public static class Capabilities
    {
        public const string AlarmCallPoint = "alarm.call_point";
        public const string AlarmDetector = "alarm.detector";
        public const string ExtinguisherPortable = "extinguisher.portable";
        public const string WaterSink = "water.sink";
        public const string WaterBath = "water.bath";
        public const string WaterShower = "water.shower";
        public const string ItemTowel = "item.towel";
        public const string DoorStandard = "door.standard";
        public const string DoorFire = "door.fire";
        public const string DoorExit = "door.exit";
        public const string Elevator = "transport.elevator";
        public const string ExitStair = "exit.stair";
        public const string ExitFinal = "exit.final";
        public const string BaitBelongings = "bait.belongings";
        public const string NpcGuide = "npc.guide";
        public const string Sprinkler = "suppression.sprinkler";

        // Hazard and timeline capabilities proposed by Unity (not yet in BE batch 1).
        public const string FireSource = "fire.source";
        public const string SmokeSource = "smoke.source";
        public const string PowerOutage = "event.power_outage";
        public const string AlarmTimeline = "event.alarm";

        public static bool IsDoor(string id) => id == DoorStandard || id == DoorFire || id == DoorExit;
        public static bool IsWater(string id) => id == WaterSink || id == WaterBath || id == WaterShower;
        public static bool IsExit(string id) => id == ExitStair || id == ExitFinal;
    }

    /// <summary>Event type names accepted by the backend (core + batch 1 capability events).</summary>
    public static class EventTypes
    {
        // Core events, valid for every runtime; the backend computes metrics from these.
        public const string ExitReached = "ExitReached";
        public const string WrongExit = "WrongExit";
        public const string HazardExposure = "HazardExposure";
        public const string Moved = "Moved";

        public const string AlarmRaised = "AlarmRaised";
        public const string DetectorTriggered = "DetectorTriggered";
        public const string ExtinguisherUsed = "ExtinguisherUsed";
        public const string ExtinguishAttemptLargeFire = "ExtinguishAttemptLargeFire";
        public const string ItemWetted = "ItemWetted";
        public const string NoseCoveredInSmoke = "NoseCoveredInSmoke";
        public const string CheckedDoorHeat = "CheckedDoorHeat";
        public const string ClosedDoor = "ClosedDoor";
        public const string UsedElevator = "UsedElevator";
        public const string WentBackForBelongings = "WentBackForBelongings";
        public const string NpcGuided = "NpcGuided";
        public const string SprinklerActivated = "SprinklerActivated";
    }

    /// <summary>Player-facing action of a capability (Docs ifc_ingestion_visual_interaction.md section 7).</summary>
    public sealed class ActionDefinition
    {
        public string Id;
        public string Label;
        /// <summary>Seconds the button must be held; 0 for a tap.</summary>
        public float HoldSeconds;
        /// <summary>Held item requirements as "capability:state"; empty means hands may be empty or full.</summary>
        public string[] RequiresHeld = System.Array.Empty<string>();
        /// <summary>Required state of the interactable itself, e.g. "wet" for a towel being used.</summary>
        public string RequiresState;
        /// <summary>Required state of the held item for actions performed with it (cover nose).</summary>
        public string RequiresHeldState;
        public string Event;
        /// <summary>True when the action is performed with the held item rather than the object in front.</summary>
        public bool UsesHeldItem;
    }

    public static class RuntimeCatalog
    {
        /// <summary>
        /// Version of this app build (Ready, Launched, runtime catalog row). Bump the patch for fixes that keep the bridge
        /// protocol, manifest schema and capability set; register each released version through
        /// POST /api/admin/runtime-catalog (see Docs/runtime-catalog.md).
        /// </summary>
        public const string RuntimeVersion = "2.0.1";

        /// <summary>
        /// Oldest runtime able to play the packages this build produces. Raise it only when packages start to depend on
        /// something older runtimes lack (new capability, protocol or manifest change), so lifecycle fixes do not force
        /// every package onto the newest app.
        /// </summary>
        public const string MinPackageRuntimeVersion = "2.0.0";

        /// <summary>
        /// True when a package that needs <paramref name="minRuntimeVersion"/> can run on this app: same major version and
        /// the required version not newer than <see cref="RuntimeVersion"/>. Unparseable versions are incompatible.
        /// </summary>
        public static bool IsCompatible(string minRuntimeVersion)
        {
            if (!TryParseVersion(minRuntimeVersion, out var required) || !TryParseVersion(RuntimeVersion, out var app))
                return false;
            return required.major == app.major && (required.minor < app.minor || (required.minor == app.minor && required.patch <= app.patch));
        }

        private static bool TryParseVersion(string text, out (int major, int minor, int patch) version)
        {
            version = default;
            var parts = text?.Split('.');
            if (parts == null || parts.Length != 3)
                return false;
            if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) || !int.TryParse(parts[2], out var patch))
                return false;
            version = (major, minor, patch);
            return true;
        }

        private static readonly Dictionary<string, ActionDefinition[]> Actions = new()
        {
            [Capabilities.AlarmCallPoint] = new[] { new ActionDefinition { Id = "press", Label = "Nhấn báo cháy", Event = EventTypes.AlarmRaised } },
            [Capabilities.ExtinguisherPortable] = new[]
            {
                new ActionDefinition { Id = "pick_up", Label = "Lấy bình" },
                new ActionDefinition { Id = "pull_pin", Label = "Rút chốt", UsesHeldItem = true, RequiresHeldState = "sealed" },
                new ActionDefinition { Id = "spray", Label = "Phun", HoldSeconds = 0.01f, UsesHeldItem = true, RequiresHeldState = "armed", Event = EventTypes.ExtinguisherUsed },
            },
            [Capabilities.WaterSink] = WaterActions(),
            [Capabilities.WaterBath] = WaterActions(),
            [Capabilities.WaterShower] = WaterActions(),
            [Capabilities.ItemTowel] = new[]
            {
                new ActionDefinition { Id = "pick_up", Label = "Nhặt khăn" },
                new ActionDefinition { Id = "cover_nose", Label = "Che mũi", UsesHeldItem = true, RequiresHeldState = "wet", Event = EventTypes.NoseCoveredInSmoke },
            },
            [Capabilities.DoorStandard] = DoorActions(),
            [Capabilities.DoorFire] = DoorActions(),
            [Capabilities.DoorExit] = DoorActions(),
            [Capabilities.Elevator] = new[] { new ActionDefinition { Id = "call", Label = "Gọi thang máy", Event = EventTypes.UsedElevator } },
            [Capabilities.BaitBelongings] = new[] { new ActionDefinition { Id = "pick_up", Label = "Lấy đồ", Event = EventTypes.WentBackForBelongings } },
            [Capabilities.NpcGuide] = new[]
            {
                new ActionDefinition { Id = "call", Label = "Gọi người" },
                new ActionDefinition { Id = "lead", Label = "Dẫn đi", Event = EventTypes.NpcGuided },
            },
        };

        private static ActionDefinition[] WaterActions() => new[]
        {
            new ActionDefinition
            {
                Id = "wet_item", Label = "Nhúng nước", HoldSeconds = 2f,
                RequiresHeld = new[] { Capabilities.ItemTowel + ":dry", Capabilities.ItemTowel + ":wet" },
                Event = EventTypes.ItemWetted,
            },
        };

        private static ActionDefinition[] DoorActions() => new[]
        {
            new ActionDefinition { Id = "touch", Label = "Sờ cửa", Event = EventTypes.CheckedDoorHeat },
            new ActionDefinition { Id = "open", Label = "Mở cửa", RequiresState = "closed" },
            new ActionDefinition { Id = "close", Label = "Đóng cửa", RequiresState = "open", Event = EventTypes.ClosedDoor },
        };

        public static IReadOnlyList<ActionDefinition> GetActions(string capabilityId) =>
            capabilityId != null && Actions.TryGetValue(capabilityId, out var actions) ? actions : System.Array.Empty<ActionDefinition>();

        public static bool IsSupported(string capabilityId) =>
            capabilityId != null && (Actions.ContainsKey(capabilityId)
                || capabilityId == Capabilities.AlarmDetector
                || capabilityId == Capabilities.Sprinkler
                || Capabilities.IsExit(capabilityId)
                || capabilityId == Capabilities.FireSource
                || capabilityId == Capabilities.SmokeSource
                || capabilityId == Capabilities.PowerOutage
                || capabilityId == Capabilities.AlarmTimeline);

        public static bool IsHoldable(string capabilityId) =>
            capabilityId == Capabilities.ItemTowel || capabilityId == Capabilities.ExtinguisherPortable;
    }
}
