using System;
using UnityEngine;

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
    // operation, a save / load, a pause or a menu. Plain `useAid` (grenade spam, eating, ladders...) is the game's own business.
    internal sealed partial class Runner
    {
        private const float WatchEvery = 1f, StuckFor = 3f;
        private float _watchAt, _stuckSince = -1f;
        private string _stuckWhy, _repairedWhy;
        private int _repairs;

        internal void ResetWatchdog() { _stuckSince = -1f; _stuckWhy = null; _repairedWhy = null; _repairs = 0; }

        private void Watchdog()
        {
            float now = Time.unscaledTime;
            if (now < _watchAt) return;
            _watchAt = now + WatchEvery;
            if (CurrentOp != null || _queued != null || Save.Busy || !GameplayActive() || !Refs.Weapons.enabled
                || Refs.InCar != null && State(Refs.InCar) != "" && State(Refs.InCar) != "OnFoot") { _stuckSince = -1f; return; }   // on foot only
            string why = StuckReason();
            if (why == null) { _stuckSince = -1f; _stuckWhy = null; _repairedWhy = null; _repairs = 0; return; }   // healthy
            if (_stuckSince < 0f || why != _stuckWhy) { _stuckSince = now; _stuckWhy = why; return; }
            if (now - _stuckSince < StuckFor) return;
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
                Plugin.Log.LogWarning("Weapon watchdog: " + why + " for " + StuckFor + "+ s -> " + what);
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
            }
            SetWeaponBools(target);
            Refs.Weapons.Fsm.SetState(target >= 0 ? "Slot " + (target + 1) : "off 2");
            if (target < 0 && Borrow == null) Selected = -1;
            Plugin.Log.LogWarning("Weapon watchdog: weapons stuck in '" + why + "' for " + StuckFor + "+ s -> " + (target >= 0 ? "slot " + (target + 1) : "holstered"));
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
