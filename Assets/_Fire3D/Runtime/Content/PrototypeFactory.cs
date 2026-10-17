using System.Collections.Generic;
using Fire3D.Contracts;
using UnityEngine;

namespace Fire3D.Unity.Content
{
    /// <summary>
    /// Graybox stand-ins for catalog objects until library models (Tripo/curated) replace them. Sizes follow
    /// real-world dimensions so reach, camera and hazard checks already behave correctly.
    /// </summary>
    public static class PrototypeFactory
    {
        private static readonly Dictionary<Color, Material> Cache = new();

        public static Material Lit(Color color, float smoothness = 0.35f, float metallic = 0f, Color? emission = null)
        {
            if (emission == null && Cache.TryGetValue(color, out var cached) && cached != null)
                return cached;
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            var m = new Material(shader) { name = $"Proto_{ColorUtility.ToHtmlStringRGB(color)}" };
            m.SetColor("_BaseColor", color);
            m.color = color;
            m.SetFloat("_Smoothness", smoothness);
            m.SetFloat("_Metallic", metallic);
            if (emission.HasValue)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission.Value);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            else
            {
                Cache[color] = m;
            }

            return m;
        }

        public static GameObject Box(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!collider)
                Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        public static GameObject Cylinder(string name, Transform parent, Vector3 localPos, float diameter, float height, Material mat, bool collider = true)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = new Vector3(diameter, height * 0.5f, diameter);
            go.GetComponent<Renderer>().sharedMaterial = mat;
            if (!collider)
                Object.DestroyImmediate(go.GetComponent<Collider>());
            return go;
        }

        /// <summary>Creates a stand-in for <paramref name="capability"/> with its pivot on the floor or wall mount.</summary>
        public static GameObject Create(string capability)
        {
            var root = new GameObject(capability);
            var t = root.transform;
            switch (capability)
            {
                case Capabilities.ExtinguisherPortable:
                    Cylinder("Body", t, new Vector3(0f, 0.3f, 0f), 0.16f, 0.55f, Lit(new Color(0.78f, 0.06f, 0.05f), 0.6f));
                    Cylinder("Valve", t, new Vector3(0f, 0.62f, 0f), 0.06f, 0.1f, Lit(new Color(0.15f, 0.15f, 0.15f), 0.5f, 0.8f), false);
                    break;
                case Capabilities.AlarmCallPoint:
                    Box("Box", t, new Vector3(0f, 1.4f, 0f), new Vector3(0.1f, 0.1f, 0.05f), Lit(new Color(0.85f, 0.05f, 0.05f), 0.5f));
                    Box("Glass", t, new Vector3(0f, 1.4f, -0.027f), new Vector3(0.06f, 0.06f, 0.005f), Lit(new Color(0.95f, 0.95f, 0.95f), 0.9f), false);
                    break;
                case Capabilities.AlarmDetector:
                    Cylinder("Detector", t, Vector3.zero, 0.12f, 0.04f, Lit(new Color(0.94f, 0.94f, 0.92f), 0.4f), false);
                    break;
                case Capabilities.Sprinkler:
                    Cylinder("Head", t, Vector3.zero, 0.05f, 0.06f, Lit(new Color(0.8f, 0.65f, 0.2f), 0.7f, 0.9f), false);
                    break;
                case Capabilities.WaterSink:
                    Box("Cabinet", t, new Vector3(0f, 0.4f, 0f), new Vector3(0.6f, 0.8f, 0.45f), Lit(new Color(0.85f, 0.85f, 0.83f)));
                    Box("Basin", t, new Vector3(0f, 0.82f, 0f), new Vector3(0.5f, 0.06f, 0.36f), Lit(new Color(0.97f, 0.97f, 0.97f), 0.8f), false);
                    Cylinder("Tap", t, new Vector3(0f, 0.95f, 0.15f), 0.03f, 0.2f, Lit(new Color(0.8f, 0.8f, 0.82f), 0.85f, 1f), false);
                    break;
                case Capabilities.WaterBath:
                    Box("Tub", t, new Vector3(0f, 0.28f, 0f), new Vector3(0.75f, 0.56f, 1.6f), Lit(new Color(0.97f, 0.97f, 0.97f), 0.8f));
                    break;
                case Capabilities.WaterShower:
                    Box("Tray", t, new Vector3(0f, 0.05f, 0f), new Vector3(0.9f, 0.1f, 0.9f), Lit(new Color(0.95f, 0.95f, 0.95f), 0.8f));
                    Cylinder("Pipe", t, new Vector3(0f, 1.1f, 0.4f), 0.03f, 2.1f, Lit(new Color(0.8f, 0.8f, 0.82f), 0.85f, 1f), false);
                    break;
                case Capabilities.ItemTowel:
                    Box("Towel", t, new Vector3(0f, 0.03f, 0f), new Vector3(0.35f, 0.05f, 0.25f), Lit(new Color(0.2f, 0.45f, 0.8f), 0.1f));
                    break;
                case Capabilities.BaitBelongings:
                    Box("Bag", t, new Vector3(0f, 0.15f, 0f), new Vector3(0.4f, 0.3f, 0.18f), Lit(new Color(0.35f, 0.22f, 0.12f), 0.3f));
                    break;
                case Capabilities.Elevator:
                    Box("Doors", t, new Vector3(0f, 1.05f, 0f), new Vector3(1.1f, 2.1f, 0.08f), Lit(new Color(0.7f, 0.72f, 0.75f), 0.75f, 0.9f));
                    Box("Button", t, new Vector3(0.75f, 1.1f, -0.03f), new Vector3(0.08f, 0.14f, 0.03f), Lit(new Color(0.9f, 0.85f, 0.4f), 0.6f), false);
                    break;
                default:
                    Box("Marker", t, new Vector3(0f, 0.25f, 0f), new Vector3(0.3f, 0.5f, 0.3f), Lit(new Color(0.9f, 0.6f, 0.1f)));
                    break;
            }

            return root;
        }

        /// <summary>Simple occupant stand-in: body capsule plus head, ~1.7 m tall.</summary>
        public static GameObject Person(string name, Color shirt)
        {
            var root = new GameObject(name);
            var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "Body";
            body.transform.SetParent(root.transform, false);
            body.transform.localPosition = new Vector3(0f, 0.75f, 0f);
            body.transform.localScale = new Vector3(0.45f, 0.75f, 0.32f);
            body.GetComponent<Renderer>().sharedMaterial = Lit(shirt, 0.2f);
            Object.DestroyImmediate(body.GetComponent<Collider>());
            var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "Head";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 1.58f, 0f);
            head.transform.localScale = Vector3.one * 0.24f;
            head.GetComponent<Renderer>().sharedMaterial = Lit(new Color(0.87f, 0.7f, 0.58f), 0.3f);
            Object.DestroyImmediate(head.GetComponent<Collider>());
            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Facing";
            nose.transform.SetParent(root.transform, false);
            nose.transform.localPosition = new Vector3(0f, 1.25f, 0.17f);
            nose.transform.localScale = new Vector3(0.18f, 0.08f, 0.05f);
            nose.GetComponent<Renderer>().sharedMaterial = Lit(new Color(0.12f, 0.12f, 0.14f), 0.3f);
            Object.DestroyImmediate(nose.GetComponent<Collider>());
            return root;
        }
    }
}
