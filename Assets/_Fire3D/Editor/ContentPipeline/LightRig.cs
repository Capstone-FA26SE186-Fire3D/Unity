using Fire3D.Unity.Content;
using UnityEditor;
using UnityEngine;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Generated building lighting shared by the dev scene and the build worker: ceiling lights + emissive panels
    /// (normal set), red emergency lights in escape routes, green EXIT signs. Ceiling lights are rectangle area
    /// lights (bake-only: an unbaked scene shows just the emissive panels), emergency and sign lights start
    /// realtime and are switched to baked by the baker.
    /// </summary>
    public static class LightRig
    {
        public static void Room(string name, string category, Bounds room, float ceilingY, Transform parent)
        {
            var spacing = category == "corridor" ? 4f : 5f;
            var nx = Mathf.Max(1, Mathf.RoundToInt(room.size.x / spacing));
            var nz = Mathf.Max(1, Mathf.RoundToInt(room.size.z / spacing));
            for (var i = 0; i < nx; i++)
            for (var k = 0; k < nz; k++)
            {
                var p = new Vector3(room.min.x + (i + 0.5f) * room.size.x / nx, ceilingY - 0.05f, room.min.z + (k + 0.5f) * room.size.z / nz);
                // Rectangle area light = a real panel: wide soft penumbrae and gentle falloff once baked, instead of the
                // hard hot spots and sharp shadow edges a point light leaves on walls and furniture.
                var go = new GameObject($"{name}_Light_{i}_{k}");
                go.transform.SetParent(parent, false);
                go.transform.SetPositionAndRotation(p - Vector3.up * 0.03f, Quaternion.Euler(90f, 0f, 0f));
                var l = go.AddComponent<Light>();
                l.type = LightType.Rectangle;
                l.areaSize = new Vector2(Mathf.Min(1.8f, room.size.x / nx * 0.4f), Mathf.Min(1.8f, room.size.z / nz * 0.4f));
                l.color = new Color(1f, 0.93f, 0.82f);
                l.intensity = 5.5f;
                l.range = Mathf.Max(6f, spacing * 1.6f);
                l.shadows = LightShadows.Soft;
                l.lightmapBakeType = LightmapBakeType.Baked;
                go.AddComponent<LightGroupMember>().Group = LightGroup.Normal;

                var panel = GrayboxMesh.Create($"{name}_Panel_{i}_{k}", parent, p, new Vector3(l.areaSize.x * 0.9f, 0.02f, l.areaSize.y * 0.9f),
                    PrototypeFactory.Lit(Color.white, 0.2f, 0f, new Color(1f, 0.92f, 0.8f) * 2.6f), false);
                panel.transform.position = p;
                panel.AddComponent<LightGroupMember>().Group = LightGroup.Normal;
            }

            if (category is not ("corridor" or "stair" or "lobby"))
                return;
            var em = new GameObject($"{name}_Emergency");
            em.transform.SetParent(parent, false);
            em.transform.position = new Vector3(room.center.x, ceilingY - 0.3f, room.center.z);
            var e = em.AddComponent<Light>();
            e.type = LightType.Point;
            e.color = new Color(1f, 0.32f, 0.22f);
            e.intensity = 0.9f;
            e.range = Mathf.Max(9f, Mathf.Max(room.size.x, room.size.z) * 0.6f);
            e.enabled = false;
            e.lightmapBakeType = LightmapBakeType.Realtime;
            em.AddComponent<LightGroupMember>().Group = LightGroup.Emergency;
        }

        public enum ExitSignKind
        {
            /// <summary>"LỐI RA / EXIT": the way out of the building (running man towards an open door).</summary>
            Final,
            /// <summary>"THOÁT HIỂM / EXIT" with stairs going down.</summary>
            StairDown,
            /// <summary>"THOÁT HIỂM / EXIT" with stairs going up.</summary>
            StairUp,
            Left,
            Right,
        }

        private const string SignFolder = "Assets/_Fire3D/Art/Signs";

        /// <summary>
        /// Emergency exit sign: a dark housing with a textured, emissive face (running-man pictogram, text, arrow). The face
        /// glows through baked emission, so the green spill on the walls has the sign's own soft shape; the old close-range
        /// point light baked into hard-edged rectangles on the wall above the door. The sign's face looks along its local -Z,
        /// so pass a rotation that turns it towards the people who need it.
        /// </summary>
        public static void ExitSign(Vector3 pos, Quaternion rot, Transform parent, ExitSignKind kind = ExitSignKind.Final)
        {
            var root = new GameObject("ExitSign_" + kind);
            root.transform.SetParent(parent, false);
            root.transform.SetPositionAndRotation(pos, rot);
            root.AddComponent<LightGroupMember>().Group = LightGroup.ExitSign;

            GrayboxMesh.Create("Housing", root.transform, Vector3.zero, new Vector3(0.54f, 0.29f, 0.06f),
                PrototypeFactory.Lit(new Color(0.1f, 0.11f, 0.12f), 0.35f), false);

            var face = GameObject.CreatePrimitive(PrimitiveType.Quad);
            face.name = "Face";
            Object.DestroyImmediate(face.GetComponent<Collider>());
            face.transform.SetParent(root.transform, false);
            face.transform.localPosition = new Vector3(0f, 0f, -0.031f);
            face.transform.localScale = new Vector3(0.5f, 0.25f, 1f);
            face.GetComponent<MeshRenderer>().sharedMaterial = SignMaterial(kind);
        }

        private static Material SignMaterial(ExitSignKind kind)
        {
            var path = $"{SignFolder}/Mat_Exit{kind}.mat";
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null)
                return existing;
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>($"{SignFolder}/Exit{kind}.png");
            var m = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = $"Mat_Exit{kind}" };
            m.SetTexture("_BaseMap", texture);
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", 0.15f);
            m.EnableKeyword("_EMISSION");
            m.SetTexture("_EmissionMap", texture);
            m.SetColor("_EmissionColor", Color.white * 1.8f);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            AssetDatabase.CreateAsset(m, path);
            return m;
        }
    }
}
