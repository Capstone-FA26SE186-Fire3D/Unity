using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>
    /// Shared runtime assets: interactable prefabs per capability, VFX, NPC view and the material library.
    /// Building geometry is not here: it comes from the per-building AssetBundles.
    /// </summary>
    [CreateAssetMenu(menuName = "Fire3D/Runtime Assets", fileName = "RuntimeAssets")]
    public sealed class RuntimeAssets : ScriptableObject
    {
        [Serializable]
        public struct CapabilityPrefab
        {
            public string Capability;
            public GameObject Prefab;
        }

        public List<CapabilityPrefab> Interactables = new();
        public GameObject PlayerPrefab;
        public GameObject NpcPrefab;

        /// <summary>Rigged occupant models (Art/Characters); NPC views pick one per NPC id so a crowd is varied.</summary>
        public List<GameObject> NpcVariants = new();

        /// <summary>Selectable player bodies; <see cref="PlayerPrefab"/> is the default.</summary>
        public List<GameObject> PlayerVariants = new();

        /// <summary>A stable variant for <paramref name="id"/>, or <see cref="NpcPrefab"/> when there are none.</summary>
        public GameObject NpcPrefabFor(string id)
        {
            if (NpcVariants == null || NpcVariants.Count == 0)
                return NpcPrefab;
            var h = 17;
            foreach (var ch in id)
                h = unchecked(h * 31 + ch);
            var pick = NpcVariants[(int)((uint)h % (uint)NpcVariants.Count)];
            return pick != null ? pick : NpcPrefab;
        }
        public ParticleSystem FirePrefab;
        public ParticleSystem SmokePrefab;
        public ParticleSystem SprayPrefab;
        public ParticleSystem SprinklerPrefab;
        public MaterialLibrary Materials;
        public AudioClip AlarmClip;
        public SoundSet Sounds = new();

        /// <summary>Sky seen through windows (Skybox/Panoramic material); null keeps the camera's solid background.</summary>
        public Material Sky;

        /// <summary>Night-time sky, used when the scenario text says it plays at night ("ban đêm", "night").</summary>
        public Material SkyNight;

        /// <summary>Burn mark projected on the floor under a fire (URP decal material); null draws no scorch.</summary>
        public Material ScorchDecal;

        /// <summary>
        /// Prefab for <paramref name="capability"/>; a belongings bait picks the look its label names (backpack, phone)
        /// from the "capability:variant" entries, otherwise the plain entry.
        /// </summary>
        public GameObject PrefabFor(string capability, string label)
        {
            var variant = BaitVariant(label);
            return (variant != null ? PrefabFor($"{capability}:{variant}") : null) ?? PrefabFor(capability);
        }

        private static string BaitVariant(string label)
        {
            if (string.IsNullOrEmpty(label))
                return null;
            var l = label.ToLowerInvariant();
            if (l.Contains("ba lô") || l.Contains("balo") || l.Contains("backpack") || l.Contains("cặp"))
                return "backpack";
            if (l.Contains("điện thoại") || l.Contains("phone"))
                return "phone";
            return null;
        }

        /// <summary>Gameplay sound effects (Art/Audio); any clip may be missing, the game then stays silent for it.</summary>
        [Serializable]
        public sealed class SoundSet
        {
            public AudioClip FireLoop;
            public AudioClip FireLarge;
            public AudioClip RoomTone;
            public AudioClip Sparks;
            public List<AudioClip> Panting = new();
            public AudioClip DoorOpen;
            public AudioClip DoorClose;
            public AudioClip DoorTouch;
            public AudioClip CallPointPress;
            public AudioClip ExtinguisherPin;
            public AudioClip ExtinguisherSpray;
            public AudioClip SprinklerSpray;
            public AudioClip TapRunning;
            public AudioClip Cough;
            public AudioClip ElevatorDing;
            public AudioClip PowerCut;

            /// <summary>Smoke detector chirp, looped on a triggered detector.</summary>
            public AudioClip DetectorBeep;
            public AudioClip UiClick;
            public AudioClip UiSuccess;
            public AudioClip UiError;
            public List<AudioClip> Footsteps = new();
            public List<AudioClip> Crawl = new();

            /// <summary>Ordinary (wooden) doors; fire and exit doors use the heavier push-bar <see cref="DoorOpen"/>/<see cref="DoorClose"/>.</summary>
            public List<AudioClip> DoorOpenWood = new();
            public List<AudioClip> DoorCloseWood = new();

            public static AudioClip Pick(List<AudioClip> clips, AudioClip fallback) =>
                clips != null && clips.Count > 0 ? clips[UnityEngine.Random.Range(0, clips.Count)] : fallback;
        }

        public GameObject PrefabFor(string capability)
        {
            foreach (var p in Interactables)
                if (p.Capability == capability)
                    return p.Prefab;
            return null;
        }
    }
}
