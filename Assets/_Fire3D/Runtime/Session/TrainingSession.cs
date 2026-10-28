using System;
using System.Collections.Generic;
using Fire3D.Contracts;
using Fire3D.Simulation;
using Fire3D.Unity.Content;
using Fire3D.Unity.Gameplay.Cameras;
using Fire3D.Unity.Gameplay.Controls;
using Fire3D.Unity.Gameplay.Interactions;
using Fire3D.Unity.Gameplay.Player;
using Fire3D.Unity.Integration;
using Fire3D.Unity.Presentation;
using Fire3D.Unity.Presentation.UI;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;

namespace Fire3D.Unity.Session
{
    public enum SessionPhase
    {
        Briefing,
        Running,
        Paused,
        Ended,
    }

    /// <summary>
    /// Runs one training or playtest session: briefing → running → ended. Steps the simulation, applies exposure,
    /// detects exits/collapse/time-out, records telemetry and forwards it to Mobile. Unity never decides
    /// Passed/NotPassed: the backend scores the pinned rubric from the events.
    /// </summary>
    public sealed class TrainingSession : MonoBehaviour
    {
        public const float TelemetryIntervalSeconds = 5f;
        public const float FlushIntervalSeconds = 1f;

        private LaunchRequest _launch;
        private ScenarioState _scenario;
        private BuiltWorld _world;
        private IFire3DBridge _bridge;
        private RuntimeAssets _assets;
        private GameContext _ctx;
        private SessionEventLog _log;
        private PlayerMotor _player;
        private PlayerVitals _vitals;
        private InteractionController _interaction;
        private GameplayInputReader _input;
        private TrainingHud _hud;
        private double _elapsed;
        private float _nextTelemetry;
        private float _nextFlush;
        private bool _reachedExit;
        private readonly List<SimulationEvent> _simEvents = new();

        public SessionPhase Phase { get; private set; }
        public SessionEventLog Log => _log;

        /// <summary>The bridge this session reports to (tests read what was sent).</summary>
        public IFire3DBridge Bridge => _bridge;
        public FireSimulation Simulation => _world?.Simulation;
        public PlayerMotor Player => _player;
        public InteractionController Interaction => _interaction;
        public double Elapsed => _elapsed;
        public SessionCompletion Completion { get; private set; }
        public event Action<SessionCompletion> Ended;
        public event Action Exited;

        /// <summary>Wires the session; call once after the world is built and the player rig exists.</summary>
        /// <summary>A scenario plays at night when its name or instructions say so; the sky through the windows follows.</summary>
        private static bool IsNight(ScenarioState scenario)
        {
            var text = $"{scenario?.Name} {scenario?.LearnerInstructions}".ToLowerInvariant();
            return text.Contains("ban đêm") || text.Contains("buổi đêm") || text.Contains("đêm khuya") || text.Contains("night");
        }

