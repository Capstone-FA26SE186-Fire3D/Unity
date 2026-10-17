using System;
using Fire3D.Contracts;
using UnityEngine;

namespace Fire3D.Unity.Bootstrap
{
    /// <summary>
    /// Development scenes only: starts a local session on the geometry already in the scene with a fixture
    /// scenario, without Mobile or a backend. Not part of production launches.
    /// </summary>
    [RequireComponent(typeof(Fire3DApp))]
    public sealed class DevLauncher : MonoBehaviour
    {
        [SerializeField] private TextAsset scenario;
        [SerializeField] private TextAsset building;
        [SerializeField] private string mode = TrainingMode.Guided;
        [SerializeField] private bool launchOnStart = true;

        public TextAsset Scenario { get => scenario; set => scenario = value; }
        public TextAsset Building { get => building; set => building = value; }
        public string Mode { get => mode; set => mode = value; }

        private void Start()
        {
            if (!launchOnStart)
                return;
            var session = Launch();
            // Development builds: `-fet3dAutoplay` plays the scenario with the bot and writes a report (see AutoPilot).
            if (session != null && Development.AutoPilot.RequestedFromCommandLine())
                Development.AutoPilot.Attach(session, Development.AutoPilot.DefaultOutputDirectory(), quitWhenDone: !Application.isEditor);
        }

        public Session.TrainingSession Launch()
        {
            var app = GetComponent<Fire3DApp>();
            var launch = new LaunchRequest
            {
                ProtocolVersion = LaunchRequest.CurrentProtocol,
                SessionId = Guid.NewGuid().ToString("D"),
                Mode = mode,
                RuntimeVersion = RuntimeCatalog.RuntimeVersion,
                ManifestPath = "dev",
            };
            var s = ContractJson.ReadScenario(scenario.text);
            var b = building != null ? ContractJson.ReadBuilding(building.text) : null;
            return app.StartSession(launch, s, b);
        }
    }
}
