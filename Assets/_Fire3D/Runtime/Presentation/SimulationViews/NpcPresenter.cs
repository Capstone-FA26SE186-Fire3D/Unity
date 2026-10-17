using System.Collections.Generic;
using Fire3D.Contracts;
using Fire3D.Simulation;
using Fire3D.Unity.Content;
using Fire3D.Unity.Gameplay.Interactions;
using UnityEngine;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// Pooled GameObject views for ECS NPCs: smoothed position/heading, a status icon above the head (no health
    /// bar, Docs section 8) and an npc.guide interactable so the player can call or lead them.
    /// </summary>
    public sealed class NpcPresenter : MonoBehaviour
    {
        private sealed class View
        {
            public Transform Root;
            public Interactable Interactable;
            public Renderer Icon;
            public NpcMode LastMode = (NpcMode)255;
            public Animator Animator;
            public CharacterController Body;
            public float Fall;
        }

        private readonly Dictionary<string, View> _views = new();
        private readonly List<NpcAgent> _agents = new();
        private FireSimulation _sim;
        private RuntimeAssets _assets;

        public void Bind(FireSimulation simulation, RuntimeAssets assets)
        {
            _sim = simulation;
            _assets = assets;
        }

        private void LateUpdate()
        {
            if (_sim == null)
                return;
            _agents.Clear();
            _sim.ForEachNpc(a => _agents.Add(a));
            foreach (var a in _agents)
            {
                var id = a.ObjectId.ToString();
                if (!_views.TryGetValue(id, out var v))
                {
                    v = CreateView(id);
                    _views[id] = v;
                    v.Root.position = a.Position;
                }

                // The simulation decides where people go; the view walks there with a capsule so it slides along
                // walls and furniture instead of cutting through them, and cannot share space with the player.
                var target = (Vector3)a.Position;
                var delta = target - v.Root.position;
                var planar = new Vector3(delta.x, 0f, delta.z);
                if (v.Body == null || !v.Body.enabled || Mathf.Abs(delta.y) > 0.9f || planar.magnitude > 2.5f)
                {
                    // Floor change (stairs are vertical links on the grid) or far behind: place directly.
                    if (v.Body != null)
                        v.Body.enabled = false;
                    v.Root.position = Vector3.Lerp(v.Root.position, target, 1f - Mathf.Exp(-12f * Time.deltaTime));
                    if (v.Body != null && (v.Root.position - target).sqrMagnitude < 0.04f)
                        v.Body.enabled = a.Mode != NpcMode.Unconscious;
                }
                else
                {
                    var step = Vector3.ClampMagnitude(planar * (1f - Mathf.Exp(-12f * Time.deltaTime)), 4f * Time.deltaTime);
                    v.Fall = v.Body.isGrounded ? -1f : v.Fall - 20f * Time.deltaTime;
                    v.Body.Move(step + Vector3.up * v.Fall * Time.deltaTime);
                }

                if (a.Mode != NpcMode.Unconscious)
                    v.Root.rotation = Quaternion.Slerp(v.Root.rotation, Quaternion.Euler(0f, a.Yaw, 0f), 1f - Mathf.Exp(-8f * Time.deltaTime));
                else
                    v.Root.rotation = Quaternion.Slerp(v.Root.rotation, Quaternion.Euler(-85f, a.Yaw, 0f), 1f - Mathf.Exp(-4f * Time.deltaTime));

                if (v.Animator != null)
                    v.Animator.SetFloat("Speed", Vector3.Magnitude(a.Velocity));

                if (v.LastMode != a.Mode)
                {
                    v.LastMode = a.Mode;
                    v.Icon.sharedMaterial = IconMaterial(a.Mode);
                    v.Icon.enabled = a.Mode != NpcMode.Evacuated;
                    // Calling/leading makes no sense once they are out or unconscious.
                    v.Interactable.enabled = a.Mode != NpcMode.Evacuated && a.Mode != NpcMode.Unconscious && a.Mode != NpcMode.Following;
                    if (v.Body != null && a.Mode == NpcMode.Unconscious)
                        v.Body.enabled = false; // Lying down: no standing capsule.
                    v.Root.gameObject.SetActive(a.Mode != NpcMode.Evacuated);
                }

                var cam = UnityEngine.Camera.main;
                if (cam != null)
                    v.Icon.transform.rotation = Quaternion.LookRotation(v.Icon.transform.position - cam.transform.position);
            }
        }

        private View CreateView(string id)
        {
            var prefab = _assets != null ? _assets.NpcPrefab : null;
            var go = prefab != null ? Instantiate(prefab, transform) : PrototypeFactory.Person($"Npc_{id}", new Color(0.25f, 0.55f, 0.35f));
            go.transform.SetParent(transform, false);
            go.name = $"Npc_{id}";
            var it = go.AddComponent<Interactable>();
            it.Configure(id, Capabilities.NpcGuide, null);
            it.DisplayName = "người cần giúp";
            var icon = GameObject.CreatePrimitive(PrimitiveType.Quad);
            icon.name = "StatusIcon";
            Destroy(icon.GetComponent<Collider>());
            icon.transform.SetParent(go.transform, false);
            icon.transform.localPosition = new Vector3(0f, 2.05f, 0f);
            icon.transform.localScale = Vector3.one * 0.28f;
            var r = icon.GetComponent<Renderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var body = go.AddComponent<CharacterController>();
            body.radius = 0.24f;
            body.height = 1.7f;
            body.center = new Vector3(0f, 0.85f, 0f);
            body.stepOffset = 0.3f;
            body.skinWidth = 0.03f;
            body.minMoveDistance = 0f;
            return new View { Root = go.transform, Interactable = it, Icon = r, Animator = go.GetComponentInChildren<Animator>(), Body = body };
        }

        private static readonly Dictionary<NpcMode, Material> Icons = new();

        private static Material IconMaterial(NpcMode mode)
        {
            if (Icons.TryGetValue(mode, out var m) && m != null)
                return m;
            var color = mode switch
            {
                NpcMode.Panicking => new Color(1f, 0.55f, 0.1f),
                NpcMode.Injured => new Color(0.95f, 0.2f, 0.2f),
                NpcMode.Unconscious => new Color(0.4f, 0.1f, 0.6f),
                NpcMode.Following => new Color(0.2f, 0.8f, 1f),
                NpcMode.Evacuating => new Color(0.3f, 0.9f, 0.4f),
                _ => new Color(0.9f, 0.9f, 0.9f),
            };
            m = PrototypeFactory.Lit(color, 0f, 0f, color * 1.5f);
            Icons[mode] = m;
            return m;
        }
    }
}
