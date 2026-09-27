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
        public const string VERSION = "1.0.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<string> Blacklist;
        internal static ConfigEntry<Key> Fallback1, Fallback2, Fallback3, FallbackOff;
        internal static ConfigEntry<float> DefaultX, DefaultY, DefaultZ;
        internal static ConfigEntry<int> IconSize;

        /// Frame on which the mod consumed a Weapon N / Weapon off press (game's GetButtonDown is suppressed that frame).
        internal static int HandledFrame = -100;
        private static GameObject _runnerGo;

        private void Awake()
        {
            Log = Logger;
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "Enabled", true, "Enable item slots. When disabled the weapon keys behave exactly as before.");
            Verbose = Config.Bind("General", "VerboseLog", true, "Log every step to the BepInEx console/log.");
            Blacklist = Config.Bind("General", "BlacklistIDs", "PartAdjusterTools", "Item ID strings (FSM 'ID') that can never be pocketed, separated by ';'.");
            DefaultX = Config.Bind("HandPose", "DefaultX", 0f, new ConfigDescription("Default hand offset X for items loaded from a save (no remembered pose).", new AcceptableValueRange<float>(-2f, 2f)));
            DefaultY = Config.Bind("HandPose", "DefaultY", 0f, new ConfigDescription("Default hand offset Y.", new AcceptableValueRange<float>(-2f, 2f)));
            DefaultZ = Config.Bind("HandPose", "DefaultZ", 0f, new ConfigDescription("Default hand offset Z (forward).", new AcceptableValueRange<float>(-2f, 3f)));
            IconSize = Config.Bind("General", "IconSize", 128, new ConfigDescription("Pixel size of the rendered item icons shown in the slots.", new AcceptableValueRange<int>(32, 512)));
            Fallback1 = Config.Bind("Keys", "FallbackSlot1", Key.Digit1, "Key polled if the game's 'Weapon 1' input axis cannot be read.");
            Fallback2 = Config.Bind("Keys", "FallbackSlot2", Key.Digit2, "Key polled if the game's 'Weapon 2' input axis cannot be read.");
            Fallback3 = Config.Bind("Keys", "FallbackSlot3", Key.Digit3, "Key polled if the game's 'Weapon 3' input axis cannot be read.");
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
        public Transform[] Slots = new Transform[3];
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

    internal class StoredItem
    {
        public GameObject Item;
        public Vector3 LocalPos;
        public Quaternion LocalRot;
        public bool HasPose;
        public int Layer = 9;
    }

    internal class Runner : MonoBehaviour
    {
        private Refs _r;
        private float _nextScan;
        private bool _legacyOk = true;
        private readonly StoredItem[] _stored = new StoredItem[3];
        // Item currently in the hand that came out of a slot (so "Weapon off" / same key can put it back).
        private GameObject _heldItem;
        private int _heldFrom = -1;
        private readonly Queue<Action> _steps = new Queue<Action>();   // one step per frame
        private GameObject _inTransit;                                  // item being taken out (ignored by Rescan)

        // -------------------------------------------------------------- frame loop
        private void Update()
        {
            if (!Plugin.Enabled.Value)
            {
                if (_r != null && _r.Valid) EjectAll("mod disabled");
                else if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + 0.5f; _r = Refs.Find(); if (_r.Valid) Icons.Slots = _r.Slots; }
                return;
            }
            if (_r != null && _r.Valid) Rescan();
            else if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + 0.5f; Rescan(); }
            if (_r == null || !_r.Valid) return;

            if (_steps.Count > 0) { var a = _steps.Dequeue(); try { a(); } catch (Exception e) { Plugin.Log.LogError("step failed: " + e); } return; }

            // Forget the "came from slot" record once the item left the hand (dropped / thrown / stored).
            if (_heldItem != null && HeldItem() != _heldItem)
            {
                string gs = _r.Grab.ActiveStateName;
                Plugin.V("Held item left the hand (" + _heldItem.name + "; GrabItem=" + gs + ", Item=" + Name(HeldItem()) + ")");
                _heldItem = null; _heldFrom = -1;
            }

            if (!GameplayActive()) return;

            int n = -1;
            if (ButtonDown("Weapon 1", Plugin.Fallback1.Value)) n = 0;
            else if (ButtonDown("Weapon 2", Plugin.Fallback2.Value)) n = 1;
            else if (ButtonDown("Weapon 3", Plugin.Fallback3.Value)) n = 2;
            else if (ButtonDown("Weapon off", Plugin.FallbackOff.Value)) n = 3;
            if (n < 0) return;

            if (n == 3) { if (WantsOff()) { Plugin.HandledFrame = Time.frameCount; OnWeaponOff(); } return; }
            if (WantsSlot(n)) { Plugin.HandledFrame = Time.frameCount; OnSlotKey(n); }
        }

        // -------------------------------------------------------------- decision (shared with the Harmony prefix)
        internal static Runner Instance;
        private void Awake() { Instance = this; }

        internal bool WantsSlot(int n) { return _r != null && _r.Valid && (HeldItem() != null || SlotItem(n) != null); }
        internal bool WantsOff() { return _r != null && _r.Valid && HeldItem() != null; }

        // -------------------------------------------------------------- actions
        private void OnSlotKey(int n)
        {
            var held = HeldItem();
            var slotItem = SlotItem(n);
            Plugin.V("Key slot " + (n + 1) + ": held=" + Name(held) + " from=" + (_heldFrom + 1) + " slotItem=" + Name(slotItem) + " slotChild=" + Name(SlotChild(n)));

            if (held != null)
            {
                if (_heldItem == held && _heldFrom == n) { StoreHeld(held, n, true); return; }          // toggle: hide back into its own slot
                if (SlotChild(n) == null) { StoreHeld(held, n, true); return; }                          // empty slot: pocket it there
                // slot n is occupied (weapon or item): put the held item away first
                bool putAway = false;
                if (_heldItem == held && _heldFrom >= 0 && SlotChild(_heldFrom) == null) putAway = StoreHeld(held, _heldFrom, false);
                if (!putAway) DropHeld(held);
                if (slotItem != null) TakeOut(n);
                else DrawWeaponNextFrame(n);
                return;
            }
            if (slotItem != null) TakeOut(n);
        }

        private void OnWeaponOff()
        {
            var held = HeldItem();
            if (held == null) return;
            Plugin.V("Weapon off: held=" + Name(held) + " from=" + (_heldFrom + 1));
            if (_heldItem == held && _heldFrom >= 0 && SlotChild(_heldFrom) == null) { StoreHeld(held, _heldFrom, true); return; }
            for (int i = 0; i < 3; i++) if (SlotChild(i) == null) { StoreHeld(held, i, true); return; }
            DropHeld(held);
        }

        /// The game's takeWeapon recipe: notHold on GrabItem, parent under the slot, hide, freeze physics.
        private bool StoreHeld(GameObject item, int n, bool emptyHands)
        {
            if (!CanPocket(item)) { Plugin.Log.LogInfo(Name(item) + " cannot be pocketed (blacklisted)"); return false; }
            var slot = _r.Slots[n];
            var rec = new StoredItem { Item = item, LocalPos = item.transform.localPosition, LocalRot = item.transform.localRotation, HasPose = true, Layer = item.layer };
            if (item.transform.parent != _r.Hand) rec.HasPose = false;

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
            _steps.Enqueue(() =>
            {
                if (_r.HandItemUse.childCount > 0) { Plugin.Log.LogWarning("HandItemUse still occupied; retrying next frame"); _steps.Enqueue(() => Grab(item, rec, n)); return; }
                Grab(item, rec, n);
            });
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
            Hide(item, false);
            var rb = item.GetComponent<Rigidbody>();
            if (rb == null) { rb = item.AddComponent<Rigidbody>(); var lp = Fsms.Find(item, "LockPhysics"); var m = lp != null ? lp.FsmVariables.GetFsmFloat("mass") : null; if (m != null && m.Value > 0) rb.mass = m.Value; }
            rb.isKinematic = false; rb.useGravity = false; rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
            var lockFsm = Fsms.Find(item, "LockPhysics");
            if (lockFsm != null) { lockFsm.enabled = true; try { if (lockFsm.Fsm.Initialized && lockFsm.ActiveStateName != "off") lockFsm.Fsm.SetState("off"); } catch (Exception e) { Plugin.V("LockPhysics off: " + e.Message); } }
            SetFsmEnabled(item, "Attach", true);
            SetFsmEnabled(item, "CheckBool", true);

            var itemVar = _r.Grab.FsmVariables.GetFsmGameObject("Item");
            if (itemVar != null) itemVar.Value = item;
            var nameVar = _r.Grab.FsmVariables.GetFsmString("ItemInHandName");
            if (nameVar != null) nameVar.Value = "";
            _r.Grab.Fsm.SetState("Grab");
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
        // ------------------------------------------------------------ eject (mod disabled)
        private void EjectAll(string why)
        {
            for (int i = 0; i < 3; i++)
            {
                var c = SlotChild(i);
                if (c == null || IsWeapon(c)) continue;
                Plugin.Log.LogInfo("Ejecting " + c.name + " from slot " + (i + 1) + " (" + why + ")");
                Eject(c, i);
                _stored[i] = null;
            }
        }

        /// Game's DropWeapon recipe for a non-weapon: visible, physics on, at the ItemDrop point, LockPhysics restarted.
        private void Eject(GameObject item, int slot)
        {
            var rec = _stored[slot];
            var drop = _r.Grab.transform.Find("ItemDrop");
            Vector3 pos = drop != null ? drop.position : _r.Grab.transform.position + _r.Grab.transform.forward * 1.0f;
            item.transform.SetParent(null, true);
            item.transform.position = pos;
            item.layer = rec != null ? rec.Layer : 9;
            Hide(item, false);
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
            if (_r == null || !_r.Valid) { _r = Refs.Find(); if (_r.Valid) { Icons.Slots = _r.Slots; Plugin.V("Found player refs"); } else return; }
            // Items sitting in slots (loaded from a save, or stored by us): make sure they are registered and hidden.
            for (int i = 0; i < 3; i++)
            {
                var c = SlotChild(i);
                if (c == null)
                {
                    // Keep the record if the item still exists somewhere else: the rescue pass below deals with it.
                    if (_stored[i] != null && _stored[i].Item == null) { Plugin.Log.LogWarning("Pocketed item in slot " + (i + 1) + " was destroyed"); _stored[i] = null; }
                    continue;
                }
                if (IsWeapon(c)) { _stored[i] = null; continue; }
                if (c == _inTransit || c == _heldItem) continue;
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
            for (int i = 0; i < 3; i++)
            {
                var rec = _stored[i];
                if (rec == null || rec.Item == null) continue;
                if (rec.Item == _inTransit || rec.Item == _heldItem) { _stored[i] = null; continue; }
                if (rec.Item.transform.parent == _r.Slots[i]) continue;
                var np = rec.Item.transform.parent;
                string where = np == null ? "scene root" : GetPath(np.gameObject);
                if (SlotChild(i) != null) { Plugin.Log.LogWarning(rec.Item.name + " left slot " + (i + 1) + " (now under " + where + ") but the slot is occupied by " + SlotChild(i).name + "; releasing it"); _stored[i] = null; continue; }
                if (np != null && np.IsChildOf(_r.Grab.transform) && np != _r.Hand)
                {
                    // Taken by the game's own camera-side logic (e.g. QuickItems) - that is legitimate, let it go.
                    Plugin.Log.LogWarning(rec.Item.name + " left slot " + (i + 1) + " for " + where + "; releasing it");
                    Hide(rec.Item, false); _stored[i] = null; continue;
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
            var rec = _stored[n];
            if (rec == null || rec.Item == null) return;
            try { Icons.Apply(n, Icons.Get(rec.Item)); }
            catch (Exception e) { Plugin.Log.LogWarning("ApplyIcon: " + e.Message); }
        }

        private GameObject SlotChild(int n)
        {
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
            if (!string.IsNullOrEmpty(idv))
                foreach (var b in (Plugin.Blacklist.Value ?? "").Split(';')) if (b.Trim() == idv) return false;
            return true;
        }

        private static void Hide(GameObject item, bool hide)
        {
            foreach (var r in item.GetComponentsInChildren<Renderer>(true)) r.enabled = !hide;
            foreach (var c in item.GetComponentsInChildren<Collider>(true)) { if (hide) { c.enabled = false; } else { c.enabled = true; } }
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

        /// True while a vehicle's DriveTrigger [Camera] FSM (enabled only while driving) is in its "3rd" state.
        private bool ThirdPerson()
        {
            if (_r.InCar != null && _r.InCar.ActiveStateName == "OnFoot") { _vehicleCam = null; return false; }
            if ((_vehicleCam == null || !_vehicleCam.enabled) && Time.unscaledTime >= _nextCamScan)
            {
                _nextCamScan = Time.unscaledTime + 0.5f;
                _vehicleCam = null;
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                    if (f != null && f.enabled && f.FsmName == "Camera" && f.gameObject.name == "DriveTrigger" && f.gameObject.scene.IsValid()) { _vehicleCam = f; break; }
            }
            return _vehicleCam != null && _vehicleCam.enabled && _vehicleCam.ActiveStateName == "3rd";
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
            if (b == "Weapon 1") n = 0; else if (b == "Weapon 2") n = 1; else if (b == "Weapon 3") n = 2; else if (b == "Weapon off") n = 3; else return true;
            var run = Runner.Instance;
            bool block = Time.frameCount == Plugin.HandledFrame || Time.frameCount == Plugin.HandledFrame + 1;
            if (!block && run != null) block = n == 3 ? run.WantsOff() : run.WantsSlot(n);
            if (!block) return true;
            if (__instance.storeResult != null) __instance.storeResult.Value = false;
            return false;
        }
    }
}
