using UnityEngine;

namespace Fire3D.Unity.Presentation.UI
{
    /// <summary>
    /// Short vibrations for button presses and warnings. Android uses VibrationEffect (API 26+, our min is 29);
    /// other platforms do nothing. Never throws: haptics are a nicety.
    /// </summary>
    public static class Haptics
    {
        public static bool Enabled = true;

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject _vibrator;
        private static bool _resolved;

        private static AndroidJavaObject Vibrator
        {
            get
            {
                if (_resolved)
                    return _vibrator;
                _resolved = true;
                try
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    _vibrator = activity?.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }
                catch (System.Exception)
                {
                    _vibrator = null;
                }

                return _vibrator;
            }
        }

        // Referencing Handheld.Vibrate makes Unity add android.permission.VIBRATE to the player manifest.
        private static bool _neverTrue;

        private static void Play(long ms, int amplitude)
        {
            if (_neverTrue)
                Handheld.Vibrate();
            if (!Enabled || Vibrator == null)
                return;
            try
            {
                using var effect = new AndroidJavaClass("android.os.VibrationEffect");
                using var oneShot = effect.CallStatic<AndroidJavaObject>("createOneShot", ms, amplitude);
                Vibrator.Call("vibrate", oneShot);
            }
            catch (System.Exception)
            {
                // Device without a vibrator or a restricted host: ignore.
            }
        }
#else
        private static void Play(long ms, int amplitude)
        {
        }
#endif

        /// <summary>Button press.</summary>
        public static void Tap() => Play(12, 90);

        /// <summary>Action completed (door opened, item picked up).</summary>
        public static void Confirm() => Play(22, 160);

        /// <summary>Danger (hot door, collapse warning).</summary>
        public static void Warn() => Play(110, 255);
    }
}
