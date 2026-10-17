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
        [SerializeField] private float acceleration = 10f;
        [SerializeField] private float turnSpeedDegrees = 600f;
        [SerializeField] private float gravity = -20f;
        [SerializeField] private float standingHeight = 1.75f;
        [SerializeField] private float crouchHeight = 1.1f;
        [SerializeField] private ThirdPersonCameraRig cameraRig;
        [SerializeField] private Transform visual;

        private CharacterController _controller;
        private IGameplayInputSource _input;
        private PlayerVitals _vitals;
        private Vector3 _planarVelocity;
        private float _verticalVelocity;
        private float _distanceSinceReport;

        public bool Crouched { get; private set; }
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
                SetCrouched(!Crouched);

            var canRun = !Crouched && frame.Sprint && (_vitals == null || _vitals.Stamina > 0.05f);
            Running = canRun && frame.Move.sqrMagnitude > 0.25f;
            var maxSpeed = (Crouched ? crouchSpeed : Running ? runSpeed : walkSpeed) * SpeedFactor;

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

            if (_planarVelocity.sqrMagnitude > 0.01f && !ActionLock)
            {
                var look = Quaternion.LookRotation(new Vector3(_planarVelocity.x, 0f, _planarVelocity.z));
                transform.rotation = Quaternion.RotateTowards(transform.rotation, look, turnSpeedDegrees * dt);
            }

            if (cameraRig != null)
                cameraRig.Crouched = Crouched;
        }

        public void SetCrouched(bool crouched)
        {
            if (crouched == Crouched)
                return;
            if (!crouched && Physics.SphereCast(transform.position + Vector3.up * crouchHeight * 0.5f, _controller.radius * 0.9f,
                    Vector3.up, out _, standingHeight - crouchHeight * 0.5f, ~0, QueryTriggerInteraction.Ignore))
                return; // No room to stand up.
            Crouched = crouched;
            ApplyHeight();
        }

        private void ApplyHeight()
        {
            if (_controller == null)
                return;
            var h = Crouched ? crouchHeight : standingHeight;
            _controller.height = h;
            _controller.center = new Vector3(0f, h * 0.5f, 0f);
            if (visual != null)
                visual.localScale = new Vector3(1f, h / standingHeight, 1f);
        }
    }
}
