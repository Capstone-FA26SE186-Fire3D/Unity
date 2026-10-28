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

            /// <summary>Other looks for the same key (Source/&lt;key&gt;@2…); <see cref="Material"/> is the first one.</summary>
            public List<Material> Variants;
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

        /// <summary>
        /// A variant chosen by <paramref name="seed"/> (building revision + IFC material name) so two buildings do not
        /// share one look while a rebuild of the same building gives the same result (Docs 5.5).
        /// </summary>
        public Material Get(string key, string seed)
        {
            foreach (var e in Entries)
            {
                if (e.Key != key)
                    continue;
                if (e.Variants == null || e.Variants.Count < 2 || string.IsNullOrEmpty(seed))
                    return e.Material;
                var h = 17;
                foreach (var ch in seed)
                    h = unchecked(h * 31 + ch);
                var pick = e.Variants[(int)((uint)h % (uint)e.Variants.Count)];
                return pick != null ? pick : e.Material;
            }

            return null;
        }
    }
}
