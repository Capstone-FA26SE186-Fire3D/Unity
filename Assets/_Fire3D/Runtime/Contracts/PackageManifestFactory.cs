using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Fire3D.Contracts
{
    /// <summary>
    /// Builds and checks <c>fet3d.package/1</c> manifests the way the backend validates them
    /// (BE <c>PackageManifest.Validate</c>), so a bad package fails on the worker instead of at the upload gate.
    /// </summary>
    public static class PackageManifestFactory
    {
        public const string ValidatorVersion = "fet3d-unity-worker/1";
        private static readonly Regex PathPattern = new("^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$", RegexOptions.Compiled);
        private static readonly Regex VersionPattern = new(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$", RegexOptions.Compiled);

        public static ContentManifest Create(ScenarioState scenario, BuildingMetadata building, string jobId, string buildTarget,
            string unityVersion, string materialLibraryVersion, bool baked)
        {
            return new ContentManifest
            {
                SchemaVersion = ContentManifest.Schema,
                JobId = jobId,
                BuildTarget = buildTarget,
                ProtocolVersion = LaunchRequest.CurrentProtocol,
                ManifestSchema = ContentManifest.ManifestSchemaVersion,
                RuntimeVersion = RuntimeCatalog.RuntimeVersion,
                MinRuntimeVersion = RuntimeCatalog.RuntimeVersion,
                RequiredCapabilities = RequiredCapabilities(scenario),
                UnityVersion = unityVersion,
                MaterialLibraryVersion = materialLibraryVersion,
                RevisionId = building?.RevisionId,
                Metadata = "building_metadata.json",
                Scenario = "scenario.json",
                LightingScenarios = baked ? new List<string> { "Normal", "Outage" } : new List<string>(),
            };
        }

        /// <summary>Distinct capability ids the scenario places, sorted; the backend checks them against the runtime catalog.</summary>
        public static List<string> RequiredCapabilities(ScenarioState scenario)
        {
            if (scenario?.Objects == null)
                return new List<string>();
            return scenario.Objects
                .Select(o => o.CapabilityId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>Lists every file under <paramref name="outputDir"/> except the manifest itself, with SHA-256 and size.</summary>
        public static void AddFiles(ContentManifest manifest, string outputDir)
        {
            manifest.Files.Clear();
            var root = Path.GetFullPath(outputDir);
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative == ContentManifest.FileName)
                    continue;
                manifest.Files.Add(new ManifestFile { Path = relative, Sha256 = Sha256(file), Size = new FileInfo(file).Length });
            }
        }

        /// <summary>Problems the backend would report, as "path: message"; empty when the manifest is acceptable.</summary>
        public static List<string> Validate(ContentManifest m)
        {
            var issues = new List<string>();
            if (m == null)
            {
                issues.Add("manifest: missing");
                return issues;
            }

            if (m.SchemaVersion != ContentManifest.Schema)
                issues.Add($"schemaVersion: expected {ContentManifest.Schema}");
            foreach (var (name, value) in new[]
                     {
                         ("buildTarget", m.BuildTarget), ("protocolVersion", m.ProtocolVersion), ("manifestSchemaVersion", m.ManifestSchema),
                         ("unityVersion", m.UnityVersion), ("materialLibraryVersion", m.MaterialLibraryVersion),
                     })
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 100)
                    issues.Add($"{name}: a nonempty value of at most 100 characters is required");
            }

            foreach (var (name, value) in new[] { ("runtimeVersion", m.RuntimeVersion), ("minRuntimeVersion", m.MinRuntimeVersion) })
            {
                if (value == null || !VersionPattern.IsMatch(value))
                    issues.Add($"{name}: use a three-part version");
            }

            if (m.RequiredCapabilities == null || m.RequiredCapabilities.Count > 100 || m.RequiredCapabilities.Any(c => string.IsNullOrEmpty(c) || c.Length > 100))
                issues.Add("requiredCapabilities: use an array of capability names");

            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (m.Files == null || m.Files.Count == 0 || m.Files.Count > 2000)
            {
                issues.Add("files: list 1-2000 files");
            }
            else
            {
                for (var i = 0; i < m.Files.Count; i++)
                {
                    var f = m.Files[i];
                    if (!IsValidPath(f.Path))
                        issues.Add($"files[{i}].path: invalid relative path");
                    else if (f.Path == ContentManifest.FileName)
                        issues.Add($"files[{i}].path: manifest.json is not listed in its own files");
                    else if (!paths.Add(f.Path))
                        issues.Add($"files[{i}].path: listed twice");
                    if (f.Sha256 == null || f.Sha256.Length != 64 || !f.Sha256.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                        issues.Add($"files[{i}].sha256: lowercase hex SHA-256 required");
                    if (f.Size <= 0)
                        issues.Add($"files[{i}].size: positive size required");
                }
            }

            if (m.Floors == null || m.Floors.Count == 0 || m.Floors.Count > 500)
            {
                issues.Add("floors: list 1-500 floors");
            }
            else
            {
                for (var i = 0; i < m.Floors.Count; i++)
                {
                    var floor = m.Floors[i];
                    if (string.IsNullOrEmpty(floor.Bundle) || !paths.Contains(floor.Bundle))
                        issues.Add($"floors[{i}].bundle: must be one of the listed files");
                    if (floor.LightingBundle != null && !paths.Contains(floor.LightingBundle))
                        issues.Add($"floors[{i}].lightingBundle: must be one of the listed files");
                }
            }

            return issues;
        }

        public static bool IsValidPath(string path) =>
            !string.IsNullOrEmpty(path) && path.Length <= 200 && PathPattern.IsMatch(path) && path.Split('/').All(p => p != "." && p != "..");

        public static string Sha256(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}
