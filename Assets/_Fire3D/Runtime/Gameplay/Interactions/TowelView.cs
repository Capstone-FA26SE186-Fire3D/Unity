using UnityEngine;

namespace Fire3D.Unity.Gameplay.Interactions
{
    /// <summary>
    /// Towel look by state: folded while it lies somewhere, hanging from the hand while held, darker once wet.
    /// </summary>
    public sealed class TowelView : MonoBehaviour
    {
        [SerializeField] private GameObject folded;
        [SerializeField] private GameObject draped;
        [SerializeField] private Color wetTint = new(0.5f, 0.52f, 0.6f);

        private Interactable _item;
        private Transform _hand;
        private Transform _handOwner;
        private MaterialPropertyBlock _block;
        private bool? _held;
        private bool? _wet;

        public void Bind(GameObject foldedVisual, GameObject drapedVisual)
        {
            folded = foldedVisual;
            draped = drapedVisual;
        }

        private void LateUpdate()
        {
            if (_item == null && !TryGetComponent(out _item))
                return;
            if (_held != _item.Held)
            {
                _held = _item.Held;
                if (folded != null)
                    folded.SetActive(!_item.Held);
                if (draped != null)
                    draped.SetActive(_item.Held);
            }

            if (_item.Held && draped != null)
                FollowHand();

            var wet = _item.State == "wet";
            if (_wet == wet)
                return;
            _wet = wet;
            _block ??= new MaterialPropertyBlock();
            foreach (var r in GetComponentsInChildren<Renderer>(true))
            {
                r.GetPropertyBlock(_block);
                _block.SetColor("_BaseColor", wet ? wetTint : Color.white);
                r.SetPropertyBlock(_block);
            }
        }

        /// <summary>
        /// Hangs the towel from the rigged right hand so it swings with the arm; stays upright (yaw only). Without a
        /// humanoid body it keeps hanging from the hand anchor.
        /// </summary>
        private void FollowHand()
        {
            if (_handOwner != transform.root)
            {
                _handOwner = transform.root;
                var animator = _handOwner.GetComponentInChildren<Animator>();
                _hand = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
            }

            if (_hand == null)
                return;
            draped.transform.SetPositionAndRotation(_hand.position + Vector3.down * 0.03f, Quaternion.Euler(0f, _handOwner.eulerAngles.y, 0f));
        }
    }
}
