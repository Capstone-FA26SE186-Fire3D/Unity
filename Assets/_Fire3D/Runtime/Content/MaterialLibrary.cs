using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>libraryKey → URP material, built by the Editor material library tool.</summary>
    [CreateAssetMenu(menuName = "Fire3D/Material Library", fileName = "MaterialLibrary")]
    public sealed class MaterialLibrary : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string Key;
            public Material Material;
            public float RepeatMeters;
        }

        public string Version = "1";
        public List<Entry> Entries = new();

        public Material Get(string key)
        {
            foreach (var e in Entries)
                if (e.Key == key)
                    return e.Material;
            return null;
        }
    }
}
