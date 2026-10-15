using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Apocapocket
{
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
                if (f == null || f.Fsm == null || f.gameObject == null || !f.gameObject.scene.IsValid()) continue;
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
            foreach (var f in go.GetComponents<PlayMakerFSM>()) if (f != null && f.Fsm != null && f.FsmName == name) return f;
            return null;
        }
    }

}
