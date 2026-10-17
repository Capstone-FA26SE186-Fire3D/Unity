using System.IO;
using Fire3D.Contracts;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Fire3D.Unity.Tests.Contracts
{
    /// <summary>The manifest must satisfy BE PackageManifest.Validate (contracts/package/v1/manifest.schema.json).</summary>
    public class PackageManifestTests
    {
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(Application.dataPath, "_Fire3D/Tests/Fixtures/Contracts", name));

        private static string TempOutput(params string[] files)
        {
            var dir = Path.Combine(Path.GetTempPath(), "fet3d-manifest-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);
            foreach (var f in files)
                File.WriteAllText(Path.Combine(dir, f), "bundle " + f);
            return dir;
        }

        [Test]
        public void CreatesBackendShapeFromScenarioAndBuilding()
        {
            var scenario = ContractJson.ReadScenario(Fixture("scenario-state.valid.json"));
            var building = ContractJson.ReadBuilding(Fixture("building-metadata.valid.json"));
            var dir = TempOutput("floor_l1", "floor_l1_lighting", "building_metadata.json", "scenario.json");
            try
            {
                var m = PackageManifestFactory.Create(scenario, building, "job-1", "Android", "6000.6.6f1", "1", baked: true);
                m.Floors.Add(new ManifestFloor { Id = "L1", Bundle = "floor_l1", LightingBundle = "floor_l1_lighting", Scene = "L1" });
                PackageManifestFactory.AddFiles(m, dir);
                File.WriteAllText(Path.Combine(dir, ContentManifest.FileName), ContractJson.Write(m));

                var json = JObject.Parse(ContractJson.Write(m));
                foreach (var key in new[] { "schemaVersion", "jobId", "buildTarget", "protocolVersion", "manifestSchemaVersion", "runtimeVersion",
                             "minRuntimeVersion", "requiredCapabilities", "unityVersion", "materialLibraryVersion", "floors", "files" })
                    Assert.IsNotNull(json[key], key);
                Assert.AreEqual("fet3d.package/1", (string)json["schemaVersion"]);
                Assert.AreEqual("1", (string)json["protocolVersion"]);
                Assert.AreEqual("1", (string)json["manifestSchemaVersion"]);
                Assert.AreEqual("L1", (string)json["floors"][0]["id"]);
                Assert.IsNull(json["floors"][0]["floorId"]);
                Assert.AreEqual(4, m.Files.Count, "manifest.json is not listed in its own files");
                foreach (var f in m.Files)
                {
                    Assert.AreEqual(64, f.Sha256.Length);
                    Assert.Greater(f.Size, 0);
                }

                CollectionAssert.AreEqual(new[] { "equipment.extinguisher", "fire.source", "npc.evacuee" }, m.RequiredCapabilities);
                CollectionAssert.IsEmpty(PackageManifestFactory.Validate(m));
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        [Test]
        public void ValidateReportsMissingBundleAndSelfReference()
        {
            var m = new ContentManifest
            {
                SchemaVersion = ContentManifest.Schema, JobId = "j", BuildTarget = "Android", ProtocolVersion = "1", ManifestSchema = "1",
                RuntimeVersion = "2.0.0", MinRuntimeVersion = "2.0.0", UnityVersion = "6000.6.6f1", MaterialLibraryVersion = "1",
            };
            m.Files.Add(new ManifestFile { Path = "manifest.json", Sha256 = new string('a', 64), Size = 10 });
            m.Files.Add(new ManifestFile { Path = "../escape", Sha256 = "ABC", Size = 0 });
            m.Floors.Add(new ManifestFloor { Id = "L1", Bundle = "floor_l1" });

            var issues = PackageManifestFactory.Validate(m);
            Assert.IsTrue(issues.Exists(i => i.Contains("not listed in its own files")));
            Assert.IsTrue(issues.Exists(i => i.Contains("files[1].path")));
            Assert.IsTrue(issues.Exists(i => i.Contains("files[1].sha256")));
            Assert.IsTrue(issues.Exists(i => i.Contains("files[1].size")));
            Assert.IsTrue(issues.Exists(i => i.Contains("floors[0].bundle")));
        }

        [Test]
        public void RuntimeCompatibilityUsesMinRuntimeVersion()
        {
            Assert.IsTrue(RuntimeCatalog.IsCompatible(RuntimeCatalog.RuntimeVersion));
            Assert.IsTrue(RuntimeCatalog.IsCompatible("2.0.0"));
            Assert.IsFalse(RuntimeCatalog.IsCompatible("2.1.0"), "package newer than the app");
            Assert.IsFalse(RuntimeCatalog.IsCompatible("1.9.0"), "different major");
            Assert.IsFalse(RuntimeCatalog.IsCompatible("2.0"));
            Assert.IsFalse(RuntimeCatalog.IsCompatible(null));
        }

        [Test]
        public void ReadManifestAcceptsBackendShape()
        {
            var json = @"{""schemaVersion"":""fet3d.package/1"",""jobId"":""j"",""buildTarget"":""Android"",""protocolVersion"":""1"",
                ""manifestSchemaVersion"":""1"",""runtimeVersion"":""2.0.0"",""minRuntimeVersion"":""2.0.0"",""requiredCapabilities"":[],
                ""unityVersion"":""6000.6.6f1"",""materialLibraryVersion"":""1"",
                ""floors"":[{""id"":""L1"",""bundle"":""floor_l1"",""lightingBundle"":""floor_l1_lighting"",""scene"":""L1""}],
                ""files"":[{""path"":""floor_l1"",""sha256"":""" + new string('0', 64) + @""",""size"":1}]}";
            var m = ContractJson.ReadManifest(json);
            Assert.AreEqual("L1", m.Floors[0].Id);
            Assert.AreEqual("floor_l1_lighting", m.Floors[0].LightingBundle);
        }
    }
}
