using Fire3D.Simulation;
using Fire3D.Unity.Content;
using UnityEngine;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// Power outage lighting (normal → emergency + EXIT signs, baked lighting scenario swap), alarm sound and
    /// sprinkler spray visuals. Reads simulation state only.
    /// </summary>
    public sealed class EnvironmentPresenter : MonoBehaviour
    {
        private FireSimulation _sim;
        private RuntimeAssets _assets;
        private AudioSource _alarm;
        private bool _outageApplied;
        private bool _alarmOn;
        private System.Collections.Generic.Dictionary<string, Vector3> _sprinklers;
        private readonly System.Collections.Generic.Dictionary<string, ParticleSystem> _sprays = new();

        public LightingScenarioSwitcher Lighting { get; set; }

        public void Bind(FireSimulation simulation, RuntimeAssets assets, System.Collections.Generic.Dictionary<string, Vector3> sprinklers)
        {
            _sim = simulation;
            _assets = assets;
            _sprinklers = sprinklers;
            SetGroup(LightGroup.Emergency, false);
            SetGroup(LightGroup.Normal, true);
            SetGroup(LightGroup.ExitSign, true);
        }

        private void Update()
        {
            if (_sim == null)
                return;

            if (!_outageApplied && _sim.Power.Outage)
            {
                _outageApplied = true;
                SetGroup(LightGroup.Normal, false);
                SetGroup(LightGroup.Emergency, true);
                RenderSettings.ambientIntensity = 0.25f;
                if (Lighting != null)
                    Lighting.Apply(LightingScenarioSwitcher.Outage);
            }

            if (!_alarmOn && _sim.Alarm.Raised)
            {
                _alarmOn = true;
                PlayAlarm();
            }

            if (_sprinklers == null)
                return;
            foreach (var kv in _sprinklers)
            {
                if (_sprays.ContainsKey(kv.Key) || !_sim.TryGetSprinkler(kv.Key, out var s) || !s.Activated || s.Mode != SprinklerMode.Active)
                    continue;
                var ps = _assets != null && _assets.SprinklerPrefab != null
                    ? Instantiate(_assets.SprinklerPrefab, kv.Value, Quaternion.identity, transform)
                    : CreateSpray(kv.Value);
                ps.Play(true);
                _sprays[kv.Key] = ps;
            }
        }

        private static void SetGroup(LightGroup group, bool on)
        {
            foreach (var m in FindObjectsByType<LightGroupMember>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (m.Group != group)
                    continue;
                if (m.TryGetComponent<Light>(out var l))
                    l.enabled = on;
                else
                    m.gameObject.SetActive(on);
            }
        }

        private void PlayAlarm()
        {
            _alarm = gameObject.AddComponent<AudioSource>();
            _alarm.clip = _assets != null && _assets.AlarmClip != null ? _assets.AlarmClip : AlarmTone();
            _alarm.loop = true;
            _alarm.spatialBlend = 0f;
            _alarm.volume = 0.35f;
            _alarm.Play();
        }

        /// <summary>Two-tone fire alarm generated in code so no audio asset is required.</summary>
        private static AudioClip AlarmTone()
        {
            const int rate = 22050;
            var samples = new float[rate];
            for (var i = 0; i < samples.Length; i++)
            {
                var t = i / (float)rate;
                var freq = t < 0.5f ? 880f : 660f;
                var env = Mathf.Clamp01(Mathf.Min(t % 0.5f, 0.5f - t % 0.5f) * 40f);
                samples[i] = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * freq * t)) * 0.25f * env;
            }

            var clip = AudioClip.Create("FireAlarm", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private ParticleSystem CreateSpray(Vector3 at)
        {
            var go = new GameObject("SprinklerSpray");
            go.transform.SetParent(transform, false);
            go.transform.position = at;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.startLifetime = 0.9f;
            main.startSpeed = 3.5f;
            main.startSize = 0.04f;
            main.startColor = new Color(0.75f, 0.85f, 1f, 0.6f);
            main.gravityModifier = 1f;
            main.maxParticles = 600;
            var emission = ps.emission;
            emission.rateOverTime = 400f;
            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 65f;
            shape.rotation = new Vector3(90f, 0f, 0f);
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = VfxFactory.ParticleMaterial(false);
            return ps;
        }
    }
}
