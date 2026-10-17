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

                var target = (Vector3)a.Position;
                if (Physics.Raycast(target + Vector3.up * 1.2f, Vector3.down, out var hit, 2.5f, ~0, QueryTriggerInteraction.Ignore)
                    && hit.collider.GetComponentInParent<CharacterController>() == null)
                    target.y = hit.point.y;

                v.Root.position = Vector3.Lerp(v.Root.position, target, 1f - Mathf.Exp(-12f * Time.deltaTime));
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
            return new View { Root = go.transform, Interactable = it, Icon = r, Animator = go.GetComponentInChildren<Animator>() };
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
