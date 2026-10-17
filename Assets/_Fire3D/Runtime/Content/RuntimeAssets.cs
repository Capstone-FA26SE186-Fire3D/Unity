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
