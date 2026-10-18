using System.IO;
using Fire3D.Contracts;
using NUnit.Framework;
using UnityEngine;

namespace Fire3D.Unity.Tests.Contracts
{
    /// <summary>Contract coordinates are in the right-handed GLB frame; Unity mirrors X like glTFast does on import.</summary>
    public class ContractFrameTests
    {
        [Test]
        public void PointsMirrorXOnly()
        {
            Assert.AreEqual(new Vector3(-3.1f, 0.5f, -2f), ContractFrame.ToUnity(3.1f, 0.5f, -2f));
            Assert.AreEqual(new Vector3(1f, 2f, 3f), ContractFrame.FromUnity(ContractFrame.ToUnity(new Vector3(1f, 2f, 3f))));
        }

        [Test]
        public void YawIsMirroredAndForwardFollowsThePoint()
        {
            // A door facing +X in the GLB (editor) must face -X in Unity, where the mirrored geometry is.
            var contract = new Quaternion(0f, Mathf.Sin(Mathf.PI / 4f), 0f, Mathf.Cos(Mathf.PI / 4f)); // +90° about +Y, right-handed
            var unity = ContractFrame.ToUnity(contract);
            Assert.AreEqual(-90f, Mathf.DeltaAngle(0f, unity.eulerAngles.y), 1e-3f);
            var q = Quaternion.Euler(10f, 35f, 0f);
            var back = ContractFrame.FromUnity(ContractFrame.ToUnity(q));
            Assert.Less(Quaternion.Angle(q, back), 1e-3f);
        }

        [Test]
        public void BoundsSwapTheMirroredXCorners()
        {
            var b = ContractFrame.ToUnityBounds(new Vector3(2f, 0f, 1f), new Vector3(5f, 3f, 4f));
            Assert.AreEqual(new Vector3(-5f, 0f, 1f), b.min);
            Assert.AreEqual(new Vector3(-2f, 3f, 4f), b.max);
        }

        [Test]
        public void DevelopmentFixturesAreWrittenInTheContractFrame()
        {
            var dir = Path.Combine(Application.dataPath, "_Fire3D/Scenes/Development");
            var scenario = ContractJson.ReadScenario(File.ReadAllText(Path.Combine(dir, "dev-scenario.json")));
            var spawn = scenario.Objects.Find(o => o.Id == "spawn-1").Placement;
            // The graybox spawn is authored at Unity (4, -, 3.2) in office C on L2.
            Assert.AreEqual(new Vector3(4f, 0f, 3.2f), ContractFrame.ToUnity(spawn.Position.X, spawn.Position.Y, spawn.Position.Z));
            var sink = scenario.Objects.Find(o => o.Id == "sink-l2").Placement.Rotation;
            var unityYaw = ContractFrame.ToUnity(new Quaternion(sink.X, sink.Y, sink.Z, sink.W)).eulerAngles.y;
            Assert.AreEqual(-90f, Mathf.DeltaAngle(0f, unityYaw), 1e-3f);

            var building = ContractJson.ReadBuilding(File.ReadAllText(Path.Combine(dir, "dev-building.json")));
            var office = building.Spaces.Find(s => s.Name == "Office A");
            var bounds = ContractFrame.ToUnityBounds(new Vector3(office.Bbox[0][0], office.Bbox[0][1], office.Bbox[0][2]),
                new Vector3(office.Bbox[1][0], office.Bbox[1][1], office.Bbox[1][2]));
            Assert.AreEqual(0.1f, bounds.min.x, 1e-3f);
            Assert.AreEqual(7.9f, bounds.max.x, 1e-3f);
        }
    }
}
