using System.Collections.Generic;
using Fire3D.Contracts;

namespace Fire3D.Unity.Gameplay.Interactions
{
    public enum ActionSource
    {
        Target,
        HeldItem,
    }

    /// <summary>A button the action bar can show.</summary>
    public readonly struct AvailableAction
    {
        public readonly ActionSource Source;
        public readonly ActionDefinition Definition;
        public readonly string Label;
        public readonly bool Recommended;

        public AvailableAction(ActionSource source, ActionDefinition definition, string label, bool recommended)
        {
            Source = source;
            Definition = definition;
            Label = label;
            Recommended = recommended;
        }

        public string Id => Definition.Id;
        public bool IsHold => Definition.HoldSeconds > 0f;
    }

    /// <summary>Snapshot of what the player holds, for rule evaluation.</summary>
    public readonly struct HeldSnapshot
    {
        public readonly string CapabilityId;
        public readonly string State;
        public readonly string DisplayName;

        public HeldSnapshot(string capabilityId, string state, string displayName)
        {
            CapabilityId = capabilityId;
            State = state;
            DisplayName = displayName;
        }

        public bool IsEmpty => string.IsNullOrEmpty(CapabilityId);
        public static HeldSnapshot Empty => default;
    }

    /// <summary>
    /// Contextual action rules (Docs ifc_ingestion_visual_interaction.md section 7): buttons depend on the object in
    /// reach, what is held and object state; at most three buttons; traps show their button like anything else.
    /// Guided mode only adds a "recommended" flag; it never hides or adds actions.
    /// </summary>
    public static class ActionResolver
    {
        public const int MaxButtons = 3;

        public static List<AvailableAction> Resolve(string targetCapability, string targetState, string targetName,
            HeldSnapshot held, bool guided, string recommendedActionId = null)
        {
            var result = new List<AvailableAction>(MaxButtons);

            if (!string.IsNullOrEmpty(targetCapability))
            {
                foreach (var def in RuntimeCatalog.GetActions(targetCapability))
                {
                    if (def.UsesHeldItem)
                        continue;
                    if (def.RequiresState != null && def.RequiresState != targetState)
                        continue;
                    if (def.RequiresHeld.Length > 0 && !MatchesHeld(def.RequiresHeld, held))
                        continue;

                    var label = def.Label;
                    if (def.Id == "pick_up" && !held.IsEmpty)
                        label = $"Đổi lấy {targetName ?? "vật này"}";
                    else if (def.Id == "wet_item" && held.State == "wet")
                        label = "Nhúng lại";

                    result.Add(new AvailableAction(ActionSource.Target, def, label, guided && def.Id == recommendedActionId));
                }
            }

            if (!held.IsEmpty)
            {
                foreach (var def in RuntimeCatalog.GetActions(held.CapabilityId))
                {
                    if (!def.UsesHeldItem)
                        continue;
                    if (def.RequiresHeldState != null && def.RequiresHeldState != held.State)
                        continue;
                    result.Add(new AvailableAction(ActionSource.HeldItem, def, def.Label, guided && def.Id == recommendedActionId));
                }
            }

            if (result.Count > MaxButtons)
            {
                // Keep the recommended action visible when trimming.
                result.Sort((a, b) => b.Recommended.CompareTo(a.Recommended));
                result.RemoveRange(MaxButtons, result.Count - MaxButtons);
            }

            return result;
        }

        private static bool MatchesHeld(string[] requirements, HeldSnapshot held)
        {
            if (held.IsEmpty)
                return false;
            foreach (var r in requirements)
            {
                var split = r.IndexOf(':');
                var cap = split < 0 ? r : r.Substring(0, split);
                var state = split < 0 ? null : r.Substring(split + 1);
                if (cap == held.CapabilityId && (state == null || state == held.State))
                    return true;
            }

            return false;
        }
    }
}
