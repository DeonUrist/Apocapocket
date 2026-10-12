using System;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Apocapocket
{
    // (2.0.7) Weapon watchdog. Players got stuck with the weapon hints (Grenade / Drop / Unequip) on screen, nothing in the hands and
    // no way to change weapon. The game's Weapons FSM can dead-end:
    //  - `reEquip` picks the slot from weapon1..3_Bool / weaponOff_Bool after `useAid` (grenade, bandage, food, held item); with none
    //    of them set it has no way out and reads no keys (arms hidden, hints left on). `checkHandItem` dead-ends the same way;
    //  - a held item resends Deactivate every frame: a "held" item that is gone, invisible or already back in a pocket keeps the
    //    weapons in `useAid` for good;
    //  - a `Slot N` state whose WeaponInHand never moved the weapons into HandItemUse shows the hints without arms.
    // Once a second (a few string compares) the state is sampled; a condition that lasts 3 s is repaired. A repair that does not
    // stick is tried once more holstered, then left alone until the state changes. Never in a vehicle, during an inventory
    // operation, a save / load, a pause or a menu. 2.0.9 also observes native item animations before any repair. New stale-aid
    // recovery requires completed/idle animations plus a recent weapon request or observed smoking completion; it never
    // changes smoking progress, effects, item quantities, pause flags or save data. 2.0.10 distinguishes the lighting
    // animation from the continuing lit on/smoke states and keeps recovery/snapshot messages verbose-only.
    internal sealed partial class Runner
    {
        private const float WatchEvery = 1f, StuckFor = 3f;
        private float _watchAt, _stuckSince = -1f;
        private string _stuckWhy, _repairedWhy;
        private int _repairs;
        private bool _smoking;
        private float _afterSmokeUntil = -1f, _requestUntil = -1f;
        private int _requestedWeapon = -1;
        private bool _aidsIdle;
        private FsmBool _itemAnimation;
        private float _diagnosticAt;

        internal void ResetWatchdog()
        {
            _watchAt = 0f; _stuckSince = -1f; _stuckWhy = null; _repairedWhy = null; _repairs = 0;
            _smoking = false; _afterSmokeUntil = _requestUntil = -1f; _requestedWeapon = -1;
            _aidsIdle = false; _itemAnimation = null;
            _diagnosticAt = 0f;
        }

        private void NoteWeaponRequest(int target)
        {
            _requestedWeapon = target >= 0 && target < Unlocked && Slots[target].Kind == Kind.Weapon && Slots[target].Content != null ? target : -1;
            _requestUntil = _requestedWeapon >= 0 ? Time.unscaledTime + 60f : -1f;
        }

        private void Watchdog()
        {
            float now = Time.unscaledTime;
            if (now < _watchAt) return;
            _watchAt = now + WatchEvery;
            TraceWeaponRequest();
            if (CurrentOp != null || _queued != null || Save.Busy || !GameplayActive() || !Refs.Weapons.enabled
                || Refs.InCar != null && State(Refs.InCar) != "" && State(Refs.InCar) != "OnFoot") { _stuckSince = -1f; return; }   // on foot only
            bool smoking;
            bool activeAid = InspectAids(out smoking);
            if (_smoking && !smoking && _aidsIdle) _afterSmokeUntil = now + 60f;
            _smoking = smoking;
            // Protect actual item animations. A lit joint's on/smoke states keep burning and reading
            // Smoke input after the lighting arms are hidden; they do not require weapons to stay off.
            if (activeAid) { ResetStuckSample(); return; }
            string why = StuckReason();
            if (why == null) { _stuckSince = -1f; _stuckWhy = null; _repairedWhy = null; _repairs = 0; return; }   // healthy
            if (_stuckSince < 0f || why != _stuckWhy) { _stuckSince = now; _stuckWhy = why; return; }
            float wait = StuckFor;
            if (why.StartsWith("after item use:", StringComparison.Ordinal))
            {
                var nativeWait = Refs.Weapons.FsmVariables.GetFsmFloat("WaitReEquip");
                // Honour the game's own delay, plus a margin; reject invalid/modded extreme values.
                if (nativeWait != null && (float.IsNaN(nativeWait.Value) || float.IsInfinity(nativeWait.Value) || nativeWait.Value > 30f)) return;
                wait = Math.Max(5f, (nativeWait != null ? Math.Max(0f, nativeWait.Value) : 2.1f) / Time.timeScale + StuckFor);
            }
            if (now - _stuckSince < wait) return;
            _stuckSince = -1f;
            if (why == _repairedWhy && _repairs >= 2) return;   // two repairs did not help: leave it until the state changes
            if (why != _repairedWhy) _repairs = 0;
            _repairedWhy = why; _repairs++;
            try { Repair(why, _repairs >= 2); }
            catch (Exception e) { Plugin.Log.LogWarning("Weapon watchdog: repair failed: " + e.Message); }
        }

        // null = fine
        private string StuckReason()
        {
            string s = State(Refs.Weapons);
            bool context = Time.unscaledTime < _afterSmokeUntil || Time.unscaledTime < _requestUntil;
            if (_aidsIdle && context && !InHandState() && HandUseClear())
            {
                if (_itemAnimation != null && _itemAnimation.Value) return "after item use: stale animBool";
                if (s == "useAid") return "after item use: useAid without an active item animation";
                var hand = Fsms.Find(Refs.Weapons.gameObject, "WeaponInHand");
                string hs = State(hand);
                if (s.StartsWith("Slot ", StringComparison.Ordinal) && hand != null && hand.Fsm != null && hand.Fsm.Initialized
                    && (hs == "off" || hs == "Deactivate" || hs == "hands") && Selected >= 0 && Selected < Unlocked
                    && Slots[Selected].Kind == Kind.Weapon && Slots[Selected].Content != null)
                    return "after item use: selected weapon has no active weapon handler";
            }
            if (s == "reEquip" || s == "checkHandItem") return s;                 // both resolve within a frame when they can
            if (InHandState())
            {
                var held = HeldItem();
                if (held == null) return "hand holds nothing";
                if (InCustody(held)) return "held item is in a pocket";
                if (!Visible(held)) return "held item invisible";
                return null;                                                         // holding a real item: the game keeps weapons away
            }
            if (s.StartsWith("Slot ", StringComparison.Ordinal) && Refs.HandItemUse.childCount == 0) return s + " without arms";
            return null;
        }

        private void Repair(string why, bool holster)
        {
            if (why.StartsWith("after item use:", StringComparison.Ordinal))
            {
                RepairAfterAid(why, holster);
                return;
            }
            if (InHandState())
            {
                // the hand first; the weapons come back by themselves once nothing is held (useAid -> reEquip)
                var held = HeldItem();
                string what;
                if (held == null || InCustody(held))
                {
                    var item = Refs.Grab.FsmVariables.GetFsmGameObject("Item");
                    if (item != null) item.Value = null;                             // notHold must not pull a pocketed item out
                    Refs.Grab.SendEvent("not_Hold");
                    what = "released the hand";
                }
                else
                {
                    var vis = Fsms.Find(held, "Visibility");
                    if (vis != null) vis.SendEvent("Activate");
                    else { var r = held.GetComponent<Renderer>(); if (r != null) r.enabled = true; }
                    what = "made " + held.name + " visible";
                }
                if (State(Refs.Weapons) == "reEquip" || State(Refs.Weapons) == "checkHandItem") SetWeaponBools(-1);
                Plugin.V("Weapon watchdog: " + why + " for " + StuckFor + "+ s -> " + what);
                return;
            }
            string s = State(Refs.Weapons);
            int target = -1;
            if (!holster)
            {
                if (s.StartsWith("Slot ", StringComparison.Ordinal) && int.TryParse(s.Substring(5), out target)) target--;
                else
                {
                    target = -1;
                    for (int i = 0; i < 3; i++) { var b = Refs.Weapons.FsmVariables.GetFsmBool("weapon" + (i + 1) + "_Bool"); if (b != null && b.Value) target = i; }
                    if (target < 0 && Selected >= 0 && Selected < 3 && Slots[Selected].Kind != Kind.Item && Refs.Slots[Selected].childCount > 0) target = Selected;
                }
                if (target < 0 || target > 2) target = -1;
                // a pocketed item is never "drawn" - except the host of a borrowed slot 4-6 weapon (its SlotState is the parked one)
                if (target >= 0 && !(Borrow != null && target == Borrow.Host) && Slots[target].Kind == Kind.Item) target = -1;
                if (target >= 0 && !IsWeapon(SlotChild(Refs.Slots[target]))) target = -1;
            }
            SetWeaponBools(target);
            Refs.Weapons.Fsm.SetState(target >= 0 ? "Slot " + (target + 1) : "off 2");
            if (target < 0 && Borrow == null) Selected = -1;
            Plugin.V("Weapon watchdog: weapons stuck in '" + why + "' for " + StuckFor + "+ s -> " + (target >= 0 ? "slot " + (target + 1) : "holstered"));
        }

        private void ResetStuckSample()
        { _stuckSince = -1f; _stuckWhy = null; _repairedWhy = null; _repairs = 0; }

        // Read-only observation. Protect lighting and unknown/incomplete animation states. Once
        // native lighting has hidden both arms, on/smoke can continue burning alongside weapons.
        // End can remain entered due to an every-frame action; require its completed native cleanup.
        private bool InspectAids(out bool smoking)
        {
            smoking = false; _aidsIdle = false; _itemAnimation = null;
            var root = Refs.Grab.transform.Find("ItemAnim");
            if (root == null) return false; // Existing repairs still work; new recovery needs positive evidence.
            var flag = Fsms.Find(root.gameObject, "animBool");
            if (flag != null) _itemAnimation = flag.FsmVariables.GetFsmBool("animBool");
            bool busy = false, known = flag != null && flag.Fsm != null && flag.Fsm.Initialized;
            int smokers = 0;
            foreach (var f in root.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (f == null || f.FsmName != "Animation") continue;
                bool smoke = f.gameObject.name == "Weed Anim" || f.gameObject.name == "Cigar Anim" || f.gameObject.name == "Cigarette Anim";
                if (smoke) smokers++;
                else if (!f.gameObject.activeInHierarchy || !f.enabled) continue;
                string state = State(f);
                bool idle = state == "off";
                if (smoke && state != "off" && state != "End") smoking = true;
                if (smoke && (state == "on" || state == "smoke"))
                {
                    var left = f.transform.Find("arm_left"); var right = f.transform.Find("arm_right");
                    var anim = Fsms.Find(f.gameObject, "Anim");
                    var scale = f.FsmVariables.GetFsmFloat("scale");
                    string stage = State(anim);
                    idle = left != null && right != null && !left.gameObject.activeSelf && !right.gameObject.activeSelf
                        && anim != null && anim.enabled && anim.Fsm != null && anim.Fsm.Initialized && (stage == "idle" || stage == "end")
                        && scale != null && scale.Value > 0.2f && scale.Value <= 1f;
                }
                if (smoke && state == "End")
                {
                    var left = f.transform.Find("arm_left"); var right = f.transform.Find("arm_right");
                    var scale = f.FsmVariables.GetFsmFloat("scale");
                    idle = left != null && right != null && !left.gameObject.activeSelf && !right.gameObject.activeSelf
                        && scale != null && Math.Abs(scale.Value - 1f) < 0.001f;
                }
                if (!idle) { busy = true; if (smoke) smoking = true; }
                if (!f.enabled || f.Fsm == null || !f.Fsm.Initialized) known = false;
            }
            var grenade = Refs.Grab.transform.Find("QuickItems/grenade");
            var attack = grenade != null ? Fsms.Find(grenade.gameObject, "Attack") : null;
            if (attack != null && attack.isActiveAndEnabled && State(attack) != "on") busy = true;
            _aidsIdle = known && smokers == 3 && !busy;
            return busy;
        }

        private bool HandUseClear()
        {
            for (int i = 0; i < Refs.HandItemUse.childCount; i++)
                if (Refs.HandItemUse.GetChild(i) != Refs.Weapons.transform) return false;
            return true;
        }

        private void RepairAfterAid(string why, bool holster)
        {
            bool smoking;
            if (InspectAids(out smoking) || !_aidsIdle || InHandState() || !HandUseClear()) return;
            string before = "weapons=" + State(Refs.Weapons) + ", grab=" + State(Refs.Grab)
                + ", weaponInHand=" + State(Fsms.Find(Refs.Weapons.gameObject, "WeaponInHand"))
                + ", animBool=" + (_itemAnimation != null && _itemAnimation.Value) + ", selected=" + (Selected + 1);
            int logical = Time.unscaledTime < _requestUntil ? _requestedWeapon : Selected;
            if (Borrow != null && logical < 0) logical = Borrow.Logical;
            if (holster || logical < 0 || logical >= Unlocked || Slots[logical].Content == null || Slots[logical].Kind != Kind.Weapon) logical = -1;
            // No inventory is discarded. Restore a borrowed host before the normal selection transaction.
            if (_itemAnimation != null) _itemAnimation.Value = false;
            EndBorrowNow();
            SetWeaponBools(-1); Refs.Weapons.Fsm.SetState("off 2"); Selected = -1;
            if (logical >= 0) _queued = new Op { Kind = OpKind.Select, Target = logical };
            _afterSmokeUntil = _requestUntil = -1f;
            Plugin.V("Weapon watchdog: " + why + " -> native holster" + (logical >= 0 ? ", retry slot " + (logical + 1) : "") + " (" + before + ")");
        }

        // Verbose-only, bounded snapshots explain why a failed draw is not recovered, including
        // another mod's pause/hand object or active animation. Read-only: no broad flag resets.
        private void TraceWeaponRequest()
        {
            if (!Plugin.Verbose.Value || !Ready || Time.unscaledTime >= _requestUntil || Time.unscaledTime < _diagnosticAt) return;
            _diagnosticAt = Time.unscaledTime + 10f;
            var root = Refs.Grab.transform.Find("ItemAnim");
            string aids = "";
            if (root != null)
                foreach (var f in root.GetComponentsInChildren<PlayMakerFSM>(true))
                    if (f != null && f.FsmName == "Animation") aids += " " + f.gameObject.name + "=" + State(f);
            var flag = root != null ? Fsms.Find(root.gameObject, "animBool") : null;
            var value = flag != null ? flag.FsmVariables.GetFsmBool("animBool") : null;
            var pause = Refs.GrabPause != null ? Refs.GrabPause.FsmVariables.GetFsmBool("GrabItem_Pause") : null;
            string hand = "";
            for (int i = 0; i < Refs.HandItemUse.childCount; i++) hand += " " + Refs.HandItemUse.GetChild(i).name;
            Plugin.V("Weapon recovery snapshot: request=" + (_requestedWeapon + 1) + ", selected=" + (Selected + 1)
                + ", weapons=" + State(Refs.Weapons) + ", weaponInHand=" + State(Fsms.Find(Refs.Weapons.gameObject, "WeaponInHand"))
                + ", grab=" + State(Refs.Grab) + ", animBool=" + (value != null && value.Value)
                + ", grabPause=" + (pause != null && pause.Value) + ", menu=" + State(Refs.Menu) + ", inCar=" + State(Refs.InCar)
                + ", operation=" + (CurrentOp != null ? CurrentOp.Phase.ToString() : "none") + ", saveBusy=" + Save.Busy
                + ", handUse=" + hand + ", animations=" + aids);
        }

        private void TraceBlockedInput()
        {
            if (!Plugin.Verbose.Value || Save.Busy || Time.timeScale <= 0f || Refs.Menu != null && State(Refs.Menu) != "play") return;
            int target = -1;
            for (int i = 0; i < 3; i++)
                if (ButtonDown("Weapon " + (i + 1), i == 0 ? Key.Digit1 : i == 1 ? Key.Digit2 : Key.Digit3)) target = i;
            for (int i = 0; i < ExtraSlots.Active; i++) if (Keybinds.Down(i)) target = i + 3;
            if (target < 0) return;
            NoteWeaponRequest(target);
            TraceWeaponRequest(); // Observation only; blocked input never changes the hand or selection.
        }

        private bool InHandState()
        {
            string g = State(Refs.Grab);
            return g == "Grab" || g == "ItemInHand" || g == "Rotate" || g == "Forward" || g == "Backward";
        }

        private bool InCustody(GameObject held)
        {
            for (int i = 0; i < Slots.Length; i++) if (Slots[i].Content == held) return true;
            var p = held.transform.parent;
            if (p == null) return false;
            if (ExtraSlots.Staging != null && p == ExtraSlots.Staging) return true;
            for (int i = 0; i < Refs.Slots.Length; i++) if (Refs.Slots[i] != null && p == Refs.Slots[i]) return true;
            return false;
        }

        private static bool Visible(GameObject go)
        {
            foreach (var r in go.GetComponentsInChildren<Renderer>()) if (r.enabled) return true;
            return false;
        }
    }
}
