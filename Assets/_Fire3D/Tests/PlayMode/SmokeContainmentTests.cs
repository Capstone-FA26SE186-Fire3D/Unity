using System;
using System.Collections;
using System.IO;
using Fire3D.Simulation;
using Fire3D.Unity.Session;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Fire3D.Unity.Tests.PlayModeTests
{
    /// <summary>
    /// Smoke puffs stay where the simulated smoke is: none in a room whose door is closed (the user saw a puff on the
    /// corridor wall in front of a closed office door), none inside walls. Shoots the user's viewpoint for review.
    /// </summary>
    public class SmokeContainmentTests
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator NoPuffsInClosedRoomsOrWalls()
        {
            var dir = Environment.GetEnvironmentVariable("FET3D_AUTOPLAY_OUT") is { Length: > 0 } d
                ? d
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Builds", "Autoplay", "contain-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(dir);
            yield return SceneManager.LoadSceneAsync("Development_Graybox", LoadSceneMode.Single);
            yield return null;
            yield return null;
            var session = UnityEngine.Object.FindAnyObjectByType<TrainingSession>();
            session.StartRunning();
            session.enabled = false;
            var sim = session.Simulation;
            // Kitchen door open, every other door stays closed: smoke fills the corridor but not the offices.
            sim.Enqueue(new HazardCommand { Kind = HazardCommandKind.SetDoorOpen, ObjectId = "door-L1-office-2", Flag = true });
            var player = session.Player;
            player.Teleport(new Vector3(21f, 0.05f, 12f), 0f);
            for (var i = 0; i < 1600; i++)
            {
                sim.Advance(0.05f);
                if (i % 10 == 0)
                    yield return null;
            }

            // Stand in the corridor in front of the closed Office A door (the user's shot) and let puffs settle.
            player.Teleport(new Vector3(4.2f, 0.05f, 7.4f), 180f);
            player.CameraRig.Snap(180f, 8f);
            for (var i = 0; i < 90; i++)
                yield return null;

            var grid = sim.Grid;
            var layout = grid.Layout;
            var ps = UnityEngine.Object.FindAnyObjectByType<Fire3D.Unity.Presentation.VolumeSmoke>().GetComponent<ParticleSystem>();
            var buffer = new ParticleSystem.Particle[ps.main.maxParticles];
            var n = ps.GetParticles(buffer);
            var inClean = 0;
            var inWall = 0;
            var offSide = 0; // in Office A (x 0..8, z 0..6, door closed) in a cell the closed-door leak did not reach
            var leaked = 0;  // in Office A where the leak under the door did put smoke: legitimate seepage
            for (var k = 0; k < n; k++)
            {
                var p = buffer[k].position;
                if (!layout.TryCellAt(p, out var cell))
                    continue;
                var flags = (CellFlags)grid.Flags[cell];
                if ((flags & CellFlags.Wall) != 0)
                    inWall++;
                else if (grid.Smoke[cell] < 0.015f)
                    inClean++;
                if (p.x > 0.3f && p.x < 7.7f && p.z > 0.3f && p.z < 5.7f && p.y < 3.1f)
                {
                    if (grid.Smoke[cell] >= 0.03f)
                        leaked++;
                    else
                    {
                        offSide++;
                        Debug.Log($"[Fire3D contain] stray office puff at {p} smoke {grid.Smoke[cell]:F3} size {buffer[k].GetCurrentSize(ps):F2}");
                    }
                }
            }

            Debug.Log($"[Fire3D contain] puffs {n}, in walls {inWall}, in clean cells {inClean}, in closed Office A {offSide} stray + {leaked} on leaked smoke");
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(dir, "closed_door_view.png"), tex.EncodeToPNG());
            UnityEngine.Object.Destroy(tex);

            Assert.Greater(n, 50, "the corridor has smoke puffs");
            Assert.AreEqual(0, inWall, "no puff sits inside a wall cell");
            Assert.AreEqual(0, offSide, "no puff inside the closed office beyond where the door leak put smoke");
            Assert.Less(inClean, n * 0.03f, "almost no puffs in cells without smoke");
        }
    }
}
