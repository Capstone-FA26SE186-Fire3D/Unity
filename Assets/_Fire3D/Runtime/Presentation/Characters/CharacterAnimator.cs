using UnityEngine;

namespace Fire3D.Unity.Presentation
{
    /// <summary>
    /// Drives the shared humanoid Animator (Fire3D/Characters/Humanoid.controller) for the player and NPC views:
    /// ground speed, stance (stand / crouch / crawl), lying down, and a right-hand IK target for touching or pushing
    /// things (door leaves, handles, buttons). Gameplay sets the values; this component only animates.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public sealed class CharacterAnimator : MonoBehaviour
    {
        public static readonly int SpeedId = Animator.StringToHash("Speed");
        public static readonly int StanceId = Animator.StringToHash("Stance");
        public static readonly int MoveMulId = Animator.StringToHash("MoveMul");
        public static readonly int DownId = Animator.StringToHash("Down");
        public static readonly int StrideMulId = Animator.StringToHash("StrideMul");

        private float _stride;

        /// <summary>Speed (m/s) at which the crouch-walk and crawl clips play at their authored rate.</summary>
        [SerializeField] private float crouchClipSpeed = 0.85f;
        [SerializeField] private float crawlClipSpeed = 0.5f;
        [SerializeField] private float speedDamping = 0.12f;

        private Animator _animator;
        private Vector3 _handTarget;
        private float _handWeight;

        /// <summary>Planar speed in m/s.</summary>
        public float Speed { get; set; }

        /// <summary>0 = standing, 1 = crouched, 2 = crawling.</summary>
        public int Stance { get; set; }

        /// <summary>Lying on the floor (unconscious).</summary>
        public bool Down { get; set; }

        public Animator Animator => _animator != null ? _animator : _animator = GetComponent<Animator>();

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _animator.applyRootMotion = false;
        }

        private void Update()
        {
            var a = Animator;
            if (a == null || a.runtimeAnimatorController == null)
                return;
            a.SetFloat(SpeedId, Speed, speedDamping, Time.deltaTime);
            a.SetInteger(StanceId, Stance);
            a.SetBool(DownId, Down);
            // Clips are authored for a ~1.75 m adult; a child covers the same ground with more, faster steps.
            if (_stride <= 0f)
                _stride = a.isHuman && a.humanScale > 0.3f ? Mathf.Clamp(1f / a.humanScale, 0.8f, 1.6f) : 1f;
            a.SetFloat(StrideMulId, _stride);
            // Crouch-walk and crawl hold their pose when standing still and match the feet to the ground speed when moving.
            var clipSpeed = Stance == 2 ? crawlClipSpeed : crouchClipSpeed;
            a.SetFloat(MoveMulId, Stance == 0 ? 1f : Mathf.Clamp(Speed / clipSpeed * _stride, 0f, 1.8f));
        }

        /// <summary>Right hand toward <paramref name="worldPoint"/> with <paramref name="weight"/> (0 releases it).</summary>
        public void SetHandTarget(Vector3 worldPoint, float weight)
        {
            _handTarget = worldPoint;
            _handWeight = Mathf.Clamp01(weight);
        }

        private void OnAnimatorIK(int layerIndex)
        {
            var a = Animator;
            a.SetIKPositionWeight(AvatarIKGoal.RightHand, _handWeight);
            a.SetIKRotationWeight(AvatarIKGoal.RightHand, _handWeight * 0.6f);
            if (_handWeight <= 0f)
                return;
            a.SetIKPosition(AvatarIKGoal.RightHand, _handTarget);
            // Palm toward the surface: fingers point from the shoulder to the target.
            var shoulder = a.GetBoneTransform(HumanBodyBones.RightUpperArm);
            if (shoulder != null)
            {
                var dir = _handTarget - shoulder.position;
                if (dir.sqrMagnitude > 1e-4f)
                    a.SetIKRotation(AvatarIKGoal.RightHand, Quaternion.LookRotation(dir, Vector3.up) * Quaternion.Euler(0f, 0f, -90f));
            }

            a.SetLookAtWeight(_handWeight * 0.7f, 0.1f, 0.6f);
            a.SetLookAtPosition(_handTarget);
        }
    }
}