        public void Begin(LaunchRequest launch, ScenarioState scenario, BuiltWorld world, IFire3DBridge bridge, RuntimeAssets assets,
            GameObject playerRig, bool autoStart = false)
        {
            _launch = launch;
            _scenario = scenario;
            _world = world;
            _bridge = bridge;
            _assets = assets;
            _log = new SessionEventLog(() => _elapsed, launch.ResumeFromSequence);
            _ctx = new GameContext(world.Simulation, _log, launch.Mode, scenario, () => _elapsed);
            GameContext.Install(_ctx);
            GameAudio.Bind(assets);

            _player = playerRig.GetComponent<PlayerMotor>();
            _vitals = playerRig.GetComponent<PlayerVitals>();
            _interaction = playerRig.GetComponent<InteractionController>();
            _input = playerRig.GetComponent<GameplayInputReader>();
            _ctx.Player = _player;
            _ctx.Vitals = _vitals;
            _player.Teleport(world.SpawnPosition, world.SpawnYaw);
            _ctx.Assets = assets;
            var sky = assets != null ? (IsNight(scenario) && assets.SkyNight != null ? assets.SkyNight : assets.Sky) : null;
            if (sky != null)
            {
                // The street seen through windows; ambient light stays the baked/flat one.
                RenderSettings.skybox = sky;
                if (Camera.main != null)
                    Camera.main.clearFlags = CameraClearFlags.Skybox;
            }
            _player.InputEnabled = false;

            foreach (var exit in world.Exits)
                exit.Entered += OnExitEntered;

            var presenters = new GameObject("Presentation").transform;
            presenters.SetParent(transform, false);
            presenters.gameObject.AddComponent<HazardPresenter>().Bind(world.Simulation, _player.transform, assets);
            presenters.gameObject.AddComponent<NpcPresenter>().Bind(world.Simulation, assets);
            var env = presenters.gameObject.AddComponent<EnvironmentPresenter>();
            env.Lighting = FindAnyObjectByType<LightingScenarioSwitcher>();
            env.Bind(world.Simulation, assets, world.SprinklerPositions);
            if (_ctx.Guided)
                presenters.gameObject.AddComponent<GuidanceArrow>().Bind(world.Simulation, _player.transform);

            _hud = new GameObject("TrainingHud").AddComponent<TrainingHud>();
            _hud.transform.SetParent(transform, false);
            _hud.Build(new TrainingHud.Model
            {
                ScenarioName = scenario.Name,
                Instructions = scenario.LearnerInstructions,
                Objectives = scenario.LearningObjectives,
                Mode = launch.Mode,
                Elapsed = () => _elapsed,
                TimeLimitSeconds = scenario.TimeLimitSeconds,
                OnStart = StartRunning,
                OnPause = () => SetPaused(true),
                OnResume = () => SetPaused(false),
                OnAbandon = () => End(EndReason.Abandoned),
                OnExit = Exit,
                OnToggleView = () =>
                {
                    if (_player.CameraRig != null)
                        _player.CameraRig.FirstPerson = !_player.CameraRig.FirstPerson;
                },
                IsFirstPerson = () => _player.CameraRig != null && _player.CameraRig.FirstPerson,
            }, _interaction, _vitals, _player, _input);
            _ctx.Message += _hud.Toast;
            _ctx.Message += (_, tone) =>
            {
                if (tone == MessageTone.Success)
                    GameAudio.Ui(GameAudio.Sounds?.UiSuccess);
                else if (tone == MessageTone.Danger)
                    GameAudio.Ui(GameAudio.Sounds?.UiError);
            };

            foreach (var w in world.Warnings)
                Debug.LogWarning($"[Fire3D] {w}");

            Phase = SessionPhase.Briefing;
            _bridge?.Send(BridgeMessages.Make(BridgeMessages.Launched, launch.SessionId, new JObject
            {
                ["runtimeVersion"] = RuntimeCatalog.RuntimeVersion,
                ["resumeFromSequence"] = launch.ResumeFromSequence,
            }));
            if (autoStart)
                StartRunning();
        }

        public void StartRunning()
        {
            if (Phase != SessionPhase.Briefing)
                return;
            Phase = SessionPhase.Running;
            _player.InputEnabled = true;
            _hud.ShowRunning();
            if (_ctx.Guided && _scenario.LearningObjectives.Count > 0)
                _ctx.Hint(_scenario.LearningObjectives[0]);
        }

        public void SetPaused(bool paused)
        {
            if (Phase == SessionPhase.Ended || Phase == SessionPhase.Briefing)
                return;
            Phase = paused ? SessionPhase.Paused : SessionPhase.Running;
            _player.InputEnabled = !paused;
            _hud.ShowPause(paused);
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused && Phase == SessionPhase.Running)
                SetPaused(true);
            if (paused)
                Flush(force: true);
        }

        private void Update()
        {
            if (_input != null && _input.Current.PausePressed && (Phase == SessionPhase.Running || Phase == SessionPhase.Paused))
                SetPaused(Phase == SessionPhase.Running);
            if (Phase != SessionPhase.Running)
                return;
            Tick(Time.deltaTime);
        }

