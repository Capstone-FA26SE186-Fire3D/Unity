using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// Scales hazard visuals to the device, the way mobile shooters do: every tier shows the same fire, the high tier
    /// adds the real refractive heat haze (fewer emitters on Medium) and more lights/emitters. Tier comes from memory, cores and the Unity
    /// quality level; <c>FET3D_FIRE_QUALITY</c> (Low/Medium/High) overrides it for testing.
    /// </summary>
    public static class FireQuality
    {
        public enum Tier
        {
            Low,
            Medium,
            High,
        }

        private static Tier? _tier;

        public static Tier Current => _tier ??= Detect();

        public static bool HeatHaze => Current != Tier.Low;
        public static int MaxEmitters => Current switch { Tier.Low => 16, Tier.Medium => 28, _ => 40 };
        public static int MaxSmoke => Current switch { Tier.Low => 8, Tier.Medium => 14, _ => 20 };
        public static int MaxLights => Current switch { Tier.Low => 2, Tier.Medium => 4, _ => 6 };
        public static int MaxHazeEmitters => Current switch { Tier.Low => 0, Tier.Medium => 3, _ => 6 };

        public static void Override(Tier tier) => _tier = tier;

        private static Tier Detect()
        {
            var env = System.Environment.GetEnvironmentVariable("FET3D_FIRE_QUALITY");
            if (!string.IsNullOrEmpty(env) && System.Enum.TryParse<Tier>(env, true, out var forced))
                return forced;
            var gpuMb = SystemInfo.graphicsMemorySize;
            var ramMb = SystemInfo.systemMemorySize;
            var cores = SystemInfo.processorCount;
            if (Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer)
            {
                if (ramMb >= 7000 && cores >= 8 && SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
                    return Tier.High;
                return ramMb >= 4500 ? Tier.Medium : Tier.Low;
            }

            return gpuMb >= 3000 && ramMb >= 12000 ? Tier.High : Tier.Medium;
        }

        /// <summary>The refractive haze reads the camera's opaque texture: request it only while a haze emitter is on.</summary>
        public static void RequestOpaqueTexture(bool on)
        {
            var cam = Camera.main;
            if (cam != null && cam.TryGetComponent<UniversalAdditionalCameraData>(out var data) && data.requiresColorTexture != on)
                data.requiresColorTexture = on;
        }
    }
}
