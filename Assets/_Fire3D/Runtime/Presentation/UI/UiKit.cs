using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Fire3D.Unity.Presentation.UI
{
    /// <summary>
    /// Code-built uGUI toolkit for the mobile HUD: theme colours, Be Vietnam Pro (same family as the Mobile app),
    /// anti-aliased procedural sprites (circle, ring, rounded rect, soft glow, edge vignette) and Lucide icons
    /// from Resources/UI/Icons.
    /// </summary>
    public static class UiKit
    {
        // Dark glass surfaces over a bright 3D scene, one warm accent (fire safety orange), semantic colours.
        public static readonly Color Glass = new(0.06f, 0.07f, 0.09f, 0.58f);
        public static readonly Color GlassStrong = new(0.06f, 0.07f, 0.09f, 0.82f);
        public static readonly Color Card = new(0.08f, 0.09f, 0.11f, 0.96f);
        public static readonly Color Stroke = new(1f, 1f, 1f, 0.16f);
        public static readonly Color Text = new(0.97f, 0.97f, 0.98f);
        public static readonly Color Muted = new(0.74f, 0.76f, 0.8f);
        public static readonly Color Accent = new(1f, 0.45f, 0.13f);
        public static readonly Color AccentSoft = new(1f, 0.45f, 0.13f, 0.28f);
        public static readonly Color Good = new(0.33f, 0.86f, 0.5f);
        public static readonly Color Danger = new(1f, 0.3f, 0.24f);
        public static readonly Color Warning = new(1f, 0.74f, 0.2f);
        public static readonly Color Hint = new(0.36f, 0.72f, 1f);
        public static readonly Color Smoke = new(0.72f, 0.74f, 0.78f);

        private static Font _regular, _semibold, _bold, _black;
        private static Sprite _circle, _ring, _ringThin, _rounded, _glow, _vignette, _pill;
        private static readonly Dictionary<string, Sprite> Icons = new();

        public static Font Regular => _regular != null ? _regular : _regular = LoadFont("BeVietnamPro_500Medium");
        public static Font SemiBold => _semibold != null ? _semibold : _semibold = LoadFont("BeVietnamPro_600SemiBold");
        public static Font Bold => _bold != null ? _bold : _bold = LoadFont("BeVietnamPro_700Bold");
        public static Font Black => _black != null ? _black : _black = LoadFont("BeVietnamPro_800ExtraBold");

        private static Font LoadFont(string name)
        {
            var f = Resources.Load<Font>("UI/Fonts/" + name);
            return f != null ? f : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        /// <summary>Lucide icon by HUD name (see Tools/UiIcons/build-icons.mjs); null when missing.</summary>
        public static Sprite Icon(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;
            if (Icons.TryGetValue(name, out var s) && s != null)
                return s;
            s = Resources.Load<Sprite>("UI/Icons/" + name);
            if (s == null)
            {
                var tex = Resources.Load<Texture2D>("UI/Icons/" + name);
                if (tex != null)
                    s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }

            Icons[name] = s;
            return s;
        }

        // ---- Layout --------------------------------------------------------------------------------------------

        public static RectTransform Rect(string name, Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 anchoredPos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = pivot;
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;
            return rt;
        }

        /// <summary>Rect anchored at one point (anchor = pivot).</summary>
        public static RectTransform At(string name, Transform parent, Vector2 anchor, Vector2 pos, Vector2 size) =>
            Rect(name, parent, anchor, anchor, anchor, pos, size);

        public static RectTransform Centered(string name, Transform parent, Vector2 pos, Vector2 size) =>
            Rect(name, parent, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), pos, size);

        public static RectTransform Stretch(string name, Transform parent, float inset = 0f) =>
            Rect(name, parent, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-inset * 2f, -inset * 2f));

        // ---- Graphics ------------------------------------------------------------------------------------------

        public static Image Image(RectTransform rt, Color color, Sprite sprite = null, bool sliced = false)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
            if (sprite != null)
            {
                img.sprite = sprite;
                img.type = sliced ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            }

            return img;
        }

        public static Image Panel(RectTransform rt, Color color) => Image(rt, color, Rounded, true);

        public static Image IconImage(RectTransform rt, string icon, Color color)
        {
            var img = Image(rt, color, Icon(icon));
            img.preserveAspect = true;
            return img;
        }

        public static Text Label(RectTransform rt, string text, int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft,
            Font font = null)
        {
            var t = rt.gameObject.AddComponent<Text>();
            t.font = font != null ? font : Regular;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.supportRichText = true;
            t.raycastTarget = false;
            return t;
        }

        /// <summary>Text with a soft drop shadow for legibility over the 3D scene.</summary>
        public static Text Shadowed(Text t, float alpha = 0.55f)
        {
            var s = t.gameObject.AddComponent<Shadow>();
            s.effectColor = new Color(0f, 0f, 0f, alpha);
            s.effectDistance = new Vector2(0f, -2f);
            return t;
        }

        /// <summary>Text button for modal cards (briefing, pause, result).</summary>
        public static Button CardButton(Transform parent, string name, string text, string icon, Vector2 anchor, Vector2 pos, Vector2 size,
            bool primary)
        {
            var rt = At(name, parent, anchor, pos, size);
            var img = Image(rt, primary ? Accent : new Color(1f, 1f, 1f, 0.08f), Pill, true);
            img.raycastTarget = true;
            if (!primary)
            {
                var o = rt.gameObject.AddComponent<Outline>();
                o.effectColor = Stroke;
                o.effectDistance = new Vector2(1.5f, -1.5f);
            }

            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var colors = b.colors;
            colors.pressedColor = new Color(0.82f, 0.82f, 0.82f, 1f);
            colors.fadeDuration = 0.06f;
            b.colors = colors;
            rt.gameObject.AddComponent<PressScale>();

            var row = Stretch("Content", rt);
            var hasIcon = Icon(icon) != null;
            if (hasIcon)
                IconImage(Rect("Icon", row, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(-size.x * 0.5f + size.y * 0.55f, 0f), Vector2.one * size.y * 0.42f), icon, primary ? Color.white : Text);
            var label = Label(Rect("Label", row, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(hasIcon ? size.y * 0.2f : 0f, 0f), Vector2.zero),
                text, Mathf.RoundToInt(size.y * 0.34f), primary ? Color.white : Text, TextAnchor.MiddleCenter, Bold);
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 16;
            label.resizeTextMaxSize = label.fontSize;
            return b;
        }

        /// <summary>Thin rounded progress bar; returns the fill (fillAmount 0..1).</summary>
        public static Image Bar(Transform parent, string name, Vector2 anchor, Vector2 pos, Vector2 size, Color fill, out Image ghost)
        {
            var track = At(name, parent, anchor, pos, size);
            Image(track, new Color(0f, 0f, 0f, 0.45f), Pill, true);
            ghost = Image(Stretch("Ghost", track, 2f), new Color(1f, 1f, 1f, 0.55f), Pill, false);
            ghost.type = UnityEngine.UI.Image.Type.Filled;
            ghost.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
            var img = Image(Stretch("Fill", track, 2f), fill, Pill, false);
            img.type = UnityEngine.UI.Image.Type.Filled;
            img.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
            img.fillAmount = 1f;
            return img;
        }

        // ---- Procedural sprites (generated once, anti-aliased by distance) --------------------------------------

        public static Sprite Circle => _circle != null ? _circle : _circle = Disc(256, 0f, 1f);
        public static Sprite Ring => _ring != null ? _ring : _ring = Disc(256, 0.86f, 1f);
        public static Sprite RingThin => _ringThin != null ? _ringThin : _ringThin = Disc(256, 0.94f, 1f);
        public static Sprite Rounded => _rounded != null ? _rounded : _rounded = RoundedRect(64, 22f);
        public static Sprite Pill => _pill != null ? _pill : _pill = RoundedRect(64, 31.5f);
        public static Sprite Glow => _glow != null ? _glow : _glow = Radial(128, false);
        public static Sprite Vignette => _vignette != null ? _vignette : _vignette = Radial(256, true);

        private static Sprite Disc(int n, float inner, float outer)
        {
            var tex = NewTexture(n, "Disc");
            var c = (n - 1) * 0.5f;
            var px = 1f / c;
            var pixels = new Color32[n * n];
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                var a = Mathf.Clamp01((outer - d) / px + 0.5f);
                if (inner > 0f)
                    a *= Mathf.Clamp01((d - inner) / px + 0.5f);
                pixels[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }

            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }

        private static Sprite RoundedRect(int n, float r)
        {
            var tex = NewTexture(n, "RoundedRect");
            var pixels = new Color32[n * n];
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var dx = Mathf.Max(0f, Mathf.Max(r - x - 0.5f, x + 0.5f - (n - r)));
                var dy = Mathf.Max(0f, Mathf.Max(r - y - 0.5f, y + 0.5f - (n - r)));
                var a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                pixels[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }

            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(r, r, r, r));
        }

        /// <summary>Soft radial glow (bright centre) or edge vignette (clear centre, dark edges).</summary>
        private static Sprite Radial(int n, bool vignette)
        {
            var tex = NewTexture(n, vignette ? "Vignette" : "Glow");
            var c = (n - 1) * 0.5f;
            var pixels = new Color32[n * n];
            for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                var a = vignette ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 1.25f, d)) : Mathf.Pow(Mathf.Clamp01(1f - d), 2.2f);
                pixels[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }

            tex.SetPixels32(pixels);
            tex.Apply(true, true);
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
        }

        private static Texture2D NewTexture(int n, string name) =>
            new(n, n, TextureFormat.RGBA32, true) { name = name, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
    }

    /// <summary>Press feedback for card buttons: 0.95× in 60 ms, back with ease-out.</summary>
    public sealed class PressScale : MonoBehaviour, UnityEngine.EventSystems.IPointerDownHandler, UnityEngine.EventSystems.IPointerUpHandler
    {
        private float _target = 1f;

        public void OnPointerDown(UnityEngine.EventSystems.PointerEventData e)
        {
            _target = 0.95f;
            Haptics.Tap();
        }

        public void OnPointerUp(UnityEngine.EventSystems.PointerEventData e) => _target = 1f;

        private void Update()
        {
            var s = transform.localScale.x;
            var k = _target < s ? 1f - Mathf.Exp(-40f * Time.unscaledDeltaTime) : 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime);
            transform.localScale = Vector3.one * Mathf.Lerp(s, _target, k);
        }
    }
}
