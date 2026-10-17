using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using ETouch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace Fire3D.Unity.Gameplay.Controls
{
    /// <summary>
    /// Reads touch (floating joystick on the left, drag-to-look and pinch-zoom on the right), keyboard/mouse and
    /// gamepad into one <see cref="GameplayInputFrame"/>. Touches that start on UI buttons are left to the UI.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public sealed class GameplayInputReader : MonoBehaviour, IGameplayInputSource
    {
        [SerializeField, Range(0.2f, 0.7f)] private float moveZoneWidth = 0.45f;
        [SerializeField] private float joystickRadiusDp = 64f;
        [SerializeField, Range(0f, 0.5f)] private float joystickDeadZone = 0.12f;
        [SerializeField] private float touchLookDegreesPerDp = 0.28f;
        [SerializeField] private float mouseLookDegreesPerPixel = 0.16f;
        [SerializeField] private float gamepadLookDegreesPerSecond = 170f;
        [SerializeField] private float scrollZoomMetersPerNotch = 0.5f;
        [SerializeField] private float pinchZoomMetersPerDp = 0.02f;
        [SerializeField, Range(0.5f, 1f)] private float sprintStickThreshold = 0.92f;

        private InputAction _move;
        private InputAction _pointerLook;
        private InputAction _lookHold;
        private InputAction _gamepadLook;
        private InputAction _zoom;
        private InputAction _sprint;
        private InputAction _crouch;
        private InputAction _pause;

        private int _moveFinger = -1;
        private Vector2 _joystickOrigin;
        private Vector2 _joystickCurrent;
        private float _lastPinch = -1f;
        private GameplayInputFrame _current;

        public GameplayInputFrame Current => _current;
        public bool JoystickActive => _moveFinger >= 0;
        public Vector2 JoystickOrigin => _joystickOrigin;
        public Vector2 JoystickCurrent => _joystickCurrent;
        public float JoystickRadiusPixels => joystickRadiusDp * DpScale;

        /// <summary>Screen rectangles (pixels, origin bottom-left) where touches belong to UI buttons.</summary>
        public System.Func<Vector2, bool> IsOverUi { get; set; }

        private static float DpScale => Screen.dpi > 0f ? Screen.dpi / 160f : 1f;

        private void Awake()
        {
            _move = new InputAction("Move", InputActionType.Value, expectedControlType: "Vector2");
            _move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            _move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/upArrow").With("Down", "<Keyboard>/downArrow").With("Left", "<Keyboard>/leftArrow").With("Right", "<Keyboard>/rightArrow");
            _move.AddBinding("<Gamepad>/leftStick");
            _pointerLook = new InputAction("PointerLook", InputActionType.Value, "<Mouse>/delta", expectedControlType: "Vector2");
            _lookHold = new InputAction("LookHold", InputActionType.Button, "<Mouse>/rightButton");
            _gamepadLook = new InputAction("GamepadLook", InputActionType.Value, "<Gamepad>/rightStick", expectedControlType: "Vector2");
            _zoom = new InputAction("Zoom", InputActionType.Value, "<Mouse>/scroll/y", expectedControlType: "Axis");
            _sprint = new InputAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
            _sprint.AddBinding("<Gamepad>/leftStickPress");
            _crouch = new InputAction("Crouch", InputActionType.Button, "<Keyboard>/c");
            _crouch.AddBinding("<Gamepad>/buttonEast");
            _pause = new InputAction("Pause", InputActionType.Button, "<Keyboard>/escape");
            _pause.AddBinding("<Gamepad>/start");
        }

        private void OnEnable()
        {
            EnhancedTouchSupport.Enable();
            foreach (var a in Actions())
                a.Enable();
        }

        private void OnDisable()
        {
            foreach (var a in Actions())
                a.Disable();
            EnhancedTouchSupport.Disable();
            ResetTouch();
        }

        private void OnDestroy()
        {
            foreach (var a in Actions())
                a.Dispose();
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
                ResetTouch();
        }

        private InputAction[] Actions() => new[] { _move, _pointerLook, _lookHold, _gamepadLook, _zoom, _sprint, _crouch, _pause };

        private void ResetTouch()
        {
            _moveFinger = -1;
            _lastPinch = -1f;
            _current = default;
        }

        private void Update()
        {
            var frame = new GameplayInputFrame();
            ReadTouch(ref frame);

            var keyMove = _move.ReadValue<Vector2>();
            if (keyMove.sqrMagnitude > frame.Move.sqrMagnitude)
                frame.Move = keyMove;
            frame.Move = Vector2.ClampMagnitude(frame.Move, 1f);

            if (_lookHold.IsPressed())
            {
                var d = _pointerLook.ReadValue<Vector2>();
                frame.LookDegrees += new Vector2(d.x, -d.y) * mouseLookDegreesPerPixel;
            }

            var pad = _gamepadLook.ReadValue<Vector2>();
            frame.LookDegrees += new Vector2(pad.x, -pad.y) * (gamepadLookDegreesPerSecond * Time.unscaledDeltaTime);

            var scroll = _zoom.ReadValue<float>();
            if (Mathf.Abs(scroll) > 0.01f)
                frame.ZoomMeters -= Mathf.Sign(scroll) * scrollZoomMetersPerNotch;

            frame.Sprint |= _sprint.IsPressed() || (_moveFinger >= 0 && frame.Move.magnitude >= sprintStickThreshold);
            frame.CrouchToggled |= _crouch.WasPressedThisFrame();
            frame.PausePressed |= _pause.WasPressedThisFrame();
            _current = frame;
        }

        private void ReadTouch(ref GameplayInputFrame frame)
        {
            var scale = DpScale;
            var lookCount = 0;
            Vector2 a = default, b = default, firstDelta = default;
            var moveSeen = false;

            foreach (var t in ETouch.activeTouches)
            {
                var id = t.touchId;
                var ended = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;

                if (t.phase == TouchPhase.Began && _moveFinger < 0 && t.startScreenPosition.x < Screen.width * moveZoneWidth
                    && (IsOverUi == null || !IsOverUi(t.startScreenPosition)))
                {
                    _moveFinger = id;
                    _joystickOrigin = t.startScreenPosition;
                }

                if (id == _moveFinger)
                {
                    moveSeen = true;
                    if (ended)
                    {
                        _moveFinger = -1;
                        continue;
                    }

                    _joystickCurrent = t.screenPosition;
                    frame.Move = VirtualJoystick.Evaluate(_joystickOrigin, _joystickCurrent, joystickRadiusDp * scale, joystickDeadZone);
                    continue;
                }

                if (ended || (IsOverUi != null && IsOverUi(t.startScreenPosition)))
                    continue;

                if (lookCount == 0)
                {
                    a = t.screenPosition;
                    firstDelta = t.delta;
                }
                else if (lookCount == 1)
                {
                    b = t.screenPosition;
                }

                lookCount++;
            }

            if (!moveSeen)
                _moveFinger = -1;

            if (lookCount == 1)
                frame.LookDegrees += new Vector2(firstDelta.x, -firstDelta.y) / scale * touchLookDegreesPerDp;

            if (lookCount >= 2)
            {
                var distance = Vector2.Distance(a, b);
                if (_lastPinch > 0f)
                    frame.ZoomMeters -= (distance - _lastPinch) / scale * pinchZoomMetersPerDp;
                _lastPinch = distance;
            }
            else
            {
                _lastPinch = -1f;
            }
        }
    }
}
