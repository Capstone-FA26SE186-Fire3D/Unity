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

        public GameObject PrefabFor(string capability)
        {
            foreach (var p in Interactables)
                if (p.Capability == capability)
                    return p.Prefab;
            return null;
        }
    }
}
