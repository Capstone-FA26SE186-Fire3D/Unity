using System;
using System.Collections.Generic;
using Fire3D.Contracts;
using Fire3D.Unity.Gameplay.Controls;
using Fire3D.Unity.Gameplay.Interactions;
using Fire3D.Unity.Gameplay.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Fire3D.Unity.Presentation.UI
{
    /// <summary>
    /// Mobile HUD in the PUBG/CoD Mobile layout family (landscape): floating joystick in the left thumb zone, a
    /// round action cluster in the right thumb zone, health at bottom centre, timer/objective at the top, a world
    /// prompt over the focused object and edge vignettes for heat/smoke. Values are training values, not medical.
    /// Assessment hides objectives and hint highlights (Docs section 8).
    /// </summary>
    public sealed class TrainingHud : MonoBehaviour
    {
        public sealed class Model
        {
            public string ScenarioName;
            public string Instructions;
            public IReadOnlyList<string> Objectives = Array.Empty<string>();
            public string Mode;
            public Func<double> Elapsed;
            public int? TimeLimitSeconds;
            public Action OnStart;
            public Action OnResume;
            public Action OnPause;
            public Action OnAbandon;
            public Action OnExit;
        }

        private sealed class ToastView
        {
            public RectTransform Root;
            public CanvasGroup Group;
            public Text Text;
            public Image Icon;
            public Image Bg;
            public float Born;
            public float Until;
        }

        private const int MaxToasts = 3;

        private Canvas _canvas;
        private RectTransform _root;
        private Model _model;
        private InteractionController _interaction;
        private PlayerVitals _vitals;
        private PlayerMotor _motor;
        private GameplayInputReader _input;

        private GameObject _briefing, _pause, _result, _hudRoot;
        private Text _resultText;

        private Image _health, _healthGhost, _stamina, _staminaGhost;
        private CanvasGroup _staminaGroup;
        private Text _healthValue;
        private RectTransform _smokeChip, _heatChip;
        private Text _smokeText, _heatText;
        private Image _vignette, _smokeVignette;

        private Text _timer, _objective;
        private RectTransform _objectivePanel;
        private Image _heldIcon, _heldRing;
        private Text _heldName, _heldStatus;

        private RectTransform _joyBase, _joyKnob, _joyGhost, _sprintChip;
        private CanvasGroup _joyGhostGroup;
        private bool _hasMoved;

        private readonly HudButton[] _actions = new HudButton[ActionResolver.MaxButtons];
        private HudButton _crouch, _pauseButton;

        private RectTransform _prompt;
        private CanvasGroup _promptGroup;
        private Text _promptName;
        private Image _promptIcon;
        private Interactable _promptTarget;
        private float _promptAlpha;

        private readonly List<ToastView> _toasts = new();
        private readonly List<RectTransform> _touchBlockers = new();
        private float _shownHealth = 1f;

        public bool Paused => _pause != null && _pause.activeSelf;

        public void Build(Model model, InteractionController interaction, PlayerVitals vitals, PlayerMotor motor, GameplayInputReader input)
        {
            _model = model;
            _interaction = interaction;
            _vitals = vitals;
            _motor = motor;
            _input = input;
            EnsureEventSystem();

            _canvas = gameObject.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 10;
            _canvas.pixelPerfect = false;
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 1f; // Landscape phones: scale by height so thumbs zones keep their size.
            gameObject.AddComponent<GraphicRaycaster>();

            var full = UiKit.Stretch("Screen", transform);
            _vignette = UiKit.Image(UiKit.Stretch("HeatVignette", full, -40f), new Color(0.75f, 0.08f, 0f, 0f), UiKit.Vignette);
            _smokeVignette = UiKit.Image(UiKit.Stretch("SmokeVignette", full, -40f), new Color(0.2f, 0.2f, 0.22f, 0f), UiKit.Vignette);

            _root = UiKit.Stretch("SafeArea", transform);
            ApplySafeArea(_root);

            _hudRoot = UiKit.Stretch("Hud", _root).gameObject;
            var hud = _hudRoot.transform;
            BuildTop(hud);
            BuildVitals(hud);
            BuildJoystick(hud);
            BuildActions(hud);
            BuildPrompt(hud);

            _briefing = BuildBriefing(_root);
            _pause = BuildPause(_root);
            _result = BuildResult(_root);
            _pause.SetActive(false);
            _result.SetActive(false);
            _hudRoot.SetActive(false);

            if (_input != null)
                _input.IsOverUi = IsOverButton;
        }

        public void ShowRunning()
        {
            _briefing.SetActive(false);
            _pause.SetActive(false);
            _hudRoot.SetActive(true);
        }

        public void ShowPause(bool on) => _pause.SetActive(on);

        public void ShowResult(string text)
        {
            _hudRoot.SetActive(false);
            _pause.SetActive(false);
            _resultText.text = text;
            _result.SetActive(true);
        }

        public void Toast(string text, MessageTone tone)
        {
            if (string.IsNullOrEmpty(text) || _hudRoot == null)
                return;
            var duration = tone == MessageTone.Hint ? 4.5f : tone == MessageTone.Danger ? 4f : 3f;
            foreach (var t in _toasts)
            {
                if (t.Text.text == text)
                {
                    t.Until = Time.unscaledTime + duration; // Same message again: keep one, extend it.
                    return;
                }
            }

            if (tone == MessageTone.Danger)
                Haptics.Warn();
            while (_toasts.Count >= MaxToasts)
            {
                Destroy(_toasts[0].Root.gameObject);
                _toasts.RemoveAt(0);
            }

            var (bg, icon) = tone switch
            {
                MessageTone.Danger => (new Color(0.62f, 0.09f, 0.06f, 0.92f), "warning"),
                MessageTone.Warning => (new Color(0.6f, 0.36f, 0.04f, 0.92f), "warning"),
                MessageTone.Success => (new Color(0.07f, 0.42f, 0.2f, 0.92f), "success"),
                MessageTone.Hint => (new Color(0.06f, 0.3f, 0.56f, 0.92f), "info"),
                _ => (UiKit.GlassStrong, "info"),
            };
            var rt = UiKit.Rect("Toast", _hudRoot.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -196f),
                new Vector2(Mathf.Clamp(140f + text.Length * 15f, 420f, 940f), 64f));
            var view = new ToastView
            {
                Root = rt,
                Group = rt.gameObject.AddComponent<CanvasGroup>(),
                Bg = UiKit.Image(rt, bg, UiKit.Pill, true),
                Icon = UiKit.IconImage(UiKit.Rect("Icon", rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(34f, 34f)), icon, Color.white),
                Text = UiKit.Shadowed(UiKit.Label(UiKit.Rect("Text", rt, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(26f, 0f), new Vector2(-96f, 0f)),
                    text, 26, Color.white, TextAnchor.MiddleCenter, UiKit.SemiBold), 0.35f),
                Born = Time.unscaledTime,
                Until = Time.unscaledTime + duration,
            };
            view.Group.blocksRaycasts = false;
            _toasts.Add(view);
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
                return;
            var es = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            DontDestroyOnLoad(es);
        }

        private static void ApplySafeArea(RectTransform rt)
        {
            var safe = Screen.safeArea;
            var size = new Vector2(Screen.width, Screen.height);
            if (size.x <= 0f || size.y <= 0f)
                return;
            rt.anchorMin = safe.position / size;
            rt.anchorMax = (safe.position + safe.size) / size;
            rt.sizeDelta = Vector2.zero;
        }

        // ---- Build --------------------------------------------------------------------------------------------

        private void BuildTop(Transform hud)
        {
            // Timer pill, top centre.
            var timer = UiKit.At("Timer", hud, new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(210f, 62f));
            UiKit.Image(timer, UiKit.Glass, UiKit.Pill, true);
            UiKit.IconImage(UiKit.Rect("Icon", timer, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(30f, 30f)), "timer", UiKit.Muted);
            _timer = UiKit.Label(UiKit.Rect("Text", timer, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(18f, 0f), new Vector2(-50f, 0f)),
                "00:00", 34, UiKit.Text, TextAnchor.MiddleCenter, UiKit.Black);

            // Objective chip under it.
            _objectivePanel = UiKit.At("Objective", hud, new Vector2(0.5f, 1f), new Vector2(0f, -96f), new Vector2(720f, 54f));
            UiKit.Image(_objectivePanel, new Color(0f, 0f, 0f, 0.42f), UiKit.Pill, true);
            UiKit.IconImage(UiKit.Rect("Icon", _objectivePanel, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(18f, 0f), new Vector2(32f, 32f)), "objective", UiKit.Accent);
            _objective = UiKit.Shadowed(UiKit.Label(UiKit.Rect("Text", _objectivePanel, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(18f, 0f), new Vector2(-80f, 0f)),
                "", 24, UiKit.Text, TextAnchor.MiddleCenter, UiKit.SemiBold), 0.3f);
            _objectivePanel.gameObject.SetActive(_model.Mode != TrainingMode.Assessment && _model.Objectives.Count > 0);
            if (_model.Objectives.Count > 0)
                _objective.text = _model.Objectives[0];

            // Held item card, top left (an inventory slot like the weapon slots of mobile shooters).
            var held = UiKit.At("Held", hud, new Vector2(0f, 1f), new Vector2(28f, -24f), new Vector2(400f, 96f));
            UiKit.Image(held, UiKit.Glass, UiKit.Pill, true);
            var slot = UiKit.Rect("Slot", held, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(78f, 78f));
            UiKit.Image(slot, new Color(1f, 1f, 1f, 0.08f), UiKit.Circle);
            _heldRing = UiKit.Image(UiKit.Stretch("Ring", slot), UiKit.Hint, UiKit.Ring);
            _heldRing.type = Image.Type.Filled;
            _heldRing.fillMethod = Image.FillMethod.Radial360;
            _heldRing.fillOrigin = (int)Image.Origin360.Top;
            _heldIcon = UiKit.IconImage(UiKit.Centered("Icon", slot, Vector2.zero, new Vector2(40f, 40f)), "hand_empty", UiKit.Muted);
            _heldName = UiKit.Label(UiKit.Rect("Name", held, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0f), new Vector2(102f, -2f), new Vector2(-120f, 34f)),
                "Tay không", 25, UiKit.Text, TextAnchor.LowerLeft, UiKit.Bold);
            _heldStatus = UiKit.Label(UiKit.Rect("Status", held, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 1f), new Vector2(102f, -2f), new Vector2(-120f, 30f)),
                "", 20, UiKit.Muted, TextAnchor.UpperLeft);

            _pauseButton = HudButton.Create(hud, "Pause", new Vector2(1f, 1f), new Vector2(-28f, -24f), 84f, showLabel: false);
            _pauseButton.SetContent("pause", "pause", null);
            _pauseButton.Up += () => _model.OnPause?.Invoke();
            _touchBlockers.Add(_pauseButton.Rect);
        }

        private void BuildVitals(Transform hud)
        {
            var root = UiKit.At("Vitals", hud, new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(560f, 110f));
            UiKit.IconImage(UiKit.Rect("Heart", root, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 22f), new Vector2(34f, 34f)), "heart", UiKit.Danger);
            _health = UiKit.Bar(root, "Health", new Vector2(0f, 0f), new Vector2(46f, 32f), new Vector2(440f, 16f), UiKit.Good, out _healthGhost);
            _healthValue = UiKit.Shadowed(UiKit.Label(UiKit.Rect("Value", root, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(496f, 20f), new Vector2(70f, 40f)),
                "100", 26, UiKit.Text, TextAnchor.MiddleLeft, UiKit.Black));

            var stamina = UiKit.At("Stamina", root, new Vector2(0f, 0f), new Vector2(46f, 10f), new Vector2(300f, 14f));
            _staminaGroup = stamina.gameObject.AddComponent<CanvasGroup>();
            _stamina = UiKit.Bar(stamina, "Bar", new Vector2(0f, 0f), Vector2.zero, new Vector2(300f, 8f), UiKit.Hint, out _staminaGhost);

            _smokeChip = Chip(root, "Smoke", "smoke", new Vector2(46f, 64f), out _smokeText);
            _heatChip = Chip(root, "Heat", "heat", new Vector2(260f, 64f), out _heatText);
        }

        private static RectTransform Chip(Transform parent, string name, string icon, Vector2 pos, out Text text)
        {
            var rt = UiKit.At(name, parent, Vector2.zero, pos, new Vector2(200f, 40f));
            UiKit.Image(rt, new Color(0f, 0f, 0f, 0.5f), UiKit.Pill, true);
            UiKit.IconImage(UiKit.Rect("Icon", rt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(26f, 26f)), icon, UiKit.Text);
            text = UiKit.Label(UiKit.Rect("Text", rt, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(20f, 0f), new Vector2(-56f, 0f)), "", 20, UiKit.Text, TextAnchor.MiddleLeft, UiKit.SemiBold);
            rt.gameObject.SetActive(false);
            return rt;
        }

        private void BuildJoystick(Transform hud)
        {
            // Idle ghost where a thumb naturally rests; the live stick appears wherever the thumb lands.
            _joyGhost = UiKit.At("JoystickGhost", hud, Vector2.zero, new Vector2(190f, 190f), new Vector2(250f, 250f));
            _joyGhostGroup = _joyGhost.gameObject.AddComponent<CanvasGroup>();
            _joyGhostGroup.alpha = 0.55f;
            UiKit.Image(UiKit.Stretch("Base", _joyGhost), new Color(0f, 0f, 0f, 0.22f), UiKit.Circle);
            UiKit.Image(UiKit.Stretch("Rim", _joyGhost), new Color(1f, 1f, 1f, 0.35f), UiKit.RingThin);
            UiKit.Image(UiKit.Centered("Knob", _joyGhost, Vector2.zero, new Vector2(100f, 100f)), new Color(1f, 1f, 1f, 0.3f), UiKit.Circle);
            UiKit.IconImage(UiKit.Centered("Move", _joyGhost, Vector2.zero, new Vector2(44f, 44f)), "sprint", new Color(1f, 1f, 1f, 0.0f));
            UiKit.Shadowed(UiKit.Label(UiKit.Rect("Hint", _joyGhost, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(360f, 34f)),
                "Kéo để di chuyển", 22, new Color(1f, 1f, 1f, 0.8f), TextAnchor.MiddleCenter, UiKit.SemiBold));

            _joyBase = UiKit.Rect("Joystick", hud, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(250f, 250f));
            UiKit.Image(UiKit.Stretch("Base", _joyBase), new Color(0f, 0f, 0f, 0.28f), UiKit.Circle);
            UiKit.Image(UiKit.Stretch("Rim", _joyBase), new Color(1f, 1f, 1f, 0.55f), UiKit.RingThin);
            _joyKnob = UiKit.Centered("Knob", _joyBase, Vector2.zero, new Vector2(110f, 110f));
            UiKit.Image(UiKit.Centered("Glow", _joyKnob, Vector2.zero, new Vector2(190f, 190f)), new Color(1f, 1f, 1f, 0.18f), UiKit.Glow);
            UiKit.Image(UiKit.Stretch("Disc", _joyKnob), new Color(1f, 1f, 1f, 0.82f), UiKit.Circle);
            _sprintChip = UiKit.Rect("Sprint", _joyBase, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(130f, 40f));
            UiKit.Image(_sprintChip, UiKit.Accent, UiKit.Pill, true);
            UiKit.IconImage(UiKit.Rect("Icon", _sprintChip, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(12f, 0f), new Vector2(24f, 24f)), "sprint", Color.white);
            UiKit.Label(UiKit.Rect("Text", _sprintChip, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(14f, 0f), new Vector2(-40f, 0f)), "Chạy", 21, Color.white, TextAnchor.MiddleCenter, UiKit.Bold);
            _joyBase.gameObject.SetActive(false);
        }

        private void BuildActions(Transform hud)
        {
            // Right thumb arc: primary bottom-right, secondaries on an arc around it, crouch at the edge.
            var anchor = new Vector2(1f, 0f);
            var slots = new[] { (new Vector2(-170f, 205f), 190f), (new Vector2(-410f, 150f), 124f), (new Vector2(-370f, 370f), 124f) };
            for (var i = 0; i < _actions.Length; i++)
            {
                var slot = i;
                var (pos, d) = slots[i];
                var b = HudButton.Create(hud, $"Action{i + 1}", anchor, pos, d);
                b.Rect.pivot = new Vector2(0.5f, 0.5f);
                b.Rect.anchoredPosition = pos;
                b.Down += () => _interaction?.Press(slot);
                b.Up += () => _interaction?.Release(slot);
                _actions[i] = b;
                _touchBlockers.Add(b.Rect);
                b.gameObject.SetActive(false);
            }

            _crouch = HudButton.Create(hud, "Crouch", anchor, Vector2.zero, 104f);
            _crouch.Rect.pivot = new Vector2(0.5f, 0.5f);
            _crouch.Rect.anchoredPosition = new Vector2(-150f, 470f);
            _crouch.Up += () => _motor?.SetCrouched(!_motor.Crouched);
            _touchBlockers.Add(_crouch.Rect);
        }

        private void BuildPrompt(Transform hud)
        {
            _prompt = UiKit.Rect("WorldPrompt", hud, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(360f, 58f));
            _promptGroup = _prompt.gameObject.AddComponent<CanvasGroup>();
            _promptGroup.blocksRaycasts = false;
            UiKit.Image(_prompt, UiKit.GlassStrong, UiKit.Pill, true);
            var badge = UiKit.Rect("Badge", _prompt, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(7f, 0f), new Vector2(44f, 44f));
            UiKit.Image(badge, UiKit.Accent, UiKit.Circle);
            _promptIcon = UiKit.IconImage(UiKit.Centered("Icon", badge, Vector2.zero, new Vector2(26f, 26f)), "touch", Color.white);
            _promptName = UiKit.Label(UiKit.Rect("Name", _prompt, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f), new Vector2(26f, 0f), new Vector2(-72f, 0f)),
                "", 23, UiKit.Text, TextAnchor.MiddleCenter, UiKit.SemiBold);
            _promptName.horizontalOverflow = HorizontalWrapMode.Overflow;
            var tail = UiKit.Rect("Tail", _prompt, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0f, 2f), new Vector2(14f, 14f));
            UiKit.Image(tail, UiKit.GlassStrong, UiKit.Circle);
            _promptGroup.alpha = 0f;
        }

        private GameObject BuildBriefing(Transform root)
        {
            var dim = UiKit.Stretch("Briefing", root);
            UiKit.Image(dim, new Color(0.01f, 0.01f, 0.02f, 0.72f)).raycastTarget = true;
            var card = UiKit.Centered("Card", dim, Vector2.zero, new Vector2(1180f, 800f));
            UiKit.Panel(card, UiKit.Card);
            UiKit.Image(UiKit.Rect("Accent", card, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 8f)), UiKit.Accent);
            var modeName = _model.Mode switch
            {
                TrainingMode.Learn => "LÀM QUEN",
                TrainingMode.Guided => "LUYỆN TẬP CÓ HƯỚNG DẪN",
                _ => "ĐÁNH GIÁ",
            };
            var chip = UiKit.Rect("Mode", card, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(56f, -44f), new Vector2(420f, 44f));
            UiKit.Image(chip, UiKit.AccentSoft, UiKit.Pill, true);
            UiKit.Label(UiKit.Stretch("Text", chip, 6f), modeName, 21, UiKit.Accent, TextAnchor.MiddleCenter, UiKit.Bold);
            UiKit.Label(UiKit.Rect("Title", card, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -102f), new Vector2(-112f, 70f)),
                _model.ScenarioName ?? "Bài tập huấn", 44, UiKit.Text, TextAnchor.MiddleLeft, UiKit.Black);
            var body = _model.Instructions ?? "";
            if (_model.Objectives.Count > 0)
                body += "\n\n<b>Mục tiêu</b>\n•  " + string.Join("\n•  ", _model.Objectives);
            if (_model.TimeLimitSeconds.HasValue && _model.Mode != TrainingMode.Learn)
                body += $"\n\n<b>Thời gian:</b> {_model.TimeLimitSeconds.Value / 60} phút {_model.TimeLimitSeconds.Value % 60:00} giây";
            body += _model.Mode == TrainingMode.Assessment
                ? "\n\nChế độ đánh giá: không có gợi ý, kết quả do hệ thống chấm sau khi đồng bộ."
                : _model.Mode == TrainingMode.Guided ? "\n\nNút nên bấm sẽ phát sáng và có gợi ý trên màn hình." : "";
            UiKit.Label(UiKit.Rect("Body", card, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 6f), new Vector2(-112f, -360f)),
                body, 26, UiKit.Muted, TextAnchor.UpperLeft);
            UiKit.Label(UiKit.Rect("Disclaimer", card, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 150f), new Vector2(-112f, 30f)),
                "Mô phỏng phục vụ tập huấn, không thay thế hướng dẫn khẩn cấp thực tế.", 19, new Color(1f, 1f, 1f, 0.45f), TextAnchor.MiddleLeft);
            var start = UiKit.CardButton(card, "Start", "Bắt đầu", "play", new Vector2(0.5f, 0f), new Vector2(0f, 76f), new Vector2(440f, 96f), true);
            start.onClick.AddListener(() => _model.OnStart?.Invoke());
            return dim.gameObject;
        }

        private GameObject BuildPause(Transform root)
        {
            var dim = UiKit.Stretch("Pause", root);
            UiKit.Image(dim, new Color(0.01f, 0.01f, 0.02f, 0.7f)).raycastTarget = true;
            var card = UiKit.Centered("Card", dim, Vector2.zero, new Vector2(640f, 440f));
            UiKit.Panel(card, UiKit.Card);
            UiKit.Image(UiKit.Rect("Accent", card, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 8f)), UiKit.Accent);
            UiKit.Label(UiKit.Rect("Title", card, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -48f), new Vector2(-60f, 60f)),
                "Tạm dừng", 42, UiKit.Text, TextAnchor.MiddleCenter, UiKit.Black);
            var resume = UiKit.CardButton(card, "Resume", "Tiếp tục", "play", new Vector2(0.5f, 0.5f), new Vector2(0f, 6f), new Vector2(440f, 92f), true);
            resume.onClick.AddListener(() => _model.OnResume?.Invoke());
            var quit = UiKit.CardButton(card, "Abandon", "Bỏ bài tập", "exit", new Vector2(0.5f, 0.5f), new Vector2(0f, -112f), new Vector2(440f, 92f), false);
            quit.onClick.AddListener(() => _model.OnAbandon?.Invoke());
            return dim.gameObject;
        }

        private GameObject BuildResult(Transform root)
        {
            var dim = UiKit.Stretch("Result", root);
            UiKit.Image(dim, new Color(0.01f, 0.01f, 0.02f, 0.76f)).raycastTarget = true;
            var card = UiKit.Centered("Card", dim, Vector2.zero, new Vector2(1000f, 640f));
            UiKit.Panel(card, UiKit.Card);
            UiKit.Image(UiKit.Rect("Accent", card, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 8f)), UiKit.Accent);
            _resultText = UiKit.Label(UiKit.Rect("Text", card, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 56f), new Vector2(-110f, -230f)),
                "", 29, UiKit.Text, TextAnchor.UpperLeft);
            var exit = UiKit.CardButton(card, "Exit", "Thoát", "exit", new Vector2(0.5f, 0f), new Vector2(0f, 80f), new Vector2(420f, 96f), true);
            exit.onClick.AddListener(() => _model.OnExit?.Invoke());
            return dim.gameObject;
        }

        private bool IsOverButton(Vector2 screen)
        {
            if (_canvas == null || !_hudRoot.activeInHierarchy)
                return _canvas != null; // Modal panels own all touches.
            foreach (var rt in _touchBlockers)
            {
                if (rt != null && rt.gameObject.activeInHierarchy && RectTransformUtility.RectangleContainsScreenPoint(rt, screen, null))
                    return true;
            }

            return false;
        }

        // ---- Update -------------------------------------------------------------------------------------------

        private void Update()
        {
            if (_model == null)
                return;
            UpdateToasts();
            if (!_hudRoot.activeSelf)
                return;
            UpdateVitals();
            UpdateTimer();
            UpdateJoystick();
            UpdateActions();
            UpdateHeld();
            UpdatePrompt();
        }

        private void UpdateVitals()
        {
            if (_vitals == null)
                return;
            var dt = Time.unscaledDeltaTime;
            var h = _vitals.Health;
            _health.fillAmount = h;
            _health.color = h > 0.5f ? Color.Lerp(UiKit.Warning, UiKit.Good, (h - 0.5f) * 2f) : Color.Lerp(UiKit.Danger, UiKit.Warning, h * 2f);
            // Ghost bar trails the real value so a hit reads as a loss.
            _shownHealth = h > _shownHealth ? h : Mathf.MoveTowards(_shownHealth, h, dt * 0.25f);
            _healthGhost.fillAmount = _shownHealth;
            _healthValue.text = Mathf.RoundToInt(h * 100f).ToString();

            _stamina.fillAmount = _vitals.Stamina;
            _staminaGhost.fillAmount = 0f;
            var showStamina = _vitals.Stamina < 0.99f || (_motor != null && _motor.Running);
            _staminaGroup.alpha = Mathf.MoveTowards(_staminaGroup.alpha, showStamina ? 1f : 0.25f, dt * 3f);

            var smoke01 = Mathf.Clamp01(_vitals.LocalSmoke);
            var heat01 = Mathf.Clamp01((_vitals.HeatC - 35f) / 120f);
            SetChip(_smokeChip, _smokeText, smoke01 > 0.05f, $"Khói {Mathf.RoundToInt(smoke01 * 100f)}%", smoke01 > 0.4f);
            SetChip(_heatChip, _heatText, _vitals.HeatC > 38f, $"Nhiệt {Mathf.RoundToInt(_vitals.HeatC)}°", heat01 > 0.4f);

            var danger = Mathf.Max(heat01 * 0.8f, (1f - h) * 0.9f);
            _vignette.color = new Color(0.75f, 0.08f, 0f, Mathf.Lerp(_vignette.color.a, Mathf.Clamp01(danger) * 0.32f, 1f - Mathf.Exp(-4f * dt)));
            // Smoke greys the screen edges; capped so a thin haze never blacks out the corners.
            _smokeVignette.color = new Color(0.42f, 0.41f, 0.4f, Mathf.Lerp(_smokeVignette.color.a, Mathf.Clamp01(smoke01 * 1.1f) * 0.5f, 1f - Mathf.Exp(-3f * dt)));
        }

        private static void SetChip(RectTransform chip, Text text, bool show, string value, bool severe)
        {
            if (chip.gameObject.activeSelf != show)
                chip.gameObject.SetActive(show);
            if (!show)
                return;
            text.text = value;
            chip.GetComponent<Image>().color = severe ? new Color(0.6f, 0.1f, 0.06f, 0.85f) : new Color(0f, 0f, 0f, 0.5f);
        }

        private void UpdateTimer()
        {
            var elapsed = _model.Elapsed?.Invoke() ?? 0;
            var limited = _model.TimeLimitSeconds.HasValue && _model.Mode != TrainingMode.Learn;
            var shown = limited ? Math.Max(0, _model.TimeLimitSeconds.Value - elapsed) : elapsed;
            var t = TimeSpan.FromSeconds(shown);
            _timer.text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
            var urgent = limited && shown < 30;
            _timer.color = urgent ? Color.Lerp(UiKit.Danger, Color.white, 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 8f)) : UiKit.Text;
        }

        private void UpdateJoystick()
        {
            if (_input == null)
                return;
            var active = _input.JoystickActive;
            _hasMoved |= active;
            _joyGhostGroup.alpha = Mathf.MoveTowards(_joyGhostGroup.alpha, active ? 0f : _hasMoved ? 0.28f : 0.6f, Time.unscaledDeltaTime * 4f);
            _joyBase.gameObject.SetActive(active);
            if (!active)
                return;
            var scale = _canvas.scaleFactor;
            var ringRadius = _input.JoystickRadiusPixels / scale;
            _joyBase.sizeDelta = Vector2.one * ringRadius * 2.2f;
            var parent = (RectTransform)_joyBase.parent;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, _input.JoystickOrigin, null, out var local);
            _joyBase.anchorMin = _joyBase.anchorMax = new Vector2(0.5f, 0.5f);
            _joyBase.anchoredPosition = local;
            var offset = Vector2.ClampMagnitude((_input.JoystickCurrent - _input.JoystickOrigin) / scale, ringRadius);
            _joyKnob.anchoredPosition = offset;
            _sprintChip.gameObject.SetActive(_motor != null && _motor.Running);
        }

        private void UpdateActions()
        {
            if (_interaction == null)
                return;
            var actions = _interaction.Actions;
            for (var i = 0; i < _actions.Length; i++)
            {
                var b = _actions[i];
                var on = i < actions.Count;
                if (b.gameObject.activeSelf != on)
                    b.gameObject.SetActive(on);
                if (!on)
                    continue;
                var a = actions[i];
                var hold = a.IsHold && a.Id != "spray";
                b.SetContent($"{a.Id}:{a.Source}", IconFor(a), hold ? $"{a.Label} · giữ" : a.Id == "spray" ? $"{a.Label} · giữ" : a.Label);
                b.SetRecommended(a.Recommended);
                b.SetProgress(hold ? _interaction.HoldProgress(i) : a.Id == "spray" && _interaction.IsPressed(i) ? 1f : 0f);
                b.SetTint(UiKit.Text);
            }

            if (_motor != null)
            {
                _crouch.SetContent(_motor.Crouched ? "stand" : "crouch", _motor.Crouched ? "stand" : "crouch", _motor.Crouched ? "Đứng dậy" : "Cúi thấp");
                _crouch.SetToggled(_motor.Crouched);
                _crouch.SetRecommended(!_motor.Crouched && _vitals != null && _vitals.LocalSmoke > 0.3f && _model.Mode == TrainingMode.Guided);
            }
        }

        private string IconFor(AvailableAction a)
        {
            var cap = a.Source == ActionSource.Target ? _interaction.Target?.CapabilityId : _interaction.Held?.CapabilityId;
            return a.Id switch
            {
                "pick_up" when cap == Capabilities.BaitBelongings => "belongings",
                "pick_up" when cap == Capabilities.ExtinguisherPortable => "extinguisher",
                "pick_up" when cap == Capabilities.ItemTowel => "towel",
                "call" when cap == Capabilities.Elevator => "elevator",
                _ => a.Id,
            };
        }

        private void UpdateHeld()
        {
            var held = _interaction?.Held;
            if (held == null)
            {
                SetHeld("hand_empty", "Tay không", "", UiKit.Muted, 0f, UiKit.Hint);
                return;
            }

            if (held.CapabilityId == Capabilities.ItemTowel)
            {
                var wet = held.State == "wet";
                var dry = held.GetFloat("dryAfterSeconds", 150f);
                var covered = _vitals != null && _vitals.NoseCovered;
                SetHeld("towel", wet ? "Khăn ướt" : "Khăn khô",
                    wet ? $"Còn {Mathf.CeilToInt(_interaction.TowelSecondsLeft)}s{(covered ? " · đang che mũi" : "")}" : "Nhúng nước trước khi dùng",
                    wet ? UiKit.Hint : UiKit.Text, wet ? _interaction.TowelSecondsLeft / Mathf.Max(1f, dry) : 0f, UiKit.Hint);
            }
            else if (held.CapabilityId == Capabilities.ExtinguisherPortable)
            {
                var sealedPin = held.State == "sealed";
                SetHeld("extinguisher", "Bình chữa cháy", sealedPin ? "Chưa rút chốt" : $"Còn {Mathf.RoundToInt(_interaction.ExtinguisherLeft01 * 100f)}%",
                    UiKit.Danger, sealedPin ? 1f : _interaction.ExtinguisherLeft01, UiKit.Accent);
            }
            else
            {
                SetHeld("pick_up", held.DisplayName ?? held.CapabilityId, "", UiKit.Text, 0f, UiKit.Hint);
            }
        }

        private void SetHeld(string icon, string name, string status, Color iconColor, float ring, Color ringColor)
        {
            var sprite = UiKit.Icon(icon);
            if (_heldIcon.sprite != sprite)
                _heldIcon.sprite = sprite;
            _heldIcon.color = iconColor;
            if (_heldName.text != name)
                _heldName.text = name;
            if (_heldStatus.text != status)
                _heldStatus.text = status;
            _heldRing.fillAmount = ring;
            _heldRing.color = ringColor;
        }

        private void UpdatePrompt()
        {
            var target = _interaction?.Target;
            var cam = UnityEngine.Camera.main;
            var visible = target != null && cam != null;
            Vector2 local = default;
            if (visible)
            {
                var screen = cam.WorldToScreenPoint(target.PromptPosition + Vector3.up * 0.15f);
                visible = screen.z > 0.1f;
                if (visible)
                    RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)_prompt.parent, screen, null, out local);
            }

            if (visible && target != _promptTarget)
            {
                _promptTarget = target;
                _promptAlpha = 0f;
                _promptName.text = PromptText(target);
                var actions = _interaction.Actions;
                _promptIcon.sprite = UiKit.Icon(actions.Count > 0 ? IconFor(actions[0]) : "touch");
                _prompt.sizeDelta = new Vector2(Mathf.Clamp(96f + _promptName.preferredWidth, 220f, 560f), 58f);
            }
            else if (visible)
            {
                var text = PromptText(target);
                if (_promptName.text != text)
                    _promptName.text = text;
            }

            _promptAlpha = Mathf.MoveTowards(_promptAlpha, visible ? 1f : 0f, Time.unscaledDeltaTime / 0.15f);
            _promptGroup.alpha = _promptAlpha;
            if (visible)
            {
                var rise = (1f - _promptAlpha) * -10f;
                _prompt.anchoredPosition = Vector2.Lerp(_prompt.anchoredPosition, local + new Vector2(0f, rise), _promptAlpha < 0.05f ? 1f : 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
            }
            else if (_promptAlpha <= 0f)
            {
                _promptTarget = null;
            }
        }

        private static string PromptText(Interactable target)
        {
            // Authored labels win; a missing label or the raw capability id falls back to the Vietnamese name.
            var label = target.DisplayName;
            var name = string.IsNullOrEmpty(label) || label == target.CapabilityId || (label.Contains('.') && !label.Contains(' '))
                ? NameOf(target.CapabilityId)
                : label;
            if (Capabilities.IsDoor(target.CapabilityId))
                return target.State switch { "open" => $"{name} · đang mở", "blocked" => $"{name} · bị khóa", _ => name };
            return name;
        }

        private static string NameOf(string capability) => capability switch
        {
            Capabilities.DoorFire => "Cửa chống cháy",
            Capabilities.DoorExit => "Cửa thoát hiểm",
            Capabilities.DoorStandard => "Cửa",
            Capabilities.AlarmCallPoint => "Nút báo cháy",
            Capabilities.ExtinguisherPortable => "Bình chữa cháy",
            Capabilities.WaterSink => "Bồn rửa",
            Capabilities.WaterBath => "Bồn tắm",
            Capabilities.WaterShower => "Vòi sen",
            Capabilities.ItemTowel => "Khăn",
            Capabilities.Elevator => "Thang máy",
            Capabilities.BaitBelongings => "Đồ cá nhân",
            Capabilities.NpcGuide => "Người cần giúp",
            _ => capability,
        };

        private void UpdateToasts()
        {
            var now = Time.unscaledTime;
            for (var i = _toasts.Count - 1; i >= 0; i--)
            {
                if (now > _toasts[i].Until + 0.3f)
                {
                    Destroy(_toasts[i].Root.gameObject);
                    _toasts.RemoveAt(i);
                }
            }

            // Newest on top, older ones slide down; each fades/slides in over 0.2 s and out over 0.3 s.
            for (var i = 0; i < _toasts.Count; i++)
            {
                var t = _toasts[i];
                var rank = _toasts.Count - 1 - i;
                var appear = Mathf.Clamp01((now - t.Born) / 0.2f);
                var leave = Mathf.Clamp01((t.Until + 0.3f - now) / 0.3f);
                t.Group.alpha = Mathf.Min(appear, leave) * (rank == 0 ? 1f : 0.85f);
                var y = -196f - rank * 74f + (1f - appear) * 24f;
                t.Root.anchoredPosition = new Vector2(0f, Mathf.Lerp(t.Root.anchoredPosition.y, y, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime)));
            }
        }
    }
}
