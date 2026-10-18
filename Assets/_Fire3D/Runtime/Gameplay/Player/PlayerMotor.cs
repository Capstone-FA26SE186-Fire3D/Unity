using Fire3D.Unity.Gameplay.Cameras;
using Fire3D.Unity.Gameplay.Controls;
using UnityEngine;

namespace Fire3D.Unity.Gameplay.Player
{
    /// <summary>
    /// CharacterController movement relative to the camera: walk, run (stamina), crouch under smoke. Owns the
    /// player's movement; the simulation only receives the pose.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-50)]
    public sealed class PlayerMotor : MonoBehaviour
    {
        [SerializeField] private float walkSpeed = 1.6f;
        [SerializeField] private float runSpeed = 3.3f;
        [SerializeField] private float crouchSpeed = 0.85f;
        [SerializeField] private float crawlSpeed = 0.5f;
        [SerializeField] private float acceleration = 10f;
        [SerializeField] private float turnSpeedDegrees = 600f;
        [SerializeField] private float gravity = -20f;
        [SerializeField] private float standingHeight = 1.75f;
        [SerializeField] private float crouchHeight = 1.1f;
        [SerializeField] private float crawlHeight = 0.6f;
        [SerializeField] private ThirdPersonCameraRig cameraRig;
        [SerializeField] private Transform visual;

        private CharacterController _controller;
        private IGameplayInputSource _input;
        private PlayerVitals _vitals;
        private Vector3 _planarVelocity;
        private float _verticalVelocity;
        private float _distanceSinceReport;
        private float _crouchBlend;
        private Fire3D.Unity.Presentation.CharacterAnimator _character;
        private bool _characterChecked;

        public bool Crouched { get; private set; }

        /// <summary>Lowest stance: on hands and knees, head at ~0.5 m, under all but the thickest smoke layer.</summary>
        public bool Crawling { get; private set; }

        /// <summary>Eye/mouth height above the feet for the current stance (what the smoke layer is tested against).</summary>
        public float HeadHeight => Crawling ? 0.5f : Crouched ? 1.0f : 1.55f;
        public bool Running { get; private set; }
        public bool InputEnabled { get; set; } = true;

        /// <summary>Set while a hand gesture plays (touching or opening a door): the body stays put.</summary>
        public bool ActionLock { get; set; }
        public Vector3 PlanarVelocity => _planarVelocity;
        public float SpeedFactor { get; set; } = 1f;

        public IGameplayInputSource InputSource
        {
            get => _input;
            set => _input = value;
        }

        public ThirdPersonCameraRig CameraRig
        {
            get => cameraRig;
            set => cameraRig = value;
        }

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _vitals = GetComponent<PlayerVitals>();
            if (_input == null && TryGetComponent<IGameplayInputSource>(out var source))
                _input = source;
            ApplyHeight();
        }

