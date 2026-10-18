using Fire3D.Simulation;
using NUnit.Framework;

namespace Fire3D.Unity.Tests.Simulation
{
    /// <summary>
    /// The smoke layer model: smoke pools under the ceiling and the layer creeps down over time, darkens with age,
    /// lifts again when the cell clears, and never crosses a wall.
    /// </summary>
    public class SmokeLayerTests
    {
        [Test]
        public void LayerThickensOverAboutAMinuteAndDarkensWithAge()
        {
            using var sim = TestGrids.TwoRooms(3, true, out _);
            var cell = sim.Grid.Layout.Index(0, 8, 6);
            sim.AddFireSource("fire-1", TestGrids.Center(sim.Grid, 0, 8, 6), 8f, 0f);

            TestGrids.Run(sim, 10f);
            var layer10 = sim.Grid.SmokeLayer[cell];
            var age10 = sim.Grid.SmokeAge[cell];
            Assert.Greater(sim.Grid.Smoke[cell], 0.03f, "the source cell holds smoke after 10 s");
            Assert.Greater(layer10, 0.1f, "a layer has formed under the ceiling");
            Assert.Less(layer10, 1.5f, "but it has not yet reached head height");
            Assert.That(age10, Is.EqualTo(10f).Within(0.6f), "age counts seconds of smoke presence");

            TestGrids.Run(sim, 80f);
            var layer90 = sim.Grid.SmokeLayer[cell];
            Assert.Greater(layer90, layer10 + 0.8f, "the layer kept creeping down");
            Assert.Greater(layer90, 1.6f, "after 90 s the layer is well below standing head height");
            Assert.LessOrEqual(layer90, HazardGrid.MaxLayerThickness);
            Assert.Greater(sim.Grid.SmokeAge[cell], 60f, "old enough to have turned black");

            var bottom = sim.Grid.LayerBottom(cell);
            var ceiling = sim.Grid.Layout.Origin.y + HazardGrid.CeilingHeight;
            Assert.That(bottom, Is.EqualTo(ceiling - layer90).Within(1e-4f));
            Assert.Less(bottom, 1.45f, "standing (head at 1.55 m) is inside the layer; crawling (0.5 m) is not");
        }

        [Test]
        public void LayerLiftsAndAgeResetsWhenTheCellClears()
        {
            using var sim = TestGrids.TwoRooms(5, true, out _);
            var cell = sim.Grid.Layout.Index(0, 8, 6);
            // Pre-fill the cell's layer as if it had been smoky, then run with no source: smoke vents, layer lifts.
            sim.Grid.SmokeLayer[cell] = 2.0f;
            sim.Grid.SmokeAge[cell] = 70f;
            TestGrids.Run(sim, 20f);
            Assert.Less(sim.Grid.SmokeLayer[cell], 2.0f, "layer lifts without smoke");
            TestGrids.Run(sim, 40f);
            Assert.LessOrEqual(sim.Grid.SmokeLayer[cell], 0.01f, "layer gone after a minute of clean air");
            Assert.AreEqual(0f, sim.Grid.SmokeAge[cell], "age resets once the layer is gone");
        }

        [Test]
        public void LayerDoesNotFormBehindAWall()
        {
            using var sim = TestGrids.TwoRooms(9, false, out _); // door closed: the far room gets only the leak
            sim.AddFireSource("fire-1", TestGrids.Center(sim.Grid, 0, 6, 6), 8f, 0f);
            TestGrids.Run(sim, 60f);
            var near = sim.Grid.SmokeLayer[sim.Grid.Layout.Index(0, 6, 6)];
            var wall = sim.Grid.SmokeLayer[sim.Grid.Layout.Index(0, 0, 6)]; // border wall cell
            Assert.Greater(near, 1.0f);
            Assert.AreEqual(0f, wall, "wall cells never carry a layer");
        }
    }
}
