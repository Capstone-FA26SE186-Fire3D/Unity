using Fire3D.Simulation;
using UnityEngine;

namespace Fire3D.Unity.Gameplay.Player
{
    /// <summary>
    /// Training-only vitals (Docs HUD section 8): health, smoke inhaled, heat and stamina. These are simulation
    /// values for the exercise, not medical measures.
    /// </summary>
    public sealed class PlayerVitals : MonoBehaviour
    {
        [SerializeField] private float ambientC = 26f;
        [SerializeField] private float staminaDrainPerSecond = 0.12f;
        [SerializeField] private float staminaRecoverPerSecond = 0.08f;
        [SerializeField] private float crouchSmokeFactor = 0.45f;

        public float Health { get; private set; } = 1f;
        public float SmokeInhaled { get; private set; }
        public float Stamina { get; private set; } = 1f;
        public float HeatC { get; private set; }
        public float LocalSmoke { get; private set; }
        public float Visibility { get; private set; } = 1f;
        public bool Collapsed => Health <= 0f;

        /// <summary>Multiplier applied while the nose is covered by a wet towel; comes from approved scenario content.</summary>
        public float CoveredSmokeMultiplier { get; set; } = 1f;
        public bool NoseCovered { get; set; }

        /// <summary>Exposure accumulated since the last HazardExposure event.</summary>
        public float PendingExposure { get; private set; }

        public void ResetVitals()
        {
            Health = 1f;
            SmokeInhaled = 0f;
            Stamina = 1f;
            PendingExposure = 0f;
        }

        public float ConsumeExposure()
        {
            var e = PendingExposure;
            PendingExposure = 0f;
            return e;
        }

        /// <summary>Applies one frame of exposure from the hazard sample at head height.</summary>
        public void Apply(in HazardSample sample, bool crouched, bool running, float dt)
        {
            if (Collapsed || dt <= 0f)
                return;

            HeatC = sample.Valid ? sample.HeatC : ambientC;
            LocalSmoke = sample.Valid ? sample.Smoke : 0f;
            Visibility = sample.Valid ? sample.Visibility : 1f;

            var smoke = LocalSmoke * (crouched ? crouchSmokeFactor : 1f);
            var intake = smoke * (NoseCovered ? CoveredSmokeMultiplier : 1f) * (running ? 1.6f : 1f);
            SmokeInhaled += intake * dt;

            var heatStress = Mathf.Max(0f, HeatC - ambientC - 30f) / 300f;
            var fire = sample.Valid ? sample.Fire : 0f;
            var damage = intake * 0.018f + heatStress * 0.22f + fire * 0.6f;
            // Long smoke exposure gets worse over time (carbon monoxide build-up in the game model).
            damage += Mathf.Max(0f, SmokeInhaled - 20f) * 0.0004f;
            Health = Mathf.Max(0f, Health - damage * dt);
            PendingExposure += (intake + heatStress) * dt;

            Stamina = running
                ? Mathf.Max(0f, Stamina - staminaDrainPerSecond * dt)
                : Mathf.Min(1f, Stamina + staminaRecoverPerSecond * (1f - smoke) * dt);
        }
    }
}
