using System.Collections.Generic;
using Fire3D.Simulation;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace Fire3D.Unity.Tests.Simulation
{
    public class HazardSimulationTests
    {
        [Test]
        public void SameSeedAndTicksGiveIdenticalFields()
        {
            using var a = TestGrids.TwoRooms(42, true, out _);
            using var b = TestGrids.TwoRooms(42, true, out _);
            a.AddFireSource("fire-1", TestGrids.Center(a.Grid, 0, 8, 6), 7f, 0f);
            b.AddFireSource("fire-1", TestGrids.Center(b.Grid, 0, 8, 6), 7f, 0f);

            TestGrids.Run(a, 60f);
            TestGrids.Run(b, 60f);

            for (var i = 0; i < a.Grid.CellCount; i++)
            {
                Assert.AreEqual(a.Grid.Fire[i], b.Grid.Fire[i], $"fire at {i}");
                Assert.AreEqual(a.Grid.Smoke[i], b.Grid.Smoke[i], $"smoke at {i}");
                Assert.AreEqual(a.Grid.Heat[i], b.Grid.Heat[i], $"heat at {i}");
            }
        }

        [Test]
        public void FireIgnitesAtActivationTimeAndSpreads()
        {
            using var sim = TestGrids.TwoRooms(7, true, out _);
            var source = TestGrids.Center(sim.Grid, 0, 8, 6);
            sim.AddFireSource("fire-1", source, 8f, 5f);

            TestGrids.Run(sim, 4f);
            Assert.AreEqual(0f, TestGrids.SumFire(sim.Grid), "no fire before activation");

            var events = new List<SimulationEvent>();
            TestGrids.Run(sim, 2f);
            sim.DrainEvents(events);
            Assert.IsTrue(events.Exists(e => e.Kind == SimulationEventKind.FireIgnited));

            TestGrids.Run(sim, 90f);
            var burning = 0;
            for (var i = 0; i < sim.Grid.CellCount; i++)
                if (sim.Grid.Fire[i] > 0.05f)
                    burning++;
            Assert.Greater(burning, 9, "fire should spread beyond the 3x3 seed area");
            Assert.Greater(sim.Grid.Sample(source).HeatC, 200f);
            Assert.Greater(sim.Grid.Sample(source).Smoke, 0.3f);
        }

        [Test]
        public void ClosedDoorHoldsBackSmoke()
        {
            using var open = TestGrids.TwoRooms(1, true, out _);
            using var closed = TestGrids.TwoRooms(1, false, out _);
            open.AddFireSource("fire-1", TestGrids.Center(open.Grid, 0, 6, 6), 6f, 0f);
            closed.AddFireSource("fire-1", TestGrids.Center(closed.Grid, 0, 6, 6), 6f, 0f);

            TestGrids.Run(open, 45f);
            TestGrids.Run(closed, 45f);

            var openFar = TestGrids.SumSmoke(open.Grid, 21, 37);
            var closedFar = TestGrids.SumSmoke(closed.Grid, 21, 37);
            Assert.Greater(openFar, 1f, "smoke reaches the far room through an open door");
            Assert.Less(closedFar, openFar * 0.25f, "a closed door keeps most smoke out");
        }

        [Test]
        public void OpeningDoorByCommandLetsSmokeThrough()
        {
            using var sim = TestGrids.TwoRooms(1, false, out var doorCell);
            Assert.IsTrue((sim.Grid.GetFlags(doorCell) & CellFlags.DoorClosed) != 0);
            sim.Enqueue(new HazardCommand { Kind = HazardCommandKind.SetDoorOpen, ObjectId = "door-1", Flag = true });
            sim.Step();
            Assert.IsTrue((sim.Grid.GetFlags(doorCell) & CellFlags.DoorClosed) == 0);
        }

        [Test]
        public void SprinklerActivatesAndSuppressesFire()
        {
            using var withSprinkler = TestGrids.TwoRooms(3, false, out _);
            using var without = TestGrids.TwoRooms(3, false, out _);
            var pos = TestGrids.Center(withSprinkler.Grid, 0, 10, 6);
            withSprinkler.AddFireSource("fire-1", pos, 6f, 0f);
            without.AddFireSource("fire-1", pos, 6f, 0f);
            withSprinkler.AddSprinkler("spr-1", pos, SprinklerMode.Active, 68f, 3f, 0.8f, 2f);

            var events = new List<SimulationEvent>();
            TestGrids.Run(withSprinkler, 60f);
            TestGrids.Run(without, 60f);
            withSprinkler.DrainEvents(events);

            Assert.IsTrue(events.Exists(e => e.Kind == SimulationEventKind.SprinklerActivated));
            Assert.Less(TestGrids.SumFire(withSprinkler.Grid), TestGrids.SumFire(without.Grid) * 0.5f);
        }

        [Test]
        public void DisabledSprinklerNeverActivates()
        {
            using var sim = TestGrids.TwoRooms(3, false, out _);
            var pos = TestGrids.Center(sim.Grid, 0, 10, 6);
            sim.AddFireSource("fire-1", pos, 6f, 0f);
            sim.AddSprinkler("spr-1", pos, SprinklerMode.Disabled, 68f, 3f, 0.8f, 0f);
            TestGrids.Run(sim, 40f);
            Assert.IsTrue(sim.TryGetSprinkler("spr-1", out var s));
            Assert.IsFalse(s.Activated);
        }

        [Test]
        public void SmokeDetectorRaisesAlarm()
        {
            using var sim = TestGrids.TwoRooms(5, true, out _);
            sim.AddFireSource("fire-1", TestGrids.Center(sim.Grid, 0, 6, 6), 6f, 0f);
            sim.AddDetector("det-1", TestGrids.Center(sim.Grid, 0, 9, 6), 0.1f);
            Assert.IsFalse(sim.Alarm.Raised);
            TestGrids.Run(sim, 30f);
            Assert.IsTrue(sim.Alarm.Raised);

            var events = new List<SimulationEvent>();
            sim.DrainEvents(events);
            Assert.IsTrue(events.Exists(e => e.Kind == SimulationEventKind.DetectorTriggered && e.ObjectId == "det-1"));
        }

        [Test]
        public void DisabledDetectorNetworkStaysSilent()
        {
            using var sim = TestGrids.TwoRooms(5, true, out _);
            sim.SetDetectorsDisabled(true);
            sim.AddFireSource("fire-1", TestGrids.Center(sim.Grid, 0, 6, 6), 6f, 0f);
            sim.AddDetector("det-1", TestGrids.Center(sim.Grid, 0, 7, 6), 0.05f);
            TestGrids.Run(sim, 30f);
            Assert.IsFalse(sim.Alarm.Raised);
        }

        [Test]
        public void PowerOutageFiresOnTimeline()
        {
            using var sim = TestGrids.TwoRooms(5, true, out _);
            sim.AddTimeline("outage", TimelineKind.PowerOutage, 10f);
            TestGrids.Run(sim, 9.5f);
            Assert.IsFalse(sim.Power.Outage);
            TestGrids.Run(sim, 1f);
            Assert.IsTrue(sim.Power.Outage);
        }

        [Test]
        public void SmokeRisesThroughStairToUpperFloor()
        {
            using var sim = new FireSimulation(TestGrids.Layout(16, 10, 2), 9);
            var g = sim.Grid;
            TestGrids.Box(g);
            for (var f = 0; f < 2; f++)
            for (var z = 3; z <= 5; z++)
                g.AddFlags(g.Layout.Index(f, 12, z), CellFlags.Stair);
            g.FillFuel(0.6f);
            sim.FinalizeBuilding();
            sim.AddFireSource("fire-1", TestGrids.Center(g, 0, 4, 4), 7f, 0f);

            TestGrids.Run(sim, 60f);
            var upper = 0f;
            for (var z = 0; z < 10; z++)
            for (var x = 0; x < 16; x++)
                upper += g.Smoke[g.Layout.Index(1, x, z)];
            Assert.Greater(upper, 0.5f, "smoke should reach the upper floor through the stair");
        }

        [Test]
        public void ExtinguisherCommandSuppressesSmallFire()
        {
            using var sprayed = TestGrids.TwoRooms(11, false, out _);
            using var left = TestGrids.TwoRooms(11, false, out _);
            var pos = TestGrids.Center(sprayed.Grid, 0, 10, 6);
            sprayed.AddFireSource("fire-1", pos, 2f, 0f);
            left.AddFireSource("fire-1", pos, 2f, 0f);
            TestGrids.Run(sprayed, 3f);
            TestGrids.Run(left, 3f);

            var nozzle = TestGrids.Center(sprayed.Grid, 0, 7, 6);
            for (var i = 0; i < 80; i++)
            {
                sprayed.Enqueue(new HazardCommand
                {
                    Kind = HazardCommandKind.Extinguish, Position = nozzle, Direction = new float3(1, 0, 0), Radius = 3f, Strength = 1.5f,
                });
                sprayed.Step();
                left.Step();
            }

            Assert.Less(TestGrids.SumFire(sprayed.Grid), TestGrids.SumFire(left.Grid) * 0.3f);
        }
    }

    public class NavigationTests
    {
        private static GridNavView View(FireSimulation sim) => new()
        {
            Layout = sim.Grid.Layout,
            Weights = NavigationWeights.Default,
            AmbientC = HazardSettings.Default.AmbientC,
            Flags = sim.Grid.Flags,
            Fire = sim.Grid.Fire,
            Smoke = sim.Grid.Smoke,
            Heat = sim.Grid.Heat,
        };

        [Test]
        public void FindsNearestExitThroughDoor()
        {
            using var sim = TestGrids.TwoRooms(1, true, out var door);
            var path = new NativeList<int>(Allocator.Temp);
            var start = sim.Grid.Layout.Index(0, 4, 4);
            Assert.IsTrue(GridPathfinder.FindPath(View(sim), start, -1, path));
            Assert.IsTrue(path.Contains(door), "the only route crosses the door");
            Assert.IsTrue((sim.Grid.GetFlags(path[path.Length - 1]) & CellFlags.Exit) != 0);
            path.Dispose();
        }

        [Test]
        public void NoRouteWhenEnclosed()
        {
            using var sim = TestGrids.TwoRooms(1, true, out var door);
            sim.Grid.SetFlags(door, CellFlags.Wall);
            var path = new NativeList<int>(Allocator.Temp);
            Assert.IsFalse(GridPathfinder.FindPath(View(sim), sim.Grid.Layout.Index(0, 4, 4), -1, path));
            path.Dispose();
        }

        [Test]
        public void RouteAvoidsBurningCorridorWhenAlternativeExists()
        {
            // 30×15 room, wall at x = 15 with two doors (z = 3 and z = 11); fire right behind the lower door.
            using var sim = new FireSimulation(TestGrids.Layout(30, 15), 1);
            var g = sim.Grid;
            TestGrids.Box(g);
            TestGrids.WallColumn(g, 15);
            TestGrids.Walkable(g, 0, 15, 3);
            TestGrids.Walkable(g, 0, 15, 11);
            for (var z = 1; z < 14; z++)
                g.AddFlags(g.Layout.Index(0, 28, z), CellFlags.Exit);
            sim.FinalizeBuilding();
            for (var x = 14; x <= 17; x++)
            for (var z = 2; z <= 4; z++)
                g.Fire[g.Layout.Index(0, x, z)] = 0.9f;

            var path = new NativeList<int>(Allocator.Temp);
            Assert.IsTrue(GridPathfinder.FindPath(View(sim), g.Layout.Index(0, 5, 4), -1, path));
            Assert.IsTrue(path.Contains(g.Layout.Index(0, 15, 11)), "route should take the upper door");
            Assert.IsFalse(path.Contains(g.Layout.Index(0, 15, 3)), "route should avoid the burning door");
            path.Dispose();
        }

        [Test]
        public void NpcEvacuatesAfterAlarm()
        {
            using var sim = TestGrids.TwoRooms(2, true, out _);
            sim.AddNpc("npc-1", TestGrids.Center(sim.Grid, 0, 5, 5), NpcBehaviour.Calm, false, 3f);
            TestGrids.Run(sim, 3f);
            Assert.IsTrue(sim.TryGetNpc("npc-1", out var idle));
            Assert.AreEqual(NpcMode.Idle, idle.Mode, "no reason to move before the alarm");

            sim.Enqueue(new HazardCommand { Kind = HazardCommandKind.RaiseAlarm, ObjectId = "call-1" });
            TestGrids.Run(sim, 40f);
            Assert.IsTrue(sim.TryGetNpc("npc-1", out var npc));
            Assert.AreEqual(NpcMode.Evacuated, npc.Mode);
        }

        [Test]
        public void LedNpcFollowsPlayer()
        {
            using var sim = TestGrids.TwoRooms(2, true, out _);
            sim.AddNpc("npc-1", TestGrids.Center(sim.Grid, 0, 5, 5), NpcBehaviour.Panic, false, 3f);
            var player = TestGrids.Center(sim.Grid, 0, 30, 6);
            sim.SetPlayer(player);
            sim.Enqueue(new HazardCommand { Kind = HazardCommandKind.LeadNpc, ObjectId = "npc-1" });
            TestGrids.Run(sim, 25f);
            Assert.IsTrue(sim.TryGetNpc("npc-1", out var npc));
            Assert.AreEqual(NpcMode.Following, npc.Mode);
            Assert.Less(math.distance(npc.Position.xz, player.xz), 2.5f);
        }
    }
}
