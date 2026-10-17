using System.IO;
using Fire3D.Contracts;
using NUnit.Framework;
using UnityEngine;

namespace Fire3D.Unity.Tests.Contracts
{
    public class ContractTests
    {
        private static string Fixture(string name) =>
            File.ReadAllText(Path.Combine(Application.dataPath, "_Fire3D/Tests/Fixtures/Contracts", name));

        [Test]
        public void ReadsBackendScenarioFixture()
        {
            var scenario = ContractJson.ReadScenario(Fixture("scenario-state.valid.json"));
            Assert.AreEqual(ScenarioState.Schema, scenario.SchemaVersion);
            Assert.AreEqual(600, scenario.TimeLimitSeconds);
            Assert.AreEqual(42, scenario.RandomSeed);
            var fire = scenario.Objects.Find(o => o.Id == "fire-1");
            Assert.AreEqual(ScenarioObjectKind.Hazard, fire.Kind);
            Assert.AreEqual(Capabilities.FireSource, fire.CapabilityId);
            Assert.AreEqual(6.5f, fire.GetFloat("intensity", 0f), 1e-4f);
            Assert.AreEqual("L1", fire.Placement.FloorId);
            Assert.AreEqual(0.7071068f, scenario.Objects[0].Placement.Rotation.W, 1e-6f);
        }

        [Test]
        public void ReadsBackendBuildingFixture()
        {
            var building = ContractJson.ReadBuilding(Fixture("building-metadata.valid.json"));
            Assert.AreEqual(2, building.Interactables.Count);
            Assert.AreEqual("water.sink", building.Interactables[1].CatalogKey);
            Assert.AreEqual("floor.tile", building.Materials["FET3D_FloorTile"].LibraryKey);
        }

        [Test]
        public void RejectsUnknownScenarioSchema()
        {
            var ex = Assert.Throws<ContractException>(() => ContractJson.ReadScenario("{\"schemaVersion\":\"fet3d.editor/2\"}"));
            Assert.AreEqual("SCENARIO_SCHEMA_UNSUPPORTED", ex.Code);
        }

        [Test]
        public void RejectsLaunchWithWrongProtocolOrMode()
        {
            var bad = Assert.Throws<ContractException>(() => ContractJson.ReadLaunch(
                "{\"protocolVersion\":\"2\",\"sessionId\":\"s\",\"mode\":\"Guided\",\"manifestPath\":\"/x\"}"));
            Assert.AreEqual("PROTOCOL_UNSUPPORTED", bad.Code);
            var mode = Assert.Throws<ContractException>(() => ContractJson.ReadLaunch(
                "{\"protocolVersion\":\"1\",\"sessionId\":\"s\",\"mode\":\"Race\",\"manifestPath\":\"/x\"}"));
            Assert.AreEqual("MODE_UNSUPPORTED", mode.Code);
        }

        [Test]
        public void SessionEventSerializesBackendShape()
        {
            var json = ContractJson.Write(new SessionEvent
            {
                EventId = "6f1d1c0e-3f0a-4c43-9d1a-0d5f3b8a2c11", Sequence = 3, Type = EventTypes.Moved, OccurredAt = "2026-10-17T00:00:00.000Z", ElapsedMs = 1500,
                Payload = new Newtonsoft.Json.Linq.JObject { ["distanceMeters"] = 4.5 },
            });
            StringAssert.Contains("\"eventId\":\"6f1d1c0e-3f0a-4c43-9d1a-0d5f3b8a2c11\"", json);
            StringAssert.Contains("\"sequence\":3", json);
            StringAssert.Contains("\"schemaVersion\":\"1\"", json);
            StringAssert.Contains("\"type\":\"Moved\"", json);
            StringAssert.Contains("\"elapsedMs\":1500", json);
            StringAssert.Contains("\"distanceMeters\":4.5", json);
        }
    }
}
