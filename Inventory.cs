using System;
using System.Collections.Generic;
using BepInEx;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Apocapocket
{
    internal enum Kind { Empty, Item, Weapon }

    internal sealed class SlotState
    {
        internal GameObject Content;
        internal Kind Kind;
        internal Vector3 Position;
        internal Quaternion Rotation = Quaternion.identity;
        internal bool HasPose;
        internal int Layer = 9;
        internal readonly List<Renderer> Renderers = new List<Renderer>();
        internal readonly List<Collider> Colliders = new List<Collider>();
        internal readonly Dictionary<Collider, bool> Triggers = new Dictionary<Collider, bool>();
    }

    internal sealed class Borrow
    {
        internal int Logical, Host;
        internal SlotState Parked;
    }

    internal enum OpKind { Select, Pickup, RestoreBorrow }
    internal enum Phase { Begin, ReturnBorrow, RestoreHost, WaitHolster, Plan, InsertBorrow, Draw, VerifyGrab }
    internal sealed class Op
    {
        internal OpKind Kind;
        internal Phase Phase;
        internal int Target, Host = -1, LastFrame = -1, StartedFrame;
        internal float Deadline;
        internal bool Toggle;
        internal GameObject Pickup;
        internal SlotState Taking;
    }

    /// Unity-independent policy, also exercised by the verification executable.
    internal static class InventoryPolicy
    {
        internal static int FirstFree(Kind[] kinds, int unlocked, int host)
        {
            for (int i = 0; i < Math.Min(kinds.Length, unlocked); i++)
                if (i != host && kinds[i] == Kind.Empty) return i;
            return -1;
        }
        internal static bool AidState(string state)
        { return state == "useAid" || state == "reEquip" || state == "checkHandItem"; }
        internal static bool WaitExpired(int startFrame, int frame, float deadline, float now)
        { return frame - startFrame >= 30 || now >= deadline; }
    }

    [DefaultExecutionOrder(-10000)]
    internal sealed partial class Runner : MonoBehaviour
    {
        internal static Runner Instance;
        internal readonly SlotState[] Slots = new SlotState[6];
        internal int Selected = -1;
        internal Op CurrentOp;
        internal Borrow Borrow;
        internal Refs Refs;
        internal int Unlocked { get { return 3 + ExtraSlots.Active; } }
        internal bool Ready { get { return Refs != null && Refs.Valid && Refs.Weapons.Fsm.Initialized && Refs.Grab.Fsm.Initialized; } }
        private Op _queued;
        private int _handOrigin = -1;
        private string _handName;
        private GameObject _guardedHand;
        private float _scanAt, _lockSince, _lockGraceUntil;
        private int _backpack = -1, _useFrame = -1;
        private bool _wasEnabled;
        private PlayMakerFSM _vehicleCam;
        private float _camAt;
        private readonly HashSet<string> _refused = new HashSet<string>();
        internal readonly Persistence Save = new Persistence();

        private void Awake()
        {
            Instance = this;
            for (int i = 0; i < Slots.Length; i++) Slots[i] = new SlotState();
            _wasEnabled = Plugin.Enabled.Value;
        }
        private void OnDestroy() { if (Instance == this) Instance = null; }

        private void Update()
        {
            try { Tick(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Inventory frame failed: " + e);
                if (Ready) Rollback("exception");
            }
        }

        private void Tick()
        {
            Keybinds.Tick();
            if (!Ready)
            {
                if (Time.unscaledTime < _scanAt) return;
                _scanAt = Time.unscaledTime + 0.5f;
                var found = Apocapocket.Refs.Find();
                if (!found.Valid) return;
                bool changed = Refs == null || Refs.Grab != found.Grab;
                Refs = found;
                Icons.Slots = Refs.Slots;
                if (changed)
                {
                    CurrentOp = null; _queued = null; Borrow = null; Selected = -1;
                    _handOrigin = -1; _handName = null; _backpack = -1;
                    for (int i = 0; i < 6; i++) Slots[i] = new SlotState();
                    ExtraSlots.ResetUi(); Icons.ResetUi(); Save.NewPlayer(); ResetWatchdog();
                    AdoptVanilla(true);
                }
                if (!Ready) return;
            }
            UpdateBackpack();
            Save.Tick(this);
            if (Save.Loading || Save.Normalised) return;

            RepairHeldAttach();
            bool enabled = Plugin.Enabled.Value;
            if (!enabled)
            {
                if (_wasEnabled) { Rollback("disabled"); EndBorrowNow(); EjectRange(0, "mod disabled"); }
                _wasEnabled = false;
                ExtraSlots.HideAll();
                return;
            }
            if (!_wasEnabled) { AdoptVanilla(true); _wasEnabled = true; }

            // Poll early in Update; the prefix also captures presses if an FSM runs first.
            if (GameplayActive()) PollKeys();
            if (CurrentOp != null) Advance();
            else if (Borrow != null && !BorrowIntact()) BeginOperation(new Op { Kind = OpKind.RestoreBorrow, Target = -1 });
            else if (_queued != null && GameplayActive()) { var next = _queued; _queued = null; BeginOperation(next); }

            Reconcile();
            Watchdog();
            GuardHand();
            if (_backpack >= 0 && GameplayActive() && Time.unscaledTime - _lockSince >= 1.5f && Time.unscaledTime >= _lockGraceUntil && CurrentOp == null)
            {
                bool overflow = false;
                for (int i = Unlocked; i < 6; i++) if (Slots[i].Content != null) overflow = true;
                if (overflow) { EndBorrowNow(); EjectRange(Unlocked, "backpack slot locked"); }
            }
            Icons.Tick(GameplayActive());
            UpdatePickupHints();
        }

        private void LateUpdate()
        {
            if (!Ready || Save.Busy || !Plugin.Enabled.Value) return;
            // PlayMaker writes its slot widgets in Update. Apply the logical override afterward.
            ExtraSlots.EnsureUi(Refs.Slots[1], Refs.Slots[2]);
            ExtraSlots.UpdateUi(i => Slots[i].Content, Selected);
            for (int i = 0; i < 3; i++)
                if (Slots[i].Kind == Kind.Item || (Slots[i].Content == null && Selected == i))
                    ExtraSlots.ShowLogicalGameSlot(i, Slots[i].Content, Selected == i);
            if (Borrow != null) ExtraSlots.ShowInGameSlot(Borrow.Host, Borrow.Parked.Content);
        }

        internal static string State(PlayMakerFSM f)
        { return f != null && f.Fsm != null && f.Fsm.Initialized ? f.ActiveStateName : ""; }

        internal GameObject HeldItem()
        {
            if (!Ready) return null;
            string s = State(Refs.Grab);
            if (s != "Grab" && s != "ItemInHand" && s != "Rotate" && s != "Forward" && s != "Backward") return null;
            var v = Refs.Grab.FsmVariables.GetFsmGameObject("Item");
            return v != null ? v.Value : null;
        }

        internal bool ButtonDown(string name, Key fallback)
        {
            var value = Keybinds.GameActionDown(name);
            if (value.HasValue) return value.Value;
            try { return Input.GetButtonDown(name); } catch { }
            try { return fallback != Key.None && Keyboard.current != null && Keyboard.current[fallback].wasPressedThisFrame; } catch { return false; }
        }

        private void PollKeys()
        {
            if (Time.frameCount == Plugin.HandledFrame) return;
            int target = -1;
            for (int i = 0; i < 3; i++) if (ButtonDown("Weapon " + (i + 1), i == 0 ? Key.Digit1 : i == 1 ? Key.Digit2 : Key.Digit3)) target = i;
            if (ButtonDown("Weapon off", Key.None)) target = -2;
            // Slot 4-6 keys work in vehicles too (first and third person), like the game's slots 1-3.
            for (int i = 0; i < ExtraSlots.Active; i++) if (Keybinds.Down(i)) target = i + 3;
            if (target == -1) return;
            HandleKey(target);
        }

        internal void HandleKey(int target)
        {
            if (!Ready || !GameplayActive() || Save.Busy || Time.frameCount == Plugin.HandledFrame) return;
            Plugin.HandledFrame = Time.frameCount;
            Submit(new Op { Kind = OpKind.Select, Target = target });
        }

        private void Submit(Op op)
        {
            if (CurrentOp != null) _queued = op;
            else BeginOperation(op);
        }
        internal void QueueSelection(int slot)
        {
            if (_queued != null) return;
            var held = HeldItem();
            if (held != null)
            {
                Selected = slot;
                if (Slots[slot].Content == null) { _handOrigin = slot; _handName = held.name; }
            }
            else _queued = new Op { Kind = OpKind.Select, Target = slot };
        }
        internal void ResetForLoad()
        {
            CurrentOp = null; _queued = null; Borrow = null; Selected = -1;
            _handOrigin = -1; _handName = null; _guardedHand = null;
            for (int i = 0; i < 6; i++) Slots[i] = new SlotState();
        }
        /// After a load only the "mod disabled" case ejects. Slots above the backpack's capacity are NOT judged here: the worn
        /// backpack is a saved item restored in the same load and may not be under QuickItems/Backpack_Item yet, which made
        /// 2.0.0/2.0.1 see "no backpack" and throw slots 4-6 out. The normal in-game lock rule decides later (grace below).
        internal void EjectLockedAfterLoad()
        {
            if (!Plugin.Enabled.Value) { EjectRange(0, "loaded while disabled"); return; }
            _lockSince = Time.unscaledTime; _lockGraceUntil = Time.unscaledTime + 5f;
            Plugin.Log.LogInfo("Load: slots restored (backpack extra slots seen now: " + ExtraSlots.Unlocked + "; lock check in 5 s)");
        }
        private void BeginOperation(Op op)
        {
            CurrentOp = op;
            op.Toggle = op.Kind == OpKind.Select && op.Target >= 0 && Selected == op.Target && IsDrawn(op.Target);
            if (op.Target >= 3 && Borrow == null) op.Host = DrawnSlot();
            SetPhase(Phase.Begin);
        }
        private void SetPhase(Phase phase)
        {
            CurrentOp.Phase = phase; CurrentOp.StartedFrame = Time.frameCount;
            CurrentOp.Deadline = Time.unscaledTime + 1f;
            Plugin.V("Op " + CurrentOp.Kind + " slot=" + (CurrentOp.Target + 1) + " phase=" + phase);
        }

        private void Advance()
        {
            var op = CurrentOp;
            if (op.LastFrame == Time.frameCount) return;
            op.LastFrame = Time.frameCount;
            switch (op.Phase)
            {
                case Phase.Begin:
                    if (op.Kind == OpKind.Pickup) PlacePickup();
                    else if (Borrow != null)
                    {
                        Holster(); SetPhase(Phase.ReturnBorrow);
                    }
                    else { Holster(); SetPhase(Phase.WaitHolster); }
                    break;
                case Phase.ReturnBorrow:
                    if (!HandsClear(op)) break;
                    ReturnBorrowWeapon(); SetPhase(Phase.RestoreHost);
                    break;
                case Phase.RestoreHost:
                    RestoreBorrowHost();
                    if (op.Kind == OpKind.RestoreBorrow) Finish();
                    else SetPhase(Phase.Plan);
                    break;
                case Phase.WaitHolster:
                    if (HandsClear(op)) SetPhase(Phase.Plan);
                    break;
                case Phase.Plan:
                    if (op.Kind == OpKind.Pickup) PlacePickup();
                    else PlanSelection();
                    break;
                case Phase.InsertBorrow:
                    Pocket(Slots[op.Target], Refs.Slots[op.Host]);
                    SetPhase(Phase.Draw);
                    break;
                case Phase.Draw:
                    if (!Ready || !Refs.Weapons.isActiveAndEnabled)
                    { if (InventoryPolicy.WaitExpired(op.StartedFrame, Time.frameCount, op.Deadline, Time.unscaledTime)) Rollback("draw timeout"); break; }
                    int host = op.Host >= 0 ? op.Host : op.Target;
                    // Explicit full entry guarantees the game read the inserted child regardless of component Update order.
                    EnterSlotFull(host);
                    SetWeaponBools(host);
                    Refs.Weapons.Fsm.SetState("Slot " + (host + 1));
                    Selected = op.Target; Finish();
                    break;
                case Phase.VerifyGrab:
                    if (HeldItem() == op.Taking.Content)
                    {
                        _handOrigin = op.Target; _handName = op.Taking.Content.name;
                        Slots[op.Target] = new SlotState(); Selected = op.Target; Finish();
                    }
                    else Rollback("GrabItem refused item");
                    break;
            }
        }

        private bool HandsClear(Op op)
        {
            if (Refs.HandItemUse.childCount == 0) return true;
            if (InventoryPolicy.WaitExpired(op.StartedFrame, Time.frameCount, op.Deadline, Time.unscaledTime)) Rollback("holster timeout");
            return false;
        }

        private void PlanSelection()
        {
            var op = CurrentOp;
            var held = HeldItem();
            if (held != null)
            {
                if (!CanPocket(held)) { Finish(); return; }
                if (op.Target >= 0 && Slots[op.Target].Content == null)
                { StoreHand(op.Target); Selected = -1; Finish(); return; }
                int put = _handName == held.name && _handOrigin >= 0 && _handOrigin < Unlocked && Slots[_handOrigin].Content == null ? _handOrigin : FirstFree();
                if (put >= 0) StoreHand(put);
                else { RestartGuard.Set(held, false); Refs.Grab.SendEvent("drop"); _handName = null; _handOrigin = -1; }
            }
            if (op.Target < 0 || op.Toggle) { Selected = -1; Finish(); return; }
            if (op.Target >= Unlocked) { Finish(); return; }
            var slot = Slots[op.Target];
            if (slot.Kind == Kind.Item && slot.Content != null)
            {
                // Grab may legitimately refuse during pause; rollback restores its slot on the next frame.
                op.Taking = slot;
                Grab(slot); SetPhase(Phase.VerifyGrab);
            }
            else if (slot.Kind == Kind.Weapon && slot.Content != null)
            {
                if (op.Target < 3) { op.Host = op.Target; SetPhase(Phase.Draw); }
                else BeginBorrow(op.Target);
            }
            else
            {
                Selected = op.Target;
                if (op.Target < 3) { SetWeaponBools(op.Target); Refs.Weapons.Fsm.SetState("Slot " + (op.Target + 1)); }
                Finish();
            }
        }

        private void StoreHand(int target)
        {
            var item = HeldItem();
            var slot = Capture(item);
            slot.HasPose = true; slot.Position = item.transform.localPosition; slot.Rotation = item.transform.localRotation;
            ApocasaverBridge.SetPose(item.name, slot.Position, slot.Rotation);
            Refs.Grab.SendEvent("not_Hold");
            Slots[target] = slot; Pocket(slot, Refs.Slots[target]);
            _handName = null; _handOrigin = -1;
        }

        private void Grab(SlotState slot)
        {
            var item = slot.Content;
            RestoreVisuals(slot);
            var hv = Refs.Grab.FsmVariables.GetFsmGameObject("Hand") ?? FsmVariables.GlobalVariables.GetFsmGameObject("Hand");
            Transform hand = hv != null && hv.Value != null ? hv.Value.transform : Refs.Hand;
            if (hv != null && hv.Value == null) hv.Value = hand.gameObject;
            item.transform.SetParent(hand, false);
            if (slot.HasPose) { item.transform.localPosition = slot.Position; item.transform.localRotation = slot.Rotation; }
            else
            {
                var guide = Refs.Grab.transform.Find("Guide");
                item.transform.position = guide != null ? guide.position : hand.position;
                item.transform.localRotation = Quaternion.identity;
            }
            item.layer = slot.Layer;
            var rb = EnsureBody(item); rb.isKinematic = false; rb.useGravity = false;
            ParkLock(item, false);
            // Pocket() switched these off (takeWeapon recipe); without them a taken-out attachable (spikes, bumpers...)
            // can no longer be attached to a car.
            EnableFsm(item, "Attach", true); EnableFsm(item, "CheckBool", true);
            RestartGuard.Set(item, true);
            var iv = Refs.Grab.FsmVariables.GetFsmGameObject("Item"); if (iv != null) iv.Value = item;
            var nv = Refs.Grab.FsmVariables.GetFsmString("ItemInHandName"); if (nv != null) nv.Value = "";
            Refs.Grab.Fsm.SetState("Grab");
        }

        /// Items taken out by 2.0.0 kept their Attach/CheckBool FSMs disabled even after being dropped (and saved that way).
        /// Anything in the hand is detached by definition, so re-enable them when the player holds such an item again.
        private GameObject _attachChecked;
        private void RepairHeldAttach()
        {
            var held = HeldItem();
            if (held == _attachChecked) return;
            _attachChecked = held;
            if (held == null) return;
            bool fixedAny = false;
            foreach (var n in new[] { "Attach", "CheckBool" })
            {
                var f = Fsms.Find(held, n);
                if (f != null && !f.enabled) { f.enabled = true; fixedAny = true; }
            }
            if (fixedAny) Plugin.Log.LogInfo("Re-enabled Attach/CheckBool on " + held.name + " (left disabled by 2.0.0)");
        }

        private void Finish()
        {
            Plugin.V("Op complete: selected=" + (Selected + 1) + " " + Snapshot());
            CurrentOp = null;
        }

        internal void Rollback(string reason)
        {
            if (CurrentOp == null) return;
            Plugin.Log.LogWarning("Op rollback: " + reason + " (" + CurrentOp.Kind + "/" + CurrentOp.Phase + ")");
            var taking = CurrentOp.Taking;
            if (taking != null && taking.Content != null)
            {
                if (HeldItem() == taking.Content) Refs.Grab.SendEvent("not_Hold");
                Slots[CurrentOp.Target] = taking;
                Pocket(taking, Refs.Slots[CurrentOp.Target]);
            }
            EndBorrowNow(); CurrentOp = null; Selected = -1;
        }

        private string Snapshot()
        {
            var names = new string[6];
            for (int i = 0; i < 6; i++) names[i] = (i + 1) + ":" + (Slots[i].Content != null ? Slots[i].Content.name : "empty");
            return string.Join(" | ", names);
        }
    }
}
