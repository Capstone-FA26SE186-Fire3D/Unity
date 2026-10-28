using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Fire3D.Contracts;
using Fire3D.Unity.Gameplay.Cameras;
using Fire3D.Unity.Gameplay.Controls;
using Fire3D.Unity.Gameplay.Interactions;
using Fire3D.Unity.Gameplay.Player;
using Fire3D.Unity.Session;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Fire3D.Unity.Development
{
    /// <summary>
    /// Development only: plays a session like a well-trained learner (towel → wet it → raise the alarm → cover the
    /// nose → open doors → stairs → exit) through the same input and interaction paths as a person, and records
    /// frame time, memory and screenshots. It drives <see cref="IGameplayInputSource"/> and
    /// <see cref="InteractionController.Press"/>, so a failure here is a gameplay or level problem.
    /// </summary>
    public sealed class AutoPilot : MonoBehaviour, IGameplayInputSource
    {
        public const string Arg = "-fet3dAutoplay";
        public const string OutArg = "-fet3dAutoplayOut";

        [SerializeField] private float screenshotIntervalSeconds = 10f;
        [SerializeField] private float stuckSeconds = 1.6f;
        [SerializeField] private float stepTimeoutSeconds = 90f;
        [SerializeField] private float cameraTurnDegreesPerSecond = 140f;
        [Tooltip("Measure real frame cost: disable vSync and the frame-rate cap while the bot plays.")]
        [SerializeField] private bool uncapFrameRate = true;

        private TrainingSession _session;
        private PlayerMotor _player;
        private PlayerVitals _vitals;
        private InteractionController _interaction;
        private ThirdPersonCameraRig _rig;
        private AutoPilotNavigator _nav;
        private AutoPilotRecorder _rec;
        private GameplayInputFrame _frame;
        private string _step = "start";
        private readonly List<JObject> _steps = new();
        private readonly HashSet<string> _touchedDoors = new();
        private readonly List<string> _notes = new();
        private int _replans;
        private int _doorsOpened;
        private float _nextShot;
        private int _crouchFrame = -1;
        private CharacterController _cc;
        private float _maxPenetration;
        private string _maxPenetrationAt;
        private float _maxSink;
        private string _maxSinkAt;
        private int _sunkFrames;
        private int _cameraInsideFrames;
        private string _cameraInsideFirstAt;
        private readonly Dictionary<string, int> _cameraInsideByStep = new();
        private int _savedVSync;
        private int _savedTargetFps;
        private bool _uncapped;

        /// <summary>Crouch is a toggle: it is reported for exactly the frame after the request.</summary>
        public GameplayInputFrame Current
        {
            get
            {
                var f = _frame;
                f.CrouchToggled = Time.frameCount == _crouchFrame;
                return f;
            }
        }
        public bool Finished { get; private set; }
        public JObject Report { get; private set; }
        public string OutputDirectory => _rec?.OutputDirectory;
        public event Action<AutoPilot> Completed;

        /// <summary>Starts the run on a session in Briefing; <paramref name="outDir"/> receives report.json, frames.csv and PNGs.</summary>
        public void Run(TrainingSession session, string outDir)
        {
            _session = session;
            _player = session.Player;
            _vitals = _player.GetComponent<PlayerVitals>();
            _interaction = session.Interaction;
            _rig = _player.CameraRig;
            _player.InputSource = this;
            if (_rig != null)
                _rig.InputSource = this;
            _nav = new AutoPilotNavigator(session.Simulation.Grid, _player.transform);
            _rec = new AutoPilotRecorder(outDir);
            if (uncapFrameRate)
            {
                _savedVSync = QualitySettings.vSyncCount;
                _savedTargetFps = Application.targetFrameRate;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                _uncapped = true;
            }
            if (_nav.Stairs.Count == 0)
                _notes.Add("no measurable stair flight found");
            StartCoroutine(Play());
        }

        private void LateUpdate()
        {
            if (_rec == null || Finished)
                return;
            _rec.Sample(_step);
            TrackContacts();
        }

        /// <summary>Per frame: how deep the capsule sits in level geometry, and whether the camera is inside a solid.</summary>
        private void TrackContacts()
        {
            _cc ??= _player.GetComponent<CharacterController>();
            if (_cc != null && _cc.enabled)
            {
                var p = _player.transform.position;
                var r = _cc.radius;
                var probe = _probe != null ? _probe : _probe = new GameObject("PenetrationProbe") { hideFlags = HideFlags.HideAndDontSave }.AddComponent<CapsuleCollider>();
                probe.radius = r;
                probe.height = _cc.height;
                probe.center = _cc.center;
                probe.isTrigger = true;
                probe.transform.SetPositionAndRotation(p, _player.transform.rotation);
                foreach (var c in Physics.OverlapCapsule(p + Vector3.up * r, p + Vector3.up * (_cc.height - r), r, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (c.transform.IsChildOf(_player.transform) || c is CharacterController)
                        continue;
                    if (Physics.ComputePenetration(probe, probe.transform.position, probe.transform.rotation, c, c.transform.position, c.transform.rotation, out _, out var d) && d > _maxPenetration)
                    {
                        _maxPenetration = d;
                        _maxPenetrationAt = $"{Fmt(p)} step {_step} vs {c.name}";
                    }
                }
            }

            // Feet below the walking surface under them ("sinking into the floor").
            var feet = _player.transform.position;
            foreach (var hit in Physics.RaycastAll(feet + Vector3.up * 0.6f, Vector3.down, 2f, ~0, QueryTriggerInteraction.Ignore).OrderBy(h => h.distance))
            {
                if (hit.collider.transform.IsChildOf(_player.transform) || hit.collider is CharacterController)
                    continue;
                var sink = hit.point.y - feet.y;
                if (sink > 0.08f)
                    _sunkFrames++;
                if (sink > _maxSink)
                {
                    _maxSink = sink;
                    _maxSinkAt = $"{Fmt(feet)} step {_step} on {hit.collider.name} (top {hit.point.y:F2})";
                }

                break;
            }

            var cam = Camera.main;
            if (cam != null && Physics.CheckSphere(cam.transform.position, 0.08f, ~0, QueryTriggerInteraction.Ignore))
            {
                foreach (var c in Physics.OverlapSphere(cam.transform.position, 0.08f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (c.transform.IsChildOf(_player.transform) || c is CharacterController)
                        continue;
                    _cameraInsideFrames++;
                    _cameraInsideByStep[_step] = _cameraInsideByStep.GetValueOrDefault(_step) + 1;
                    _cameraInsideFirstAt ??= $"{Fmt(cam.transform.position)} inside {c.name} (player {Fmt(_player.transform.position)}, step {_step})";
                    break;
                }
            }
        }

        private CapsuleCollider _probe;

        /// <summary>Window closed or Alt+F4 mid-run: keep what was measured.</summary>
        private void OnApplicationQuit()
        {
            if (_rec != null && !Finished)
                Abort("application quit before the run finished");
        }

        private void OnDestroy()
        {
            if (_probe != null)
                Destroy(_probe.gameObject);
            RestoreFrameRate();
            _nav?.Dispose();
            _rec?.Dispose();
        }

        private IEnumerator Play()
        {
            yield return _rec.Capture("briefing");
            _session.StartRunning();
            _nextShot = Time.time + screenshotIntervalSeconds;

            var towel = Find(Capabilities.ItemTowel);
            var water = Interactable.All.FirstOrDefault(i => i != null && Capabilities.IsWater(i.CapabilityId));
            var callPoint = Find(Capabilities.AlarmCallPoint);

            if (towel != null)
                yield return Step("pick_towel", UseOn(towel, "pick_up", () => _interaction.Held == towel));
            if (towel != null && water != null && _interaction.Held == towel)
                yield return Step("wet_towel", UseOn(water, "wet_item", () => towel.State == "wet"));
            if (callPoint != null)
                yield return Step("raise_alarm", UseOn(callPoint, "press", () => _session.Log.Count(EventTypes.AlarmRaised) > 0));
            if (towel != null && towel.State == "wet")
                yield return Step("cover_nose", UseHeld("cover_nose", () => _vitals.NoseCovered));
            yield return Step("evacuate", Evacuate());

            _frame = default;
            yield return new WaitForSeconds(0.5f);
            yield return _rec.Capture("result");
            Finish();
        }

        private IEnumerator Step(string name, IEnumerator body)
        {
            if (_session.Phase == SessionPhase.Ended)
                yield break;
            _step = name;
            var started = (float)_session.Elapsed;
            var result = new JObject { ["name"] = name, ["startSeconds"] = Math.Round(started, 2) };
            _steps.Add(result);
            var ok = false;
            var deadline = Time.time + stepTimeoutSeconds;
            var routine = Wrap(body, v => ok = v);
            while (routine.MoveNext())
            {
                if (Time.time > deadline)
                {
                    _notes.Add($"{name}: timed out after {stepTimeoutSeconds}s");
                    break;
                }

                TakeTimedScreenshot();
                yield return routine.Current;
            }

            _frame = default;
            result["ok"] = ok;
            result["endSeconds"] = Math.Round(_session.Elapsed, 2);
            yield return _rec.Capture(name + (ok ? "" : "_failed"));
        }

        /// <summary>Runs a nested routine and reports its bool result (the last yielded <c>bool</c>).</summary>
        private static IEnumerator Wrap(IEnumerator body, Action<bool> result)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            while (stack.Count > 0)
            {
                var top = stack.Peek();
                if (!top.MoveNext())
                {
                    stack.Pop();
                    continue;
                }

                switch (top.Current)
                {
                    case IEnumerator nested:
                        stack.Push(nested);
                        break;
                    case bool b:
                        result(b);
                        break;
                    default:
                        yield return top.Current;
                        break;
                }
            }
        }

        private void TakeTimedScreenshot()
        {
            if (Time.time < _nextShot)
                return;
            _nextShot = Time.time + screenshotIntervalSeconds;
            StartCoroutine(_rec.Capture($"t{(int)_session.Elapsed:000}s_{_step}"));
        }

        // ---- Tasks ----------------------------------------------------------------------------------------------

        private IEnumerator UseOn(Interactable target, string actionId, Func<bool> done)
        {
            yield return GoTo(target.transform.position, 0.9f);
            yield return Face(target);
            if (_interaction.Target != target)
            {
                _notes.Add($"{_step}: could not target {target.ObjectId}");
                yield return false;
                yield break;
            }

            yield return Act(actionId, done);
        }

        private IEnumerator UseHeld(string actionId, Func<bool> done)
        {
            yield return Act(actionId, done);
        }

        private IEnumerator Act(string actionId, Func<bool> done)
        {
            var slot = Slot(actionId);
            if (slot < 0)
            {
                _notes.Add($"{_step}: action '{actionId}' not offered ({string.Join(",", _interaction.Actions.Select(a => a.Id))})");
                yield return false;
                yield break;
            }

            // Like a player: object actions are tapped on the world tile (a timed one then fills by itself),
            // held-item actions use the thumb buttons.
            var action = _interaction.Actions[slot];
            var tile = action.Source == ActionSource.Target;
            var hold = action.IsHold;
            if (tile)
                _interaction.Tap(slot);
            else
                _interaction.Press(slot);
            var until = Time.time + (hold ? action.Definition.HoldSeconds + 3f : 1f);
            while (!done() && Time.time < until)
                yield return null;
            if (hold && !tile)
                _interaction.Release(slot);
            yield return done();
        }

        private IEnumerator Evacuate()
        {
            var exits = ExitZone.All.Where(e => e != null && !e.Blocked).ToList();
            if (exits.Count == 0)
            {
                _notes.Add("no open exit in the scenario");
                yield return false;
                yield break;
            }

            var from = _player.transform.position;
            var exit = exits.OrderBy(e => Vector3.Distance(e.transform.position, from)).First();
            var goal = exit.WorldBounds.center;
            goal.y = exit.transform.position.y;
            yield return GoTo(goal, 0.2f, untilEnded: true);
            yield return _session.Phase == SessionPhase.Ended && _session.Completion.ReachedExit;
        }

        // ---- Movement -------------------------------------------------------------------------------------------

        /// <summary>Walks to <paramref name="goal"/>, replanning at floor changes, when stuck and every few seconds.</summary>
        private IEnumerator GoTo(Vector3 goal, float arriveMeters, bool untilEnded = false)
        {
            var legs = 0;
            while (_session.Phase == SessionPhase.Running && legs++ < 8)
            {
                var route = _nav.Plan(_player.transform.position, goal);
                if (route == null)
                {
                    _notes.Add($"{_step}: no route to {Fmt(goal)} from {Fmt(_player.transform.position)}");
                    yield break;
                }

                yield return Follow(route.Points, route.ReachesGoal ? arriveMeters : 0.35f, route.ReachesGoal);
                if (_session.Phase != SessionPhase.Running)
                    yield break;
                if (route.ReachesGoal && Planar(_player.transform.position, goal) <= arriveMeters + 0.4f && !untilEnded)
                    yield break;
                if (route.ReachesGoal && untilEnded && Planar(_player.transform.position, goal) <= arriveMeters + 0.4f)
                {
                    // Inside the exit trigger area but not ended: step forward a little.
                    yield return Nudge((goal - _player.transform.position).normalized, 0.8f);
                    yield break;
                }
            }
        }

        private IEnumerator Follow(List<Vector3> points, float arriveMeters, bool isFinalLeg)
        {
            var i = 0;
            var lastProgressAt = Time.time;
            var lastPos = _player.transform.position;
            var replanAt = Time.time + 3f;
            var stuckCount = 0;

            while (_session.Phase == SessionPhase.Running && i < points.Count)
            {
                var pos = _player.transform.position;
                while (i < points.Count - 1 && Planar(pos, points[i]) < 0.45f)
                    i++;
                var target = points[i];
                var last = i == points.Count - 1;
                if (last && Planar(pos, target) <= arriveMeters)
                    break;

                if (TryHandleDoor())
                {
                    yield return Door();
                    lastProgressAt = Time.time;
                    continue;
                }

                UpdateCrouch();
                var dir = target - pos;
                dir.y = 0f;
                Steer(dir.normalized, last ? Mathf.Clamp01(Planar(pos, target) / 0.6f + 0.35f) : 1f);

                if (Planar(pos, lastPos) > 0.2f)
                {
                    lastPos = pos;
                    lastProgressAt = Time.time;
                }
                else if (Time.time - lastProgressAt > stuckSeconds)
                {
                    stuckCount++;
                    _replans++;
                    _notes.Add($"{_step}: stuck at {Fmt(pos)} heading to {Fmt(target)} (#{stuckCount})");
                    if (stuckCount >= 4)
                        yield break;
                    var side = Vector3.Cross(Vector3.up, dir.normalized) * (stuckCount % 2 == 0 ? 1f : -1f);
                    yield return Nudge((side - dir.normalized * 0.5f).normalized, 0.5f);
                    lastProgressAt = Time.time;
                    if (isFinalLeg)
                        yield break; // Replan from GoTo.
                }

                if (isFinalLeg && Time.time > replanAt)
                {
                    _replans++;
                    yield break; // Hazards changed: let GoTo plan again with fresh smoke/fire costs.
                }

                yield return null;
            }

            _frame = default;
        }

        private IEnumerator Nudge(Vector3 worldDir, float seconds)
        {
            var until = Time.time + seconds;
            while (Time.time < until && _session.Phase == SessionPhase.Running)
            {
                Steer(worldDir, 0.8f);
                yield return null;
            }

            _frame = default;
        }

        private IEnumerator Face(Interactable target)
        {
            var until = Time.time + 2.5f;
            while (_interaction.Target != target && Time.time < until && _session.Phase == SessionPhase.Running)
            {
                var to = target.transform.position - _player.transform.position;
                to.y = 0f;
                // Lean in slowly: the motor turns the body towards its velocity.
                Steer(to.normalized, to.magnitude > 0.7f ? 0.45f : 0.18f);
                yield return null;
            }

            _frame = default;
            yield return null;
        }

        private void Steer(Vector3 worldDir, float magnitude)
        {
            var yaw = _rig != null ? _rig.Yaw : 0f;
            var local = Quaternion.Euler(0f, -yaw, 0f) * worldDir;
            _frame.Move = new Vector2(local.x, local.z).normalized * Mathf.Clamp01(magnitude);
            _frame.Sprint = false;
            var desiredYaw = Mathf.Atan2(worldDir.x, worldDir.z) * Mathf.Rad2Deg;
            var delta = Mathf.DeltaAngle(yaw, desiredYaw);
            _frame.LookDegrees = new Vector2(Mathf.Clamp(delta, -cameraTurnDegreesPerSecond * Time.deltaTime, cameraTurnDegreesPerSecond * Time.deltaTime), 0f);
        }

        private void UpdateCrouch()
        {
            if (_vitals == null)
                return;
            if (_crouchFrame >= Time.frameCount)
                return;
            if (_player.Crouched ? _vitals.LocalSmoke < 0.1f : _vitals.LocalSmoke > 0.3f)
                _crouchFrame = Time.frameCount + 1;
        }

        // ---- Doors ----------------------------------------------------------------------------------------------

        private bool TryHandleDoor()
        {
            var t = _interaction.Target;
            if (t == null || !Capabilities.IsDoor(t.CapabilityId) || t.State != "closed")
                return false;
            var to = t.transform.position - _player.transform.position;
            to.y = 0f;
            return to.magnitude < 1.4f;
        }

        private IEnumerator Door()
        {
            var door = _interaction.Target;
            _frame = default;
            yield return null;
            var firstDoor = _touchedDoors.Count == 0;
            if (_touchedDoors.Add(door.ObjectId) && Slot("touch") >= 0)
            {
                _interaction.Press(Slot("touch"));
                if (firstDoor)
                {
                    yield return new WaitForSeconds(0.55f); // Hand on the leaf.
                    yield return _rec.Capture("door_touch");
                }

                yield return WhileBusy();
            }

            var open = Slot("open");
            if (open < 0)
            {
                _notes.Add($"{_step}: door {door.ObjectId} offers no Open ({door.State})");
                yield return Nudge(-_player.transform.forward, 0.4f);
                yield break;
            }

            _interaction.Press(open);
            if (firstDoor)
            {
                yield return new WaitForSeconds(0.6f); // Leaf mid-swing.
                yield return _rec.Capture("door_open");
            }

            yield return WhileBusy();
            yield return new WaitForSeconds(0.35f);
            if (door.State == "open")
                _doorsOpened++;
        }

        private IEnumerator WhileBusy()
        {
            yield return null;
            var until = Time.time + 4f;
            while (_interaction.Busy && Time.time < until)
                yield return null;
        }

        // ---- Helpers --------------------------------------------------------------------------------------------

        private int Slot(string actionId)
        {
            var actions = _interaction.Actions;
            for (var i = 0; i < actions.Count; i++)
                if (actions[i].Id == actionId)
                    return i;
            return -1;
        }

        private static Interactable Find(string capability) =>
            Interactable.All.FirstOrDefault(i => i != null && i.CapabilityId == capability);

        private static float Planar(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.x, a.z), new Vector2(b.x, b.z));

        private static string Fmt(Vector3 v) => $"({v.x:F1},{v.y:F1},{v.z:F1})";

        private void Finish()
        {
            if (Finished)
                return;
            Finished = true;
            var c = _session.Completion;
            var counts = new JObject();
            foreach (var g in _session.Log.All.GroupBy(e => e.Type).OrderBy(g => g.Key))
                counts[g.Key] = g.Count();

            Report = new JObject
            {
                ["schema"] = "fet3d.autoplay/1",
                ["createdAt"] = DateTime.UtcNow.ToString("o"),
                ["machine"] = new JObject
                {
                    ["platform"] = Application.platform.ToString(),
                    ["editor"] = Application.isEditor,
                    ["batchMode"] = Application.isBatchMode,
                    ["unity"] = Application.unityVersion,
                    ["device"] = SystemInfo.deviceModel,
                    ["cpu"] = SystemInfo.processorType,
                    ["gpu"] = SystemInfo.graphicsDeviceName,
                    ["graphicsApi"] = SystemInfo.graphicsDeviceType.ToString(),
                    ["memoryMB"] = SystemInfo.systemMemorySize,
                    ["resolution"] = $"{Screen.width}x{Screen.height}",
                    ["quality"] = QualitySettings.names[QualitySettings.GetQualityLevel()],
                    ["frameRateUncapped"] = _uncapped,
                    ["projectVSync"] = _uncapped ? _savedVSync : QualitySettings.vSyncCount,
                    ["projectTargetFrameRate"] = _uncapped ? _savedTargetFps : Application.targetFrameRate,
                },
                ["outcome"] = new JObject
                {
                    ["phase"] = _session.Phase.ToString(),
                    ["reachedExit"] = c?.ReachedExit ?? false,
                    ["collapsed"] = c?.Collapsed ?? false,
                    ["endReason"] = c?.EndReason,
                    ["elapsedSeconds"] = Math.Round(_session.Elapsed, 2),
                    ["health"] = _vitals != null ? Math.Round(_vitals.Health, 3) : null,
                    ["smokeInhaled"] = _vitals != null ? Math.Round(_vitals.SmokeInhaled, 3) : null,
                    ["doorsOpened"] = _doorsOpened,
                    ["replans"] = _replans,
                },
                ["steps"] = new JArray(_steps),
                ["events"] = counts,
                ["frames"] = _rec.Frames(),
                ["rendering"] = _rec.Rendering(),
                ["memory"] = _rec.Memory(),
                ["stairs"] = new JArray(_nav.Stairs.Select(s => new JObject { ["top"] = Fmt(s.Top), ["bottom"] = Fmt(s.Bottom), ["topEntry"] = Fmt(s.TopEntry), ["bottomEntry"] = Fmt(s.BottomEntry) })),
                ["contacts"] = new JObject
                {
                    ["maxCapsulePenetrationMeters"] = Math.Round(_maxPenetration, 3),
                    ["maxCapsulePenetrationAt"] = _maxPenetrationAt,
                    ["maxFeetBelowSurfaceMeters"] = Math.Round(_maxSink, 3),
                    ["maxFeetBelowSurfaceAt"] = _maxSinkAt,
                    ["framesFeetBelowSurface"] = _sunkFrames,
                    ["cameraInsideGeometryFrames"] = _cameraInsideFrames,
                    ["cameraInsideFirstAt"] = _cameraInsideFirstAt,
                    ["cameraInsideByStep"] = JObject.FromObject(_cameraInsideByStep),
                },
                ["notes"] = new JArray(_notes),
                ["screenshots"] = new JArray(_rec.Screenshots),
            };
            RestoreFrameRate();
            _rec.WriteCsv();
            _rec.WriteReport(Report);
            Debug.Log($"[Fire3D autoplay] reachedExit={c?.ReachedExit ?? false} elapsed={_session.Elapsed:F1}s avgFps={Report["frames"]?["avgFps"]} p95={Report["frames"]?["p95Ms"]}ms → {_rec.OutputDirectory}");
            Completed?.Invoke(this);
        }

        private void RestoreFrameRate()
        {
            if (!_uncapped)
                return;
            _uncapped = false;
            QualitySettings.vSyncCount = _savedVSync;
            Application.targetFrameRate = _savedTargetFps;
        }

        // ---- Hosting --------------------------------------------------------------------------------------------

        public static bool RequestedFromCommandLine() => Array.IndexOf(Environment.GetCommandLineArgs(), Arg) >= 0;

        /// <summary>Output folder: <c>-fet3dAutoplayOut</c>, else Builds/Autoplay (Editor) or Autoplay next to the player.</summary>
        public static string DefaultOutputDirectory()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, OutArg);
            if (i >= 0 && i + 1 < args.Length)
                return args[i + 1];
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var root = Application.isEditor ? System.IO.Path.Combine(Application.dataPath, "..", "Builds", "Autoplay")
                : Application.platform == RuntimePlatform.Android ? System.IO.Path.Combine(Application.persistentDataPath, "Autoplay")
                : System.IO.Path.Combine(Application.dataPath, "..", "Autoplay");
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, stamp));
        }

        /// <summary>Attaches a pilot to a session in Briefing. In a player, <paramref name="quitWhenDone"/> exits with 0 on a safe exit.</summary>
        public static AutoPilot Attach(TrainingSession session, string outDir, bool quitWhenDone, float timeoutSeconds = 420f)
        {
            var pilot = new GameObject("AutoPilot").AddComponent<AutoPilot>();
            pilot.Run(session, outDir);
            pilot.StartCoroutine(pilot.Watchdog(timeoutSeconds));
            if (quitWhenDone)
                pilot.Completed += p => Application.Quit(p.Report?["outcome"]?["reachedExit"]?.Value<bool>() == true ? 0 : 2);
            return pilot;
        }

        private IEnumerator Watchdog(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
            Abort($"no result after {seconds}s");
        }

        /// <summary>Stops the run early (time-out from the host) and still writes the report.</summary>
        public void Abort(string reason)
        {
            if (Finished)
                return;
            _notes.Add("aborted: " + reason);
            StopAllCoroutines();
            _frame = default;
            Finish();
        }
    }
}
