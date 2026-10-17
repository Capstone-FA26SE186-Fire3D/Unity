using Fire3D.Simulation;
using Unity.Collections;
using UnityEngine;

namespace Fire3D.Unity.Presentation.UI
{
    /// <summary>
    /// Guided mode only: a floor arrow pointing along the risk-aware A* route to the nearest exit, replanned every
    /// second. Never created in Assessment.
    /// </summary>
    public sealed class GuidanceArrow : MonoBehaviour
    {
        private FireSimulation _sim;
        private Transform _player;
        private Transform _arrow;
        private float _nextPlan;
        private Vector3 _waypoint;
        private bool _hasRoute;

        public void Bind(FireSimulation simulation, Transform player)
        {
            _sim = simulation;
            _player = player;
            _arrow = BuildArrow();
        }

        private Transform BuildArrow()
        {
            var root = new GameObject("GuidanceArrow").transform;
            root.SetParent(transform, false);
            var mat = Content.PrototypeFactory.Lit(new Color(0.2f, 0.85f, 1f), 0.2f, 0f, new Color(0.1f, 0.6f, 1f) * 1.8f);
            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(shaft.GetComponent<Collider>());
            shaft.transform.SetParent(root, false);
            shaft.transform.localPosition = new Vector3(0f, 0f, 0.15f);
            shaft.transform.localScale = new Vector3(0.12f, 0.02f, 0.5f);
            shaft.GetComponent<Renderer>().sharedMaterial = mat;
            for (var s = -1; s <= 1; s += 2)
            {
                var head = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(head.GetComponent<Collider>());
                head.transform.SetParent(root, false);
                head.transform.localPosition = new Vector3(s * 0.09f, 0f, 0.42f);
                head.transform.localRotation = Quaternion.Euler(0f, -s * 40f, 0f);
                head.transform.localScale = new Vector3(0.1f, 0.02f, 0.3f);
                head.GetComponent<Renderer>().sharedMaterial = mat;
            }

            return root;
        }

        private void Update()
        {
            if (_sim == null || _player == null)
                return;
            if (Time.time >= _nextPlan)
            {
                _nextPlan = Time.time + 1f;
                Plan();
            }

            _arrow.gameObject.SetActive(_hasRoute);
            if (!_hasRoute)
                return;
            var dir = _waypoint - _player.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f)
                return;
            _arrow.position = _player.position + Vector3.up * 0.06f + dir.normalized * 0.9f;
            _arrow.rotation = Quaternion.Slerp(_arrow.rotation, Quaternion.LookRotation(dir.normalized), 1f - Mathf.Exp(-10f * Time.deltaTime));
        }

        private void Plan()
        {
            var grid = _sim.Grid;
            var view = new GridNavView
            {
                Layout = grid.Layout,
                Weights = NavigationWeights.Default,
                AmbientC = HazardSettings.Default.AmbientC,
                Flags = grid.Flags,
                Fire = grid.Fire,
                Smoke = grid.Smoke,
                Heat = grid.Heat,
            };
            var path = new NativeList<int>(Allocator.Temp);
            _hasRoute = GridPathfinder.FindPath(view, grid.Layout.CellAtClamped(_player.position), -1, path) && path.Length > 1;
            if (_hasRoute)
                _waypoint = grid.Layout.CellCenter(path[Mathf.Min(path.Length - 1, 4)]);
            path.Dispose();
        }
    }
}
