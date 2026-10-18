using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Fire3D.Unity.Editor.ContentPipeline
{
    /// <summary>
    /// Characters (Art/Characters/&lt;Name&gt;/&lt;Name&gt;.fbx, rigged with Tripo's "Unity Humanoid Compatible" skeleton) and the
    /// shared Mixamo clips (Art/Characters/Animations, downloaded "Without Skin") import as Humanoid so every clip
    /// retargets onto every character. Locomotion clips loop and stay in place: gameplay code moves the character.
    /// </summary>
    public sealed class CharacterAssetImporter : AssetPostprocessor
    {
        private const string CharacterRoot = "Assets/_Fire3D/Art/Characters/";
        private const string AnimationFolder = CharacterRoot + "Animations/";

        // Clips that repeat while the state lasts.
        private static readonly string[] LoopingClips =
        {
            "Idle", "Walking", "Running", "Sprint", "Jogging", "CrouchWalk", "CrouchedSneakingLeft", "CrouchedSneakingRight",
            "SneakingForward", "Crawling", "LowCrawl", "FallingIdle", "StandingW_BriefcaseIdle", "SlowJogBackwards",
            "WrithingInPain", "InjuredWalk", "Terrified",
        };

        private void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(CharacterRoot, StringComparison.Ordinal) || !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                return;

            var importer = (ModelImporter)assetImporter;
            var isClip = assetPath.StartsWith(AnimationFolder, StringComparison.Ordinal);
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.useFileScale = true;
            importer.globalScale = 1f;
            importer.importBlendShapes = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importAnimation = isClip;
            if (isClip)
            {
                // Skeleton-only files: no mesh or materials to bring in.
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.animationCompression = ModelImporterAnimationCompression.Optimal;
                importer.resampleCurves = true;
            }
            else
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
                importer.isReadable = false;
                importer.optimizeGameObjects = false; // gameplay reaches into hand bones for held items and IK
                importer.meshCompression = ModelImporterMeshCompression.Medium;
            }
        }

        private void OnPreprocessAnimation()
        {
            if (!assetPath.StartsWith(AnimationFolder, StringComparison.Ordinal))
                return;

            var importer = (ModelImporter)assetImporter;
            var fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
            var loop = LoopingClips.Contains(fileName);
            var clips = importer.defaultClipAnimations;
            foreach (var clip in clips)
            {
                clip.name = fileName;
                clip.loopTime = loop;
                clip.loopPose = loop;
                // In place: root rotation and height follow the body, horizontal travel is left to the CharacterController.
                clip.lockRootRotation = true;
                clip.keepOriginalOrientation = true;
                clip.lockRootHeightY = true;
                clip.keepOriginalPositionY = true;
                clip.lockRootPositionXZ = true;
                clip.keepOriginalPositionXZ = false;
            }

            importer.clipAnimations = clips;
        }
    }
}
