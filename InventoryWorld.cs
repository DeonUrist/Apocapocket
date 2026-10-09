using System;
using System.Collections.Generic;
using HutongGames.PlayMaker;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Apocapocket
{
    internal sealed partial class Runner
    {
        internal static bool IsWeapon(GameObject item) { return Fsms.Find(item, "weaponType") != null; }

        internal static SlotState Capture(GameObject item)
        {
            var slot = new SlotState { Content = item, Kind = IsWeapon(item) ? Kind.Weapon : Kind.Item, Layer = item.layer };
            foreach (var r in item.GetComponentsInChildren<Renderer>(true)) if (r != null && r.enabled) slot.Renderers.Add(r);
            foreach (var c in item.GetComponentsInChildren<Collider>(true))
                if (c != null) { if (c.enabled) slot.Colliders.Add(c); slot.Triggers[c] = c.isTrigger; }
            Vector3 p; Quaternion q;
            if (ApocasaverBridge.TryGetPose(item.name, out p, out q)) { slot.HasPose = true; slot.Position = p; slot.Rotation = q; }
            return slot;
        }

        internal static Rigidbody EnsureBody(GameObject item)
        {
            var rb = item.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = item.AddComponent<Rigidbody>();
                var lp = Fsms.Find(item, "LockPhysics");
                var mass = lp != null ? lp.FsmVariables.GetFsmFloat("mass") : null;
                if (mass != null && mass.Value > 0f) rb.mass = mass.Value;
            }
            return rb;
        }

        internal static void EnableFsm(GameObject item, string name, bool enabled)
        { var f = Fsms.Find(item, name); if (f != null) f.enabled = enabled; }

        internal static void ParkLock(GameObject item, bool disable)
        {
            var lp = Fsms.Find(item, "LockPhysics");
            if (lp != null)
            {
                if (State(lp) != "" && State(lp) != "off") lp.Fsm.SetState("off");
                lp.enabled = !disable;
            }
        }

        internal static void RestoreVisuals(SlotState slot)
        {
            if (slot.Content == null) return;
            if (slot.Kind == Kind.Weapon)
            {
                // Also repairs legacy 1.x saves whose extra components were disabled by the old item recipe.
                foreach (var r in slot.Content.GetComponentsInChildren<Renderer>(true)) if (r != null) r.enabled = true;
                foreach (var c in slot.Content.GetComponentsInChildren<Collider>(true)) if (c != null) c.enabled = true;
                var vis = Fsms.Find(slot.Content, "Visibility"); if (vis != null) vis.SendEvent("Activate");
                var cols = Fsms.Find(slot.Content, "Colliders"); if (cols != null) cols.SendEvent("CollidersActivate");
            }
            else
            {
                foreach (var r in slot.Renderers) if (r != null) r.enabled = true;
                foreach (var c in slot.Colliders) if (c != null) c.enabled = true;
            }
            foreach (var pair in slot.Triggers) if (pair.Key != null) pair.Key.isTrigger = pair.Value;
        }

        internal void Pocket(SlotState slot, Transform parent)
        {
            var item = slot.Content;
            if (item == null || parent == null) return;
            EnableFsm(item, "Attach", false); EnableFsm(item, "CheckBool", false);
            item.transform.SetParent(parent, false);
            item.transform.localPosition = Vector3.zero; item.transform.localRotation = Quaternion.identity;
            if (slot.Kind == Kind.Weapon)
            {
                item.layer = 0;
                var vis = Fsms.Find(item, "Visibility"); if (vis != null) vis.SendEvent("Deactivate");
                var root = item.GetComponent<Renderer>(); if (root != null) root.enabled = false;
                var cols = Fsms.Find(item, "Colliders"); if (cols != null) cols.SendEvent("CollidersDisable");
                var collider = item.GetComponent<Collider>(); if (collider != null) collider.isTrigger = true;
            }
            else
            {
                foreach (var r in slot.Renderers) if (r != null) r.enabled = false;
                foreach (var c in slot.Colliders) if (c != null) c.enabled = false;
            }
            var rb = EnsureBody(item);
            if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            rb.useGravity = false; rb.isKinematic = true;
            RestartGuard.Set(item, true); ParkLock(item, true);
        }

        internal void MakeWorld(SlotState slot, int index, bool impulse)
        {
            var item = slot.Content;
            if (item == null) return;
            RestoreVisuals(slot);
            item.transform.SetParent(null, true);
            var drop = Refs.Grab.transform.Find("ItemDrop");
            item.transform.position = (drop != null ? drop.position : Refs.Grab.transform.position + Refs.Grab.transform.forward)
                + Refs.Grab.transform.right * ((index - 2.5f) * 0.08f);
            item.layer = slot.Kind == Kind.Weapon ? 9 : slot.Layer;
            foreach (var c in item.GetComponentsInChildren<Collider>(true)) if (c != null) c.isTrigger = false;
            var rb = EnsureBody(item); rb.isKinematic = false; rb.useGravity = true;
            rb.angularVelocity = Vector3.zero;
            rb.velocity = impulse ? (Refs.Grab.transform.forward + Vector3.up * 0.4f).normalized * 2f + UnityEngine.Random.insideUnitSphere * 0.3f : Vector3.zero;
            RestartGuard.Set(item, false);
            EnableFsm(item, "Attach", true); EnableFsm(item, "CheckBool", true);
            var lp = Fsms.Find(item, "LockPhysics");
            if (lp != null) { lp.enabled = true; lp.SendEvent("LockPhysics_OFF"); }
        }

        private void EjectRange(int start, string reason)
        {
            for (int i = start; i < 6; i++)
            {
                if (Slots[i].Content == null) continue;
                if (i < 3 && Slots[i].Kind == Kind.Weapon)
                {
                    if (!Plugin.Enabled.Value) { RestartGuard.Set(Slots[i].Content, false); EnableFsm(Slots[i].Content, "LockPhysics", true); }
                    continue;
                }
                Plugin.Log.LogWarning("Eject " + Slots[i].Content.name + " from slot " + (i + 1) + ": " + reason);
                MakeWorld(Slots[i], i, true); Slots[i] = new SlotState();
                if (Selected == i) Selected = -1;
            }
        }

        private int DrawnSlot()
        {
            string state = State(Refs.Weapons);
            for (int i = 0; i < 3; i++) if (state == "Slot " + (i + 1)) return i;
            if (InventoryPolicy.AidState(state))
                for (int i = 0; i < 3; i++) { var b = Refs.Weapons.FsmVariables.GetFsmBool("weapon" + (i + 1) + "_Bool"); if (b != null && b.Value) return i; }
            return -1;
        }
        private bool IsDrawn(int slot)
        { return Borrow != null ? Borrow.Logical == slot && BorrowIntact() : slot < 3 && Slots[slot].Kind == Kind.Weapon && DrawnSlot() == slot; }

        internal void SetWeaponBools(int selected)
        {
            var v = Refs.Weapons.FsmVariables;
            var off = v.GetFsmBool("weaponOff_Bool"); if (off != null) off.Value = selected < 0;
            for (int i = 0; i < 3; i++) { var b = v.GetFsmBool("weapon" + (i + 1) + "_Bool"); if (b != null) b.Value = i == selected; }
        }
        internal void Holster()
        { SetWeaponBools(-1); Refs.Weapons.SendEvent("back"); }

        private void BeginBorrow(int logical)
        {
            var op = CurrentOp;
            int host = -1;
            for (int i = 0; i < 3; i++) if (Slots[i].Content == null) { host = i; break; }
            if (host < 0) host = op.Host >= 0 && op.Host < 3 ? op.Host : 0;
            op.Host = host;
            Borrow = new Borrow { Logical = logical, Host = host, Parked = Slots[host] };
            // The logical model does not change during the physical swap.
            if (Borrow.Parked.Content != null) Pocket(Borrow.Parked, ExtraSlots.Staging);
            SetPhase(Phase.InsertBorrow); // host remains empty for this frame
        }

        private bool BorrowIntact()
        {
            var weapon = Slots[Borrow.Logical].Content;
            if (weapon == null || weapon.transform.parent != Refs.Slots[Borrow.Host]) return false;
            string state = State(Refs.Weapons);
            return state == "Slot " + (Borrow.Host + 1) || InventoryPolicy.AidState(state);
        }
        private void ReturnBorrowWeapon()
        {
            if (Borrow == null) return;
            var slot = Slots[Borrow.Logical];
            if (slot.Content != null && (slot.Content.transform.parent == Refs.Slots[Borrow.Host] || slot.Content.transform.parent == Refs.Slots[Borrow.Logical])) Pocket(slot, Refs.Slots[Borrow.Logical]);
            else
            {
                if (slot.Content != null) ReleaseCustody(slot.Content);
                Slots[Borrow.Logical] = new SlotState();
            }
            if (Selected == Borrow.Logical) Selected = -1;
        }
        private void RestoreBorrowHost()
        {
            if (Borrow == null) return;
            if (Borrow.Parked.Content != null) Pocket(Borrow.Parked, Refs.Slots[Borrow.Host]);
            Slots[Borrow.Host] = Borrow.Parked;
            // Re-run full even for synchronous save/disable restoration; no stale borrowed icon survives.
            EnterSlotFull(Borrow.Host);
            ExtraSlots.ShowLogicalGameSlot(Borrow.Host, Borrow.Parked.Content, false);
            Borrow = null;
        }
        internal void EndBorrowNow()
        { if (Borrow != null) { Holster(); ReturnBorrowWeapon(); RestoreBorrowHost(); } }

        private void EnterSlotFull(int index)
        {
            var f = Fsms.Find(Refs.Slots[index].gameObject, "SlotEmptyFull");
            if (f != null && f.Fsm.Initialized) f.Fsm.SetState(Refs.Slots[index].childCount > 0 ? "full" : "empty");
        }

        internal int FirstFree()
        {
            var kinds = new Kind[6];
            for (int i = 0; i < 6; i++) kinds[i] = Slots[i].Content != null ? Slots[i].Kind : Kind.Empty;
            return InventoryPolicy.FirstFree(kinds, Unlocked, Borrow != null ? Borrow.Host : -1);
        }
        internal bool CanPocket(GameObject item)
        {
            if (item == null) return false;
            string reason = null;
            string prefab = Icons.PrefabName(item);
            foreach (string blocked in Plugin.Blacklist.Value.Split(';'))
                if (string.Equals(prefab, blocked, StringComparison.OrdinalIgnoreCase)) reason = "container/tool";
            var fb = Fsms.Find(item, "ForBuy");
            if (fb != null) { var v = fb.FsmVariables.GetFsmInt("ForBuyInt"); if (v != null && v.Value == 1) reason = "merchant stock"; }
            foreach (var f in item.GetComponentsInChildren<PlayMakerFSM>(true))
                if (f.transform != item.transform && (f.FsmName == "saveItemVar" || f.FsmName == "ItemName")) { reason = "contains a saveable item"; break; }
            if (reason == null) return true;
            if (_refused.Add(item.name)) Plugin.Log.LogWarning("Refused " + item.name + ": " + reason);
            return false;
        }

        internal bool UseOnWorldWeapon(PlayMakerFSM owner)
        {
            if (!Ready || !Plugin.Enabled.Value || !GameplayActive() || Save.Busy) return false;
            if (State(owner) != "over") return true;
            var iv = owner.FsmVariables.GetFsmGameObject("Item");
            var item = iv != null ? iv.Value : null;
            int target = FirstFree();
            SetPickupHint(owner, target);
            if (item == null || !IsWeapon(item) || !CanPocket(item)) return true;
            if (!ButtonDown("Use", Key.None) || _useFrame == Time.frameCount || target < 0) return true;
            _useFrame = Time.frameCount;
            Submit(new Op { Kind = OpKind.Pickup, Target = target, Pickup = item });
            return true;
        }
        private void PlacePickup()
        {
            var op = CurrentOp;
            var item = op.Pickup;
            if (item == null || !CanPocket(item)) { Finish(); return; }
            for (int i = 0; i < 6; i++) if (Slots[i].Content == item) { Finish(); return; }
            int target = FirstFree();
            if (target < 0) { Finish(); return; }
            op.Target = target;
            bool draw = Selected == target;
            if (HeldItem() != null) Refs.Grab.SendEvent("not_Hold");
            Slots[target] = Capture(item); Pocket(Slots[target], Refs.Slots[target]);
            if (draw)
            {
                if (target < 3) { op.Host = target; SetPhase(Phase.Draw); }
                else { Holster(); SetPhase(Phase.WaitHolster); op.Kind = OpKind.Select; }
            }
            else Finish();
        }
        private void SetPickupHint(PlayMakerFSM owner, int target)
        {
            var v = owner.FsmVariables.GetFsmGameObject("UI_ItemUse");
            if (v == null || v.Value == null) return;
            var t = v.Value.GetComponent<Text>() ?? v.Value.GetComponentInChildren<Text>(true);
            if (t != null) t.text = target < 0 ? "Slots full" : "Take Weapon to Slot " + (target + 1) + " (F)";
        }
        private void UpdatePickupHints()
        {
            var use = Fsms.Find(Refs.Weapons.gameObject, "UseWeapon");
            if (State(use) == "over") SetPickupHint(use, FirstFree());
            for (int i = 0; i < 3; i++) { use = Fsms.Find(Refs.Slots[i].gameObject, "UseWeapon"); if (State(use) == "over") SetPickupHint(use, FirstFree()); }
        }

        internal bool SlotHoldsItem(Transform slot)
        {
            if (!Ready) return false;
            for (int i = 0; i < 3; i++)
                if (Refs.Slots[i] == slot) return Borrow != null && Borrow.Host == i ? false : Slots[i].Kind == Kind.Item;
            return false;
        }
        internal void BeforeGameDrop(Transform host)
        {
            if (!Ready) return;
            for (int i = 0; i < 3; i++)
                if (Refs.Slots[i] == host)
                {
                    var slot = Borrow != null && Borrow.Host == i ? Slots[Borrow.Logical] : Slots[i];
                    if (slot.Content == null || slot.Kind != Kind.Weapon) return;
                    RestoreVisuals(slot); ReleaseCustody(slot.Content);
                }
        }
        private static void ReleaseCustody(GameObject item)
        {
            RestartGuard.Set(item, false); EnableFsm(item, "LockPhysics", true);
            EnableFsm(item, "Attach", true); EnableFsm(item, "CheckBool", true);
        }

        internal void AdoptVanilla(bool migrate)
        {
            if (Refs == null || !Refs.Valid) return;
            for (int i = 0; i < (migrate ? 6 : 3); i++)
            {
                if (Borrow != null && (Borrow.Host == i || Borrow.Logical == i) || Slots[i].Content != null) continue;
                GameObject item = SlotChild(Refs.Slots[i]);
                if (item == null || !migrate && !IsWeapon(item)) continue;
                if (migrate && (!IsWeapon(item) || i >= 3))
                {
                    foreach (var r in item.GetComponentsInChildren<Renderer>(true)) if (r != null) r.enabled = true;
                    foreach (var c in item.GetComponentsInChildren<Collider>(true)) if (c != null) c.enabled = true;
                }
                Slots[i] = Capture(item);
                if (Plugin.Enabled.Value) Pocket(Slots[i], Refs.Slots[i]);
            }
        }
        /// Content of a slot holder / game slot: anything with a Rigidbody or Collider counts (the slot belongs to this mod or
        /// the game's weapon slots, so no item-FSM requirement - an empty alcohol canister has only an ID FSM).
        internal static GameObject SlotChild(Transform parent)
        {
            if (parent == null) return null;
            foreach (Transform t in parent)
                if (t.GetComponent<Rigidbody>() != null || t.GetComponent<Collider>() != null || IsWeapon(t.gameObject)) return t.gameObject;
            return null;
        }

        internal static GameObject ChildItem(Transform parent)
        {
            if (parent == null) return null;
            foreach (Transform t in parent)
                if (Fsms.Find(t.gameObject, "saveItemVar") != null || Fsms.Find(t.gameObject, "ItemName") != null || IsWeapon(t.gameObject)) return t.gameObject;
            return null;
        }

        private void Reconcile()
        {
            var seen = new HashSet<int>();
            for (int i = 0; i < 6; i++)
            {
                var slot = Slots[i];
                if (slot.Content == null) { slot.Kind = Kind.Empty; continue; }
                if (!seen.Add(slot.Content.GetInstanceID()))
                { Plugin.Log.LogWarning("Invariant: duplicate slot " + (i + 1)); Slots[i] = new SlotState(); continue; }
                if (CurrentOp != null && CurrentOp.Taking == slot) continue;
                if (Borrow != null && (Borrow.Host == i || Borrow.Logical == i))
                {
                    // Only the transaction may move objects during the borrow's empty-frame phases.
                    if (Borrow.Logical == i && slot.Content.transform.parent != Refs.Slots[Borrow.Host]) continue;
                    ParkLock(slot.Content, true); RestartGuard.Set(slot.Content, true); continue;
                }
                if (slot.Content.transform.parent != Refs.Slots[i])
                {
                    if (slot.Kind == Kind.Weapon)
                    { ReleaseCustody(slot.Content); Slots[i] = new SlotState(); continue; }
                    Plugin.Log.LogWarning("Invariant: restoring " + slot.Content.name + " to slot " + (i + 1));
                    Pocket(slot, Refs.Slots[i]);
                }
                RestartGuard.Set(slot.Content, true); ParkLock(slot.Content, true);
                var rb = EnsureBody(slot.Content); rb.useGravity = false; rb.isKinematic = true;
                if (slot.Kind == Kind.Item)
                {
                    foreach (var r in slot.Renderers) if (r != null) r.enabled = false;
                    foreach (var c in slot.Colliders) if (c != null) c.enabled = false;
                }
            }
            AdoptVanilla(false);
            if (CurrentOp == null && Borrow == null && Selected < 3)
            {
                int drawn = DrawnSlot();
                if (drawn >= 0 && Slots[drawn].Kind != Kind.Item) Selected = drawn;
            }
        }

        private void GuardHand()
        {
            var held = HeldItem();
            if (held != _guardedHand)
            {
                if (_guardedHand != null)
                {
                    bool pocketed = false; foreach (var s in Slots) if (s.Content == _guardedHand) pocketed = true;
                    if (!pocketed) RestartGuard.Set(_guardedHand, false);
                }
                _guardedHand = held;
            }
            if (held != null) RestartGuard.Set(held, true);
            if (held == null || held.name != _handName) { _handName = null; _handOrigin = -1; }
        }

        private void UpdateBackpack()
        {
            int n = 3;
            if (Plugin.RequireBackpack.Value)
            {
                n = 0;
                var holder = Refs.Grab.transform.Find("QuickItems/Backpack_Item");
                var item = ChildItem(holder);
                if (item != null)
                {
                    string name = Icons.PrefabName(item).ToLowerInvariant();
                    n = name.Contains("small") ? 1 : name.Contains("medium") ? 2 : name.Contains("large") || name.Contains("huge") ? 3 : 1;
                }
            }
            if (_backpack != n) { _backpack = n; _lockSince = Time.unscaledTime; Plugin.V("Backpack extra slots=" + n); }
            if (!GameplayActive()) _lockSince = Time.unscaledTime;
            ExtraSlots.Unlocked = n;
        }

        internal bool GameplayActive()
        {
            if (!Ready || Save.Loading || Save.Normalised || Time.timeScale <= 0f) return false;
            if (Refs.Menu != null && State(Refs.Menu) != "play") return false;
            // 2.0.0-2.0.5 also refused everything in the vehicle third-person camera (ThirdPerson()), so slots 4-6 were dead
            // there while the game's own slots 1-3 kept working. What matters is only that the weapon/grab FSMs are running.
            if (!Refs.Grab.gameObject.activeInHierarchy || Refs.Weapons != null && !Refs.Weapons.gameObject.activeInHierarchy) return false;
            if (Refs.GrabPause != null) { var b = Refs.GrabPause.FsmVariables.GetFsmBool("GrabItem_Pause"); if (b != null && b.Value) return false; }
            return true;
        }
        private bool ThirdPerson()
        {
            if (Refs.InCar == null || State(Refs.InCar) == "OnFoot") { _vehicleCam = null; return false; }
            if ((_vehicleCam == null || !_vehicleCam.enabled) && Time.unscaledTime >= _camAt)
            {
                _camAt = Time.unscaledTime + 0.5f;
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                    if (f != null && f.enabled && f.FsmName == "Camera" && f.gameObject.name == "DriveTrigger" && f.gameObject.scene.IsValid() && f.gameObject.activeInHierarchy) { _vehicleCam = f; break; }
            }
            if (State(_vehicleCam).StartsWith("3", StringComparison.Ordinal)) return true;
            var camera = Refs.Grab.GetComponent<Camera>();
            if (camera != null && !camera.isActiveAndEnabled || !Refs.Grab.gameObject.activeInHierarchy) return true;
            var main = Camera.main;
            return main != null && main.gameObject != Refs.Grab.gameObject && !main.transform.IsChildOf(Refs.Grab.transform);
        }
    }
}
