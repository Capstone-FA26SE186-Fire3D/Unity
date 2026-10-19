using System;
using System.Collections;
using Fire3D.Contracts;
using Fire3D.Unity.Content;
using Fire3D.Unity.Gameplay.Player;
using Fire3D.Unity.Integration;
using Fire3D.Unity.Session;
using UnityEngine;

namespace Fire3D.Unity.Bootstrap
{
    /// <summary>
    /// App entry in the Bootstrap scene. Receives the launch from Mobile (UnitySendMessage on the GameObject named
    /// <see cref="ReceiverName"/>), loads the verified package, builds the world and runs one session at a time.
    /// Unity never starts a session by itself: Mobile does the online start and only then sends Launch.
    /// </summary>
    public sealed class Fire3DApp : MonoBehaviour
    {
        public const string ReceiverName = "Fire3DBridge";

        [SerializeField] private RuntimeAssets assets;

        /// <summary>Launch lifecycle. One launch at a time; Ready is announced only when back to Idle.</summary>
        public enum AppState
        {
            Idle,
            Loading,
            Running,
            Unloading,
        }

        private readonly Fire3DBridge _bridge = new();
        private LoadedContent _content;
        private TrainingSession _session;
        private GameObject _player;
        private AppState _state = AppState.Idle;
        private bool _cancelRequested;

        public RuntimeAssets Assets
        {
            get => assets;
            set => assets = value;
        }

        public IFire3DBridge Bridge => _bridge;

        public AppState State => _state;

        /// <summary>Runs right after the world is built, before the player and session exist. Tests throw here to exercise WORLD_BUILD_FAILED.</summary>
        public Action<BuiltWorld> WorldBuilt;

        private void Awake()
        {
            gameObject.name = ReceiverName;
            DontDestroyOnLoad(gameObject);
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        private void Start() => SendReady();

        /// <summary>
        /// Ready is sent at start and again whenever the runtime is back to idle after a session, so a host that keeps the
        /// Unity player alive between sessions can deliver the next Launch without restarting it.
        /// </summary>
        private void SendReady() => _bridge.Send(BridgeMessages.Make(BridgeMessages.Ready, null, new Newtonsoft.Json.Linq.JObject
        {
            ["runtimeVersion"] = RuntimeCatalog.RuntimeVersion,
            ["protocolVersion"] = LaunchRequest.CurrentProtocol,
        }));

        /// <summary>Called by Mobile through UnitySendMessage(ReceiverName, "Launch", json).</summary>
        public void Launch(string json)
        {
            if (_state != AppState.Idle)
            {
                _bridge.Send(BridgeMessages.Failure(null, "SESSION_BUSY", $"The runtime is {_state}; wait for Ready."));
                return;
            }

            LaunchRequest launch;
            try
            {
                launch = ContractJson.ReadLaunch(json);
            }
            catch (ContractException ex)
            {
                _bridge.Send(BridgeMessages.Failure(null, ex.Code, ex.Message));
                return;
            }

            _state = AppState.Loading;
            _cancelRequested = false;
            StartCoroutine(Run(launch));
        }

        /// <summary>
        /// Mobile asks to stop (Back, user left, app closing). While the package is loading the launch is cancelled
        /// (LAUNCH_CANCELLED, no Launched); once the session runs it ends as Abandoned (Completed, result screen); Back on
        /// the result screen closes it like its exit button (Exited, then Ready).
        /// </summary>
        public void Abort(string _)
        {
            switch (_state)
            {
                case AppState.Loading:
                    _cancelRequested = true;
                    break;
                case AppState.Running when _session != null && _session.Phase == SessionPhase.Ended:
                    _session.Close();
                    break;
                case AppState.Running when _session != null:
                    _session.End(EndReason.Abandoned);
                    break;
            }
        }

        public void Pause(string _) => _session?.SetPaused(true);

        /// <summary>Mobile returns to the Unity activity (app foregrounded).</summary>
        public void Resume(string _) => _session?.SetPaused(false);

        private IEnumerator Run(LaunchRequest launch)
        {
            var content = new LoadedContent();
            _content = content;
            string errorCode = null, errorMessage = null;
            yield return ContentLoader.Load(launch.ManifestPath, content, (code, msg) =>
            {
                errorCode = code;
                errorMessage = msg;
            }, () => _cancelRequested);

            // Abort can arrive in the same frame the last load step finished.
            if (errorCode == null && _cancelRequested)
            {
                errorCode = ContentLoader.Cancelled;
                errorMessage = "Launch cancelled after the package loaded.";
            }
            else if (errorCode == null && content.Scenario.ModePolicy?.AllowedModes is { Count: > 0 } allowed && !allowed.Contains(launch.Mode))
            {
                errorCode = "MODE_NOT_ALLOWED";
                errorMessage = launch.Mode;
            }

            if (errorCode != null)
            {
                yield return FailLaunch(launch, errorCode, errorMessage);
                yield break;
            }

            if (StartSession(launch, content.Scenario, content.Building) == null)
            {
                // StartSession already reported WORLD_BUILD_FAILED and removed the half-built world.
                _state = AppState.Unloading;
                yield return ReleaseContent();
                _state = AppState.Idle;
                SendReady();
            }
        }

        /// <summary>Releases everything the failed launch loaded, then reports the error and Ready so Mobile can retry.</summary>
        private IEnumerator FailLaunch(LaunchRequest launch, string code, string message)
        {
            _state = AppState.Unloading;
            yield return ReleaseContent();
            _bridge.Send(BridgeMessages.Failure(launch.SessionId, code, message));
            _state = AppState.Idle;
            _cancelRequested = false;
            SendReady();
        }

        private IEnumerator ReleaseContent()
        {
            var content = _content;
            _content = null;
            yield return ContentLoader.Unload(content);
        }

        /// <summary>Starts a session on geometry that is already loaded (package scenes or a dev scene).</summary>
        public TrainingSession StartSession(LaunchRequest launch, ScenarioState scenario, BuildingMetadata building, bool autoStart = false)
        {
            BuiltWorld world = null;
            try
            {
                world = new WorldBuilder(assets, scenario, building, launch.Mode).Build();
                WorldBuilt?.Invoke(world);
                _player = PlayerRigFactory.Create(assets);
                _session = new GameObject("TrainingSession").AddComponent<TrainingSession>();
                var session = _session;
                _session.Exited += () => StartCoroutine(Teardown(world));
                _state = AppState.Running;
                _session.Begin(launch, scenario, world, _bridge, assets, _player, autoStart);
                return session;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                // Begin may have thrown before the session took ownership of the simulation.
                DestroySessionObjects(world);
                world?.Simulation?.Dispose();
                _state = AppState.Idle;
                _bridge.Send(BridgeMessages.Failure(launch.SessionId, "WORLD_BUILD_FAILED", ex.Message));
                return null;
            }
        }

        private void DestroySessionObjects(BuiltWorld world)
        {
            if (_session != null)
                Destroy(_session.gameObject);
            if (_player != null)
                Destroy(_player);
            if (world?.Root != null)
                Destroy(world.Root.gameObject);
            // The session disposes the simulation in OnDestroy (end of frame), after its last Update.
            _session = null;
            _player = null;
        }

        private IEnumerator Teardown(BuiltWorld world)
        {
            _state = AppState.Unloading;
            DestroySessionObjects(world);
            yield return ReleaseContent();
            _state = AppState.Idle;
            _cancelRequested = false;
            SendReady();
        }
    }
}
