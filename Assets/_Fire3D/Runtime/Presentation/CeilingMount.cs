using UnityEngine;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// Keeps a wall part (the fire bell) just under the ceiling of whatever building it is placed in: on start it
    /// looks up for the ceiling and moves so its top sits <see cref="gap"/> below it. Without a ceiling above it stays
    /// where the prefab put it; it never goes below <see cref="minHeight"/> above its floor so it stays over people's
    /// heads.
    /// </summary>
    public sealed class CeilingMount : MonoBehaviour
    {
        [SerializeField] private float gap = 0.2f;
        [SerializeField] private float minHeight = 2.1f;
        [SerializeField] private float maxSearch = 6f;

        private void Start()
        {
            var floor = transform.parent != null ? transform.parent.position.y : 0f;
            var renderer = GetComponentInChildren<Renderer>();
            var halfHeight = renderer != null ? renderer.bounds.extents.y : 0.08f;
            var from = transform.position + Vector3.up * (halfHeight + 0.01f);
            if (!Physics.Raycast(from, Vector3.up, out var hit, maxSearch, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return;
            var centre = Mathf.Max(floor + minHeight, hit.point.y - gap - halfHeight);
            var offset = renderer != null ? renderer.bounds.center.y - transform.position.y : 0f;
            transform.position = new Vector3(transform.position.x, centre - offset, transform.position.z);
        }
    }
}
