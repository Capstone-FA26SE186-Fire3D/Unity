using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Writes a proper T-pose into a character's humanoid avatar. Tripo rigs are bound in an A-pose with unusual bone
    /// rolls, and Unity's automatic "enforce T-pose" leaves the arms pointing forward/down; every retargeted clip then
    /// inherits that offset (arms flung up, twisted knees, head off to the side). Here the T-pose is built from the bind
    /// pose by joint positions only: arms straight out sideways with palms down, fingers straight, legs straight down
    /// with the feet pointing forward, everything else as bound.
    /// </summary>
    public static class CharacterTPose
    {
        public static string Apply(string modelPath)
        {
            var importer = AssetImporter.GetAtPath(modelPath) as ModelImporter;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            if (importer == null || model == null)
                return $"{modelPath}: not a model";
            var hd = importer.humanDescription;
            if (hd.human == null || hd.human.Length == 0)
                return $"{modelPath}: no humanoid mapping yet";
            // Mixamo rigs are delivered in a true T-pose; Unity's own setup is right for them.
            if (hd.human.Any(h => h.boneName.StartsWith("mixamorig", System.StringComparison.OrdinalIgnoreCase)))
                return $"{System.IO.Path.GetFileNameWithoutExtension(modelPath)}: Mixamo rig, T-pose kept";

            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            try
            {
                var byHuman = hd.human.ToDictionary(h => h.humanName, h => h.boneName);
                // The FBX root and the armature can share the character's name; bones are deeper, so the last match wins.
                var all = new Dictionary<string, Transform>();
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    all[t.name] = t;
                Transform H(string human) => byHuman.TryGetValue(human, out var b) && all.TryGetValue(b, out var t) ? t : null;

                foreach (var side in new[] { ("Left", Vector3.left), ("Right", Vector3.right) })
                {
                    var (s, axis) = side;
                    var upper = H($"{s}UpperArm");
                    var lower = H($"{s}LowerArm");
                    var hand = H($"{s}Hand");
                    if (upper == null || lower == null || hand == null)
                        continue;
                    // Elbow hinge: with the arm out sideways the forearm should fold forward.
                    Align(upper, lower.position - upper.position, axis);
                    var bend = Vector3.ProjectOnPlane(hand.position - lower.position, axis);
                    if (bend.magnitude > 0.005f)
                        Roll(upper, axis, bend, Vector3.forward);
                    Align(lower, hand.position - lower.position, axis);
                    // Palm down: the index side of the hand is in front of the little-finger side.
                    var index = H($"{s} Index Proximal");
                    var little = H($"{s} Little Proximal");
                    var middle = H($"{s} Middle Proximal");
                    if (index != null && little != null)
                    {
                        var across = Vector3.ProjectOnPlane(index.position - little.position, axis);
                        if (across.magnitude > 0.005f)
                            Roll(hand, axis, across, Vector3.forward);
                    }

                    if (middle != null)
                        Align(hand, middle.position - hand.position, axis);
                    foreach (var finger in new[] { "Index", "Middle", "Ring", "Little" })
                    {
                        var chain = new[] { "Proximal", "Intermediate", "Distal" }.Select(p => H($"{s} {finger} {p}")).Where(t => t != null).ToList();
                        for (var i = 0; i < chain.Count; i++)
                        {
                            var next = i + 1 < chain.Count ? chain[i + 1] : chain[i].childCount > 0 ? chain[i].GetChild(0) : null;
                            if (next != null)
                                Align(chain[i], next.position - chain[i].position, axis);
                        }
                    }

                    var thigh = H($"{s}UpperLeg");
                    var shin = H($"{s}LowerLeg");
                    var foot = H($"{s}Foot");
                    var toes = H($"{s}Toes");
                    if (thigh == null || shin == null || foot == null)
                        continue;
                    Align(thigh, shin.position - thigh.position, Vector3.down);
                    if (toes != null)
                    {
                        // Knee hinge: the foot points forward, so the knee folds backward.
                        var fwd = Vector3.ProjectOnPlane(toes.position - foot.position, Vector3.up);
                        if (fwd.magnitude > 0.01f)
                            Roll(thigh, Vector3.down, fwd, Vector3.forward);
                    }

                    Align(shin, foot.position - shin.position, Vector3.down);
                    if (toes != null)
                    {
                        // Straight ahead, but keep the bound pitch: the foot bone runs from the ankle down to the ball of the
                        // foot, so flattening it would store a toes-up ankle and every clip would walk on its heels.
                        var fwd = toes.position - foot.position;
                        var flat = new Vector3(fwd.x, 0f, fwd.z).magnitude;
                        Align(foot, fwd, new Vector3(0f, fwd.y, flat));
                    }
                }

                var skeleton = go.GetComponentsInChildren<Transform>(true)
                    .Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale })
                    .ToArray();
                hd.skeleton = skeleton;
                importer.humanDescription = hd;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
                return $"{System.IO.Path.GetFileNameWithoutExtension(modelPath)}: T-pose written ({skeleton.Length} bones)";
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        /// <summary>Rotates <paramref name="bone"/> (children follow) so <paramref name="currentDir"/> becomes <paramref name="targetDir"/>.</summary>
        private static void Align(Transform bone, Vector3 currentDir, Vector3 targetDir)
        {
            if (currentDir.sqrMagnitude < 1e-8f)
                return;
            bone.rotation = Quaternion.FromToRotation(currentDir.normalized, targetDir.normalized) * bone.rotation;
        }

        /// <summary>Rotates <paramref name="bone"/> about <paramref name="axis"/> so <paramref name="currentDir"/> (⊥ axis) turns to <paramref name="targetDir"/>.</summary>
        private static void Roll(Transform bone, Vector3 axis, Vector3 currentDir, Vector3 targetDir)
        {
            var a = Vector3.ProjectOnPlane(currentDir, axis);
            var b = Vector3.ProjectOnPlane(targetDir, axis);
            if (a.sqrMagnitude < 1e-8f || b.sqrMagnitude < 1e-8f)
                return;
            bone.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(a, b, axis), axis) * bone.rotation;
        }
    }
}
