using UnityEngine;
using UnityEngine.UI;

namespace Fire3D.Unity.Presentation.UI
{
    /// <summary>Small helpers to build the uGUI HUD in code (no prefab or font assets required).</summary>
    public static class UiKit
    {
        public static readonly Color Panel = new(0.05f, 0.06f, 0.08f, 0.72f);
        public static readonly Color PanelSolid = new(0.07f, 0.08f, 0.1f, 0.94f);
        public static readonly Color Text = new(0.95f, 0.95f, 0.96f);
        public static readonly Color Muted = new(0.7f, 0.72f, 0.76f);
        public static readonly Color Accent = new(1f, 0.42f, 0.16f);
        public static readonly Color Good = new(0.3f, 0.85f, 0.45f);
        public static readonly Color Danger = new(0.95f, 0.25f, 0.2f);
        public static readonly Color Hint = new(0.3f, 0.75f, 1f);

        private static Font _font;
        private static Sprite _rounded;

        public static Font Font => _font != null ? _font : _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

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

        public static RectTransform Stretch(string name, Transform parent, float inset = 0f)
        {
            var rt = Rect(name, parent, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-inset * 2f, -inset * 2f));
            return rt;
        }

        public static Image Image(RectTransform rt, Color color, bool rounded = true)
        {
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            if (rounded)
            {
                img.sprite = Rounded;
                img.type = UnityEngine.UI.Image.Type.Sliced;
            }

            return img;
        }

        public static Text Label(RectTransform rt, string text, int size, Color color, TextAnchor anchor = TextAnchor.MiddleLeft,
            FontStyle style = FontStyle.Normal)
        {
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.fontStyle = style;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            return t;
        }

        public static Button Button(Transform parent, string name, string text, Vector2 anchor, Vector2 pos, Vector2 size,
            Color color, int fontSize = 30)
        {
            var rt = Rect(name, parent, anchor, anchor, new Vector2(0.5f, 0.5f), pos, size);
            var img = Image(rt, color);
            var b = rt.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var colors = b.colors;
            colors.highlightedColor = new Color(1f, 1f, 1f, 1f);
            colors.pressedColor = new Color(0.8f, 0.8f, 0.8f, 1f);
            b.colors = colors;
            var label = Label(Stretch("Label", rt, 8f), text, fontSize, Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            label.resizeTextForBestFit = true;
            label.resizeTextMinSize = 14;
            label.resizeTextMaxSize = fontSize;
            return b;
        }

        /// <summary>9-sliced rounded rectangle generated once.</summary>
        public static Sprite Rounded
        {
            get
            {
                if (_rounded != null)
                    return _rounded;
                const int n = 32;
                const float r = 12f;
                var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "RoundedRect", wrapMode = TextureWrapMode.Clamp };
                for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var dx = Mathf.Max(0f, Mathf.Max(r - x - 0.5f, x + 0.5f - (n - r)));
                    var dy = Mathf.Max(0f, Mathf.Max(r - y - 0.5f, y + 0.5f - (n - r)));
                    var a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }

                tex.Apply();
                _rounded = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                    new Vector4(r, r, r, r));
                return _rounded;
            }
        }

        /// <summary>Horizontal bar: returns the fill image (use fillAmount).</summary>
        public static Image Bar(Transform parent, string label, Vector2 pos, Color fill, out Text value)
        {
            var row = Rect(label, parent, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), pos, new Vector2(400f, 46f));
            Label(Rect("Name", row, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(150f, 0f)),
                label, 24, Muted);
            var bg = Rect("Track", row, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(150f, 0f),
                new Vector2(190f, 18f));
            Image(bg, new Color(1f, 1f, 1f, 0.12f));
            var fillRt = Stretch("Fill", bg);
            var img = Image(fillRt, fill);
            img.type = UnityEngine.UI.Image.Type.Filled;
            img.fillMethod = UnityEngine.UI.Image.FillMethod.Horizontal;
            img.fillAmount = 1f;
            value = Label(Rect("Value", row, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(348f, 0f),
                new Vector2(60f, 30f)), "", 20, Text, TextAnchor.MiddleRight);
            return img;
        }
    }
}
