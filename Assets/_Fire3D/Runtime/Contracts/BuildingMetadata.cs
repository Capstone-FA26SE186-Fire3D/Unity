using System.Collections.Generic;
using Newtonsoft.Json;

namespace Fire3D.Contracts
{
    /// <summary>
    /// <c>fet3d.building/1</c> metadata produced by the IFC/Blender worker (BE contracts/ifc/v1/building-metadata.schema.json).
    /// Positions use the same frame as the building geometry.
    /// </summary>
    public sealed class BuildingMetadata
    {
        public const string Schema = "fet3d.building/1";

        [JsonProperty("schemaVersion")] public string SchemaVersion;
        [JsonProperty("revisionId")] public string RevisionId;
        [JsonProperty("units")] public string Units;
        [JsonProperty("storeys")] public List<StoreyInfo> Storeys = new();
        [JsonProperty("spaces")] public List<SpaceInfo> Spaces = new();
        [JsonProperty("portals")] public List<PortalInfo> Portals = new();
        [JsonProperty("interactables")] public List<InteractableInfo> Interactables = new();
        [JsonProperty("materials")] public Dictionary<string, MaterialMapping> Materials = new();
    }

    public sealed class StoreyInfo
    {
        [JsonProperty("ifcGlobalId")] public string IfcGlobalId;
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("elevation")] public float Elevation;
        [JsonProperty("height")] public float? Height;
        [JsonProperty("chunk")] public string Chunk;

        /// <summary>Floor id used by scenario placements; falls back to the storey name.</summary>
        public string FloorId => Id ?? Name ?? IfcGlobalId;
    }

    public sealed class SpaceInfo
    {
        [JsonProperty("ifcGlobalId")] public string IfcGlobalId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("longName")] public string LongName;
        [JsonProperty("category")] public string Category;
        [JsonProperty("storey")] public string Storey;
        /// <summary>Axis-aligned box as [[minX,minY,minZ],[maxX,maxY,maxZ]].</summary>
        [JsonProperty("bbox")] public float[][] Bbox;
    }

    public sealed class PortalInfo
    {
        [JsonProperty("ifcGlobalId")] public string IfcGlobalId;
        [JsonProperty("type")] public string Type;
        [JsonProperty("fireExit")] public bool? FireExit;
        [JsonProperty("fireRating")] public string FireRating;
        [JsonProperty("spaces")] public List<string> Spaces = new();
        [JsonProperty("exitInferred")] public bool? ExitInferred;
    }

    public sealed class InteractableInfo
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("ifcGlobalId")] public string IfcGlobalId;
        [JsonProperty("catalogKey")] public string CatalogKey;
        [JsonProperty("source")] public string Source;
        [JsonProperty("ifcClass")] public string IfcClass;
        [JsonProperty("confidence")] public float Confidence;
        [JsonProperty("confirmedBy")] public string ConfirmedBy;
        [JsonProperty("floorId")] public string FloorId;
        [JsonProperty("position")] public float[] Position;
        [JsonProperty("rotationY")] public float? RotationY;
        [JsonProperty("bbox")] public float[] Bbox;
    }

    public sealed class MaterialMapping
    {
        [JsonProperty("libraryKey")] public string LibraryKey;
        [JsonProperty("ifcNames")] public List<string> IfcNames = new();
        [JsonProperty("variant")] public int? Variant;
        /// <summary>Optional tint as hex <c>#RRGGBB</c> taken from the IFC surface style or the colour scheme.</summary>
        [JsonProperty("tint")] public string Tint;
    }
}