        /// <summary>Moves the controller to a spawn pose without interpolation.</summary>
        public void Teleport(Vector3 position, float yawDegrees)
        {
            _controller.enabled = false;
            transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yawDegrees, 0f));
            _controller.enabled = true;
            _planarVelocity = Vector3.zero;
            _verticalVelocity = 0f;
            if (cameraRig != null)
                cameraRig.SnapBehind(yawDegrees);
        }

        /// <summary>Distance walked since the last call (meters), for the Moved telemetry event.</summary>
        public float ConsumeDistance()
        {
            var d = _distanceSinceReport;
            _distanceSinceReport = 0f;
            return d;
        }

        private void Update() => Step(Time.deltaTime);

        public void Step(float dt)
        {
            if (dt <= 0f)
                return;
            var frame = InputEnabled && !ActionLock ? _input?.Current ?? default : default;

            if (frame.CrouchToggled)
                CycleStance();

            var canRun = !Crouched && frame.Sprint && (_vitals == null || _vitals.Stamina > 0.05f);
            Running = canRun && frame.Move.sqrMagnitude > 0.25f;
            var maxSpeed = (Crawling ? crawlSpeed : Crouched ? crouchSpeed : Running ? runSpeed : walkSpeed) * SpeedFactor;

            var yaw = cameraRig != null ? cameraRig.Yaw : transform.eulerAngles.y;
            var desired = CameraRelativeMovement.PlanarDirection(frame.Move, yaw) * maxSpeed;
            _planarVelocity = Vector3.MoveTowards(_planarVelocity, desired, acceleration * dt);

            if (_controller.isGrounded && _verticalVelocity < 0f)
                _verticalVelocity = -2f;
            else
                _verticalVelocity += gravity * dt;

            var before = transform.position;
            _controller.Move((_planarVelocity + Vector3.up * _verticalVelocity) * dt);
            var moved = transform.position - before;
            moved.y = 0f;
            _distanceSinceReport += moved.magnitude;

            if (cameraRig != null && cameraRig.FirstPerson && !ActionLock)
            {
                // First person: the body faces where the camera looks, so interactions target what the player sees.
                transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.Euler(0f, cameraRig.Yaw, 0f), turnSpeedDegrees * 2f * dt);
            }
            else if (_planarVelocity.sqrMagnitude > 0.01f && !ActionLock)
            {
                var look = Quaternion.LookRotation(new Vector3(_planarVelocity.x, 0f, _planarVelocity.z));
                transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnSpeedDegrees * dt);
            }

            if (cameraRig != null)
                cameraRig.Crouched = Crouched;
            BlendCrouchPose(dt);
        }

        /// <summary>
        /// Crouch as a pose, not a squash: squashing the body vertically turned the stand-in into a flat dome with its arms on
        /// the floor, which read as "sinking into the ground". Lower the body by about a quarter, widen it a little and lean forward slightly, easing in and out.
        /// </summary>
        private void BlendCrouchPose(float dt)
        {
            if (visual == null)
                return;
            // A rigged character animates its own stances; the transform tricks below are for the capsule stand-in.
            if (_character == null && !_characterChecked)
            {
                _character = visual.GetComponentInChildren<Fire3D.Unity.Presentation.CharacterAnimator>();
                _characterChecked = true;
            }

            if (_character != null)
            {
                _character.Speed = _planarVelocity.magnitude;
                _character.Stance = Crawling ? 2 : Crouched ? 1 : 0;
                return;
            }

            // 0 = standing, 1 = crouched, 2 = crawling (flat, leaning well forward).
            var stance = Crawling ? 2f : Crouched ? 1f : 0f;
            _crouchBlend = Mathf.MoveTowards(_crouchBlend, stance, dt * 7f);
            var c = Mathf.Clamp01(_crouchBlend);
            var k = Mathf.Clamp01(_crouchBlend - 1f);
            var e = c * c * (3f - 2f * c);
            var f = k * k * (3f - 2f * k);
            visual.localScale = new Vector3(Mathf.Lerp(1f, 1.1f, e), Mathf.Lerp(Mathf.Lerp(1f, 0.72f, e), 0.42f, f), Mathf.Lerp(1f, 1.1f, e));
            visual.localRotation = Quaternion.Euler(Mathf.Lerp(Mathf.Lerp(0f, 14f, e), 62f, f), 0f, 0f);
        }

        public void SetCrouched(bool crouched)
        {
            if (crouched == Crouched && !Crawling)
                return;
            if (!crouched && !HeadroomFor(standingHeight))
                return; // No room to stand up.
            Crouched = crouched;
            Crawling = false;
            ApplyHeight();
        }

        /// <summary>Hands and knees: the lowest stance, slowest, with the head under most smoke layers.</summary>
        public void SetCrawling(bool crawling)
        {
            if (crawling == Crawling)
                return;
            if (!crawling && !HeadroomFor(crouchHeight))
                return;
            Crawling = crawling;
            Crouched = crawling || Crouched;
            ApplyHeight();
        }

        /// <summary>Stand → crouch → crawl → stand, as the one crouch button cycles.</summary>
        public void CycleStance()
        {
            if (Crawling)
                SetCrouched(false);
            else if (Crouched)
                SetCrawling(true);
            else
                SetCrouched(true);
        }

        private bool HeadroomFor(float height)
        {
            var current = _controller.height;
            return !Physics.SphereCast(transform.position + Vector3.up * current * 0.5f, _controller.radius * 0.9f,
                Vector3.up, out _, height - current * 0.5f, ~0, QueryTriggerInteraction.Ignore);
        }

        private void ApplyHeight()
        {
            if (_controller == null)
                return;
            var h = Crawling ? crawlHeight : Crouched ? crouchHeight : standingHeight;
            _controller.height = h;
            _controller.center = new Vector3(0f, h * 0.5f, 0f);
            // Visual pose is handled by BlendCrouchPose.
        }
    }
}
