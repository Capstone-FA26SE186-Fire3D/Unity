using System.Collections;
using System.Collections.Generic;
using Fire3D.Contracts;
using Fire3D.Simulation;
using Fire3D.Unity.Content;
using Fire3D.Unity.Gameplay.Player;
using Fire3D.Unity.Presentation;
using Fire3D.Unity.Presentation.UI;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Fire3D.Unity.Gameplay.Interactions
{
    /// <summary>
    /// Finds the nearest interactable in reach, resolves its contextual actions and executes them: telemetry
    /// events, simulation commands and local effects. One hand slot plus the nose-cover state (Docs section 7).
    /// </summary>
    public sealed class InteractionController : MonoBehaviour
    {
        [SerializeField] private float reach = 1.2f;
        [SerializeField] private float frontDot = 0.25f;
        [SerializeField] private Transform handAnchor;

        private readonly float[] _holdProgress = new float[ActionResolver.MaxButtons];
        private readonly bool[] _pressed = new bool[ActionResolver.MaxButtons];
        private List<AvailableAction> _actions = new();
        private InputAction[] _keys;
        private PlayerVitals _vitals;
        private PlayerMotor _motor;
        private PlayerGestures _gestures;

        private Interactable _held;
        private float _towelWetUntil = -1f;
        private bool _coverEventPending;
        private bool _sprayActive;

        // A timed object action started by one tap (dipping in water): it fills on its own while the player stays.
        private int _autoSlot = -1;
        private Interactable _autoTarget;
        private Vector3 _autoFrom;
        private string _autoLabel;
        [SerializeField] private float autoLeaveMeters = 0.6f;
        private AudioSource _sprayAudio;
        private float _sprayStart;
        private ParticleSystem _sprayFx;
        private float _dischargeLeft;
        private bool _largeFireReported;

        public Interactable Target { get; private set; }

        /// <summary>True while a hand gesture (touch/open/close a door) plays; buttons are ignored meanwhile.</summary>
        public bool Busy { get; private set; }
        public Interactable Held => _held;
        public IReadOnlyList<AvailableAction> Actions => _actions;
        public float HoldProgress(int slot) => slot >= 0 && slot < _holdProgress.Length ? _holdProgress[slot] : 0f;
        public bool IsPressed(int slot) => slot >= 0 && slot < _pressed.Length && _pressed[slot];
        public float TowelSecondsLeft => _held != null && _held.CapabilityId == Capabilities.ItemTowel && _held.State == "wet" && Ctx != null
            ? Mathf.Max(0f, _towelWetUntil - Ctx.Time) : 0f;
        public float ExtinguisherLeft01 => _held != null && _held.CapabilityId == Capabilities.ExtinguisherPortable
            ? Mathf.Clamp01(_dischargeLeft / Mathf.Max(1f, _held.GetFloat("dischargeSeconds", 15f))) : 0f;

        private static GameContext Ctx => GameContext.Current;

        private void Awake()
        {
            _vitals = GetComponent<PlayerVitals>();
            _motor = GetComponent<PlayerMotor>();
            _gestures = GetComponent<PlayerGestures>();
            if (_gestures == null)
                _gestures = gameObject.AddComponent<PlayerGestures>();
            _keys = new[]
            {
                new InputAction("Action1", InputActionType.Button, "<Keyboard>/e"),
                new InputAction("Action2", InputActionType.Button, "<Keyboard>/f"),
                new InputAction("Action3", InputActionType.Button, "<Keyboard>/r"),
            };
            _keys[0].AddBinding("<Keyboard>/1");
            _keys[0].AddBinding("<Gamepad>/buttonSouth");
            _keys[1].AddBinding("<Keyboard>/2");
            _keys[1].AddBinding("<Gamepad>/buttonWest");
            _keys[2].AddBinding("<Keyboard>/3");
            _keys[2].AddBinding("<Gamepad>/buttonNorth");
        }

        private void OnEnable()
        {
            foreach (var k in _keys)
                k.Enable();
        }

        private void OnDisable()
        {
            foreach (var k in _keys)
                k.Disable();
        }

        private void OnDestroy()
        {
            foreach (var k in _keys)
                k.Dispose();
        }

        /// <summary>UI or keyboard pressed button <paramref name="slot"/>.</summary>
        public void Press(int slot)
        {
            if (slot < 0 || slot >= _actions.Count || Busy)
                return;
            var chosen = _actions[slot];
            if (Target != null && Capabilities.IsDoor(Target.CapabilityId) && chosen.Id is "touch" or "open" or "close")
            {
                StartCoroutine(DoorGesture(chosen, Target));
                return;
            }

            _pressed[slot] = true;
            _holdProgress[slot] = 0f;
            var action = _actions[slot];
            if (!action.IsHold)
            {
                Execute(action);
                _pressed[slot] = false;
            }
            else if (action.Id == "spray")
            {
                BeginSpray();
            }
        }

        /// <summary>
        /// An object tile was tapped. Instant actions run at once; a timed one (dipping a towel) starts filling and
        /// completes on its own, but fails if the player walks away or turns to something else first.
        /// </summary>
        public void Tap(int slot)
        {
            if (slot < 0 || slot >= _actions.Count || Busy || _autoSlot >= 0)
                return;
            var action = _actions[slot];
            if (!action.IsHold || action.Id == "spray")
            {
                Press(slot);
                return;
            }

            _pressed[slot] = true;
            _holdProgress[slot] = 0f;
            _autoSlot = slot;
            _autoTarget = Target;
            _autoFrom = transform.position;
            _autoLabel = action.Label;
        }

        /// <summary>Slot of the tapped timed action still filling, or -1.</summary>
        public int RunningTap => _autoSlot;

        private void FailTap(GameContext ctx)
        {
            if (_autoSlot < _pressed.Length)
            {
                _pressed[_autoSlot] = false;
                _holdProgress[_autoSlot] = 0f;
            }

            _autoSlot = -1;
            Haptics.Warn();
            ctx.Show($"{_autoLabel} chưa xong. Hãy đứng yên và làm lại.", MessageTone.Danger);
        }

        public void Release(int slot)
        {
            if (slot < 0 || slot >= _pressed.Length || slot == _autoSlot)
                return;
            if (_pressed[slot] && slot < _actions.Count && _actions[slot].Id == "spray")
                EndSpray();
            _pressed[slot] = false;
            _holdProgress[slot] = 0f;
        }

        private void Update()
        {
            var ctx = Ctx;
            if (ctx == null || ctx.Simulation == null)
                return;

            for (var i = 0; i < _keys.Length; i++)
            {
                if (_keys[i].WasPressedThisFrame())
                    Press(i);
                if (_keys[i].WasReleasedThisFrame())
                    Release(i);
            }

            UpdateHeldItem(ctx);
            Target = FindTarget();
            if (_autoSlot >= 0 && (Target != _autoTarget || (transform.position - _autoFrom).sqrMagnitude > autoLeaveMeters * autoLeaveMeters))
                FailTap(ctx);
            var held = _held != null ? new HeldSnapshot(_held.CapabilityId, _held.State, _held.DisplayName) : HeldSnapshot.Empty;
            var previous = _actions;
            _actions = ActionResolver.Resolve(Target?.CapabilityId, Target?.State, Target?.DisplayName, held, ctx.Guided, Recommend(ctx));
            if (!SameButtons(previous, _actions))
            {
                for (var i = 0; i < _pressed.Length; i++)
                {
                    if (_pressed[i] && i < previous.Count && previous[i].Id == "spray")
                        EndSpray();
                    _pressed[i] = false;
                    _holdProgress[i] = 0f;
                }

                _autoSlot = -1;
            }

            for (var i = 0; i < _actions.Count; i++)
            {
                if (!_pressed[i])
                    continue;
                var a = _actions[i];
                if (a.Id == "spray")
                {
                    TickSpray(ctx);
                    continue;
                }

                _holdProgress[i] += Time.deltaTime / Mathf.Max(0.01f, a.Definition.HoldSeconds);
                if (_holdProgress[i] >= 1f)
                {
                    if (i == _autoSlot)
                        _autoSlot = -1;
                    Execute(a);
                    _pressed[i] = false;
                    _holdProgress[i] = 0f;
                }
            }
        }

        private static bool SameButtons(List<AvailableAction> a, List<AvailableAction> b)
        {
            if (a.Count != b.Count)
                return false;
            for (var i = 0; i < a.Count; i++)
                if (a[i].Id != b[i].Id || a[i].Source != b[i].Source)
                    return false;
            return true;
        }

        private Interactable FindTarget()
        {
            Interactable best = null;
            var bestScore = float.MaxValue;
            var origin = transform.position;
            var forward = transform.forward;
            foreach (var it in Interactable.All)
            {
                if (it == null || it.Held || it.Consumed || !it.isActiveAndEnabled)
                    continue;
                if (RuntimeCatalog.GetActions(it.CapabilityId).Count == 0)
                    continue;
                var to = it.transform.position - origin;
                if (Mathf.Abs(to.y) > 1.8f)
                    continue;
                to.y = 0f;
                var d = to.magnitude;
                if (d > reach + 0.35f)
                    continue;
                var dot = d > 0.05f ? Vector3.Dot(to / d, forward) : 1f;
                if (dot < frontDot)
                    continue;
                var score = d - dot * 0.3f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = it;
                }
            }

            return best;
        }

        /// <summary>Guided hint: which button to highlight for the object in front.</summary>
        private string Recommend(GameContext ctx)
        {
            if (!ctx.Guided)
                return null;
            if (Target != null && Capabilities.IsDoor(Target.CapabilityId))
                return Target.State == "closed" && !_checkedDoors.Contains(Target.ObjectId) ? "touch" : Target.State == "open" ? null : "open";
            if (Target != null && Capabilities.IsWater(Target.CapabilityId))
                return "wet_item";
            if (Target != null && Target.CapabilityId == Capabilities.AlarmCallPoint && !ctx.Simulation.Alarm.Raised)
                return "press";
            if (Target != null && Target.CapabilityId == Capabilities.NpcGuide)
                return "lead";
            if (_held != null && _held.CapabilityId == Capabilities.ItemTowel && _held.State == "wet" && !(_vitals?.NoseCovered ?? false))
                return "cover_nose";
            if (_held != null && _held.CapabilityId == Capabilities.ExtinguisherPortable)
                return _held.State == "sealed" ? "pull_pin" : "spray";
            if (Target != null && Target.CapabilityId == Capabilities.ItemTowel)
                return "pick_up";
            return null;
        }

        private readonly HashSet<string> _checkedDoors = new();

        private void Execute(AvailableAction action, Interactable explicitTarget = null)
        {
            var ctx = Ctx;
            if (ctx == null)
                return;
            var target = explicitTarget != null ? explicitTarget : action.Source == ActionSource.Target ? Target : _held;
            if (target == null)
                return;
            var cap = target.CapabilityId;
            var log = ctx.Log;

            switch (action.Id)
            {
                case "press" when cap == Capabilities.AlarmCallPoint:
                    ctx.Simulation.Enqueue(new HazardCommand { Kind = HazardCommandKind.RaiseAlarm, ObjectId = target.SimulationId });
                    GameAudio.PlayAt(GameAudio.Sounds?.CallPointPress, target.PromptPosition, 0.8f);
                    log.EmitObject(EventTypes.AlarmRaised, target.ObjectId);
                    ctx.Show("Đã nhấn chuông báo cháy", MessageTone.Success);
                    break;

                case "touch":
                {
                    var hot = DoorIsHot(ctx, target);
                    _checkedDoors.Add(target.ObjectId);
                    GameAudio.PlayAt(GameAudio.Sounds?.DoorTouch, target.PromptPosition, 0.35f);
                    log.Emit(EventTypes.CheckedDoorHeat, new JObject { ["objectId"] = target.ObjectId, ["hot"] = hot });
                    ctx.Show(hot ? "Cửa nóng! Có thể có lửa phía sau, đừng mở." : "Cửa không nóng.", hot ? MessageTone.Danger : MessageTone.Info);
                    var heatView = target.GetComponent<DoorView>();
                    if (heatView != null)
                        heatView.ShowHeat(hot);
                    if (hot)
                    {
                        Haptics.Warn();
                        if (_motor != null && _motor.CameraRig != null)
                            _motor.CameraRig.Shake(0.045f, 0.35f);
                    }
                    else
                    {
                        Haptics.Confirm();
                    }

                    break;
                }

                case "open":
                    SetDoor(ctx, target, true, transform.position);
                    Haptics.Confirm();
                    break;

                case "close":
                    SetDoor(ctx, target, false, transform.position);
                    log.EmitObject(EventTypes.ClosedDoor, target.ObjectId);
                    break;

                case "call" when cap == Capabilities.Elevator:
                    GameAudio.PlayAt(GameAudio.Sounds?.ElevatorDing, target.PromptPosition, 0.7f);
                    log.EmitObject(EventTypes.UsedElevator, target.ObjectId);
                    if (ctx.Guided)
                        ctx.Hint("Không dùng thang máy khi có cháy. Hãy dùng cầu thang bộ.");
                    else
                        ctx.Show("Đang gọi thang máy…");
                    break;

                case "pick_up" when cap == Capabilities.BaitBelongings:
                    log.EmitObject(EventTypes.WentBackForBelongings, target.ObjectId);
                    target.Consumed = true;
                    target.gameObject.SetActive(false);
                    ctx.Show("Đã lấy đồ cá nhân");
                    if (ctx.Guided)
                        ctx.Hint("Đừng quay lại lấy đồ khi đang sơ tán.");
                    break;

                case "pick_up":
                    PickUp(ctx, target);
                    break;

                case "pull_pin":
                    target.State = "armed";
                    GameAudio.PlayAt(GameAudio.Sounds?.ExtinguisherPin, target.transform.position, 0.8f);
                    ctx.Show("Đã rút chốt bình chữa cháy");
                    ctx.Hint("Giữ nút Phun và hướng vào gốc lửa.");
                    break;

                case "wet_item":
                    if (_held != null && _held.CapabilityId == Capabilities.ItemTowel)
                    {
                        _held.State = "wet";
                        GameAudio.PlayAt(GameAudio.Sounds?.TapRunning, target.PromptPosition, 0.6f, 2.5f);
                        _towelWetUntil = _held.TryGetFloat("dryAfterSeconds", out var dry) ? ctx.Time + dry : float.MaxValue;
                        log.Emit(EventTypes.ItemWetted, new JObject { ["objectId"] = target.ObjectId, ["itemId"] = _held.ObjectId });
                        ctx.Show("Khăn đã ướt");
                        ctx.Hint("Bấm Che mũi khi đi qua vùng có khói.");
                    }

                    break;

                case "cover_nose":
                    CoverNose(ctx, target);
                    break;

                case "call" when cap == Capabilities.NpcGuide:
                    ctx.Simulation.Enqueue(new HazardCommand { Kind = HazardCommandKind.CallNpc, ObjectId = target.SimulationId });
                    ctx.Show("Bạn đã gọi người này");
                    break;

                case "lead":
                    ctx.Simulation.Enqueue(new HazardCommand { Kind = HazardCommandKind.LeadNpc, ObjectId = target.SimulationId });
                    log.EmitObject(EventTypes.NpcGuided, target.ObjectId);
                    ctx.Show("Người này đang đi theo bạn", MessageTone.Success);
                    break;
            }
        }

        /// <summary>
        /// Touch / open / close as a body action: turn to the door, reach the leaf (back of the hand for heat, the
        /// handle to open or close), act at the moment of contact, then lower the hand. Movement pauses meanwhile.
        /// </summary>
        private IEnumerator DoorGesture(AvailableAction action, Interactable door)
        {
            Busy = true;
            if (_motor != null)
                _motor.ActionLock = true;
            try
            {
                var view = door.GetComponent<DoorView>();
                var touch = action.Id == "touch";
                Vector3 Point() => view != null ? view.HandPoint(transform.position, handle: !touch) : door.PromptPosition;

                // Face the leaf.
                var look = Point() - transform.position;
                look.y = 0f;
                if (look.sqrMagnitude > 1e-4f)
                {
                    var from = transform.rotation;
                    var to = Quaternion.LookRotation(look);
                    for (var t = 0f; t < 1f; t += Time.deltaTime / 0.16f)
                    {
                        transform.rotation = Quaternion.Slerp(from, to, t);
                        yield return null;
                    }

                    transform.rotation = to;
                }

                var hot = false;
                void Contact()
                {
                    if (touch && Ctx != null)
                        hot = DoorIsHot(Ctx, door);
                    Execute(action, door);
                }

                if (_gestures != null)
                    yield return _gestures.Reach(Point(), touch ? 0.32f : 0.24f, touch ? 0.8f : 0.3f, Contact, () => hot);
                else
                    Contact();
            }
            finally
            {
                Busy = false;
                if (_motor != null)
                    _motor.ActionLock = false;
            }
        }

        private static bool DoorIsHot(GameContext ctx, Interactable door)
        {
            var grid = ctx.Simulation.Grid;
            var p = door.transform.position;
            var hottest = 0f;
            var forward = door.transform.forward;
            foreach (var side in new[] { forward, -forward })
            {
                var s = grid.Sample(new float3(p.x + side.x * 0.75f, p.y + 0.1f, p.z + side.z * 0.75f));
                if (s.Valid)
                    hottest = Mathf.Max(hottest, s.HeatC);
            }

            return hottest > 60f;
        }

        private static void SetDoor(GameContext ctx, Interactable door, bool open, Vector3 opener)
        {
            door.State = open ? "open" : "closed";
            var s = GameAudio.Sounds;
            if (s != null)
            {
                var wood = door.CapabilityId == Capabilities.DoorStandard;
                var clip = open
                    ? wood ? RuntimeAssets.SoundSet.Pick(s.DoorOpenWood, s.DoorOpen) : s.DoorOpen
                    : wood ? RuntimeAssets.SoundSet.Pick(s.DoorCloseWood, s.DoorClose) : s.DoorClose;
                GameAudio.PlayAt(clip, door.PromptPosition, 0.75f);
            }
            var view = door.GetComponent<DoorView>();
            if (view != null)
                view.SetOpen(open, opener);
            ctx.Simulation.Enqueue(new HazardCommand { Kind = HazardCommandKind.SetDoorOpen, ObjectId = door.SimulationId, Flag = open });
        }

        private void PickUp(GameContext ctx, Interactable item)
        {
            if (_held != null)
                Drop();
            _held = item;
            _sprayAudio = null;
            item.Held = true;
            if (item.CapabilityId == Capabilities.ExtinguisherPortable)
            {
                _dischargeLeft = item.GetFloat("dischargeSeconds", 15f);
                _largeFireReported = false;
            }

            var anchor = handAnchor != null ? handAnchor : transform;
            item.transform.SetParent(anchor, false);
            item.transform.localPosition = handAnchor != null ? Vector3.zero : new Vector3(0.3f, 0.9f, 0.35f);
            item.transform.localRotation = Quaternion.identity;
            foreach (var c in item.GetComponentsInChildren<Collider>())
                c.enabled = false;
            ctx.Show(item.CapabilityId == Capabilities.ItemTowel ? "Đã nhặt khăn" : "Đã lấy bình chữa cháy");
        }

        private void Drop()
        {
            if (_held == null)
                return;
            if (_sprayActive)
                EndSpray();
            UncoverNose();
            var item = _held;
            _held = null;
            item.Held = false;
            item.transform.SetParent(null, true);
            var pos = transform.position + transform.forward * 0.5f;
            item.transform.position = new Vector3(pos.x, transform.position.y + 0.05f, pos.z);
            foreach (var c in item.GetComponentsInChildren<Collider>())
                c.enabled = true;
        }

        private void CoverNose(GameContext ctx, Interactable towel)
        {
            if (_vitals == null)
                return;
            // Effect strength comes from approved scenario content; without it the towel has no effect.
            _vitals.CoveredSmokeMultiplier = towel.TryGetFloat("smokeIntakeMultiplierWhenCovered", out var m) ? Mathf.Clamp01(m) : 1f;
            _vitals.NoseCovered = true;
            if (_vitals.LocalSmoke > 0.05f)
                ctx.Log.EmitObject(EventTypes.NoseCoveredInSmoke, towel.ObjectId);
            else
                _coverEventPending = true;
            ctx.Show("Đang che mũi bằng khăn ướt");
        }

        private void UncoverNose()
        {
            if (_vitals == null)
                return;
            _vitals.NoseCovered = false;
            _vitals.CoveredSmokeMultiplier = 1f;
            _coverEventPending = false;
        }

        private void UpdateHeldItem(GameContext ctx)
        {
            if (_held == null)
                return;
            if (_held.CapabilityId == Capabilities.ItemTowel)
            {
                if (_held.State == "wet" && ctx.Time >= _towelWetUntil)
                {
                    _held.State = "dry";
                    if (_vitals != null && _vitals.NoseCovered)
                    {
                        UncoverNose();
                        ctx.Show("Khăn đã khô", MessageTone.Warning);
                    }
                }

                if (_coverEventPending && _vitals != null && _vitals.NoseCovered && _vitals.LocalSmoke > 0.05f)
                {
                    _coverEventPending = false;
                    ctx.Log.EmitObject(EventTypes.NoseCoveredInSmoke, _held.ObjectId);
                }
            }
        }

        private void BeginSpray()
        {
            if (_held == null || _held.State != "armed" || _dischargeLeft <= 0f || Ctx == null)
                return;
            _sprayActive = true;
            _sprayStart = Ctx.Time;
            if (_sprayAudio == null)
                _sprayAudio = _held.GetComponent<AudioSource>();
            if (_sprayAudio == null)
                _sprayAudio = GameAudio.Loop(GameAudio.Sounds?.ExtinguisherSpray, _held.gameObject, 0.7f);
            if (_sprayAudio != null)
                _sprayAudio.Play();
            // Powder cloud from the nozzle, blown the way the player faces.
            if (_sprayFx == null && Ctx.Assets != null && Ctx.Assets.SprayPrefab != null)
                _sprayFx = Instantiate(Ctx.Assets.SprayPrefab, _held.transform);
            if (_sprayFx != null)
            {
                AimSpray();
                _sprayFx.Play(true);
            }
        }

        private void AimSpray()
        {
            if (_sprayFx == null || _held == null)
                return;
            var r = _held.GetComponentInChildren<Renderer>();
            var from = r != null ? r.bounds.center + Vector3.up * 0.1f : _held.transform.position;
            _sprayFx.transform.SetPositionAndRotation(from + transform.forward * 0.25f, Quaternion.LookRotation(transform.forward + Vector3.down * 0.25f));
        }

        private void TickSpray(GameContext ctx)
        {
            if (!_sprayActive)
                return;
            AimSpray();
            _dischargeLeft -= Time.deltaTime;
            if (_dischargeLeft <= 0f)
            {
                _held.State = "empty";
                EndSpray();
                ctx.Show("Bình đã hết", MessageTone.Warning);
                return;
            }

            var strength = _held.GetString("agent") switch
            {
                "Powder" => 2.2f,
                "Foam" => 1.8f,
                _ => 1.4f,
            };
            var p = transform.position + Vector3.up * 0.6f;
            ctx.Simulation.Enqueue(new HazardCommand
            {
                Kind = HazardCommandKind.Extinguish,
                Position = new float3(p.x, transform.position.y + 0.1f, p.z),
                Direction = new float3(transform.forward.x, 0f, transform.forward.z),
                Radius = 3f,
                Strength = strength,
            });

            if (!_largeFireReported && LargeFireAhead(ctx))
            {
                _largeFireReported = true;
                ctx.Log.EmitObject(EventTypes.ExtinguishAttemptLargeFire, _held.ObjectId);
                ctx.Hint("Đám cháy quá lớn. Hãy rời đi và đóng cửa lại.");
            }
        }

        private bool LargeFireAhead(GameContext ctx)
        {
            var grid = ctx.Simulation.Grid;
            var layout = grid.Layout;
            if (!layout.TryCellAt(transform.position + transform.forward * 1.5f, out var center))
                return false;
            var c = layout.Decode(center);
            var burning = 0;
            for (var dz = -6; dz <= 6; dz++)
            for (var dx = -6; dx <= 6; dx++)
            {
                if (layout.InBounds(c.y, c.x + dx, c.z + dz) && grid.Fire[layout.Index(c.y, c.x + dx, c.z + dz)] > 0.5f)
                    burning++;
            }

            // About 4 m² of fully developed fire is beyond a portable extinguisher in this training model.
            return burning * layout.CellSize * layout.CellSize > 4f;
        }

        private void EndSpray()
        {
            if (!_sprayActive || Ctx == null)
                return;
            _sprayActive = false;
            if (_sprayAudio != null)
                _sprayAudio.Stop();
            if (_sprayFx != null)
                _sprayFx.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            var duration = Mathf.Max(0f, Ctx.Time - _sprayStart);
            Ctx.Log.Emit(EventTypes.ExtinguisherUsed, new JObject { ["objectId"] = _held != null ? _held.ObjectId : "", ["durationSeconds"] = System.Math.Round(duration, 2) });
        }

        public bool Spraying => _sprayActive;
    }
}
