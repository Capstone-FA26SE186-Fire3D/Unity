using Fire3D.Unity.Content;
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

        public static void ExitSign(Vector3 pos, Quaternion rot, Transform parent)
        {
            var sign = GrayboxMesh.Create("ExitSign", parent, Vector3.zero, new Vector3(0.36f, 0.16f, 0.04f),
                PrototypeFactory.Lit(new Color(0.1f, 0.8f, 0.3f), 0.3f, 0f, new Color(0.1f, 1f, 0.35f) * 2.5f), false);
            sign.transform.SetPositionAndRotation(pos, rot);
            sign.AddComponent<LightGroupMember>().Group = LightGroup.ExitSign;
            var glow = new GameObject("ExitGlow").AddComponent<Light>();
            glow.transform.SetParent(sign.transform, false);
            glow.transform.localPosition = new Vector3(0f, -0.2f, -0.2f);
            glow.type = LightType.Point;
            glow.color = new Color(0.25f, 1f, 0.45f);
            glow.intensity = 0.6f;
            glow.range = 3f;
            glow.lightmapBakeType = LightmapBakeType.Realtime;
            glow.gameObject.AddComponent<LightGroupMember>().Group = LightGroup.ExitSign;
        }
    }
}
