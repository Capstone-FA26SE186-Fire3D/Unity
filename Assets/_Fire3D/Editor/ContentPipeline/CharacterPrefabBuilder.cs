using System.Collections.Generic;
using System.IO;
using System.Linq;
using Fire3D.Unity.Content;
using Fire3D.Unity.Presentation;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Builds the character set from Art/Characters: extracts each model's embedded textures, generates the shared
    /// humanoid Animator controller from the Mixamo clips, saves one prefab per character (Prefabs/Characters) and
    /// assigns the player/NPC variants in RuntimeAssets.
    /// </summary>
    public static class CharacterPrefabBuilder
    {
        public const string ArtRoot = "Assets/_Fire3D/Art/Characters";
        public const string ControllerPath = ArtRoot + "/Humanoid.controller";
        public const string PrefabDir = "Assets/_Fire3D/Prefabs/Characters";

        public static readonly string[] Players = { "PlayerMale", "PlayerFemale" };

        public static readonly string[] Npcs =
            { "NpcOfficeMale", "NpcOfficeFemale", "NpcSecurity", "NpcTechnician", "NpcCleaner", "NpcWorker", "NpcBoy", "NpcGirl" };

        // Player walk/run speeds (PlayerMotor) are the blend thresholds so the feet roughly match the ground.
        private const float WalkSpeed = 1.6f;
        private const float RunSpeed = 3.3f;

        [MenuItem("Fire3D/Characters/Build Prefabs")]
        public static void BuildMenu() => Debug.Log(Build());

        /// <summary>Last <see cref="Build"/> report, for callers that schedule <see cref="BuildDeferred"/>.</summary>
        public static string LastReport { get; private set; } = "";

        /// <summary>Runs <see cref="Build"/> on the next editor tick (outside a remote command's time limit).</summary>
        public static void BuildDeferred(bool tpose = true)
        {
            LastReport = "running";
            EditorApplication.delayCall += () =>
            {
                try { LastReport = Build(tpose); }
                catch (System.Exception e) { LastReport = "failed: " + e; }
            };
        }

        /// <param name="tpose">Also rewrite each avatar's T-pose (slow: ten re-imports); needed after a model changes.</param>
        public static string Build(bool tpose = true)
        {
            var log = new List<string>();
            foreach (var name in Players.Concat(Npcs))
            {
                ExtractTextures(name, log);
                if (tpose)
                    log.Add(CharacterTPose.Apply($"{ArtRoot}/{name}/{name}.fbx"));
            }

            var controller = BuildController();
            Directory.CreateDirectory(PrefabDir);
            var prefabs = new Dictionary<string, GameObject>();
            foreach (var name in Players.Concat(Npcs))
            {
                var prefab = BuildPrefab(name, controller, Players.Contains(name));
                if (prefab != null)
                    prefabs[name] = prefab;
                else
                    log.Add($"missing model {name}");
            }

            var assets = AssetDatabase.LoadAssetAtPath<RuntimeAssets>(RuntimeAssetsBuilder.AssetPath);
            if (assets != null)
            {
                assets.PlayerVariants = Players.Where(prefabs.ContainsKey).Select(n => prefabs[n]).ToList();
                assets.NpcVariants = Npcs.Where(prefabs.ContainsKey).Select(n => prefabs[n]).ToList();
                if (assets.PlayerVariants.Count > 0)
                    assets.PlayerPrefab = assets.PlayerVariants[0];
                if (assets.NpcVariants.Count > 0)
                    assets.NpcPrefab = assets.NpcVariants[0];
                EditorUtility.SetDirty(assets);
            }

            AssetDatabase.SaveAssets();
            log.Add($"prefabs {prefabs.Count}, controller {ControllerPath}");
            return string.Join("\n", log);
        }

        private static void ExtractTextures(string name, List<string> log)
        {
            var model = $"{ArtRoot}/{name}/{name}.fbx";
            var dir = $"{ArtRoot}/{name}/Textures";
            if (AssetDatabase.IsValidFolder(dir) || AssetImporter.GetAtPath(model) is not ModelImporter importer)
                return;
            Directory.CreateDirectory(dir);
            importer.ExtractTextures(dir);
            AssetDatabase.Refresh();
            // Re-import so the material finds the extracted file (matched by the texture's embedded name).
            AssetDatabase.ImportAsset(model, ImportAssetOptions.ForceUpdate);
            log.Add($"textures {name}");
        }

        private static AnimationClip Clip(string name) =>
            AssetDatabase.LoadAllAssetsAtPath($"{ArtRoot}/Animations/{name}.fbx").OfType<AnimationClip>()
                .FirstOrDefault(c => !c.name.StartsWith("__preview"));

        private static AnimatorController BuildController()
        {
            // Rebuild in place: deleting the asset would give it a new GUID and break every prefab that references it.
            var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath) ?? AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            c.parameters = new AnimatorControllerParameter[0];
            var root = c.layers[0].stateMachine;
            foreach (var t in root.anyStateTransitions)
                root.RemoveAnyStateTransition(t);
            foreach (var s in root.states)
                root.RemoveState(s.state);
            while (c.layers.Length > 1)
                c.RemoveLayer(c.layers.Length - 1);
            c.AddParameter("Speed", AnimatorControllerParameterType.Float);
            c.AddParameter("Stance", AnimatorControllerParameterType.Int);
            c.AddParameter("MoveMul", AnimatorControllerParameterType.Float);
            c.AddParameter("Down", AnimatorControllerParameterType.Bool);
            c.AddParameter("StrideMul", AnimatorControllerParameterType.Float);
            c.parameters = c.parameters.Select(p =>
            {
                if (p.name == "MoveMul" || p.name == "StrideMul")
                    p.defaultFloat = 1f;
                return p;
            }).ToArray();

            var layers = c.layers;
            layers[0].iKPass = true; // CharacterAnimator.OnAnimatorIK places the hand on doors and buttons.
            c.layers = layers;
            var sm = c.layers[0].stateMachine;

            var locomotion = c.CreateBlendTreeInController("Locomotion", out var tree, 0);
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            tree.AddChild(Clip("Idle"), 0f);
            tree.AddChild(Clip("Walking"), WalkSpeed);
            tree.AddChild(Clip("Jogging"), RunSpeed); // steadier than Mixamo "Running", which bounces with high knees
            // Shorter legs take more steps per metre: CharacterAnimator sets StrideMul from the avatar's scale.
            locomotion.speedParameterActive = true;
            locomotion.speedParameter = "StrideMul";
            sm.defaultState = locomotion;

            var crouch = sm.AddState("Crouch");
            crouch.motion = Clip("CrouchWalk");
            crouch.speedParameterActive = true;
            crouch.speedParameter = "MoveMul";
            var crawl = sm.AddState("Crawl");
            crawl.motion = Clip("Crawling");
            crawl.speedParameterActive = true;
            crawl.speedParameter = "MoveMul";
            var down = sm.AddState("Down");
            down.motion = Clip("Dying");

            void Enter(AnimatorState target, int stance)
            {
                var t = sm.AddAnyStateTransition(target);
                t.canTransitionToSelf = false;
                t.hasExitTime = false;
                t.duration = 0.25f;
                t.AddCondition(AnimatorConditionMode.Equals, stance, "Stance");
                t.AddCondition(AnimatorConditionMode.IfNot, 0, "Down");
            }

            Enter(locomotion, 0);
            Enter(crouch, 1);
            Enter(crawl, 2);
            var toDown = sm.AddAnyStateTransition(down);
            toDown.canTransitionToSelf = false;
            toDown.hasExitTime = false;
            toDown.duration = 0.3f;
            toDown.AddCondition(AnimatorConditionMode.If, 0, "Down");
            EditorUtility.SetDirty(c);
            return c;
        }

        private static GameObject BuildPrefab(string name, RuntimeAnimatorController controller, bool player)
        {
            var model = AssetDatabase.LoadAssetAtPath<GameObject>($"{ArtRoot}/{name}/{name}.fbx");
            if (model == null)
                return null;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(model);
            go.name = name;
            var animator = go.GetComponent<Animator>() ?? go.AddComponent<Animator>();
            animator.runtimeAnimatorController = controller;
            animator.applyRootMotion = false;
            // The player is always on screen (and in first person the body is hidden but the hand IK still matters).
            animator.cullingMode = player ? AnimatorCullingMode.AlwaysAnimate : AnimatorCullingMode.CullUpdateTransforms;
            if (go.GetComponent<CharacterAnimator>() == null)
                go.AddComponent<CharacterAnimator>();
            // Skinned bounds follow the hips bone; when the body crouches or lies down they sink under the floor and the
            // light probes sampled there are black. Anchor probes at chest height on the root and widen the bounds so a
            // low pose is neither unlit nor culled.
            var anchor = new GameObject("ProbeAnchor").transform;
            anchor.SetParent(go.transform, false);
            anchor.localPosition = new Vector3(0f, 1.0f, 0f);
            foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                r.updateWhenOffscreen = false;
                r.probeAnchor = anchor;
                var b = r.localBounds;
                b.Expand(new Vector3(1.2f, 1.0f, 1.2f));
                r.localBounds = b;
            }

            var path = $"{PrefabDir}/{name}.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(go, path);
            Object.DestroyImmediate(go);
            return prefab;
        }
    }
}