        /// <summary>Advances the session by <paramref name="dt"/> seconds (public for PlayMode tests).</summary>
        public void Tick(float dt)
        {
            if (Phase != SessionPhase.Running)
                return;
            _elapsed += dt;
            var sim = _world.Simulation;
            sim.SetPlayer(_player.transform.position);
            sim.Advance(dt);

            var feet = _player.transform.position;
            var head = feet + Vector3.up * _player.HeadHeight;
            var sample = sim.Grid.Sample(new float3(head.x, feet.y + 0.1f, head.z));
            // Real smoke pools under the ceiling and creeps down: you breathe it only once your head is inside the
            // layer, which is why crouching and crawling work. Within the layer, the first 0.5 m is a soft edge.
            var inLayer = Mathf.Clamp01((head.y - sample.LayerBottomY + 0.25f) / 0.35f);
            sample.Smoke *= inLayer;
            sample.Visibility = Mathf.Lerp(1f, sample.Visibility, inLayer);
            _vitals.Apply(sample, _player.Crouched, _player.Running, dt);
            _player.SpeedFactor = 1f - 0.45f * (_vitals.Valid() ? _vitals.LocalSmoke : 0f) * (_player.Crouched ? 0.3f : 1f);
            UpdateSmokeHint(sample, head.y);

            foreach (var exit in _world.Exits)
                exit.Check(_player.transform.position);
            DrainSimulationEvents();

            if (Time.time >= _nextTelemetry)
            {
                _nextTelemetry = Time.time + TelemetryIntervalSeconds;
                EmitPeriodic();
            }

            if (_vitals.Collapsed)
            {
                _ctx.Show("Bạn đã gục ngã vì khói và nhiệt", MessageTone.Danger);
                End(EndReason.Finished, collapsed: true);
                return;
            }

            if (_scenario.TimeLimitSeconds.HasValue && !_ctx.Learn && _elapsed >= _scenario.TimeLimitSeconds.Value)
            {
                End(EndReason.TimedOut);
                return;
            }

            if (Time.time >= _nextFlush)
            {
                _nextFlush = Time.time + FlushIntervalSeconds;
                Flush();
            }
        }

        private float _nextSmokeHint;

        /// <summary>Guided mode: tell the learner to get low when the layer reaches their head, and lower when crouching is not enough.</summary>
        private void UpdateSmokeHint(in HazardSample sample, float headY)
        {
            if (!_ctx.Guided || Time.time < _nextSmokeHint || sample.Smoke < 0.08f)
                return;
            var clearance = sample.LayerBottomY - headY;
            if (clearance > 0.2f)
                return;
            _nextSmokeHint = Time.time + 12f;
            if (!_player.Crouched)
                _ctx.Hint("Khói đang hạ thấp. Hãy cúi người để đầu ở dưới lớp khói.");
            else if (!_player.Crawling)
                _ctx.Hint("Khói xuống sát đầu. Bấm Cúi thấp lần nữa để bò.");
        }

        private void EmitPeriodic()
        {
            var distance = _player.ConsumeDistance();
            if (distance > 0.05f)
                _log.Emit(EventTypes.Moved, new JObject { ["distanceMeters"] = Math.Round(distance, 2) });
            var exposure = _vitals.ConsumeExposure();
            if (exposure > 0.001f)
                _log.Emit(EventTypes.HazardExposure, new JObject { ["amount"] = Math.Round(exposure, 3) });
        }

