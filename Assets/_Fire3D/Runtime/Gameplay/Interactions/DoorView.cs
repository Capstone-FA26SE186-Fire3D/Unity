using UnityEngine;

namespace Fire3D.Unity.Gameplay.Interactions
{
    /// <summary>
    /// Visual door leaf. The hazard grid owns smoke/heat blocking through the simulation door entity; this swings
    /// the leaf away from whoever opens it (ease-out with a small settle, closing ease-in-out) and keeps the leaf
    /// collider on in every pose, so nobody walks through a closed door or through an open leaf.
    /// </summary>
    public sealed class DoorView : MonoBehaviour
    {
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

        [SerializeField] private Transform leaf;
        [SerializeField] private float openAngle = 100f;
        [SerializeField] private float openSeconds = 0.9f;
        [SerializeField] private float closeSeconds = 0.65f;
        [SerializeField] private Collider blocker;

        private float _angle;
        private float _from;
        private float _to;
        private float _t = 1f;
        private float _duration = 1f;
        private Quaternion _closedRotation;
        private Renderer[] _renderers;
        private MaterialPropertyBlock _block;
        private Color[] _baseColors;
        private float _heatUntil = -1f;
        private Color _heatColor;

        public bool Open { get; private set; }
        public bool Moving => _t < 1f;

        /// <summary>Door frame bounds used to find the grid cells this door controls.</summary>
        public Bounds FrameBounds
        {
            get
            {
                if (blocker != null)
                {
                    // The leaf collider moves with the leaf; measure the frame as if closed.
                    var saved = leaf != null ? leaf.localRotation : Quaternion.identity;
                    if (leaf != null)
                        leaf.localRotation = _closedRotation;
                    Physics.SyncTransforms();
                    var b = blocker.bounds;
                    if (leaf != null)
                        leaf.localRotation = saved;
                    return b;
                }

                var r = GetComponentInChildren<Renderer>();
                return r != null ? r.bounds : new Bounds(transform.position + Vector3.up, new Vector3(1f, 2f, 0.2f));
            }
        }

        public void Bind(Transform leafTransform, Collider leafCollider)
        {
            leaf = leafTransform;
            blocker = leafCollider;
            _closedRotation = leaf != null ? leaf.localRotation : Quaternion.identity;
        }

        private void Awake()
        {
            if (leaf != null)
                _closedRotation = leaf.localRotation;
            if (blocker != null)
                blocker.enabled = true;
        }

        public void SetOpen(bool open, bool instant = false) => SetOpen(open, null, instant);

        /// <summary>Opens away from <paramref name="opener"/> (world position) when given.</summary>
        public void SetOpen(bool open, Vector3? opener, bool instant = false)
        {
            Open = open;
            var side = Mathf.Sign(_to == 0f ? 1f : _to);
            if (open && opener.HasValue)
            {
                // A positive yaw swings the leaf (+X from the hinge) towards local -Z.
                side = transform.InverseTransformPoint(opener.Value).z >= 0f ? 1f : -1f;
            }

            if (blocker != null)
                blocker.enabled = true;
            _from = _angle;
            _to = open ? side * openAngle : 0f;
            _duration = open ? openSeconds : closeSeconds;
            _t = 0f;
            if (instant)
            {
                _t = 1f;
                _angle = _to;
                Apply();
            }
        }

        /// <summary>World point on the leaf a hand reaches for, on the side of <paramref name="from"/>.</summary>
        public Vector3 HandPoint(Vector3 from, bool handle)
        {
            if (leaf == null)
                return transform.position + Vector3.up * 1.1f;
            var col = blocker as BoxCollider;
            var size = col != null ? col.size : new Vector3(0.85f, 2f, 0.05f);
            var center = col != null ? col.center : new Vector3(size.x * 0.5f, size.y * 0.5f, 0f);
            var leafSpaceFrom = (col != null ? col.transform : leaf).InverseTransformPoint(from);
            var z = (leafSpaceFrom.z >= 0f ? 1f : -1f) * (size.z * 0.5f + 0.01f);
            var local = handle
                ? new Vector3(center.x + size.x * 0.38f, center.y - size.y * 0.5f + 1.0f, z)
                : new Vector3(center.x + size.x * 0.1f, center.y - size.y * 0.5f + 1.35f, z);
            return (col != null ? col.transform : leaf).TransformPoint(local);
        }

        /// <summary>Tints the leaf: a slow red throb for a hot door, a brief cool flash otherwise.</summary>
        public void ShowHeat(bool hot)
        {
            _renderers ??= leaf != null ? leaf.GetComponentsInChildren<Renderer>() : GetComponentsInChildren<Renderer>();
            if (_baseColors == null)
            {
                _block = new MaterialPropertyBlock();
                _baseColors = new Color[_renderers.Length];
                for (var i = 0; i < _renderers.Length; i++)
                {
                    var m = _renderers[i].sharedMaterial;
                    _baseColors[i] = m != null && m.HasProperty(BaseColorId) ? m.GetColor(BaseColorId) : Color.white;
                }
            }

            _heatColor = hot ? new Color(1f, 0.32f, 0.12f) : new Color(0.55f, 0.8f, 1f);
            _heatUntil = Time.time + (hot ? 3.5f : 1.1f);
        }

        private void Update()
        {
            if (_t < 1f)
            {
                _t = Mathf.Min(1f, _t + Time.deltaTime / Mathf.Max(0.05f, _duration));
                var e = Open ? EaseOutSettle(_t) : EaseInOut(_t);
                _angle = Mathf.LerpUnclamped(_from, _to, e);
                Apply();
            }

            if (_baseColors != null && _heatUntil > -1f)
            {
                var left = _heatUntil - Time.time;
                var k = left <= 0f ? 0f : Mathf.Clamp01(left / 0.6f) * (0.55f + 0.45f * Mathf.Sin(Time.time * 7f));
                for (var i = 0; i < _renderers.Length; i++)
                {
                    if (_renderers[i] == null)
                        continue;
                    _renderers[i].GetPropertyBlock(_block);
                    _block.SetColor(BaseColorId, Color.Lerp(_baseColors[i], _heatColor, k * 0.65f));
                    _renderers[i].SetPropertyBlock(_block);
                }

                if (left <= 0f)
                {
                    foreach (var r in _renderers)
                        if (r != null)
                            r.SetPropertyBlock(null);
                    _heatUntil = -1f;
                }
            }
        }

        private void Apply()
        {
            if (leaf != null)
                leaf.localRotation = _closedRotation * Quaternion.Euler(0f, _angle, 0f);
        }

        /// <summary>Fast start, 4% overshoot, settles: a door pushed open with one hand.</summary>
        private static float EaseOutSettle(float t)
        {
            const float c1 = 0.9f;
            const float c3 = c1 + 1f;
            var u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        private static float EaseInOut(float t) => t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;
    }
}
