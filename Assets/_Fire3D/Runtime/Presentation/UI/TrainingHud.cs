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
    /// Mobile-first HUD: vitals (training values, not medical), objective + timer, floating joystick, up to three
    /// contextual action buttons with hold progress, held item, toasts, briefing, pause and end panels.
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

        private sealed class ActionSlot
        {
            public RectTransform Root;
            public Image Background;
            public Image Progress;
            public Text Label;
            public Outline Highlight;
        }

        private Canvas _canvas;
        private Model _model;
        private InteractionController _interaction;
        private PlayerVitals _vitals;
        private PlayerMotor _motor;
        private GameplayInputReader _input;

        private Image _health, _smoke, _heat, _stamina;
        private Text _healthV, _smokeV, _heatV, _staminaV;
        private Text _timer, _objective, _held, _target;
        private RectTransform _objectivePanel, _joyRing, _joyKnob;
        private readonly ActionSlot[] _slots = new ActionSlot[ActionResolver.MaxButtons];
        private Button _crouch;
        private Text _crouchLabel;
        private RectTransform _toastRoot;
        private Text _toast;
        private Image _toastBg;
        private float _toastUntil;
        private GameObject _briefing, _pause, _result, _hudRoot;
        private Text _resultText;
        private Image _vignette;
        private readonly List<RectTransform> _touchBlockers = new();

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
            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.6f;
            gameObject.AddComponent<GraphicRaycaster>();

            var root = UiKit.Stretch("SafeArea", transform);
            ApplySafeArea(root);

            _vignette = UiKit.Image(UiKit.Stretch("Vignette", root), new Color(0.6f, 0.1f, 0f, 0f), false);
            _vignette.raycastTarget = false;

            _hudRoot = UiKit.Stretch("Hud", root).gameObject;
            var hud = _hudRoot.transform;
            BuildVitals(hud);
            BuildTop(hud);
            BuildJoystick(hud);
            BuildActions(hud);
            BuildToast(hud);

            _briefing = BuildBriefing(root);
            _pause = BuildPause(root);
            _result = BuildResult(root);
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
            _toast.text = text;
            _toastBg.color = tone switch
            {
                MessageTone.Danger => new Color(0.55f, 0.08f, 0.06f, 0.9f),
                MessageTone.Warning => new Color(0.55f, 0.35f, 0.05f, 0.9f),
                MessageTone.Success => new Color(0.08f, 0.4f, 0.18f, 0.9f),
                MessageTone.Hint => new Color(0.05f, 0.3f, 0.55f, 0.9f),
                _ => UiKit.Panel,
            };
            _toastRoot.gameObject.SetActive(true);
            _toastUntil = Time.unscaledTime + (tone == MessageTone.Hint ? 4.5f : 3f);
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

        private void BuildVitals(Transform hud)
        {
            var panel = UiKit.Rect("Vitals", hud, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(24f, -24f), new Vector2(430f, 220f));
            UiKit.Image(panel, UiKit.Panel);
            _health = UiKit.Bar(panel, "Sức khỏe", new Vector2(16f, -12f), UiKit.Good, out _healthV);
            _smoke = UiKit.Bar(panel, "Khói đã hít", new Vector2(16f, -62f), new Color(0.6f, 0.6f, 0.62f), out _smokeV);
            _heat = UiKit.Bar(panel, "Nhiệt", new Vector2(16f, -112f), UiKit.Accent, out _heatV);
            _stamina = UiKit.Bar(panel, "Thể lực", new Vector2(16f, -162f), UiKit.Hint, out _staminaV);
        }

        private void BuildTop(Transform hud)
        {
            var timer = UiKit.Rect("Timer", hud, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -24f), new Vector2(220f, 64f));
            UiKit.Image(timer, UiKit.Panel);
            _timer = UiKit.Label(UiKit.Stretch("Text", timer), "00:00", 36, UiKit.Text, TextAnchor.MiddleCenter, FontStyle.Bold);

            _objectivePanel = UiKit.Rect("Objective", hud, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -98f), new Vector2(760f, 56f));
            UiKit.Image(_objectivePanel, UiKit.Panel);
            _objective = UiKit.Label(UiKit.Stretch("Text", _objectivePanel, 14f), "", 24, UiKit.Text, TextAnchor.MiddleCenter);
            _objectivePanel.gameObject.SetActive(_model.Mode != TrainingMode.Assessment && _model.Objectives.Count > 0);
            if (_model.Objectives.Count > 0)
                _objective.text = _model.Objectives[0];

            var pause = UiKit.Button(hud, "PauseButton", "II", new Vector2(1f, 1f), new Vector2(-64f, -64f), new Vector2(88f, 88f), UiKit.Panel, 34);
            pause.onClick.AddListener(() => _model.OnPause?.Invoke());
            _touchBlockers.Add((RectTransform)pause.transform);

            var held = UiKit.Rect("Held", hud, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-130f, -24f), new Vector2(360f, 88f));
            UiKit.Image(held, UiKit.Panel);
            _held = UiKit.Label(UiKit.Stretch("Text", held, 14f), "Tay: trống", 24, UiKit.Text, TextAnchor.MiddleLeft);
        }

        private void BuildJoystick(Transform hud)
        {
            _joyRing = UiKit.Rect("JoystickRing", hud, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(240f, 240f));
            var ring = UiKit.Image(_joyRing, new Color(1f, 1f, 1f, 0.12f));
            ring.raycastTarget = false;
            _joyKnob = UiKit.Rect("Knob", _joyRing, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(110f, 110f));
            UiKit.Image(_joyKnob, new Color(1f, 1f, 1f, 0.35f)).raycastTarget = false;
            _joyRing.gameObject.SetActive(false);

            var hint = UiKit.Rect("MoveHint", hud, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(40f, 40f), new Vector2(420f, 40f));
            UiKit.Label(hint, "Chạm và kéo bên trái để di chuyển", 20, new Color(1f, 1f, 1f, 0.45f));
        }

        private void BuildActions(Transform hud)
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = i;
                var pos = new Vector2(-150f - i * 0f, 150f + i * 150f);
                var rt = UiKit.Rect($"Action{i + 1}", hud, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0.5f),
                    new Vector2(-170f - (i == 1 ? 190f : 0f), i == 2 ? 330f : 150f + (i == 1 ? 0f : 0f)), new Vector2(240f, 130f));
                var bg = UiKit.Image(rt, new Color(0.12f, 0.13f, 0.16f, 0.88f));
                var progress = UiKit.Image(UiKit.Stretch("Progress", rt), new Color(1f, 0.55f, 0.2f, 0.55f));
                progress.type = Image.Type.Filled;
                progress.fillMethod = Image.FillMethod.Horizontal;
                progress.fillAmount = 0f;
                progress.raycastTarget = false;
                var label = UiKit.Label(UiKit.Stretch("Label", rt, 10f), "", 28, UiKit.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
                label.resizeTextForBestFit = true;
                label.resizeTextMinSize = 16;
                label.resizeTextMaxSize = 28;
                var outline = rt.gameObject.AddComponent<Outline>();
                outline.effectColor = UiKit.Hint;
                outline.effectDistance = new Vector2(4f, -4f);
                outline.enabled = false;

                var trigger = rt.gameObject.AddComponent<EventTrigger>();
                AddTrigger(trigger, EventTriggerType.PointerDown, () => _interaction?.Press(slot));
                AddTrigger(trigger, EventTriggerType.PointerUp, () => _interaction?.Release(slot));
                AddTrigger(trigger, EventTriggerType.PointerExit, () => _interaction?.Release(slot));

                _slots[i] = new ActionSlot { Root = rt, Background = bg, Progress = progress, Label = label, Highlight = outline };
                _touchBlockers.Add(rt);
                rt.gameObject.SetActive(false);
            }

            _crouch = UiKit.Button(hud, "Crouch", "Cúi người", new Vector2(1f, 0f), new Vector2(-560f, 110f), new Vector2(200f, 90f),
                new Color(0.12f, 0.13f, 0.16f, 0.88f), 24);
            _crouch.onClick.AddListener(() => _motor?.SetCrouched(!_motor.Crouched));
            _crouchLabel = _crouch.GetComponentInChildren<Text>();
            _touchBlockers.Add((RectTransform)_crouch.transform);

            var target = UiKit.Rect("TargetName", hud, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(600f, 44f));
            _target = UiKit.Label(target, "", 24, UiKit.Muted, TextAnchor.MiddleCenter);
        }

        private static void AddTrigger(EventTrigger trigger, EventTriggerType type, Action action)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => action());
            trigger.triggers.Add(entry);
        }

        private void BuildToast(Transform hud)
        {
            _toastRoot = UiKit.Rect("Toast", hud, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 180f), new Vector2(820f, 76f));
            _toastBg = UiKit.Image(_toastRoot, UiKit.Panel);
            _toast = UiKit.Label(UiKit.Stretch("Text", _toastRoot, 16f), "", 28, UiKit.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            _toastRoot.gameObject.SetActive(false);
        }

        private GameObject BuildBriefing(Transform root)
        {
            var dim = UiKit.Stretch("Briefing", root);
            UiKit.Image(dim, new Color(0f, 0f, 0f, 0.6f), false);
            var card = UiKit.Rect("Card", dim, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1100f, 760f));
            UiKit.Image(card, UiKit.PanelSolid);
            var modeName = _model.Mode switch
            {
                TrainingMode.Learn => "LÀM QUEN",
                TrainingMode.Guided => "LUYỆN TẬP CÓ HƯỚNG DẪN",
                _ => "ĐÁNH GIÁ",
            };
            UiKit.Label(UiKit.Rect("Mode", card, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(-80f, 40f)),
                modeName, 24, UiKit.Accent, TextAnchor.MiddleLeft, FontStyle.Bold);
            UiKit.Label(UiKit.Rect("Title", card, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -84f), new Vector2(-80f, 70f)),
                _model.ScenarioName ?? "Bài tập huấn", 46, UiKit.Text, TextAnchor.MiddleLeft, FontStyle.Bold);
            var body = _model.Instructions ?? "";
            if (_model.Objectives.Count > 0)
                body += "\n\nMục tiêu:\n• " + string.Join("\n• ", _model.Objectives);
            if (_model.TimeLimitSeconds.HasValue && _model.Mode != TrainingMode.Learn)
                body += $"\n\nThời gian: {_model.TimeLimitSeconds.Value / 60} phút {_model.TimeLimitSeconds.Value % 60:00} giây";
            body += _model.Mode == TrainingMode.Assessment
                ? "\n\nChế độ đánh giá: không có gợi ý, kết quả do hệ thống chấm sau khi đồng bộ."
                : _model.Mode == TrainingMode.Guided ? "\n\nNút nên bấm sẽ được làm nổi bật và có gợi ý trên màn hình." : "";
            body += "\n\nĐây là mô phỏng phục vụ tập huấn, không thay thế hướng dẫn khẩn cấp thực tế.";
            UiKit.Label(UiKit.Rect("Body", card, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(-80f, -330f)),
                body, 27, UiKit.Muted, TextAnchor.UpperLeft);
            var start = UiKit.Button(card, "Start", "Bắt đầu", new Vector2(0.5f, 0f), new Vector2(0f, 80f), new Vector2(420f, 100f), UiKit.Accent, 36);
            start.onClick.AddListener(() => _model.OnStart?.Invoke());
            return dim.gameObject;
        }

        private GameObject BuildPause(Transform root)
        {
            var dim = UiKit.Stretch("Pause", root);
            UiKit.Image(dim, new Color(0f, 0f, 0f, 0.65f), false);
            var card = UiKit.Rect("Card", dim, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(640f, 420f));
            UiKit.Image(card, UiKit.PanelSolid);
            UiKit.Label(UiKit.Rect("Title", card, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(-60f, 60f)),
                "Tạm dừng", 42, UiKit.Text, TextAnchor.MiddleCenter, FontStyle.Bold);
            var resume = UiKit.Button(card, "Resume", "Tiếp tục", new Vector2(0.5f, 0.5f), new Vector2(0f, 10f), new Vector2(440f, 96f), UiKit.Accent, 32);
            resume.onClick.AddListener(() => _model.OnResume?.Invoke());
            var quit = UiKit.Button(card, "Abandon", "Bỏ bài tập", new Vector2(0.5f, 0.5f), new Vector2(0f, -110f), new Vector2(440f, 96f), new Color(0.3f, 0.12f, 0.1f, 1f), 30);
            quit.onClick.AddListener(() => _model.OnAbandon?.Invoke());
            return dim.gameObject;
        }

        private GameObject BuildResult(Transform root)
        {
            var dim = UiKit.Stretch("Result", root);
            UiKit.Image(dim, new Color(0f, 0f, 0f, 0.7f), false);
            var card = UiKit.Rect("Card", dim, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 620f));
            UiKit.Image(card, UiKit.PanelSolid);
            _resultText = UiKit.Label(UiKit.Rect("Text", card, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 50f), new Vector2(-90f, -220f)),
                "", 30, UiKit.Text, TextAnchor.UpperLeft);
            var exit = UiKit.Button(card, "Exit", "Thoát", new Vector2(0.5f, 0f), new Vector2(0f, 80f), new Vector2(420f, 100f), UiKit.Accent, 34);
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

        private void Update()
        {
            if (_model == null || !_hudRoot.activeSelf)
                return;
            UpdateVitals();
            UpdateTimer();
            UpdateJoystick();
            UpdateActions();
            if (_toastRoot.gameObject.activeSelf && Time.unscaledTime > _toastUntil)
                _toastRoot.gameObject.SetActive(false);
        }

        private void UpdateVitals()
        {
            if (_vitals == null)
                return;
            _health.fillAmount = _vitals.Health;
            _health.color = Color.Lerp(UiKit.Danger, UiKit.Good, _vitals.Health);
            _healthV.text = $"{Mathf.RoundToInt(_vitals.Health * 100f)}";
            var smoke01 = Mathf.Clamp01(_vitals.SmokeInhaled / 60f);
            _smoke.fillAmount = smoke01;
            _smokeV.text = $"{Mathf.RoundToInt(smoke01 * 100f)}";
            var heat01 = Mathf.Clamp01((_vitals.HeatC - 25f) / 150f);
            _heat.fillAmount = heat01;
            _heatV.text = $"{Mathf.RoundToInt(_vitals.HeatC)}°";
            _stamina.fillAmount = _vitals.Stamina;
            _staminaV.text = $"{Mathf.RoundToInt(_vitals.Stamina * 100f)}";
            var danger = Mathf.Max(heat01 * 0.6f, 1f - _vitals.Health);
            _vignette.color = new Color(0.6f, 0.08f, 0f, Mathf.Clamp01(danger) * 0.35f);
        }

        private void UpdateTimer()
        {
            var elapsed = _model.Elapsed?.Invoke() ?? 0;
            var shown = _model.TimeLimitSeconds.HasValue && _model.Mode != TrainingMode.Learn
                ? Math.Max(0, _model.TimeLimitSeconds.Value - elapsed)
                : elapsed;
            var t = TimeSpan.FromSeconds(shown);
            _timer.text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
            _timer.color = _model.TimeLimitSeconds.HasValue && shown < 30 && _model.Mode != TrainingMode.Learn ? UiKit.Danger : UiKit.Text;
        }

        private void UpdateJoystick()
        {
            if (_input == null)
                return;
            var active = _input.JoystickActive;
            _joyRing.gameObject.SetActive(active);
            if (!active)
                return;
            var scale = _canvas.scaleFactor;
            var ringRadius = _input.JoystickRadiusPixels / scale;
            _joyRing.sizeDelta = Vector2.one * ringRadius * 2f;
            var parent = (RectTransform)_joyRing.parent;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(parent, _input.JoystickOrigin, null, out var local);
            _joyRing.anchorMin = _joyRing.anchorMax = new Vector2(0.5f, 0.5f);
            _joyRing.anchoredPosition = local;
            var offset = Vector2.ClampMagnitude((_input.JoystickCurrent - _input.JoystickOrigin) / scale, ringRadius);
            _joyKnob.anchoredPosition = offset;
        }

        private void UpdateActions()
        {
            if (_interaction == null)
                return;
            var actions = _interaction.Actions;
            for (var i = 0; i < _slots.Length; i++)
            {
                var s = _slots[i];
                var on = i < actions.Count;
                if (s.Root.gameObject.activeSelf != on)
                    s.Root.gameObject.SetActive(on);
                if (!on)
                    continue;
                var a = actions[i];
                s.Label.text = a.IsHold && a.Id != "spray" ? $"{a.Label}\n<size=18>(giữ)</size>" : a.Id == "spray" ? $"{a.Label}\n<size=18>(giữ để phun)</size>" : a.Label;
                s.Label.supportRichText = true;
                s.Highlight.enabled = a.Recommended;
                s.Progress.fillAmount = a.IsHold && a.Id != "spray" ? _interaction.HoldProgress(i) : _interaction.IsPressed(i) ? 1f : 0f;
                s.Background.color = _interaction.IsPressed(i) ? new Color(0.3f, 0.18f, 0.1f, 0.95f) : new Color(0.12f, 0.13f, 0.16f, 0.88f);
            }

            var held = _interaction.Held;
            if (held == null)
                _held.text = "Tay: trống";
            else if (held.CapabilityId == Capabilities.ItemTowel)
                _held.text = held.State == "wet"
                    ? $"Khăn ướt · còn {Mathf.CeilToInt(_interaction.TowelSecondsLeft)}s{(_vitals != null && _vitals.NoseCovered ? " · đang che mũi" : "")}"
                    : "Khăn khô";
            else if (held.CapabilityId == Capabilities.ExtinguisherPortable)
                _held.text = held.State == "sealed" ? "Bình chữa cháy (chưa rút chốt)" : $"Bình chữa cháy · {Mathf.RoundToInt(_interaction.ExtinguisherLeft01 * 100f)}%";
            else
                _held.text = held.DisplayName ?? held.CapabilityId;

            _target.text = _interaction.Target != null ? _interaction.Target.DisplayName ?? "" : "";
            if (_crouchLabel != null && _motor != null)
                _crouchLabel.text = _motor.Crouched ? "Đứng dậy" : "Cúi người";
        }
    }
}
