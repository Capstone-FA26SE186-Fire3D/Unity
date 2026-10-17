using Fire3D.Unity.Content;
using Fire3D.Unity.Gameplay.Cameras;
using Fire3D.Unity.Gameplay.Controls;
using Fire3D.Unity.Gameplay.Interactions;
using UnityEngine;

namespace Fire3D.Unity.Gameplay.Player
{
    /// <summary>Creates the player (controller, vitals, input, interactions) and its third-person camera.</summary>
    public static class PlayerRigFactory
    {
        public static GameObject Create(RuntimeAssets assets)
        {
            var player = new GameObject("Player");
            var cc = player.AddComponent<CharacterController>();
            cc.radius = 0.28f;
            cc.height = 1.75f;
            cc.center = new Vector3(0f, 0.875f, 0f);
            cc.stepOffset = 0.32f;
            cc.slopeLimit = 48f;
            cc.skinWidth = 0.03f;
            cc.minMoveDistance = 0f;

            Transform visual;
            if (assets != null && assets.PlayerPrefab != null)
            {
                visual = Object.Instantiate(assets.PlayerPrefab, player.transform).transform;
            }
            else
            {
                visual = PrototypeFactory.Person("Visual", new Color(0.95f, 0.45f, 0.1f)).transform;
                visual.SetParent(player.transform, false);
            }

            var hand = new GameObject("Hand").transform;
            hand.SetParent(player.transform, false);
            hand.localPosition = new Vector3(0.28f, 0.95f, 0.32f);

            player.AddComponent<GameplayInputReader>();
            player.AddComponent<PlayerVitals>();
            var motor = player.AddComponent<PlayerMotor>();
            var interaction = player.AddComponent<InteractionController>();
            SetPrivate(interaction, "handAnchor", hand);
            SetPrivate(motor, "visual", visual);

            var cam = UnityEngine.Camera.main;
            GameObject camGo;
            if (cam == null)
            {
                camGo = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = camGo.AddComponent<UnityEngine.Camera>();
                camGo.AddComponent<AudioListener>();
            }
            else
            {
                camGo = cam.gameObject;
            }

            cam.fieldOfView = 62f;
            cam.farClipPlane = 250f;
            var rig = camGo.GetComponent<ThirdPersonCameraRig>();
            if (rig == null)
                rig = camGo.AddComponent<ThirdPersonCameraRig>();
            rig.Target = player.transform;
            rig.InputSource = player.GetComponent<GameplayInputReader>();
            motor.CameraRig = rig;
            motor.InputSource = rig.InputSource;
            return player;
        }

        private static void SetPrivate(object target, string field, object value)
        {
            var f = target.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            f?.SetValue(target, value);
        }
    }
}
