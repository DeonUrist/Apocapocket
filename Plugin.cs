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
    /// Six logical item/weapon slots integrated with vanilla PlayMaker inventory.
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocapocket";
        public const string NAME = "Apocapocket";
        public const string VERSION = "2.0.5";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> Verbose;
        internal static ConfigEntry<bool> RequireBackpack;
        // Slot 4-6 keys: rebound from the game's Controls screen (Keybinds.cs), kept in a file of their own so the Mods menu
        // (which lists this plugin's Config) shows only the player-facing settings.
        internal static ConfigEntry<KeyCode> Item4Key, Item5Key, Item6Key, Item4Alt, Item5Alt, Item6Alt;
        // Everything below used to be configurable (<= 1.4.1); fixed since 1.5.0.
        internal static readonly Fixed<string> Blacklist = new Fixed<string>("PartAdjusterTools;box_cardboard;crate_metal;crate_plastic");
        internal static readonly Fixed<Key> Fallback1 = new Fixed<Key>(Key.Digit1), Fallback2 = new Fixed<Key>(Key.Digit2), Fallback3 = new Fixed<Key>(Key.Digit3), FallbackOff = new Fixed<Key>(Key.None);
        internal static readonly Fixed<float> DefaultX = new Fixed<float>(0f), DefaultY = new Fixed<float>(0f), DefaultZ = new Fixed<float>(0f);
        internal static readonly Fixed<int> IconSize = new Fixed<int>(128);
        internal static readonly Fixed<int> ExtraSlotCount = new Fixed<int>(3);
        internal static readonly Fixed<bool> ExtraKeysInVehicle = new Fixed<bool>(false);

        /// Frame on which the mod consumed a Weapon N / Weapon off press (game's GetButtonDown is suppressed that frame).
        internal static int HandledFrame = -100;
        private static GameObject _runnerGo;

        private void Awake()
        {
            Log = Logger;
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "Enabled", true, "Enable the item slots. Off = the weapon keys behave exactly as in the vanilla game and everything kept in slots 4-6 is dropped in front of you.");
            RequireBackpack = Config.Bind("General", "RequireBackpack", true, "Slots 4-6 are unlocked by the backpack you wear: small = slot 4, medium = 4-5, large / huge = 4-6. Off = all three are always available.");
            Verbose = Config.Bind("Debug", "VerboseLog", false, "Log every step to the BepInEx console/log (debugging only).");

            // Slot 4-6 keys live in their own file (Controls screen <-> file, two-way).
            string keysPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Config.ConfigFilePath), GUID + ".keys.cfg");
            var keys = new ConfigFile(keysPath, true);
            Item4Key = keys.Bind("Keys", "Item4Key", KeyCode.Alpha4, "Key for slot 4 (rebind it in the game's Controls screen, row \"Item 4\").");
            Item4Alt = keys.Bind("Keys", "Item4AltKey", KeyCode.None, "Alternative key for slot 4.");
            Item5Key = keys.Bind("Keys", "Item5Key", KeyCode.Alpha5, "Key for slot 5.");
            Item5Alt = keys.Bind("Keys", "Item5AltKey", KeyCode.None, "Alternative key for slot 5.");
            Item6Key = keys.Bind("Keys", "Item6Key", KeyCode.Alpha6, "Key for slot 6.");
            Item6Alt = keys.Bind("Keys", "Item6AltKey", KeyCode.None, "Alternative key for slot 6.");
            MigrateOldConfig(keys);

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

        /// Config files written by 1.4.1 and earlier: carry the slot 4-6 keys over to the keys file, then drop every setting
        /// that is hardcoded now so the file (and the Mods menu) only shows what a player needs.
        private void MigrateOldConfig(ConfigFile keys)
        {
            try
            {
                var old = new[] { "Item4Key", "Item4AltKey", "Item5Key", "Item5AltKey", "Item6Key", "Item6AltKey" };
                var mine = new[] { Item4Key, Item4Alt, Item5Key, Item5Alt, Item6Key, Item6Alt };
                bool moved = false;
                for (int i = 0; i < old.Length; i++)
                {
                    var def = new ConfigDefinition("ExtraSlots", old[i]);
                    string raw;
                    if (!TryGetOrphan(Config, def, out raw) || string.IsNullOrEmpty(raw)) continue;
                    KeyCode kc;
                    try { kc = (KeyCode)Enum.Parse(typeof(KeyCode), raw.Trim(), true); } catch { continue; }
                    if (kc != mine[i].Value) { mine[i].Value = kc; moved = true; }
                }
                string rb;
                if (TryGetOrphan(Config, new ConfigDefinition("ExtraSlots", "RequireBackpack"), out rb)) { bool b; if (bool.TryParse(rb, out b)) RequireBackpack.Value = b; }
                int removed = PurgeOrphans(Config);
                if (moved || removed > 0) { Config.Save(); Logger.LogInfo("Config cleaned up: " + removed + " old setting(s) removed" + (moved ? ", slot 4-6 keys moved to " + System.IO.Path.GetFileName(keys.ConfigFilePath) : "")); }
            }
            catch (Exception e) { Logger.LogWarning("Config migration: " + e.Message); }
        }

        private static System.Collections.IDictionary Orphans(ConfigFile cfg)
        {
            var t = typeof(ConfigFile);
            var p = t.GetProperty("OrphanedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            if (p != null) return p.GetValue(cfg, null) as System.Collections.IDictionary;
            var f = t.GetField("OrphanedEntries", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return f != null ? f.GetValue(cfg) as System.Collections.IDictionary : null;
        }

        private static bool TryGetOrphan(ConfigFile cfg, ConfigDefinition def, out string raw)
        {
            raw = null;
            var d = Orphans(cfg);
            if (d == null || !d.Contains(def)) return false;
            raw = d[def] as string;
            return raw != null;
        }

        private static int PurgeOrphans(ConfigFile cfg)
        {
            var d = Orphans(cfg);
            if (d == null) return 0;
            int n = d.Count;
            d.Clear();
            return n;
        }
    }

    /// A former config value that is hardcoded now; keeps the `.Value` call sites unchanged.
    internal sealed class Fixed<T>
    {
        public readonly T Value;
        public Fixed(T v) { Value = v; }
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

        public bool Valid { get { return Grab != null && Weapons != null && Hand != null && HandItemUse != null && Slots.All(s => s != null) && ExtraSlots.Staging != null; } }

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
    /// Apocapocket also persists its own slot poses; this bridge shares them with Apocasaver for hand-carried items.
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
                Plugin.V(_get != null ? "Apocasaver HandPose bridge found." : "Apocasaver not present: using slot mapping poses.");
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

}