        private void DrainSimulationEvents()
        {
            _simEvents.Clear();
            _world.Simulation.DrainEvents(_simEvents);
            foreach (var e in _simEvents)
            {
                var id = e.ObjectId.ToString();
                switch (e.Kind)
                {
                    case SimulationEventKind.SprinklerActivated:
                        _log.EmitObject(EventTypes.SprinklerActivated, id);
                        break;
                    case SimulationEventKind.DetectorTriggered:
                        _log.EmitObject(EventTypes.DetectorTriggered, id);
                        foreach (var it in _world.Interactables)
                            if (it != null && it.ObjectId == id)
                                GameAudio.Loop(GameAudio.Sounds?.DetectorBeep, it.gameObject, 0.6f, 14f)?.Play();
                        break;
                    case SimulationEventKind.AlarmRaised:
                        _ctx.Show("Chuông báo cháy đang kêu!", MessageTone.Warning);
                        break;
                    case SimulationEventKind.FireIgnited when !_ctx.Assessment:
                        _ctx.Hint("Có cháy! Báo động và tìm lối thoát an toàn.");
                        break;
                }
            }
        }

        private void OnExitEntered(ExitZone exit)
        {
            if (Phase != SessionPhase.Running)
                return;
            if (exit.Blocked)
            {
                _log.EmitObject(EventTypes.WrongExit, exit.ObjectId);
                _ctx.Show("Lối này bị chặn. Hãy tìm lối khác.", MessageTone.Warning);
                return;
            }

            _reachedExit = true;
            _log.EmitObject(EventTypes.ExitReached, exit.ObjectId);
            _ctx.Show("Bạn đã thoát ra an toàn", MessageTone.Success);
            End(EndReason.Finished);
        }

        public void End(string reason, bool collapsed = false)
        {
            if (Phase == SessionPhase.Ended)
                return;
            EmitPeriodic();
            Phase = SessionPhase.Ended;
            _player.InputEnabled = false;
            _log.Close();
            Flush(force: true);
            Completion = new SessionCompletion
            {
                LastEventSequence = _log.LastSequence,
                EndReason = reason,
                ElapsedMs = (long)(_elapsed * 1000),
                ReachedExit = _reachedExit,
                Collapsed = collapsed,
            };
            _bridge?.Send(BridgeMessages.Make(BridgeMessages.Completed, _launch.SessionId, JObject.FromObject(Completion)));
            _hud.ShowResult(ResultText(reason, collapsed));
            Ended?.Invoke(Completion);
        }

        private string ResultText(string reason, bool collapsed)
        {
            var t = TimeSpan.FromSeconds(_elapsed);
            var headline = collapsed ? "Bạn đã gục ngã trước khi thoát ra."
                : _reachedExit ? "Bạn đã thoát ra an toàn."
                : reason == EndReason.TimedOut ? "Hết thời gian."
                : "Bạn đã dừng bài tập.";
            var text = $"<b>{headline}</b>\n\nThời gian: {(int)t.TotalMinutes:00}:{t.Seconds:00}\nSự kiện đã ghi: {_log.All.Count}";
            if (_ctx.Learn)
                text += "\n\nChế độ làm quen không chấm điểm.";
            else
                text += "\n\nKết quả Đạt/Chưa đạt và phần phân tích sẽ có sau khi ứng dụng đồng bộ dữ liệu với máy chủ.";
            return text;
        }

        /// <summary>Sends unsent events to Mobile in batches of ≤ 500 (backend limit).</summary>
        public void Flush(bool force = false)
        {
            if (_log == null || (_log.UnsentCount == 0))
                return;
            while (_log.UnsentCount > 0)
            {
                var batch = _log.TakeBatch();
                _bridge?.Send(BridgeMessages.EventsBatch(_launch.SessionId, batch));
                if (!force)
                    break;
            }
        }

        /// <summary>Back on the result screen: same as its exit button. Ignored before the session has ended.</summary>
        public void Close()
        {
            if (Phase == SessionPhase.Ended)
                Exit();
        }

        private void Exit()
        {
            _bridge?.Send(BridgeMessages.Make(BridgeMessages.Exited, _launch.SessionId));
            Exited?.Invoke();
        }

        private void OnDestroy()
        {
            if (_ctx != null)
                GameContext.Clear(_ctx);
            _world?.Simulation?.Dispose();
        }
    }

    internal static class VitalsExtensions
    {
        public static bool Valid(this PlayerVitals v) => v != null;
    }
}
