using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Apocapocket
{
    /// Lets the weapon slots 1/2/3 also hold ordinary hand-carried items:
    ///  - holding an item + press N (slot N empty)            -> item is stored (hidden) in slot N, hands empty
    ///  - press N (slot N holds a stored item)                 -> item appears in your hands (game's GrabItem)
    ///  - "Weapon off" while holding a slot item               -> item hides back into its slot
    ///  - switching away while holding an item that can't go back -> item is dropped
    ///  - slots holding weapons keep working exactly as before
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocapocket";
        public const string NAME = "Apocapocket";
        public const string VERSION = "1.2.2";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<string> Blacklist;
        internal static ConfigEntry<Key> Fallback1, Fallback2, Fallback3, FallbackOff;
        internal static ConfigEntry<float> DefaultX, DefaultY, DefaultZ;
        internal static ConfigEntry<int> IconSize;
        internal static ConfigEntry<int> ExtraSlotCount;
        internal static ConfigEntry<KeyCode> Item4Key, Item5Key, Item6Key, Item4Alt, Item5Alt, Item6Alt;
        internal static ConfigEntry<bool> ExtraKeysInVehicle, RequireBackpack;

        /// Frame on which the mod consumed a Weapon N / Weapon off press (game's GetButtonDown is suppressed that frame).
        internal static int HandledFrame = -100;
        private static GameObject _runnerGo;

        private void Awake()
        {
            Log = Logger;
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "Enabled", true, "Enable item slots. When disabled the weapon keys behave exactly as before.");
            Verbose = Config.Bind("General", "VerboseLog", true, "Log every step to the BepInEx console/log.");
            Blacklist = Config.Bind("General", "BlacklistIDs", "PartAdjusterTools;box_cardboard;crate_metal;crate_plastic",
                "Items that can never be pocketed, separated by ';'. Each entry matches an item's ID string (FSM 'ID') or its prefab name (e.g. crate_metal). Crates are listed because they carry other items inside.");
            // Config files written by 1.0.3 and earlier hold the old default only; upgrade them so the crates are covered.
            if ((Blacklist.Value ?? "").Trim() == "PartAdjusterTools") { Blacklist.Value = (string)Blacklist.DefaultValue; Config.Save(); Logger.LogInfo("BlacklistIDs upgraded to the 1.0.4 default: " + Blacklist.Value); }
            DefaultX = Config.Bind("HandPose", "DefaultX", 0f, new ConfigDescription("Default hand offset X for items loaded from a save (no remembered pose).", new AcceptableValueRange<float>(-2f, 2f)));
            DefaultY = Config.Bind("HandPose", "DefaultY", 0f, new ConfigDescription("Default hand offset Y.", new AcceptableValueRange<float>(-2f, 2f)));
            DefaultZ = Config.Bind("HandPose", "DefaultZ", 0f, new ConfigDescription("Default hand offset Z (forward).", new AcceptableValueRange<float>(-2f, 3f)));
            IconSize = Config.Bind("General", "IconSize", 128, new ConfigDescription("Pixel size of the rendered item icons shown in the slots.", new AcceptableValueRange<int>(32, 512)));
            Fallback1 = Config.Bind("Keys", "FallbackSlot1", Key.Digit1, "Key polled if the game's 'Weapon 1' input axis cannot be read.");
            Fallback2 = Config.Bind("Keys", "FallbackSlot2", Key.Digit2, "Key polled if the game's 'Weapon 2' input axis cannot be read.");
            Fallback3 = Config.Bind("Keys", "FallbackSlot3", Key.Digit3, "Key polled if the game's 'Weapon 3' input axis cannot be read.");
            ExtraSlotCount = Config.Bind("ExtraSlots", "Count", 3, new ConfigDescription("Maximum number of item-only slots 4..6 (0 = off). Anything in a slot that becomes unavailable is dropped in front of you.", new AcceptableValueRange<int>(0, 3)));
            RequireBackpack = Config.Bind("ExtraSlots", "RequireBackpack", true, "Slots 4..6 are unlocked by the backpack you wear: small = slot 4, medium = 4-5, large / huge = 4-6. Off = always available (up to Count).");
            Item4Key = Config.Bind("ExtraSlots", "Item4Key", KeyCode.Alpha4, "Key for slot 4 (also shown as \"Item 4\" in the game's Controls screen; a rebind there is saved here).");
            Item4Alt = Config.Bind("ExtraSlots", "Item4AltKey", KeyCode.None, "Alternative key for slot 4.");
            Item5Key = Config.Bind("ExtraSlots", "Item5Key", KeyCode.Alpha5, "Key for slot 5.");
            Item5Alt = Config.Bind("ExtraSlots", "Item5AltKey", KeyCode.None, "Alternative key for slot 5.");
            Item6Key = Config.Bind("ExtraSlots", "Item6Key", KeyCode.Alpha6, "Key for slot 6.");
            Item6Alt = Config.Bind("ExtraSlots", "Item6AltKey", KeyCode.None, "Alternative key for slot 6.");
            ExtraKeysInVehicle = Config.Bind("ExtraSlots", "KeysInVehicle", false, "Also react to the slot 4..6 keys while sitting in a vehicle (off by default: digit keys may be bound to gears).");
            FallbackOff = Config.Bind("Keys", "FallbackWeaponOff", Key.None, "Key polled if the game's 'Weapon off' input axis cannot be read (None = disabled).");

            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);

            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureRunner("Awake");
            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) { EnsureRunner("sceneLoaded"); }

        internal static void EnsureRunner(string reason)
        {
            if (_runnerGo != null && _runnerGo.activeInHierarchy) return;
            _runnerGo = new GameObject("Apocapocket.Runner");
            _runnerGo.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_runnerGo);
            _runnerGo.AddComponent<Runner>();
            V("Runner created (" + reason + ")");
        }

        internal static void V(string s) { if (Verbose.Value) Log.LogInfo(s); }
    }

    /// Everything the mod knows about the player's slots / hand.
    internal class Refs
    {
        public PlayMakerFSM Grab;        // PlayerCamera [GrabItem]
        public PlayMakerFSM Weapons;     // HandItemUse/Weapons [Weapons]
        public Transform Hand;           // PlayerCamera/Hand
        public Transform HandItemUse;    // PlayerCamera/HandItemUse
        public Transform[] Slots = new Transform[3 + ExtraSlots.Max];   // 0..2 = game's Slot 1..3, 3..5 = mod holders Slot 4..6
        public PlayMakerFSM Menu;        // __GameManager__ [Menu]
        public PlayMakerFSM GrabPause;   // __GameManager__ [GrabItem_Pause]
        public PlayMakerFSM InCar;       // Player [InCar] (OnFoot / InCar)

        public bool Valid { get { return Grab != null && Weapons != null && Hand != null && HandItemUse != null && Slots[0] != null && Slots[1] != null && Slots[2] != null; } }

        public static Refs Find()
        {
            var r = new Refs();
            var grabs = new List<PlayMakerFSM>();
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
            {
                if (f == null || f.gameObject == null || !f.gameObject.scene.IsValid()) continue;
                string n = f.FsmName, go = f.gameObject.name;
                if (n == "GrabItem" && go == "PlayerCamera") grabs.Add(f);
                else if (n == "Weapons" && go == "Weapons" && (r.Weapons == null || f.gameObject.activeInHierarchy)) r.Weapons = f;
                else if (go == "__GameManager__" && n == "Menu") r.Menu = f;
                else if (go == "__GameManager__" && n == "GrabItem_Pause") r.GrabPause = f;
                else if (go == "Player" && n == "InCar") r.InCar = f;
            }
            foreach (var g in grabs)
                if (r.Grab == null || (r.Weapons != null && r.Weapons.transform.IsChildOf(g.transform)) || (g.gameObject.activeInHierarchy && !r.Grab.gameObject.activeInHierarchy)) r.Grab = g;
            if (grabs.Count > 1) Plugin.Log.LogWarning(grabs.Count + " GrabItem FSMs found; using " + (r.Grab != null && r.Grab.gameObject.activeInHierarchy ? "active" : "inactive") + " one");
            if (r.Grab != null)
            {
                var cam = r.Grab.transform;
                r.Hand = cam.Find("Hand");
                r.HandItemUse = cam.Find("HandItemUse");
            }
            if (r.Weapons != null)
                for (int i = 0; i < 3; i++) r.Slots[i] = r.Weapons.transform.Find("Slot " + (i + 1));
            if (r.Grab != null)
            {
                ExtraSlots.EnsureHolders(r.Grab.transform);
                for (int i = 0; i < ExtraSlots.Max; i++) r.Slots[3 + i] = ExtraSlots.Holders[i];
            }
            return r;
        }
    }

    internal static class Fsms
    {
        /// Like PlayMakerFSM.FindFsmOnGameObject but without the "Could not find FSM" warning.
        internal static PlayMakerFSM Find(GameObject go, string name)
        {
            if (go == null) return null;
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f != null && f.FsmName == name) return f;
            return null;
        }
    }

    /// Optional link to Apocasaver's per-item hand-pose store (kept in the save file). Resolved by reflection, so
    /// Apocapocket works without Apocasaver (poses then fall back to the default hand position after a load).
    internal static class ApocasaverBridge
    {
        private static bool _resolved;
        private static System.Reflection.MethodInfo _get, _set;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = asm.GetType("Apocasaver.HandPose", false);
                    if (t == null) continue;
                    _get = t.GetMethod("Get", new[] { typeof(string) });
                    _set = t.GetMethod("Set", new[] { typeof(string), typeof(string) });
                    break;
                }
                Plugin.V(_get != null ? "Apocasaver HandPose store found: pocketed item poses will be saved with the game." : "Apocasaver not present: pocketed item poses are not saved.");
            }
            catch (Exception e) { Plugin.V("Apocasaver bridge: " + e.Message); }
        }

        internal static bool TryGetPose(string itemName, out Vector3 p, out Quaternion q)
        {
            p = Vector3.zero; q = Quaternion.identity;
            Resolve();
            if (_get == null) return false;
            try
            {
                var v = _get.Invoke(null, new object[] { itemName }) as string;
                if (string.IsNullOrEmpty(v)) return false;
                var a = v.Split(',');
                if (a.Length != 7) return false;
                var f = new float[7];
                for (int i = 0; i < 7; i++) if (!float.TryParse(a[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f[i])) return false;
                p = new Vector3(f[0], f[1], f[2]); q = new Quaternion(f[3], f[4], f[5], f[6]);
                return true;
            }
            catch (Exception e) { Plugin.V("Apocasaver bridge get: " + e.Message); return false; }
        }

        internal static void SetPose(string itemName, Vector3 p, Quaternion q)
        {
            Resolve();
            if (_set == null) return;
            var c = System.Globalization.CultureInfo.InvariantCulture;
            string v = string.Join(",", new[] { p.x.ToString("R", c), p.y.ToString("R", c), p.z.ToString("R", c), q.x.ToString("R", c), q.y.ToString("R", c), q.z.ToString("R", c), q.w.ToString("R", c) });
            try { _set.Invoke(null, new object[] { itemName, v }); } catch (Exception e) { Plugin.V("Apocasaver bridge set: " + e.Message); }
        }
    }

    /// Vehicle parts (cassette, radio, headlight...) carry a CheckTag FSM whose start state reparents the object to the scene
    /// root. Switching the vehicle camera deactivates PlayerCamera, and when it comes back PlayMaker restarts every FSM on the
    /// re-enabled objects (RestartOnEnable) - so a held or pocketed vehicle part is thrown out of the hand / slot. While an
    /// item is in our custody the restart is switched off for the FSMs whose start path has side effects.
    internal static class RestartGuard
    {
        private static readonly string[] Fsms = { "CheckTag", "LockPhysics" };

        internal static void Set(GameObject item, bool guarded)
        {
            if (item == null) return;
            foreach (var f in item.GetComponents<PlayMakerFSM>())
            {
                if (f == null || f.Fsm == null || Array.IndexOf(Fsms, f.FsmName) < 0) continue;
                f.Fsm.RestartOnEnable = !guarded;
            }
        }
    }

    internal class StoredItem
    {
        public GameObject Item;
        public Vector3 LocalPos;
        public Quaternion LocalRot;
        public bool HasPose;
        public int Layer = 9;
        // Renderers / colliders that were enabled when the item was pocketed (null = unknown, e.g. loaded from a save).
        public List<Renderer> Renderers;
        public List<Collider> Colliders;
    }

    internal class Runner : MonoBehaviour
    {
        private Refs _r;
        private float _nextScan;
        private bool _legacyOk = true;
        private readonly StoredItem[] _stored = new StoredItem[3 + ExtraSlots.Max];
        private const int OffKey = -2;
        /// Slots usable right now: the three weapon slots + the configured extra slots.
        private int SlotCount { get { return 3 + ExtraSlots.Active; } }
        // Item currently in the hand that came out of a slot (so "Weapon off" / same key can put it back).
        private GameObject _heldItem;
        private int _heldFrom = -1;
        private readonly Queue<Action> _steps = new Queue<Action>();   // one step per frame
        private GameObject _inTransit;                                  // item being taken out (ignored by Rescan)

        // -------------------------------------------------------------- frame loop
        private void Update()
        {
            try { Keybinds.Tick(); } catch (Exception e) { if (Time.frameCount % 600 == 0) Plugin.Log.LogWarning("Keybinds: " + e.Message); }
            if (!Plugin.Enabled.Value)
            {
                ExtraSlots.HideAll();
                if (_r != null && _r.Valid) EjectAll("mod disabled");
                else if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + 0.5f; _r = Refs.Find(); if (_r.Valid) Icons.Slots = _r.Slots; }
                return;
            }
            if (_r != null && _r.Valid) Rescan();
            else if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + 0.5f; Rescan(); }
            if (_r == null || !_r.Valid) return;

            UpdateBackpack();
            ExtraSlots.EnsureUi(_r.Slots[1], _r.Slots[2]);
            try { ExtraSlots.UpdateUi(SlotItem, _heldFrom); } catch (Exception e) { if (Time.frameCount % 600 == 0) Plugin.Log.LogWarning("Extra slot UI: " + e.Message); }

            if (_steps.Count > 0)
            {
                if (!GameplayActive()) return;   // never move items around while paused / in a menu (GrabItem's Grab would bail out)
                var a = _steps.Dequeue(); try { a(); } catch (Exception e) { Plugin.Log.LogError("step failed: " + e); }
                return;
            }

            GuardHeld();

            // Forget the "came from slot" record once the item left the hand (dropped / thrown / stored).
            if (_heldItem != null && HeldItem() != _heldItem)
            {
                string gs = _r.Grab.ActiveStateName;
                Plugin.V("Held item left the hand (" + _heldItem.name + "; GrabItem=" + gs + ", Item=" + Name(HeldItem()) + ")");
                _heldItem = null; _heldFrom = -1;
            }

            if (!GameplayActive())
            {
                if (_thirdReason != null && (ButtonDown("Weapon 1", Plugin.Fallback1.Value) || ButtonDown("Weapon 2", Plugin.Fallback2.Value) || ButtonDown("Weapon 3", Plugin.Fallback3.Value)))
                    Plugin.V("Slot key ignored: third person (" + _thirdReason + ")");
                return;
            }

            int n = -1;
            if (ButtonDown("Weapon 1", Plugin.Fallback1.Value)) n = 0;
            else if (ButtonDown("Weapon 2", Plugin.Fallback2.Value)) n = 1;
            else if (ButtonDown("Weapon 3", Plugin.Fallback3.Value)) n = 2;
            else if (ButtonDown("Weapon off", Plugin.FallbackOff.Value)) n = OffKey;
            else n = ExtraKey();
            if (n == -1) return;

            if (n == OffKey) { if (WantsOff()) { Plugin.HandledFrame = Time.frameCount; OnWeaponOff(); } return; }
            if (WantsSlot(n)) { if (n < 3) Plugin.HandledFrame = Time.frameCount; OnSlotKey(n); }
        }

        /// Slot index 3..5 whose (configurable) key was pressed this frame, else -1.
        private int ExtraKey()
        {
            int active = ExtraSlots.Active;
            if (active == 0) return -1;
            if (!Plugin.ExtraKeysInVehicle.Value && _r.InCar != null && _r.InCar.ActiveStateName != "OnFoot") return -1;
            for (int i = 0; i < active; i++) if (Keybinds.Down(i)) return 3 + i;
            return -1;
        }

        // ------------------------------------------------------------ backpack gate for slots 4..6
        private int _backpackSlots = -1;
        private float _lockStableSince;
        /// Locked slots are only emptied once the backpack state has been stable for a moment during normal play (after a load
        /// the pocketed items and the worn backpack are restored in the same pass; never eject in between).
        private bool LockSettled { get { return _backpackSlots >= 0 && Time.unscaledTime - _lockStableSince > 1.5f && GameplayActive(); } }
        private string _backpackName;
        /// Small = 1 extra slot, medium = 2, large / huge = 3, none = 0 (from the backpack worn in QuickItems/Backpack_Item).
        private void UpdateBackpack()
        {
            int n = 3; string name = "(not required)";
            if (Plugin.RequireBackpack.Value)
            {
                n = 0; name = "none";
                var holder = _r.Grab.transform.Find("QuickItems/Backpack_Item");
                GameObject bp = null;
                if (holder != null)
                    for (int i = 0; i < holder.childCount; i++) { var c = holder.GetChild(i).gameObject; if (Fsms.Find(c, "saveItemVar") != null || Fsms.Find(c, "ItemName") != null) { bp = c; break; } }
                if (bp != null)
                {
                    name = Icons.PrefabName(bp);
                    string l = name.ToLowerInvariant();
                    if (l.Contains("small")) n = 1; else if (l.Contains("medium")) n = 2; else if (l.Contains("large") || l.Contains("huge")) n = 3;
                    else n = 1;
                }
            }
            if (n != _backpackSlots) _lockStableSince = Time.unscaledTime;
            if (n != _backpackSlots || name != _backpackName)
            {
                if (_backpackSlots >= 0) Plugin.Log.LogInfo("Backpack: " + name + " -> extra slots available: " + Mathf.Min(n, Mathf.Clamp(Plugin.ExtraSlotCount.Value, 0, 3)));
                _backpackSlots = n; _backpackName = name;
            }
            ExtraSlots.Unlocked = n;
        }

        private GameObject _guardedHeld;
        /// Any item in the hand (from a slot or picked up by hand) keeps its FSMs from restarting on the camera toggle.
        private void GuardHeld()
        {
            var h = HeldItem();
            if (h == _guardedHeld) return;
            if (_guardedHeld != null && !IsPocketed(_guardedHeld)) RestartGuard.Set(_guardedHeld, false);
            _guardedHeld = h;
            if (h != null) { RestartGuard.Set(h, true); Plugin.V("Restart guard on " + h.name); }
        }

        private bool IsPocketed(GameObject go) { for (int i = 0; i < _stored.Length; i++) if (_stored[i] != null && _stored[i].Item == go) return true; return false; }

        // -------------------------------------------------------------- decision (shared with the Harmony prefix)
        internal static Runner Instance;
        private void Awake() { Instance = this; }

        internal bool WantsSlot(int n) { return _r != null && _r.Valid && (HeldItem() != null || SlotItem(n) != null); }
        internal bool WantsOff() { return _r != null && _r.Valid && HeldItem() != null; }
        internal bool SlotHoldsItem(Transform slot) { if (_r == null || !_r.Valid) return false; for (int i = 0; i < 3; i++) if (_r.Slots[i] == slot) return SlotItem(i) != null; return false; }

        // -------------------------------------------------------------- actions
        private void OnSlotKey(int n)
        {
            var held = HeldItem();
            var slotItem = SlotItem(n);
            Plugin.V("Key slot " + (n + 1) + ": held=" + Name(held) + " from=" + (_heldFrom + 1) + " slotItem=" + Name(slotItem) + " slotChild=" + Name(SlotChild(n))
                + (_r.InCar != null ? " InCar=" + _r.InCar.ActiveStateName : "") + " vehicleCam=" + (_vehicleCam != null && _vehicleCam.enabled ? _vehicleCam.ActiveStateName : "none") + " main=" + (Camera.main != null ? Camera.main.name : "null"));

            if (held != null)
            {
                if (_heldItem == held && _heldFrom == n) { StoreHeld(held, n, true); return; }          // toggle: hide back into its own slot
                if (SlotChild(n) == null) { StoreHeld(held, n, true); return; }                          // empty slot: pocket it there
                // slot n is occupied (weapon or item): put the held item away first
                bool putAway = false;
                if (_heldItem == held && _heldFrom >= 0 && _heldFrom < SlotCount && SlotChild(_heldFrom) == null) putAway = StoreHeld(held, _heldFrom, false);
                if (!putAway) DropHeld(held);
                if (slotItem != null) TakeOut(n);
                else if (n < 3) DrawWeaponNextFrame(n);
                return;
            }
            if (slotItem != null) TakeOut(n);
        }

        private void OnWeaponOff()
        {
            var held = HeldItem();
            if (held == null) return;
            Plugin.V("Weapon off: held=" + Name(held) + " from=" + (_heldFrom + 1));
            if (_heldItem == held && _heldFrom >= 0 && _heldFrom < SlotCount && SlotChild(_heldFrom) == null) { StoreHeld(held, _heldFrom, true); return; }
            for (int i = 0; i < SlotCount; i++) if (SlotChild(i) == null) { StoreHeld(held, i, true); return; }
            DropHeld(held);
        }

        /// The game's takeWeapon recipe: notHold on GrabItem, parent under the slot, hide, freeze physics.
        private bool StoreHeld(GameObject item, int n, bool emptyHands)
        {
            if (!CanPocket(item)) return false;
            var slot = _r.Slots[n];
            var rec = new StoredItem { Item = item, LocalPos = item.transform.localPosition, LocalRot = item.transform.localRotation, HasPose = true, Layer = item.layer };
            if (item.transform.parent != _r.Hand) rec.HasPose = false;
            rec.Renderers = new List<Renderer>(); foreach (var r in item.GetComponentsInChildren<Renderer>(true)) if (r != null && r.enabled) rec.Renderers.Add(r);
            rec.Colliders = new List<Collider>(); foreach (var c in item.GetComponentsInChildren<Collider>(true)) if (c != null && c.enabled) rec.Colliders.Add(c);

            SetFsmEnabled(item, "Attach", false);
            SetFsmEnabled(item, "CheckBool", false);
            _r.Grab.SendEvent("not_Hold");
            item.transform.SetParent(slot, false);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            Hide(item, true);
            _stored[n] = rec;
            if (rec.HasPose) ApocasaverBridge.SetPose(item.name, rec.LocalPos, rec.LocalRot);
            _heldItem = null; _heldFrom = -1;
            PlayClip("draw_holster", 0.3f);
            if (emptyHands) SetWeaponBools(-1);
            // A weapon dropped with the game's Drop Weapon key leaves the Weapons FSM sitting in that slot's state (its
            // DropWeapon/UseWeapon FSMs live) - never let that apply to a slot that now holds a pocketed item.
            if (_r.Weapons.ActiveStateName == "Slot " + (n + 1)) { Plugin.V("Weapons FSM was still in " + _r.Weapons.ActiveStateName + " for an empty slot; sending back"); SetWeaponBools(-1); _r.Weapons.SendEvent("back"); }
            _steps.Enqueue(() => ApplyIcon(n));
            Plugin.V("Stored " + item.name + " in slot " + (n + 1));
            return true;
        }

        private void DropHeld(GameObject item)
        {
            Plugin.V("Dropping " + item.name);
            _r.Grab.SendEvent("drop");
            _heldItem = null; _heldFrom = -1;
        }

        /// Holster (if needed), then hand the item to GrabItem's Grab state.
        private void TakeOut(int n)
        {
            var rec = _stored[n];
            if (rec == null || rec.Item == null) return;
            var item = rec.Item;
            _stored[n] = null;
            _inTransit = item;
            bool weaponOut = _r.HandItemUse.childCount > 0;
            if (weaponOut) { Plugin.V("Holstering weapon before taking out " + item.name); _r.Weapons.SendEvent("Deactivate"); SetWeaponBools(-1); }
            int tries = 0;
            Action step = null;
            step = () =>
            {
                if (_r.HandItemUse.childCount > 0 && ++tries < 10) { Plugin.V("HandItemUse still occupied; retrying (" + tries + ")"); _steps.Enqueue(step); return; }
                if (_r.HandItemUse.childCount > 0) { Plugin.Log.LogWarning("Weapon would not holster; leaving " + item.name + " in slot " + (n + 1)); _inTransit = null; Restore(item, rec, n); return; }
                Grab(item, rec, n);
            };
            _steps.Enqueue(step);
        }

        /// Put an item back into a slot as a pocketed item (used when a take-out fails).
        private void Restore(GameObject item, StoredItem rec, int n)
        {
            item.transform.SetParent(_r.Slots[n], false);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            Hide(item, true);
            _stored[n] = rec;
            _steps.Enqueue(() => ApplyIcon(n));
        }

        /// Last resort: the item cannot go back (slot taken) - release it into the world at the drop point, visible and physical.
        private void DropLoose(GameObject item, StoredItem rec)
        {
            Plugin.Log.LogWarning("Releasing " + item.name + " into the world");
            var drop = _r.Grab.transform.Find("ItemDrop");
            item.transform.SetParent(null, true);
            item.transform.position = drop != null ? drop.position : _r.Grab.transform.position + _r.Grab.transform.forward;
            item.layer = rec != null ? rec.Layer : 9;
            Hide(item, false, rec);
            var rb = item.GetComponent<Rigidbody>();
            if (rb == null) rb = item.AddComponent<Rigidbody>();
            rb.isKinematic = false; rb.useGravity = true; rb.velocity = Vector3.zero;
            var lp = Fsms.Find(item, "LockPhysics");
            if (lp != null) { lp.enabled = true; try { lp.SendEvent("LockPhysics_OFF"); } catch { } }
        }

        private void Grab(GameObject item, StoredItem rec, int n)
        {
            // The FSM's own Hand reference is what ItemInHand polls (GameObjectHasChildren) - make sure we agree with it.
            var handVar = _r.Grab.FsmVariables.GetFsmGameObject("Hand") ?? FsmVariables.GlobalVariables.GetFsmGameObject("Hand");
            Transform hand = _r.Hand;
            if (handVar != null)
            {
                if (handVar.Value != null) hand = handVar.Value.transform;
                else handVar.Value = _r.Hand.gameObject;
            }
            if (hand != _r.Hand) Plugin.Log.LogWarning("GrabItem Hand variable (" + hand.name + ") differs from PlayerCamera/Hand; using the FSM's");
            item.transform.SetParent(hand, false);
            if (rec.HasPose) { item.transform.localPosition = rec.LocalPos; item.transform.localRotation = rec.LocalRot; }
            else
            {
                var guide = _r.Grab.transform.Find("Guide");
                if (guide != null) item.transform.position = guide.position;
                else item.transform.localPosition = Vector3.zero;
                item.transform.localPosition += new Vector3(Plugin.DefaultX.Value, Plugin.DefaultY.Value, Plugin.DefaultZ.Value);
                item.transform.rotation = Quaternion.LookRotation(_r.Grab.transform.forward, Vector3.up);
            }
            item.layer = rec.Layer;
            Hide(item, false, rec);
            var rb = item.GetComponent<Rigidbody>();
            if (rb == null) { rb = item.AddComponent<Rigidbody>(); var lp = Fsms.Find(item, "LockPhysics"); var m = lp != null ? lp.FsmVariables.GetFsmFloat("mass") : null; if (m != null && m.Value > 0) rb.mass = m.Value; }
            rb.isKinematic = false; rb.useGravity = false; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            var lockFsm = Fsms.Find(item, "LockPhysics");
            if (lockFsm != null) { lockFsm.enabled = true; try { if (lockFsm.Fsm.Initialized && lockFsm.ActiveStateName != "off") lockFsm.Fsm.SetState("off"); } catch (Exception e) { Plugin.V("LockPhysics off: " + e.Message); } }
            SetFsmEnabled(item, "Attach", true);
            SetFsmEnabled(item, "CheckBool", true);

            RestartGuard.Set(item, true);
            var itemVar = _r.Grab.FsmVariables.GetFsmGameObject("Item");
            if (itemVar != null) itemVar.Value = item;
            var nameVar = _r.Grab.FsmVariables.GetFsmString("ItemInHandName");
            if (nameVar != null) nameVar.Value = "";
            _r.Grab.Fsm.SetState("Grab");
            string gs = _r.Grab.ActiveStateName;
            if (gs != "Grab" && gs != "ItemInHand")
            {
                // The Grab state bailed out (weapon still out, GrabItem_Pause, ...): never leave the item dangling under the Hand.
                Plugin.Log.LogWarning("GrabItem refused " + item.name + " (state " + gs + "); putting it back into slot " + (n + 1));
                _inTransit = null;
                if (n >= 0 && SlotChild(n) == null) Restore(item, rec, n);
                else DropLoose(item, rec);
                return;
            }
            _heldItem = item; _heldFrom = n; _inTransit = null;
            PlayClip("draw_holster", 0.3f);
            Plugin.V((n < 0 ? "Grabbed " : "Took ") + item.name + (n < 0 ? "" : " out of slot " + (n + 1)) + " -> GrabItem state " + _r.Grab.ActiveStateName + ", hand children=" + hand.childCount + ", parent=" + Name(item.transform.parent != null ? item.transform.parent.gameObject : null) + ", pos=" + item.transform.localPosition);
        }

        private void DrawWeaponNextFrame(int n)
        {
            SetWeaponBools(n);
            _steps.Enqueue(() =>
            {
                if (_r.Weapons.ActiveStateName == "Slot " + (n + 1)) return;
                Plugin.V("Drawing weapon slot " + (n + 1) + " (Weapons was " + _r.Weapons.ActiveStateName + ")");
                _r.Weapons.Fsm.SetState("Slot " + (n + 1));
            });
        }

        // -------------------------------------------------------------- helpers
        /// Forget everything tied to the previous world (queued steps, in-transit / held / stored records).
        private void ResetTransient(string why)
        {
            if (_steps.Count > 0 || _inTransit != null || _heldItem != null) Plugin.V("Reset transient state (" + why + "): " + _steps.Count + " queued step(s) dropped");
            _steps.Clear(); _inTransit = null; _heldItem = null; _heldFrom = -1;
            for (int i = 0; i < _stored.Length; i++) _stored[i] = null;
            _backpackSlots = -1; _lockStableSince = Time.unscaledTime; ExtraSlots.Unlocked = 3;
            ExtraSlots.ResetUi();
        }

        // ------------------------------------------------------------ eject (mod disabled)
        private void EjectAll(string why)
        {
            for (int i = 0; i < _stored.Length; i++)
            {
                if (_r.Slots[i] == null) continue;
                var c = SlotChild(i);
                if (c == null || IsWeapon(c) || c == _inTransit) continue;
                Plugin.Log.LogInfo("Ejecting " + c.name + " from slot " + (i + 1) + " (" + why + ")");
                Eject(c, i);
                _stored[i] = null;
            }
        }

        /// Game's DropWeapon recipe for a non-weapon: visible, physics on, at the ItemDrop point, LockPhysics restarted.
        private void Eject(GameObject item, int slot)
        {
            var rec = _stored[slot];
            if (IsWeapon(item))
            {
                // The game's DropWeapon recipe: weapon models/colliders come back through its Visibility/Colliders FSMs.
                foreach (var f in item.GetComponents<PlayMakerFSM>()) { try { f.SendEvent("CollidersActivate"); f.SendEvent("Activate"); } catch { } }
                SetFsmEnabled(item, "LockPhysics", true);
            }
            var drop = _r.Grab.transform.Find("ItemDrop");
            Vector3 pos = drop != null ? drop.position : _r.Grab.transform.position + _r.Grab.transform.forward * 1.0f;
            item.transform.SetParent(null, true);
            item.transform.position = pos;
            item.layer = rec != null ? rec.Layer : 9;
            Hide(item, false, rec);
            var rb = item.GetComponent<Rigidbody>();
            if (rb == null) rb = item.AddComponent<Rigidbody>();
            rb.isKinematic = false; rb.useGravity = true; rb.velocity = Vector3.zero;
            SetFsmEnabled(item, "Attach", true);
            SetFsmEnabled(item, "CheckBool", true);
            var lp = Fsms.Find(item, "LockPhysics");
            if (lp != null) { lp.enabled = true; try { lp.SendEvent("LockPhysics_OFF"); } catch { } }
        }

        private void Rescan()
        {
            if (_r == null || !_r.Valid) { _r = Refs.Find(); if (_r.Valid) { Icons.Slots = _r.Slots; Plugin.V("Found player refs"); ResetTransient("new player refs"); } else return; }
            // Holders of slots 4..6 are mod objects: recreate / re-register them if the scene lost them.
            if (_r.Slots[3] == null) { ExtraSlots.EnsureHolders(_r.Grab.transform); for (int k = 0; k < ExtraSlots.Max; k++) _r.Slots[3 + k] = ExtraSlots.Holders[k]; }
            // Items sitting in slots (loaded from a save, or stored by us): make sure they are registered and hidden.
            for (int i = 0; i < _stored.Length; i++)
            {
                if (_r.Slots[i] == null) continue;
                var c = SlotChild(i);
                if (c == null)
                {
                    // Keep the record if the item still exists somewhere else: the rescue pass below deals with it.
                    if (_stored[i] != null && _stored[i].Item == null) { Plugin.Log.LogWarning("Pocketed item in slot " + (i + 1) + " was destroyed"); _stored[i] = null; }
                    continue;
                }
                if (c == _inTransit || c == _heldItem) continue;
                if (i >= SlotCount)
                {
                    if (!LockSettled) continue;
                    // Slot locked (backpack taken off / Count lowered) while it held something - item or weapon: throw it out
                    // in front of the player, same as when the mod is disabled.
                    Plugin.Log.LogInfo("Slot " + (i + 1) + " is locked; dropping " + c.name);
                    if (_stored[i] == null || _stored[i].Item != c) _stored[i] = new StoredItem { Item = c, Layer = 9 };
                    Eject(c, i); _stored[i] = null;
                    continue;
                }
                if (IsWeapon(c)) { _stored[i] = null; continue; }
                if (!c.activeSelf) { if (_stored[i] == null || _stored[i].Item != c) { Plugin.Log.LogWarning("Inactive object " + c.name + " in slot " + (i + 1) + "; activating it"); c.SetActive(true); } }
                if (_stored[i] == null || _stored[i].Item != c)
                {
                    var rec0 = new StoredItem { Item = c, HasPose = false, Layer = 9 };
                    Vector3 pp; Quaternion pq;
                    if (ApocasaverBridge.TryGetPose(c.name, out pp, out pq)) { rec0.LocalPos = pp; rec0.LocalRot = pq; rec0.HasPose = true; }
                    _stored[i] = rec0;
                    Plugin.V("Registered stored item in slot " + (i + 1) + ": " + c.name + (rec0.HasPose ? " (pose from save)" : ""));
                    Hide(c, true);
                    ApplyIcon(i);
                }
                else
                {
                    // Something (LoadVar, SlotEmptyFull) may re-enable bits of the item: keep it hidden and frozen.
                    var r0 = c.GetComponentInChildren<Renderer>(true);
                    if (r0 != null && r0.enabled) { Plugin.V("Re-hiding " + c.name + " in slot " + (i + 1)); Hide(c, true); }
                    if (Time.frameCount % 30 == 0) ApplyIcon(i);
                }
                GuardLockPhysics(c, i);
            }
            // Rescue: a pocketed item that something moved out of its slot (e.g. the car "keep items in the truck bed"
            // logic while driving) is put straight back.
            for (int i = 0; i < _stored.Length; i++)
            {
                var rec = _stored[i];
                if (rec == null || rec.Item == null || _r.Slots[i] == null) continue;
                if (rec.Item == _inTransit || rec.Item == _heldItem) { _stored[i] = null; continue; }
                if (rec.Item.transform.parent == _r.Slots[i]) continue;
                var np = rec.Item.transform.parent;
                string where = np == null ? "scene root" : GetPath(np.gameObject);
                if (SlotChild(i) != null) { Plugin.Log.LogWarning(rec.Item.name + " left slot " + (i + 1) + " (now under " + where + ") but the slot is occupied by " + SlotChild(i).name + "; releasing it"); _stored[i] = null; continue; }
                if (np != null && np.IsChildOf(_r.Grab.transform) && np != _r.Hand)
                {
                    // Taken by the game's own camera-side logic (e.g. QuickItems) - that is legitimate, let it go.
                    Plugin.Log.LogWarning(rec.Item.name + " left slot " + (i + 1) + " for " + where + "; releasing it");
                    Hide(rec.Item, false, rec); _stored[i] = null; continue;
                }
                Plugin.Log.LogWarning(rec.Item.name + " was moved out of slot " + (i + 1) + " (now under " + where + ", active=" + rec.Item.activeInHierarchy + "); putting it back");
                rec.Item.transform.SetParent(_r.Slots[i], false);
                rec.Item.transform.localPosition = Vector3.zero;
                rec.Item.transform.localRotation = Quaternion.identity;
                Hide(rec.Item, true);
                GuardLockPhysics(rec.Item, i);
            }
        }

        /// The game's LockPhysics FSM (LockPhysics_OFF -> wait 3 s -> raycasts -> parent to a car + destroy rigidbody)
        /// gets poked even while disabled (e.g. entering a vehicle). Keep it parked in "off" while the item is pocketed.
        private static void GuardLockPhysics(GameObject item, int slot)
        {
            var lp = Fsms.Find(item, "LockPhysics");
            if (lp == null) return;
            try
            {
                if (lp.Fsm.Initialized && lp.ActiveStateName != "off" && lp.ActiveStateName != "")
                {
                    Plugin.V("LockPhysics of " + item.name + " (slot " + (slot + 1) + ") was in '" + lp.ActiveStateName + "'; parking it in 'off'");
                    lp.Fsm.SetState("off");
                }
            }
            catch (Exception e) { Plugin.V("GuardLockPhysics: " + e.Message); }
            if (lp.enabled) lp.enabled = false;
            var rb = item.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.useGravity = false; rb.isKinematic = true; }
        }

        private void ApplyIcon(int n)
        {
            if (n >= 3) return;   // slots 4..6: ExtraSlots.UpdateUi draws the icon every frame
            var rec = _stored[n];
            if (rec == null || rec.Item == null) return;
            try { Icons.Apply(n, Icons.Get(rec.Item)); }
            catch (Exception e) { Plugin.Log.LogWarning("ApplyIcon: " + e.Message); }
        }

        private GameObject SlotChild(int n)
        {
            if (n < 0 || n >= _r.Slots.Length || _r.Slots[n] == null) return null;
            var s = _r.Slots[n];
            for (int i = 0; i < s.childCount; i++) { var t = s.GetChild(i); if (t.gameObject.activeSelf) return t.gameObject; }
            return s.childCount > 0 ? s.GetChild(0).gameObject : null;
        }

        private GameObject SlotItem(int n) { var r = _stored[n]; return r != null && r.Item != null && r.Item.transform.parent == _r.Slots[n] ? r.Item : null; }

        private static bool IsWeapon(GameObject go) { return Fsms.Find(go, "weaponType") != null; }

        /// Item in the player's hand (GrabItem holding states), else null.
        internal GameObject HeldItem()
        {
            if (_r == null || _r.Grab == null) return null;
            string s = _r.Grab.ActiveStateName;
            if (s != "ItemInHand" && s != "Rotate" && s != "Forward" && s != "Backward" && s != "Grab") return null;
            var v = _r.Grab.FsmVariables.GetFsmGameObject("Item");
            return v != null ? v.Value : null;
        }

        private bool CanPocket(GameObject item)
        {
            var id = Fsms.Find(item, "ID");
            string idv = null;
            if (id != null) { var s = id.FsmVariables.GetFsmString("ID"); if (s != null) idv = s.Value; }
            string prefab = Icons.PrefabName(item);
            foreach (var raw in (Plugin.Blacklist.Value ?? "").Split(';'))
            {
                var b = raw.Trim();
                if (b.Length == 0) continue;
                if (string.Equals(b, prefab, StringComparison.OrdinalIgnoreCase) || (!string.IsNullOrEmpty(idv) && b == idv))
                { Plugin.Log.LogInfo(Name(item) + " cannot be pocketed (blacklisted: " + b + ")"); return false; }
            }
            // Merchant stock (ForBuy.ForBuyInt == 1) is bought with Use, never pocketed.
            var fb = Fsms.Find(item, "ForBuy");
            if (fb != null) { var v = fb.FsmVariables.GetFsmInt("ForBuyInt"); if (v != null && v.Value == 1) { Plugin.Log.LogInfo(Name(item) + " is merchant stock; not pocketing"); return false; } }
            return true;
        }

        private static void Hide(GameObject item, bool hide) { Hide(item, hide, null); }

        /// hide=true disables every renderer/collider; hide=false re-enables only those recorded in `rec` (all if unknown).
        private static void Hide(GameObject item, bool hide, StoredItem rec)
        {
            RestartGuard.Set(item, hide);
            if (hide)
            {
                foreach (var r in item.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (var c in item.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            }
            else if (rec != null && rec.Renderers != null && rec.Colliders != null)
            {
                foreach (var r in rec.Renderers) if (r != null) r.enabled = true;
                foreach (var c in rec.Colliders) if (c != null) c.enabled = true;
            }
            else
            {
                foreach (var r in item.GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                foreach (var c in item.GetComponentsInChildren<Collider>(true)) c.enabled = true;
            }
            var rb = item.GetComponent<Rigidbody>();
            if (rb != null && hide) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.useGravity = false; rb.isKinematic = true; }
            if (hide) SetFsmEnabled(item, "LockPhysics", false);
        }

        private static void SetFsmEnabled(GameObject go, string fsm, bool on)
        {
            var f = Fsms.Find(go, fsm);
            if (f != null) f.enabled = on;
        }

        private void SetWeaponBools(int n)
        {
            var v = _r.Weapons.FsmVariables;
            var off = v.GetFsmBool("weaponOff_Bool"); if (off != null) off.Value = n < 0;
            for (int i = 0; i < 3; i++) { var b = v.GetFsmBool("weapon" + (i + 1) + "_Bool"); if (b != null) b.Value = i == n; }
        }

        private PlayMakerFSM _vehicleCam;
        private float _nextCamScan;

        /// Third person exists only in vehicles (DriveTrigger [Camera] FSM, states 1st/3rd). Checked only while Player/InCar
        /// says we are in a vehicle, so on-foot behaviour never depends on it. Several independent signals, any one is enough.
        private string _thirdReason;
        private bool ThirdPerson()
        {
            _thirdReason = null;
            if (_r.InCar != null && _r.InCar.ActiveStateName == "OnFoot") { _vehicleCam = null; return false; }
            if (_r.InCar == null) return false;
            // 1) the vehicle camera FSM (enabled only while driving) in its "3rd" state
            if ((_vehicleCam == null || !_vehicleCam.enabled) && Time.unscaledTime >= _nextCamScan)
            {
                _nextCamScan = Time.unscaledTime + 0.5f;
                _vehicleCam = null;
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                    if (f != null && f.enabled && f.FsmName == "Camera" && f.gameObject.name == "DriveTrigger" && f.gameObject.scene.IsValid() && f.gameObject.activeInHierarchy) { _vehicleCam = f; break; }
            }
            if (_vehicleCam != null && _vehicleCam.enabled)
            {
                string st = _vehicleCam.ActiveStateName;
                if (st == "3rd" || st.StartsWith("3")) { _thirdReason = "vehicle Camera FSM state '" + st + "'"; return true; }
            }
            // 2) the player camera is not the one rendering (the 3rd-person camera is a different object)
            var pc = _r.Grab.GetComponent<Camera>();
            if (pc != null && !pc.isActiveAndEnabled) { _thirdReason = "PlayerCamera's Camera is disabled"; return true; }
            if (!_r.Grab.gameObject.activeInHierarchy) { _thirdReason = "PlayerCamera is inactive"; return true; }
            var main = Camera.main;
            if (main != null && main.gameObject != _r.Grab.gameObject && !main.transform.IsChildOf(_r.Grab.transform)) { _thirdReason = "Camera.main is " + GetPath(main.gameObject); return true; }
            return false;
        }

        private bool GameplayActive()
        {
            if (Time.timeScale <= 0f) return false;
            if (_r.Menu != null && _r.Menu.ActiveStateName != "play") return false;
            if (ThirdPerson()) return false;
            if (_r.GrabPause != null) { var b = _r.GrabPause.FsmVariables.GetFsmBool("GrabItem_Pause"); if (b != null && b.Value) return false; }
            return true;
        }

        private bool ButtonDown(string axis, Key fallback)
        {
            var game = Keybinds.GameActionDown(axis);   // the game's own action: follows rebinds in the Controls screen
            if (game.HasValue) return game.Value;
            if (_legacyOk) { try { return Input.GetButtonDown(axis); } catch (Exception e) { _legacyOk = false; Plugin.Log.LogWarning("Legacy input axis '" + axis + "' unavailable (" + e.Message + "); using fallback keys."); } }
            if (fallback == Key.None) return false;
            try { var kb = Keyboard.current; if (kb != null) return kb[fallback].wasPressedThisFrame; } catch { }
            return false;
        }

        private static AudioClip _holsterClip;
        private void PlayClip(string name, float vol)
        {
            try
            {
                if (_holsterClip == null) _holsterClip = Resources.FindObjectsOfTypeAll<AudioClip>().FirstOrDefault(c => c.name == name);
                if (_holsterClip != null) AudioSource.PlayClipAtPoint(_holsterClip, _r.Hand.position, vol);
            }
            catch { }
        }

        private static string Name(GameObject g) { return g == null ? "null" : g.name; }
        private static string GetPath(GameObject go)
        {
            string p = go.name; var t = go.transform.parent;
            while (t != null) { p = t.name + "/" + p; t = t.parent; }
            return p;
        }
    }

    /// Suppress the game's own Weapon 1/2/3/off handling on frames the mod consumed the press,
    /// or when the pressed slot holds a pocketed item / the player is holding an item.
    [HarmonyPatch(typeof(GetButtonDown), "OnUpdate")]
    internal static class GetButtonDown_Patch
    {
        static bool Prefix(GetButtonDown __instance)
        {
            if (!Plugin.Enabled.Value || __instance.buttonName == null) return true;
            string b = __instance.buttonName.Value;
            int n;
            var run = Runner.Instance;
            if (b == "Drop Weapon")
            {
                // Slot N's DropWeapon FSM: only block when that slot holds a pocketed item.
                if (run == null || __instance.Fsm == null || __instance.Fsm.GameObject == null) return true;
                if (!run.SlotHoldsItem(__instance.Fsm.GameObject.transform)) return true;
                if (__instance.storeResult != null) __instance.storeResult.Value = false;
                return false;
            }
            if (b == "Weapon 1") n = 0; else if (b == "Weapon 2") n = 1; else if (b == "Weapon 3") n = 2; else if (b == "Weapon off") n = 3; else return true;
            bool block = Time.frameCount == Plugin.HandledFrame || Time.frameCount == Plugin.HandledFrame + 1;
            if (!block && run != null) block = n == 3 ? run.WantsOff() : run.WantsSlot(n);
            if (!block) return true;
            if (__instance.storeResult != null) __instance.storeResult.Value = false;
            return false;
        }
    }
}
