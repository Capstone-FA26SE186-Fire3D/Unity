using Fire3D.Unity.Gameplay.Controls;
using UnityEngine;

namespace Fire3D.Unity.Gameplay.Cameras
{
    /// <summary>Pure distance logic so the collision behaviour can be unit tested.</summary>
    public static class CameraCollisionSolver
    {
        /// <summary>
        /// Pulls the camera in immediately when an obstacle is closer than the desired distance and eases it back
        /// out at <paramref name="recoverSpeed"/> once there is room again.
        /// </summary>
        public static float Resolve(float current, float desired, float? hitDistance, float padding, float minDistance,
            float recoverSpeed, float deltaTime)
        {
            var allowed = desired;
            if (hitDistance.HasValue)
                allowed = Mathf.Min(desired, Mathf.Max(minDistance, hitDistance.Value - padding));
            if (allowed < current)
                return allowed;
            return Mathf.Min(allowed, current + recoverSpeed * deltaTime);
        }
    }

    /// <summary>
    /// Third-person orbit camera that follows the player with limited pitch/zoom and sphere-cast collision against
    /// walls and ceilings (Docs technology 1.2). It never changes geometry, colliders or what the hazard model sees.
    /// </summary>
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(UnityEngine.Camera))]
    public sealed class ThirdPersonCameraRig : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private float standingPivotHeight = 1.55f;
        [SerializeField] private float crouchPivotHeight = 1.0f;
        [SerializeField] private float shoulderOffset = 0.25f;
        [SerializeField] private Vector2 pitchLimits = new(-25f, 70f);
        [SerializeField] private float initialPitch = 18f;
        [SerializeField] private Vector2 distanceLimits = new(1.4f, 5f);
        [SerializeField] private float initialDistance = 3.2f;
        [SerializeField] private float collisionRadius = 0.22f;
        [SerializeField] private float collisionPadding = 0.06f;
        [SerializeField] private float minCollisionDistance = 0.3f;
        [SerializeField] private float recoverSpeed = 2.5f;
        [SerializeField] private float followSharpness = 18f;
        [SerializeField] private float hideTargetBelowDistance = 0.55f;
        [SerializeField] private LayerMask obstacleMask = ~0;

        private readonly RaycastHit[] _hits = new RaycastHit[16];
        private Renderer[] _targetRenderers = System.Array.Empty<Renderer>();
        private IGameplayInputSource _input;
        private float _yaw;
        private float _pitch;
        private float _desiredDistance;
        private float _currentDistance;
        private float _pivotHeight;
        private Vector3 _smoothedPivot;
        private bool _targetHidden;
        private bool _snapped;

        public float Yaw => _yaw;
        public float Pitch => _pitch;
        public float CurrentDistance => _currentDistance;
        public float DesiredDistance => _desiredDistance;
        public bool Crouched { get; set; }

        public IGameplayInputSource InputSource
        {
            get => _input;
            set => _input = value;
        }

        public Transform Target
        {
            get => target;
            set
            {
                target = value;
                _targetRenderers = target != null ? target.GetComponentsInChildren<Renderer>() : System.Array.Empty<Renderer>();
                _snapped = false;
            }
        }

        private void Awake()
        {
            _pitch = initialPitch;
            _desiredDistance = Mathf.Clamp(initialDistance, distanceLimits.x, distanceLimits.y);
            _currentDistance = _desiredDistance;
            _pivotHeight = standingPivotHeight;
            var cam = GetComponent<UnityEngine.Camera>();
            cam.nearClipPlane = 0.05f;
            if (target != null)
                Target = target;
        }

        private float _shakeAmplitude;
        private float _shakeUntil;
        private float _shakeSeconds = 1f;

        /// <summary>Short positional shake (meters), e.g. touching a hot door. Decays linearly.</summary>
        public void Shake(float amplitude, float seconds)
        {
            _shakeAmplitude = Mathf.Max(_shakeAmplitude * Mathf.Clamp01((_shakeUntil - Time.time) / _shakeSeconds), amplitude);
            _shakeSeconds = Mathf.Max(0.05f, seconds);
            _shakeUntil = Time.time + _shakeSeconds;
        }

        /// <summary>Points the camera behind the target, e.g. after spawning.</summary>
        public void SnapBehind(float yawDegrees)
        {
            _yaw = yawDegrees;
            _snapped = false;
        }

        private void LateUpdate()
        {
            if (target == null)
                return;
            if (_input == null)
                _input = target.GetComponentInParent<IGameplayInputSource>();
            if (_input == null || (_input is Object o && o == null))
                _input = FindAnyObjectByType<GameplayInputReader>();
            Step(Time.deltaTime);
        }

        public void Step(float deltaTime)
        {
            var frame = _input?.Current ?? default;
            _yaw = Mathf.Repeat(_yaw + frame.LookDegrees.x, 360f);
            _pitch = Mathf.Clamp(_pitch + frame.LookDegrees.y, pitchLimits.x, pitchLimits.y);
            _desiredDistance = Mathf.Clamp(_desiredDistance + frame.ZoomMeters, distanceLimits.x, distanceLimits.y);

            _pivotHeight = Mathf.MoveTowards(_pivotHeight, Crouched ? crouchPivotHeight : standingPivotHeight, 3f * deltaTime);
            var rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            var targetPivot = target.position + Vector3.up * _pivotHeight + rotation * Vector3.right * shoulderOffset;
            if (!_snapped)
            {
                _smoothedPivot = targetPivot;
                _snapped = true;
            }
            else
            {
                _smoothedPivot = Vector3.Lerp(_smoothedPivot, targetPivot, 1f - Mathf.Exp(-followSharpness * deltaTime));
            }

            // Cast from the head (no shoulder offset) so the pivot itself never ends up inside a wall.
            var head = target.position + Vector3.up * _pivotHeight;
            var pivot = SafePivot(head, _smoothedPivot);
            var back = rotation * Vector3.back;
            var hit = CastObstacle(pivot, back, _desiredDistance + collisionPadding);
            _currentDistance = CameraCollisionSolver.Resolve(_currentDistance, _desiredDistance, hit, collisionPadding,
                minCollisionDistance, recoverSpeed, deltaTime);

            var position = pivot + back * _currentDistance;
            var shake = _shakeUntil > Time.time ? _shakeAmplitude * (_shakeUntil - Time.time) / _shakeSeconds : 0f;
            if (shake > 0f)
            {
                var t = Time.time * 28f;
                position += rotation * new Vector3(Mathf.PerlinNoise(t, 0.3f) - 0.5f, Mathf.PerlinNoise(0.7f, t) - 0.5f, 0f) * (2f * shake);
            }

            transform.SetPositionAndRotation(position, rotation);
            UpdateTargetVisibility();
        }

        private Vector3 SafePivot(Vector3 head, Vector3 desiredPivot)
        {
            var offset = desiredPivot - head;
            var length = offset.magnitude;
            if (length < 1e-4f)
                return desiredPivot;
            var hit = CastObstacle(head, offset / length, length + collisionPadding);
            return hit.HasValue ? head + offset / length * Mathf.Max(0f, hit.Value - collisionPadding) : desiredPivot;
        }

        private float? CastObstacle(Vector3 origin, Vector3 direction, float maxDistance)
        {
            var count = Physics.SphereCastNonAlloc(origin, collisionRadius, direction, _hits, maxDistance, obstacleMask,
                QueryTriggerInteraction.Ignore);
            float? nearest = null;
            for (var i = 0; i < count; i++)
            {
                var h = _hits[i];
                if (target != null && h.collider.transform.IsChildOf(target))
                    continue;
                if (h.collider is CharacterController)
                    continue; // People (NPC capsules) passing behind the player must not yank the camera in.
                var d = h.distance <= 0f && h.point == Vector3.zero ? 0f : h.distance;
                if (!nearest.HasValue || d < nearest.Value)
                    nearest = d;
            }

            return nearest;
        }

        private void UpdateTargetVisibility()
        {
            var hide = _currentDistance < hideTargetBelowDistance;
            if (hide == _targetHidden)
                return;
            _targetHidden = hide;
            foreach (var r in _targetRenderers)
            {
                if (r != null)
                    r.shadowCastingMode = hide ? UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly : UnityEngine.Rendering.ShadowCastingMode.On;
            }
        }
    }
}
