using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Fire3D.Unity.Presentation.UI
{
    /// <summary>
    /// Round mobile-shooter style action button: glass disc, icon, label chip, radial hold ring, recommended glow.
    /// Pops in when its action changes, dips to 0.9× while pressed (60 ms) and springs back (ease-out).
    /// </summary>
    public sealed class HudButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        private RectTransform _rt;
        private Image _disc, _rim, _progress, _glow, _icon, _labelBg;
        private Text _label;
        private float _scale = 1f;
        private float _pop = 1f;
        private bool _down;
        private bool _recommended;
        private bool _toggled;
        private string _key;
        private Color _tint = UiKit.Text;

        public event Action Down;
        public event Action Up;

        public RectTransform Rect => _rt;
        public bool IsDown => _down;

        public static HudButton Create(Transform parent, string name, Vector2 anchor, Vector2 pos, float diameter, bool showLabel = true)
        {
            var rt = UiKit.At(name, parent, anchor, pos, Vector2.one * diameter);
            var b = rt.gameObject.AddComponent<HudButton>();
            b._rt = rt;
            b._glow = UiKit.Image(UiKit.Centered("Glow", rt, Vector2.zero, Vector2.one * diameter * 1.9f), new Color(1f, 0.5f, 0.15f, 0f), UiKit.Glow);
            b._disc = UiKit.Image(UiKit.Stretch("Disc", rt), UiKit.Glass, UiKit.Circle);
            b._disc.raycastTarget = true;
            b._rim = UiKit.Image(UiKit.Stretch("Rim", rt), UiKit.Stroke, UiKit.RingThin);
            b._progress = UiKit.Image(UiKit.Stretch("Progress", rt, -2f), UiKit.Accent, UiKit.Ring);
            b._progress.type = Image.Type.Filled;
            b._progress.fillMethod = Image.FillMethod.Radial360;
            b._progress.fillOrigin = (int)Image.Origin360.Top;
            b._progress.fillClockwise = true;
            b._progress.fillAmount = 0f;
            b._icon = UiKit.IconImage(UiKit.Centered("Icon", rt, Vector2.zero, Vector2.one * diameter * 0.46f), null, UiKit.Text);
            if (showLabel)
            {
                var chip = UiKit.Rect("LabelChip", rt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -6f),
                    new Vector2(Mathf.Max(150f, diameter * 1.25f), 36f));
                b._labelBg = UiKit.Image(chip, new Color(0f, 0f, 0f, 0.5f), UiKit.Pill, true);
                b._label = UiKit.Label(UiKit.Stretch("Text", chip, 6f), "", 21, UiKit.Text, TextAnchor.MiddleCenter, UiKit.SemiBold);
                b._label.resizeTextForBestFit = true;
                b._label.resizeTextMinSize = 14;
                b._label.resizeTextMaxSize = 21;
            }

            return b;
        }

        /// <summary>Sets icon and label; a different <paramref name="key"/> plays the pop-in.</summary>
        public void SetContent(string key, string icon, string label)
        {
            if (_key != key)
            {
                _key = key;
                _pop = 0f;
                _icon.sprite = UiKit.Icon(icon);
            }

            if (_label != null && _label.text != label)
                _label.text = label;
        }

        public void SetProgress(float progress01) => _progress.fillAmount = Mathf.Clamp01(progress01);

        public void SetRecommended(bool on) => _recommended = on;

        /// <summary>Latched state (crouched, nose covered): accent disc.</summary>
        public void SetToggled(bool on) => _toggled = on;

        /// <summary>Icon tint, e.g. danger red for a hot door.</summary>
        public void SetTint(Color c) => _tint = c;

        public void OnPointerDown(PointerEventData e)
        {
            if (_down)
                return;
            _down = true;
            Haptics.Tap();
            Down?.Invoke();
        }

        public void OnPointerUp(PointerEventData e) => Release();

        public void OnPointerExit(PointerEventData e)
        {
            // Sliding a finger off a hold button cancels it, like releasing.
            if (_down && e.pointerPress == gameObject)
                Release();
        }

        private void Release()
        {
            if (!_down)
                return;
            _down = false;
            Up?.Invoke();
        }

        private void OnDisable()
        {
            if (_down)
                Release();
            _pop = 0f;
        }

        private void Update()
        {
            var dt = Time.unscaledDeltaTime;
            var target = _down ? 0.9f : 1f;
            _scale = Mathf.Lerp(_scale, target, 1f - Mathf.Exp((_down ? -40f : -16f) * dt));
            _pop = Mathf.MoveTowards(_pop, 1f, dt / 0.22f);
            var pop = EaseOutBack(_pop);
            _rt.localScale = Vector3.one * (_scale * Mathf.Lerp(0.72f, 1f, pop));

            var pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 5.2f);
            _glow.color = new Color(1f, 0.5f, 0.15f, _recommended ? 0.35f + 0.3f * pulse : 0f);
            _rim.color = _recommended ? Color.Lerp(UiKit.Accent, Color.white, pulse * 0.4f) : UiKit.Stroke;
            _disc.color = _down ? new Color(0.25f, 0.13f, 0.06f, 0.82f) : _toggled ? new Color(0.55f, 0.24f, 0.07f, 0.78f) : UiKit.Glass;
            _icon.color = new Color(_tint.r, _tint.g, _tint.b, Mathf.Lerp(0f, 1f, _pop * 1.4f));
            if (_labelBg != null)
                _labelBg.color = _recommended ? new Color(0.65f, 0.26f, 0.05f, 0.85f) : new Color(0f, 0f, 0f, 0.5f);
        }

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            t -= 1f;
            return 1f + c3 * t * t * t + c1 * t * t;
        }
    }
}
