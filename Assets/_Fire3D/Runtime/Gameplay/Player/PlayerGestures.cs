using System;
using System.Collections;
using UnityEngine;

namespace Fire3D.Unity.Gameplay.Player
{
    /// <summary>
    /// Procedural hand gestures for the stand-in body (shoulder pivots "ArmR"/"ArmL", arm along -Y): reach a world
    /// point, hold, then return, plus a walk swing. A rigged character (Mixamo) can replace this with IK later;
    /// callers only rely on <see cref="Reach"/> timing and the contact callback.
    /// </summary>
    public sealed class PlayerGestures : MonoBehaviour
    {
        [SerializeField] private float swingDegrees = 22f;

        private Transform _armR, _armL;
        private Quaternion _restR, _restL;
        private PlayerMotor _motor;
        private float _gesture; // 0 = free (walk swing), 1 = arm driven by a gesture.
        private Quaternion _gestureRotation;
        private float _phase;

        public bool Busy { get; private set; }

        private void Start()
        {
            _motor = GetComponent<PlayerMotor>();
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "ArmR")
                    _armR = t;
                else if (t.name == "ArmL")
                    _armL = t;
            }

            if (_armR != null)
                _restR = _armR.localRotation;
            if (_armL != null)
                _restL = _armL.localRotation;
        }

        /// <summary>
        /// Reaches <paramref name="worldPoint"/> with the right hand in <paramref name="reachSeconds"/>, calls
        /// <paramref name="atContact"/>, holds for <paramref name="holdSeconds"/> and returns. A true
        /// <paramref name="flinch"/> (checked at contact) snaps the hand back fast, as from a hot surface.
        /// </summary>
        public IEnumerator Reach(Vector3 worldPoint, float reachSeconds, float holdSeconds, Action atContact, Func<bool> flinch = null)
        {
            Busy = true;
            try
            {
                if (_armR == null)
                {
                    yield return new WaitForSeconds(reachSeconds);
                    atContact?.Invoke();
                    yield return new WaitForSeconds(holdSeconds);
                    yield break;
                }

                var start = _armR.localRotation;
                for (var t = 0f; t < 1f; t += Time.deltaTime / Mathf.Max(0.05f, reachSeconds))
                {
                    _gestureRotation = Quaternion.Slerp(start, Aim(worldPoint), Smooth(t));
                    _gesture = 1f;
                    yield return null;
                }

                _gestureRotation = Aim(worldPoint);
                atContact?.Invoke();
                var snap = flinch != null && flinch();
                var hold = snap ? 0.12f : holdSeconds;
                for (var t = 0f; t < hold; t += Time.deltaTime)
                {
                    // Palm pressed on the surface follows the leaf if the door moves.
                    _gestureRotation = Aim(worldPoint);
                    yield return null;
                }

                var from = _gestureRotation;
                var back = snap ? 0.12f : 0.3f;
                for (var t = 0f; t < 1f; t += Time.deltaTime / back)
                {
                    _gestureRotation = Quaternion.Slerp(from, _restR, Smooth(t));
                    yield return null;
                }
            }
            finally
            {
                _gesture = 0f;
                Busy = false;
            }
        }

        private Quaternion Aim(Vector3 worldPoint)
        {
            var parent = _armR.parent;
            var dir = parent.InverseTransformDirection(worldPoint - _armR.position);
            if (dir.sqrMagnitude < 1e-6f)
                return _restR;
            return Quaternion.FromToRotation(Vector3.down, dir.normalized);
        }

        private static float Smooth(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * (3f - 2f * t);
        }

        private void LateUpdate()
        {
            if (_armR == null)
                return;
            var speed = _motor != null ? _motor.PlanarVelocity.magnitude : 0f;
            _phase += Time.deltaTime * (2.2f + speed * 2.4f);
            var amount = Mathf.Clamp01(speed / 1.6f) * swingDegrees;
            var swing = Mathf.Sin(_phase) * amount;
            var freeR = _restR * Quaternion.Euler(-swing, 0f, 0f);
            _armR.localRotation = _gesture > 0f ? _gestureRotation : freeR;
            if (_armL != null)
                _armL.localRotation = _restL * Quaternion.Euler(swing, 0f, 0f);
        }
    }
}
