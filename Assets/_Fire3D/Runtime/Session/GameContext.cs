using System;
using Fire3D.Contracts;
using Fire3D.Simulation;
using Fire3D.Unity.Gameplay.Player;
using Fire3D.Unity.Session;
using UnityEngine;

namespace Fire3D.Unity
{
    /// <summary>
    /// Services shared by gameplay components during one session: simulation, telemetry, mode and player.
    /// Created by the session controller; cleared when the session is torn down.
    /// </summary>
    public sealed class GameContext
    {
        public static GameContext Current { get; private set; }

        public FireSimulation Simulation { get; }
        public SessionEventLog Log { get; }
        public string Mode { get; }
        public ScenarioState Scenario { get; }
        public PlayerMotor Player { get; set; }
        public PlayerVitals Vitals { get; set; }

        /// <summary>Shared art (VFX prefabs, sounds); null in tests that build no world.</summary>
        public Content.RuntimeAssets Assets { get; set; }
        public Func<double> Elapsed { get; }

        /// <summary>Short on-screen message (HUD toast).</summary>
        public event Action<string, MessageTone> Message;

        public GameContext(FireSimulation simulation, SessionEventLog log, string mode, ScenarioState scenario, Func<double> elapsed)
        {
            Simulation = simulation;
            Log = log;
            Mode = mode;
            Scenario = scenario;
            Elapsed = elapsed;
        }

        public bool Guided => Mode == TrainingMode.Guided;
        public bool Assessment => Mode == TrainingMode.Assessment;
        public bool Learn => Mode == TrainingMode.Learn;
        public float Time => Simulation?.Time ?? 0f;

        public void Show(string text, MessageTone tone = MessageTone.Info) => Message?.Invoke(text, tone);

        /// <summary>Guided-only hint; Assessment never shows hints (Docs technology 1.2).</summary>
        public void Hint(string text)
        {
            if (Guided)
                Message?.Invoke(text, MessageTone.Hint);
        }

        public static void Install(GameContext context) => Current = context;

        public static void Clear(GameContext context)
        {
            if (Current == context)
                Current = null;
        }
    }

    public enum MessageTone
    {
        Info,
        Warning,
        Danger,
        Hint,
        Success,
    }
}
