using System.Collections.Generic;
using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>
    /// Box meshes with world-scale UVs (1 UV unit = 1 meter, like the Blender worker's box projection) so library
    /// materials tile at real size; UV1 is a simple non-overlapping layout for lightmaps.
    /// </summary>
    public static class GrayboxMesh
    {
        private static readonly Dictionary<Vector3, Mesh> Cache = new();

        public static Mesh Box(Vector3 size)
        {
            var key = new Vector3(Mathf.Round(size.x * 1000f), Mathf.Round(size.y * 1000f), Mathf.Round(size.z * 1000f));
            if (Cache.TryGetValue(key, out var cached) && cached != null)
                return cached;

            var h = size * 0.5f;
            var verts = new List<Vector3>(24);
            var normals = new List<Vector3>(24);
            var uv0 = new List<Vector2>(24);
            var uv1 = new List<Vector2>(24);
            var tris = new List<int>(36);

            // +X, -X, +Y, -Y, +Z, -Z faces; each face gets a cell of a 3x2 atlas in UV1.
            AddFace(new Vector3(h.x, 0, 0), Vector3.right, Vector3.forward, Vector3.up, size.z, size.y, 0);
            AddFace(new Vector3(-h.x, 0, 0), Vector3.left, Vector3.back, Vector3.up, size.z, size.y, 1);
            AddFace(new Vector3(0, h.y, 0), Vector3.up, Vector3.right, Vector3.forward, size.x, size.z, 2);
            AddFace(new Vector3(0, -h.y, 0), Vector3.down, Vector3.right, Vector3.back, size.x, size.z, 3);
            AddFace(new Vector3(0, 0, h.z), Vector3.forward, Vector3.left, Vector3.up, size.x, size.y, 4);
            AddFace(new Vector3(0, 0, -h.z), Vector3.back, Vector3.right, Vector3.up, size.x, size.y, 5);

            var mesh = new Mesh { name = $"GrayboxBox_{size.x:0.##}x{size.y:0.##}x{size.z:0.##}" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uv0);
            mesh.SetUVs(1, uv1);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            Cache[key] = mesh;
            return mesh;

            void AddFace(Vector3 center, Vector3 normal, Vector3 uAxis, Vector3 vAxis, float uSize, float vSize, int cell)
            {
                var start = verts.Count;
                var hu = uAxis * (uSize * 0.5f);
                var hv = vAxis * (vSize * 0.5f);
                verts.Add(center - hu - hv);
                verts.Add(center + hu - hv);
                verts.Add(center + hu + hv);
                verts.Add(center - hu + hv);
                for (var i = 0; i < 4; i++)
                    normals.Add(normal);
                uv0.Add(new Vector2(0f, 0f));
                uv0.Add(new Vector2(uSize, 0f));
                uv0.Add(new Vector2(uSize, vSize));
                uv0.Add(new Vector2(0f, vSize));
                var cx = cell % 3 / 3f;
                var cy = cell / 3 / 2f;
                const float pad = 0.01f;
                const float w = 1f / 3f - pad * 2f;
                const float hh = 0.5f - pad * 2f;
                uv1.Add(new Vector2(cx + pad, cy + pad));
                uv1.Add(new Vector2(cx + pad + w, cy + pad));
                uv1.Add(new Vector2(cx + pad + w, cy + pad + hh));
                uv1.Add(new Vector2(cx + pad, cy + pad + hh));
                tris.Add(start);
                tris.Add(start + 2);
                tris.Add(start + 1);
                tris.Add(start);
                tris.Add(start + 3);
                tris.Add(start + 2);
            }
        }

        /// <summary>Creates a static box with collider; position is the box center in parent space.</summary>
        public static GameObject Create(string name, Transform parent, Vector3 center, Vector3 size, Material material, bool collider = true)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            var mesh = Box(size);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            if (collider)
                go.AddComponent<BoxCollider>().size = size;
            return go;
        }
    }
}
