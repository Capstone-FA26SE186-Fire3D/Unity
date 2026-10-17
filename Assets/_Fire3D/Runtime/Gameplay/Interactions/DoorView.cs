using UnityEngine;

namespace Fire3D.Unity.Gameplay.Interactions
{
    /// <summary>
    /// Visual door leaf. The hazard grid owns smoke/heat blocking through the simulation door entity; this only
    /// swings the leaf and toggles its collider so the player cannot walk through a closed door.
    /// </summary>
    public sealed class DoorView : MonoBehaviour
    {
        [SerializeField] private Transform leaf;
        [SerializeField] private float openAngle = 95f;
        [SerializeField] private float speedDegrees = 160f;
        [SerializeField] private Collider blocker;

        private float _angle;
        private float _target;
        private Quaternion _closedRotation;

        public bool Open { get; private set; }

        /// <summary>Door frame bounds used to find the grid cells this door controls.</summary>
        public Bounds FrameBounds
        {
            get
            {
                if (blocker != null)
                    return blocker.bounds;
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
        }

        public void SetOpen(bool open, bool instant = false)
        {
            Open = open;
            _target = open ? openAngle : 0f;
            if (blocker != null)
                blocker.enabled = !open;
            if (instant)
            {
                _angle = _target;
                Apply();
            }
        }

        private void Update()
        {
            if (Mathf.Approximately(_angle, _target))
                return;
            _angle = Mathf.MoveTowards(_angle, _target, speedDegrees * Time.deltaTime);
            Apply();
        }

        private void Apply()
        {
            if (leaf != null)
                leaf.localRotation = _closedRotation * Quaternion.Euler(0f, _angle, 0f);
        }
    }
}
