using System;
using System.Collections.Generic;
using UnityEngine;

namespace Fire3D.Unity.Gameplay.Interactions
{
    /// <summary>
    /// Goal volume (exit.final / exit.stair). Entering it sends the core <c>ExitReached</c> event; a blocked exit
    /// (BlockedElement in the scenario) sends <c>WrongExit</c> instead and the session continues.
    /// </summary>
    public sealed class ExitZone : MonoBehaviour
    {
        private static readonly List<ExitZone> Registry = new();

        [SerializeField] private string objectId;
        [SerializeField] private string capabilityId = "exit.final";
        [SerializeField] private Vector3 size = new(2f, 2.5f, 2f);
        [SerializeField] private bool blocked;

        private bool _inside;

        public string ObjectId { get => objectId; set => objectId = value; }
        public string CapabilityId { get => capabilityId; set => capabilityId = value; }
        public bool Blocked { get => blocked; set => blocked = value; }
        public Vector3 Size { get => size; set => size = value; }
        public Bounds WorldBounds => new(transform.position + Vector3.up * size.y * 0.5f, size);
        public static IReadOnlyList<ExitZone> All => Registry;

        public event Action<ExitZone> Entered;

        private void OnEnable() => Registry.Add(this);
        private void OnDisable() => Registry.Remove(this);

        /// <summary>Checks the player position; called by the session each frame.</summary>
        public void Check(Vector3 playerPosition)
        {
            var inside = WorldBounds.Contains(playerPosition + Vector3.up * 0.5f);
            if (inside && !_inside)
                Entered?.Invoke(this);
            _inside = inside;
        }

        private void OnDrawGizmos()
        {
            Gizmos.color = blocked ? new Color(1f, 0.3f, 0.2f, 0.4f) : new Color(0.2f, 1f, 0.4f, 0.4f);
            Gizmos.DrawCube(WorldBounds.center, WorldBounds.size);
        }
    }
}
