using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using InsaneSystems.InputManager;
using UnityEngine;

namespace Apocapocket
{
    /// Integration with the game's rebinding asset (InsaneSystems InputManager). The game's PlayMaker GetButtonDown actions
    /// call InputController.GetKeyActionIsDown(name) on the actions in InputStorage.keys, and the Controls screen
    /// (InsaneSystems.InputManager.UI.Settings) builds one row per entry of that list.
    ///  - "Item 4/5/6" are inserted right after "Weapon 3" while the mod is enabled. InputStorage drops entries that are not in
    ///    its built-in defaults whenever it loads from PlayerPrefs, so they are re-added on every start; their keys live in this
    ///    mod's config (two-way sync: a rebind in the Controls screen is written to the config, a config edit to the action).
    ///  - The rows "Weapon 1/2/3" are shown as "Item 1/2/3" (display only: the game looks actions up by name).
    internal static class Keybinds
    {
        internal static readonly string[] Names = { "Item 4", "Item 5", "Item 6" };
        private static readonly KeyCode[] _lastKey = new KeyCode[3], _lastAlt = new KeyCode[3];
        private static float _next;
        private static bool _lastEnabled;
        private static bool _warned;

        private static ConfigEntry<KeyCode> KeyCfg(int i) { return i == 0 ? Plugin.Item4Key : i == 1 ? Plugin.Item5Key : Plugin.Item6Key; }
        private static ConfigEntry<KeyCode> AltCfg(int i) { return i == 0 ? Plugin.Item4Alt : i == 1 ? Plugin.Item5Alt : Plugin.Item6Alt; }

        /// True if the key (or its alternative) of extra slot i (0..2) went down this frame.
        internal static bool Down(int i)
        {
            var k = KeyCfg(i).Value; var a = AltCfg(i).Value;
            return (k != KeyCode.None && Input.GetKeyDown(k)) || (a != KeyCode.None && Input.GetKeyDown(a));
        }

        /// The game's own "Weapon N" action (follows rebinds made in the Controls screen); null if unavailable.
        internal static bool? GameActionDown(string name)
        {
            try { return InputController.GetKeyActionIsDown(name); }
            catch (Exception e) { if (!_warned) { _warned = true; Plugin.Log.LogWarning("InputController unavailable (" + e.Message + "); using legacy input"); } return null; }
        }

        internal static void Tick()
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 0.5f;
            InputStorage st;
            try { st = InputStorage.Singleton; } catch { return; }
            if (st == null || st.Keys == null) return;
            var keys = st.Keys;
            bool enabled = Plugin.Enabled.Value;
            bool changed = false;

            if (enabled)
            {
                int after = keys.FindIndex(k => k != null && k.Name == "Weapon 3");
                for (int i = 0; i < 3; i++)
                {
                    var ka = keys.Find(k => k != null && k.Name == Names[i]);
                    if (ka == null)
                    {
                        ka = Make(Names[i], KeyCfg(i).Value, AltCfg(i).Value);
                        if (ka == null) return;
                        int at = after >= 0 ? Mathf.Min(after + 1 + i, keys.Count) : keys.Count;
                        keys.Insert(at, ka);
                        _lastKey[i] = KeyCfg(i).Value; _lastAlt[i] = AltCfg(i).Value;
                        changed = true;
                        Plugin.V("Controls: added \"" + Names[i] + "\" (" + KeyCfg(i).Value + ")");
                        continue;
                    }
                    // Two-way sync between the action (Controls screen) and the config (Mods menu / cfg file).
                    Sync(ka, i);
                }
            }
            else
            {
                for (int i = 0; i < 3; i++) { int idx = keys.FindIndex(k => k != null && k.Name == Names[i]); if (idx >= 0) { keys.RemoveAt(idx); changed = true; } }
            }
            if (changed || enabled != _lastEnabled) { _lastEnabled = enabled; RefreshMenu(); }
        }

        private static void Sync(KeyAction ka, int i)
        {
            KeyCode ak = ka.Key, aa = ka.AlternativeKey;
            var ck = KeyCfg(i); var ca = AltCfg(i);
            if (ck.Value != _lastKey[i]) { SetKey(ka, "key", ck.Value); _lastKey[i] = ck.Value; RefreshMenu(); }
            else if (ak != _lastKey[i]) { ck.Value = ak; _lastKey[i] = ak; Plugin.V("Controls: " + Names[i] + " rebound to " + ak); }
            if (ca.Value != _lastAlt[i]) { SetKey(ka, "alternativeKey", ca.Value); _lastAlt[i] = ca.Value; RefreshMenu(); }
            else if (aa != _lastAlt[i]) { ca.Value = aa; _lastAlt[i] = aa; }
        }

        private static KeyAction Make(string name, KeyCode key, KeyCode alt)
        {
            try
            {
                var ka = new KeyAction();
                SetField(typeof(InputAction), ka, "name", name);
                SetKey(ka, "key", key);
                SetKey(ka, "alternativeKey", alt);
                return ka;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Could not create control \"" + name + "\": " + e.Message); return null; }
        }

        private static void SetKey(KeyAction ka, string field, KeyCode v) { SetField(typeof(KeyAction), ka, field, v); }

        private static void SetField(Type t, object o, string name, object v)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f == null) throw new MissingFieldException(t.Name, name);
            f.SetValue(o, v);
        }

        /// Rebuild the Controls screen if it exists (its rows are generated from InputStorage.keys).
        private static void RefreshMenu()
        {
            try
            {
                var s = InsaneSystems.InputManager.UI.Settings.singleton;
                if (s == null) return;
                var m = typeof(InsaneSystems.InputManager.UI.Settings).GetMethod("RefreshUI", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (m != null) m.Invoke(s, null);
            }
            catch (Exception e) { Plugin.V("Controls refresh: " + e.Message); }
        }
    }

    /// Display "Weapon 1/2/3" as "Item 1/2/3" in the Controls screen while the mod is enabled.
    [HarmonyPatch(typeof(InsaneSystems.InputManager.UI.SettingsEntry), "SetName")]
    internal static class SettingsEntry_SetName_Patch
    {
        static void Postfix(InsaneSystems.InputManager.UI.SettingsEntry __instance, string value)
        {
            if (!Plugin.Enabled.Value || value == null || !value.StartsWith("Weapon ") || value.Length != 8 || value[7] < '1' || value[7] > '3') return;
            try
            {
                var t = Traverse.Create(__instance).Field("nameText").GetValue<UnityEngine.UI.Text>();
                if (t != null) t.text = "Item " + value[7];
            }
            catch { }
        }
    }
}
