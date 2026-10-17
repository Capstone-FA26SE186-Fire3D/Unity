using UnityEngine;

namespace Fire3D.Unity.Gameplay.Controls
{
    /// <summary>
    /// Device-independent gameplay input for one frame, already scaled so consumers do not care whether it came
    /// from touch, mouse/keyboard or a gamepad.
    /// </summary>
    public struct GameplayInputFrame
    {
        /// <summary>Planar move intent, x = right, y = forward, magnitude in [0, 1].</summary>
        public Vector2 Move;

        /// <summary>Camera orbit this frame in degrees; x = yaw, y = pitch (positive looks down).</summary>
        public Vector2 LookDegrees;

        /// <summary>Camera zoom this frame in meters; positive moves the camera farther away.</summary>
        public float ZoomMeters;

        public bool Sprint;
        public bool CrouchToggled;
        public bool PausePressed;
    }

    public interface IGameplayInputSource
    {
        GameplayInputFrame Current { get; }
    }

    /// <summary>Floating on-screen joystick math: the stick origin is where the finger first touched.</summary>
    public static class VirtualJoystick
    {
        /// <summary>
        /// Maps a finger offset to a stick value with magnitude in [0, 1]. Offsets inside the dead zone return zero
        /// and the remaining range is rescaled so output starts at zero.
        /// </summary>
        public static Vector2 Evaluate(Vector2 origin, Vector2 current, float radiusPixels, float deadZone)
        {
            if (radiusPixels <= 0f)
                return Vector2.zero;
            var offset = (current - origin) / radiusPixels;
            var magnitude = offset.magnitude;
            if (magnitude <= deadZone || magnitude <= Mathf.Epsilon)
                return Vector2.zero;
            var scaled = (Mathf.Min(magnitude, 1f) - deadZone) / (1f - deadZone);
            return offset / magnitude * scaled;
        }
    }

    /// <summary>Converts stick input to a world direction relative to the camera yaw.</summary>
    public static class CameraRelativeMovement
    {
        public static Vector3 PlanarDirection(Vector2 input, float cameraYawDegrees)
        {
            var clamped = Vector2.ClampMagnitude(input, 1f);
            return Quaternion.Euler(0f, cameraYawDegrees, 0f) * new Vector3(clamped.x, 0f, clamped.y);
        }
    }
}
