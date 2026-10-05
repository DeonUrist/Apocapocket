using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Apocapocket
{
    [HarmonyPatch(typeof(GetButtonDown), "OnUpdate")]
    internal static class GetButtonDownPatch
    {
        static bool Prefix(GetButtonDown __instance)
        {
            var run = Runner.Instance;
            if (run == null || !Plugin.Enabled.Value || !run.Ready || __instance.buttonName == null || __instance.Fsm == null) return true;
            string name = __instance.buttonName.Value;
            bool suppress = false;
            if (name == "Use" && __instance.Fsm.Name == "UseWeapon")
                suppress = run.UseOnWorldWeapon(__instance.Fsm.FsmComponent);
            else if (name == "Drop Weapon" && __instance.Fsm.Name == "DropWeapon")
            {
                var host = __instance.Fsm.GameObject.transform;
                suppress = run.CurrentOp != null || run.Save.Busy || run.SlotHoldsItem(host);
                if (!suppress && run.ButtonDown(name, Key.None)) run.BeforeGameDrop(host);
            }
            else if (__instance.Fsm.Name == "Weapons")
            {
                int slot = name == "Weapon 1" ? 0 : name == "Weapon 2" ? 1 : name == "Weapon 3" ? 2 : name == "Weapon off" ? -2 : -1;
                if (slot != -1)
                {
                    Key fallback = slot == 0 ? Key.Digit1 : slot == 1 ? Key.Digit2 : slot == 2 ? Key.Digit3 : Key.None;
                    if (run.ButtonDown(name, fallback)) run.HandleKey(slot);
                    // The mod owns these four actions while gameplay is active. Paused FSMs never consume a pending op.
                    suppress = true;
                }
            }
            if (!suppress) return true;
            if (__instance.storeResult != null) __instance.storeResult.Value = false;
            return false;
        }
    }
}
