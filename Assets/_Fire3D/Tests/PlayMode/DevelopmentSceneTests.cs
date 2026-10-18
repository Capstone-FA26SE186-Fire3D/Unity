using System.Collections;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Simulation;
using Fire3D.Unity.Bootstrap;
using Fire3D.Unity.Gameplay.Cameras;
using Fire3D.Unity.Gameplay.Interactions;
using Fire3D.Unity.Integration;
using Fire3D.Unity.Session;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>End-to-end checks on the graybox building with the dev scenario (no Mobile, no backend).</summary>
    public class DevelopmentSceneTests
    {
        private const string Scene = "Development_Graybox";
        private TrainingSession _session;
        private Fire3DApp _app;

        [UnitySetUp]
        public IEnumerator Load()
        {
            yield return SceneManager.LoadSceneAsync(Scene, LoadSceneMode.Single);
            yield return null;
            _app = Object.FindAnyObjectByType<Fire3DApp>();
            Assert.IsNotNull(_app, "Fire3DApp in dev scene");
            yield return null;
            _session = Object.FindAnyObjectByType<TrainingSession>();
            Assert.IsNotNull(_session, "DevLauncher should start a session");
            _session.StartRunning();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator Unload()
        {
            if (_session != null)
                Object.Destroy(_session.gameObject);
            if (_app != null)
                Object.Destroy(_app.gameObject);
            yield return null;
        }

        [UnityTest]
        public IEnumerator WorldIsBuiltFromScenario()
        {
            var grid = _session.Simulation.Grid;
            var walkable = 0;
            var exits = 0;
            var stairs = 0;
            var doors = 0;
            for (var i = 0; i < grid.CellCount; i++)
            {
                var f = grid.GetFlags(i);
                if ((f & CellFlags.Walkable) != 0) walkable++;
                if ((f & CellFlags.Exit) != 0) exits++;
                if ((f & CellFlags.Stair) != 0) stairs++;
                if ((f & CellFlags.Door) != 0) doors++;
            }

            Assert.AreEqual(2, grid.Layout.Floors);
            Assert.Greater(walkable, 1500, "both floors should be mostly walkable");
            Assert.Greater(exits, 4, "north exit cells");
            Assert.Greater(stairs, 20, "stairwell connects floors");
            Assert.Greater(doors, 10, "door cells");
            Assert.IsTrue(Interactable.All.Any(i => i.CapabilityId == Capabilities.ItemTowel));
            Assert.IsTrue(Interactable.All.Any(i => i.CapabilityId == Capabilities.DoorExit && i.State == "blocked"), "west exit is locked");

            // The spawn (L2 office) must have a risk-aware route to the open north exit.
            var view = new GridNavView
            {
                Layout = grid.Layout, Weights = NavigationWeights.Default, AmbientC = HazardSettings.Default.AmbientC,
                Flags = grid.Flags, Fire = grid.Fire, Smoke = grid.Smoke, Heat = grid.Heat,
            };
            var path = new NativeList<int>(Allocator.Temp);
            Assert.IsTrue(GridPathfinder.FindPath(view, grid.Layout.CellAtClamped(_session.Player.transform.position), -1, path),
                "route from spawn to an exit");
            path.Dispose();
            yield return null;
        }

        [UnityTest]
        public IEnumerator WalkingOutOfTheNorthDoorReachesExit()
        {
            var player = _session.Player;
            player.Teleport(new Vector3(21f, 0.05f, 12.6f), 0f);
            yield return new WaitForFixedUpdate();
            yield return null;

            var interaction = _session.Interaction;
            Assert.IsNotNull(interaction.Target, "north door should be in reach");
            var open = interaction.Actions.ToList().FindIndex(a => a.Id == "open");
            Assert.GreaterOrEqual(open, 0, "door offers Open");
            interaction.Press(open);
            // Opening is a hand gesture now (turn, reach, act, lower the hand) and the leaf then swings: wait for both.
            while (interaction.Busy)
                yield return null;
            yield return new WaitForSeconds(1.0f);

            // Walk by real time, not frame count: the suite runs at very different frame rates.
            var until = Time.time + 8f;
            while (_session.Phase == SessionPhase.Running && Time.time < until)
            {
                player.InputSource = new FixedInput(new Vector2(0f, 1f));
                yield return null;
            }

            var state = $"phase {_session.Phase}, player {player.transform.position}, elapsed {_session.Elapsed:F1}s, completion {(_session.Completion == null ? "none" : $"reason {_session.Completion.EndReason} exit {_session.Completion.ReachedExit} collapsed {_session.Completion.Collapsed}")}";
            Assert.AreEqual(SessionPhase.Ended, _session.Phase, state);
            Assert.IsTrue(_session.Completion.ReachedExit, state);
            Assert.AreEqual(EndReason.Finished, _session.Completion.EndReason, state);
            Assert.AreEqual(1, _session.Log.Count(EventTypes.ExitReached), state);
            var bridge = (Fire3DBridge)_app.Bridge;
            Assert.IsTrue(bridge.Sent.Any(m => m.Type == BridgeMessages.Completed), "Completed message sent; " + state);
            Assert.IsTrue(bridge.Sent.Any(m => m.Type == BridgeMessages.Events), "Events message sent; " + state);
        }

        [UnityTest]
        public IEnumerator OpenedDoorLeafVisiblySwingsAwayFromPlayer()
        {
            foreach (var view in Object.FindObjectsByType<DoorView>())
                foreach (var r in view.GetComponentsInChildren<MeshRenderer>())
                    if (r.name == "Leaf")
                        Assert.IsFalse(r.isPartOfStaticBatch, $"{view.name} leaf is static-batched and cannot move");

            var player = _session.Player;
            player.Teleport(new Vector3(21f, 0.05f, 12.6f), 0f); // Lobby, facing the north exit door.
            yield return new WaitForFixedUpdate();
            yield return null;
            var interaction = _session.Interaction;
            var door = interaction.Target;
            Assert.IsNotNull(door);
            var leaf = door.GetComponentsInChildren<MeshRenderer>().First(r => r.name == "Leaf");
            var before = leaf.bounds.center;
            interaction.Press(interaction.Actions.ToList().FindIndex(a => a.Id == "open"));
            yield return new WaitForSeconds(1.6f);
            Assert.AreEqual("open", door.State);
            var after = leaf.bounds.center;
            Assert.Greater(Vector3.Distance(before, after), 0.25f, "leaf mesh moved");
            // Swung away: the leaf centre moved to the far side of the doorway (+Z, outside), not towards the player.
            Assert.Greater(after.z, before.z, "door opens away from the player");
        }

        [UnityTest]
        public IEnumerator LockedWestExitSendsWrongExit()
        {
            var player = _session.Player;
            player.Teleport(new Vector3(1.2f, 0.05f, 7f), -90f);
            for (var i = 0; i < 90; i++)
            {
                player.InputSource = new FixedInput(new Vector2(0f, 1f));
                yield return null;
            }

            Assert.AreEqual(SessionPhase.Running, _session.Phase, "a locked exit does not end the session");
            Assert.GreaterOrEqual(_session.Log.Count(EventTypes.WrongExit), 1);
        }

        [UnityTest]
        public IEnumerator CameraNeverEndsInsideWalls()
        {
            var player = _session.Player;
            player.Teleport(new Vector3(4f, 0.05f, 6.6f), 180f); // Corridor, back to the office wall.
            yield return null;
            var rig = Object.FindAnyObjectByType<ThirdPersonCameraRig>();
            for (var yaw = 0; yaw < 360; yaw += 30)
            {
                rig.SnapBehind(yaw);
                for (var k = 0; k < 3; k++)
                    rig.Step(0.016f);
                Assert.IsFalse(Physics.CheckSphere(rig.transform.position, 0.12f, ~0, QueryTriggerInteraction.Ignore) && !Overlaps(rig.transform.position, player.transform),
                    $"camera inside geometry at yaw {yaw}");
            }
        }

        private static bool Overlaps(Vector3 p, Transform player)
        {
            foreach (var c in Physics.OverlapSphere(p, 0.12f, ~0, QueryTriggerInteraction.Ignore))
                if (!c.transform.IsChildOf(player))
                    return false;
            return true;
        }

        [UnityTest]
        public IEnumerator KitchenFireSpreadsAndSmokeReachesUpperFloor()
        {
            var sim = _session.Simulation;
            // Doors left open: kitchen, both stair doors (closed doors would hold most smoke back, see EditMode tests).
            foreach (var id in new[] { "door-L1-office-2", "door-L1-stair", "door-L2-stair" })
                sim.Enqueue(new HazardCommand { Kind = HazardCommandKind.SetDoorOpen, ObjectId = id, Flag = true });
            for (var i = 0; i < 900; i++) // 90 s of simulation
                sim.Step();
            var kitchen = sim.Grid.Sample(new float3(12.2f, 0.1f, 1.6f));
            Assert.Greater(kitchen.Fire, 0.3f, "kitchen burns");
            var stairTop = sim.Grid.Sample(new float3(22f, 3.3f, 4f));
            Assert.Greater(stairTop.Smoke + sim.Grid.Sample(new float3(21f, 3.3f, 7f)).Smoke, 0.02f, "smoke rises to L2");
            Assert.IsTrue(sim.Alarm.Raised, "detectors raise the alarm");
            yield return null;
        }

        [UnityTest]
        public IEnumerator TowelWetAtSinkAndCoverNose()
        {
            var player = _session.Player;
            var interaction = _session.Interaction;
            var towel = Interactable.All.First(i => i.CapabilityId == Capabilities.ItemTowel);
            player.Teleport(new Vector3(towel.transform.position.x, 3.25f, towel.transform.position.z - 0.85f), 0f);
            yield return null;
            yield return null;
            var pick = interaction.Actions.ToList().FindIndex(a => a.Id == "pick_up");
            Assert.GreaterOrEqual(pick, 0, "towel offers pick up");
            interaction.Press(pick);
            Assert.AreEqual(towel, interaction.Held);

            var sink = Interactable.All.First(i => i.CapabilityId == Capabilities.WaterSink);
            var front = sink.transform.position + sink.transform.forward * 0.8f;
            player.Teleport(new Vector3(front.x, sink.transform.position.y + 0.05f, front.z),
                Quaternion.LookRotation(-sink.transform.forward).eulerAngles.y);
            yield return null;
            yield return null;
            var wet = interaction.Actions.ToList().FindIndex(a => a.Id == "wet_item");
            Assert.GreaterOrEqual(wet, 0, "sink offers wet when holding a dry towel");
            interaction.Press(wet);
            yield return new WaitForSeconds(2.3f);
            Assert.AreEqual("wet", towel.State);
            Assert.AreEqual(1, _session.Log.Count(EventTypes.ItemWetted));

            var cover = interaction.Actions.ToList().FindIndex(a => a.Id == "cover_nose");
            Assert.GreaterOrEqual(cover, 0);
            interaction.Press(cover);
            Assert.IsTrue(player.GetComponent<Gameplay.Player.PlayerVitals>().NoseCovered);
        }

        private sealed class FixedInput : Gameplay.Controls.IGameplayInputSource
        {
            private readonly Vector2 _move;
            public FixedInput(Vector2 move) => _move = move;
            public Gameplay.Controls.GameplayInputFrame Current => new() { Move = _move };
        }
    }
}
