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

        private readonly Fire3DBridge _bridge = new();
        private LoadedContent _content;
        private TrainingSession _session;
        private GameObject _player;
        private bool _busy;

        public RuntimeAssets Assets
        {
            get => assets;
            set => assets = value;
        }

        public IFire3DBridge Bridge => _bridge;

        private void Awake()
        {
            gameObject.name = ReceiverName;
            DontDestroyOnLoad(gameObject);
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        private void Start() => _bridge.Send(BridgeMessages.Make(BridgeMessages.Ready, null, new Newtonsoft.Json.Linq.JObject
        {
            ["runtimeVersion"] = RuntimeCatalog.RuntimeVersion,
            ["protocolVersion"] = LaunchRequest.CurrentProtocol,
        }));

        /// <summary>Called by Mobile through UnitySendMessage(ReceiverName, "Launch", json).</summary>
        public void Launch(string json)
        {
            if (_busy)
            {
                _bridge.Send(BridgeMessages.Failure(null, "SESSION_BUSY", "A session is already running."));
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

            StartCoroutine(Run(launch));
        }

        /// <summary>Mobile asks to stop (user left, app closing). The session ends as Abandoned.</summary>
        public void Abort(string _)
        {
            if (_session != null)
                _session.End(EndReason.Abandoned);
        }

        public void Pause(string _) => _session?.SetPaused(true);

        private IEnumerator Run(LaunchRequest launch)
        {
            _busy = true;
            string errorCode = null, errorMessage = null;
            yield return ContentLoader.Load(launch.ManifestPath, c => _content = c, (code, msg) =>
            {
                errorCode = code;
                errorMessage = msg;
            });
            if (errorCode != null)
            {
                _bridge.Send(BridgeMessages.Failure(launch.SessionId, errorCode, errorMessage));
                _busy = false;
                yield break;
            }

            if (_content.Scenario.ModePolicy?.AllowedModes is { Count: > 0 } allowed && !allowed.Contains(launch.Mode))
            {
                _bridge.Send(BridgeMessages.Failure(launch.SessionId, "MODE_NOT_ALLOWED", launch.Mode));
                yield return ContentLoader.Unload(_content);
                _busy = false;
                yield break;
            }

            StartSession(launch, _content.Scenario, _content.Building);
        }

        /// <summary>Starts a session on geometry that is already loaded (package scenes or a dev scene).</summary>
        public TrainingSession StartSession(LaunchRequest launch, ScenarioState scenario, BuildingMetadata building, bool autoStart = false)
        {
            _busy = true;
            BuiltWorld world;
            try
            {
                world = new WorldBuilder(assets, scenario, building, launch.Mode).Build();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                _bridge.Send(BridgeMessages.Failure(launch.SessionId, "WORLD_BUILD_FAILED", ex.Message));
                _busy = false;
                return null;
            }

            _player = PlayerRigFactory.Create(assets);
            _session = new GameObject("TrainingSession").AddComponent<TrainingSession>();
            _session.Exited += () => StartCoroutine(Teardown(world));
            _session.Begin(launch, scenario, world, _bridge, assets, _player, autoStart);
            return _session;
        }

        private IEnumerator Teardown(BuiltWorld world)
        {
            if (_session != null)
                Destroy(_session.gameObject);
            if (_player != null)
                Destroy(_player);
            if (world?.Root != null)
                Destroy(world.Root.gameObject);
            yield return ContentLoader.Unload(_content);
            _content = null;
            _busy = false;
        }
    }
}
