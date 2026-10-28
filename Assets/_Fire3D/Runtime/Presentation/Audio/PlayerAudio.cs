using Fire3D.Unity.Gameplay.Player;
using UnityEngine;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// The player's own sounds: footsteps paced by speed and stance (quieter crouched, soft shuffles crawling) and a
    /// cough while breathing smoke without a covered nose.
    /// </summary>
    public sealed class PlayerAudio : MonoBehaviour
    {
        [SerializeField] private float strideMeters = 0.75f;
        [SerializeField] private float coughSmoke = 0.25f;
        [SerializeField] private float coughEverySeconds = 4.5f;

        private PlayerMotor _motor;
        private PlayerVitals _vitals;
        private float _travelled;
        private float _nextCough;
        private int _lastStep = -1;
        private AudioSource _breath;

        private void Awake()
        {
            _motor = GetComponent<PlayerMotor>();
            _vitals = GetComponent<PlayerVitals>();
        }

        private void Update()
        {
            var sounds = GameAudio.Sounds;
            if (sounds == null || _motor == null || !_motor.InputEnabled)
                return;

            var speed = _motor.PlanarVelocity.magnitude;
            var steps = _motor.Crawling && sounds.Crawl.Count > 0 ? sounds.Crawl : sounds.Footsteps;
            if (speed > 0.2f && steps.Count > 0)
            {
                var stride = _motor.Crawling ? strideMeters * 0.6f : _motor.Crouched ? strideMeters * 0.8f : _motor.Running ? strideMeters * 1.3f : strideMeters;
                _travelled += speed * Time.deltaTime;
                if (_travelled >= stride)
                {
                    _travelled = 0f;
                    var volume = _motor.Crawling ? 0.35f : _motor.Crouched ? 0.3f : _motor.Running ? 0.6f : 0.45f;
                    // Never the same sample twice in a row.
                    var pick = Random.Range(0, steps.Count);
                    if (pick == _lastStep && steps.Count > 1)
                        pick = (pick + 1) % steps.Count;
                    _lastStep = pick;
                    GameAudio.PlayAt(steps[pick], transform.position, volume, 0f, 10f);
                }
            }

            Breathe(sounds);

            if (_vitals != null && !_vitals.NoseCovered && _vitals.LocalSmoke > coughSmoke && Time.time >= _nextCough)
            {
                _nextCough = Time.time + coughEverySeconds * Random.Range(0.8f, 1.3f);
                GameAudio.PlayAt(sounds.Cough, transform.position + Vector3.up * _motor.HeadHeight, 0.7f, 0f, 8f);
            }
        }

        /// <summary>Heavy breathing fades in as stamina runs low and out as it recovers.</summary>
        private void Breathe(Content.RuntimeAssets.SoundSet sounds)
        {
            if (_vitals == null)
                return;
            if (_breath == null)
            {
                _breath = GameAudio.Loop(Content.RuntimeAssets.SoundSet.Pick(sounds.Panting, null), gameObject, 0f, 6f);
                if (_breath == null)
                    return;
                _breath.Play();
            }

            var target = Mathf.Clamp01((0.4f - _vitals.Stamina) / 0.3f) * 0.6f;
            _breath.volume = Mathf.MoveTowards(_breath.volume, target, Time.deltaTime * 0.5f);
        }
    }
}
