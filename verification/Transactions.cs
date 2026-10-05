using System;
using System.Linq;
using System.Reflection;
using Apocapocket;
using HutongGames.PlayMaker;
using UnityEngine;

internal static class Transactions
{
    private static int checks;
    private static readonly MethodInfo update = typeof(Runner).GetMethod("Update", BindingFlags.NonPublic | BindingFlags.Instance);
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    private static PlayMakerFSM Fsm(GameObject go, string name, string state = "off")
    { var f = go.AddComponent<PlayMakerFSM>(); f.FsmName = name; f.ActiveStateName = state; return f; }
    private static Transform Child(Transform parent, string name)
    { var t = new GameObject(name).transform; t.SetParent(parent, false); return t; }

    private static Runner Create()
    {
        Resources.All.Clear(); Plugin.Enabled.Value = true; Plugin.RequireBackpack.Value = false;
        Plugin.HandledFrame = -100; Plugin.Log.Warnings.Clear();
        Keybinds.Pressed.Clear();
        Time.frameCount = 0; Time.unscaledTime = 0; Time.timeScale = 1; Camera.main = null;
        var r = new GameObject("Runner").AddComponent<Runner>();
        var camera = new GameObject("PlayerCamera");
        var refs = new Refs(); r.Refs = refs;
        refs.Grab = Fsm(camera, "GrabItem", "notHold");
        refs.Grab.FsmVariables.Objects["Item"] = new FsmGameObject();
        refs.Hand = Child(camera.transform, "Hand");
        refs.Grab.FsmVariables.Objects["Hand"] = new FsmGameObject { Value = refs.Hand.gameObject };
        refs.HandItemUse = Child(camera.transform, "HandItemUse");
        refs.Weapons = Fsm(new GameObject("Weapons"), "Weapons", "off 2");
        refs.Weapons.FsmVariables.Bools["weaponOff_Bool"] = new FsmBool { Value = true };
        for (int i = 0; i < 6; i++)
        {
            refs.Slots[i] = Child(i < 3 ? refs.Weapons.transform : camera.transform, "Slot " + (i + 1));
            if (i < 3)
            {
                Fsm(refs.Slots[i].gameObject, "SlotEmptyFull", "empty");
                refs.Weapons.FsmVariables.Bools["weapon" + (i + 1) + "_Bool"] = new FsmBool();
            }
        }
        ExtraSlots.Staging = Child(camera.transform, "Staging"); ExtraSlots.Unlocked = 3;
        refs.Grab.OnEvent = ev =>
        {
            if (ev == "not_Hold" || ev == "drop")
            {
                var held = refs.Grab.FsmVariables.GetFsmGameObject("Item").Value;
                if (ev == "drop" && held != null) held.transform.SetParent(null);
                refs.Grab.ActiveStateName = "notHold";
            }
        };
        refs.Grab.OnState = state => { if (state == "Grab") refs.Grab.ActiveStateName = Time.timeScale > 0 ? "ItemInHand" : "notHold"; };
        refs.Weapons.OnState = state =>
        {
            if (state.StartsWith("Slot ") && refs.HandItemUse.childCount == 0) Child(refs.HandItemUse, "DrawnArms");
        };
        refs.Weapons.OnEvent = ev =>
        {
            if (ev != "back") return;
            refs.Weapons.ActiveStateName = "off 2";
            while (refs.HandItemUse.childCount > 0) refs.HandItemUse.GetChild(0).SetParent(null);
        };
        return r;
    }
    private static GameObject Item(string name, bool weapon = false)
    {
        var item = new GameObject(name); item.AddComponent<Renderer>(); item.AddComponent<Collider>(); item.AddComponent<Rigidbody>();
        Fsm(item, "saveItemVar"); Fsm(item, "LockPhysics"); Fsm(item, "CheckTag");
        if (weapon) { Fsm(item, "weaponType"); Fsm(item, "Visibility"); Fsm(item, "Colliders"); }
        return item;
    }
    private static void Put(Runner r, int slot, GameObject item)
    { r.Slots[slot] = Runner.Capture(item); r.Pocket(r.Slots[slot], r.Refs.Slots[slot]); }
    private static void Hold(Runner r, GameObject item)
    {
        item.transform.SetParent(r.Refs.Hand, false);
        r.Refs.Grab.FsmVariables.GetFsmGameObject("Item").Value = item;
        r.Refs.Grab.ActiveStateName = "ItemInHand";
    }
    private static void Step(Runner r, int count = 1)
    { for (int i = 0; i < count; i++) { Time.frameCount++; Time.unscaledTime += 1f / 60f; update.Invoke(r, null); } }
    private static void Drain(Runner r)
    {
        for (int i = 0; i < 60; i++)
        {
            Step(r);
            if (r.CurrentOp == null && typeof(Runner).GetField("_queued", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(r) == null) return;
        }
        throw new Exception("Transaction did not finish");
    }
    private static void Press(Runner r, int target) { Time.frameCount++; r.HandleKey(target); Drain(r); }

    private static void ItemsAndPause()
    {
        var r = Create(); var can = Item("can(Clone)1");
        var disabled = Child(can.transform, "disabled").gameObject.AddComponent<Renderer>(); disabled.enabled = false;
        var disabledCol = disabled.gameObject.AddComponent<Collider>(); disabledCol.enabled = false;
        can.transform.localPosition = new Vector3(0.2f, 0.3f, 0.4f);
        Hold(r, can); Press(r, 0);
        Check(r.Slots[0].Content == can && r.HeldItem() == null, "Hand store must occupy slot 1");
        Check(!can.GetComponent<Renderer>().enabled && !can.GetComponent<Collider>().enabled, "Pocket item hidden");
        Press(r, 0);
        Check(r.HeldItem() == can && r.Slots[0].Content == null, "Take out clears logical slot after verified grab");
        Check(can.GetComponent<Renderer>().enabled && can.GetComponent<Collider>().enabled && !disabled.enabled && !disabledCol.enabled, "Restore only recorded enabled components");
        Check(Math.Abs(can.transform.localPosition.x - 0.2f) < 0.001f, "Pose roundtrip");
        Press(r, -2); Check(r.Slots[0].Content == can, "Weapon off returns to origin");
        Press(r, 0); r.QueueSelection(0); Step(r, 5);
        Check(r.HeldItem() == can && r.Slots[0].Content == null, "Save selection restoration leaves a hand-carried item in hand");
        Press(r, -2);
        Time.frameCount++; r.HandleKey(0); Step(r, 2); Time.timeScale = 0; Drain(r);
        Check(r.CurrentOp == null && r.Slots[0].Content == can && can.transform.parent == r.Refs.Slots[0], "Paused grab rolls back without a stranded item");
        Check(Plugin.Log.Warnings.Any(s => s.Contains("rollback")), "Grab refusal must report rollback");
    }

    private static void BorrowAndQueue()
    {
        var r = Create(); var weapons = Enumerable.Range(0, 6).Select(i => Item("gun(Clone)" + i, true)).ToArray();
        for (int i = 0; i < 6; i++) Put(r, i, weapons[i]);
        Press(r, 3);
        Check(r.Borrow != null && r.Borrow.Logical == 3 && weapons[3].transform.parent == r.Refs.Slots[0], "Extra weapon borrowed into host");
        Check(r.Slots[0].Content == weapons[0] && weapons[0].transform.parent == ExtraSlots.Staging, "Host model remains parked");
        Time.frameCount++; r.HandleKey(4); Step(r);
        Time.frameCount++; r.HandleKey(0);
        Time.frameCount++; r.HandleKey(5); // latest pending key replaces 1
        Drain(r);
        Check(r.Borrow != null && r.Borrow.Logical == 5 && r.Selected == 5, "Latest queued press executes after borrow restoration");
        Check(weapons[3].transform.parent == r.Refs.Slots[3] && weapons[4].transform.parent == r.Refs.Slots[4], "Previous weapons returned to logical holders");
        Press(r, 5); Check(r.Borrow == null && r.Selected == -1, "Drawn extra key toggles holster");
        Check(weapons[0].transform.parent == r.Refs.Slots[0], "Parked host restored");
        for (int repeat = 0; repeat < 20; repeat++)
            foreach (int target in new[] { 3, 4, 5, 0, 3, 3, 5 }) Press(r, target);
        Check(!Plugin.Log.Warnings.Any(s => s.Contains("rollback") || s.Contains("Invariant")), "Repeated swapping needs no repairs or rollback");
    }

    private static void DropThrowAndAid()
    {
        var r = Create(); var host = Item("host", true); var gun = Item("gun", true);
        Put(r, 0, host); Put(r, 1, Item("second", true)); Put(r, 2, Item("third", true)); Put(r, 4, gun);
        Press(r, 4); r.Refs.Weapons.Fsm.SetState("useAid"); Step(r, 10);
        Check(r.Borrow != null && r.Selected == 4, "Aid does not tear down borrow");
        r.Refs.Weapons.Fsm.SetState("reEquip"); Step(r); r.Refs.Weapons.Fsm.SetState("Slot 1");
        r.BeforeGameDrop(r.Refs.Slots[0]); gun.transform.SetParent(null); Drain(r);
        Check(r.Slots[4].Content == null && r.Borrow == null && host.transform.parent == r.Refs.Slots[0], "Drop clears logical extra and restores host");
        Check(gun.GetComponent<Renderer>().enabled && gun.GetComponent<Collider>().enabled, "Drop safety restores weapon components");
        Put(r, 3, Item("lance", true)); Press(r, 3);
        r.Slots[3].Content = null; Drain(r);
        Check(r.Borrow == null && host.transform.parent == r.Refs.Slots[0], "Destroyed lance restores host");
    }

    private static void RollbackAndCustody()
    {
        var r = Create(); var host = Item("host", true); var gun = Item("extra", true);
        Put(r, 0, host); Put(r, 1, Item("second", true)); Put(r, 2, Item("third", true)); Put(r, 3, gun);
        Time.frameCount++; r.HandleKey(3); Step(r, 3);
        Check(r.CurrentOp.Phase == Phase.InsertBorrow, "Test interrupts before borrowed insertion");
        r.Rollback("test interruption");
        Check(r.Slots[3].Content == gun && gun.transform.parent == r.Refs.Slots[3] && host.transform.parent == r.Refs.Slots[0], "Rollback before insertion retains both weapons");
        r.Refs.Weapons.Fsm.SetState("Slot 1"); r.Refs.Weapons.OnEvent = ev => { };
        Time.frameCount++; r.HandleKey(3); Step(r); Time.timeScale = 0; Step(r, 32);
        Check(r.CurrentOp == null && r.Slots[3].Content == gun, "Timeout advances while paused and retains inventory");
        Time.timeScale = 1; var radio = Item("radio"); Put(r, 4, radio);
        var lp = Fsms.Find(radio, "LockPhysics"); lp.enabled = true; lp.ActiveStateName = "wait"; Step(r);
        Check(lp.ActiveStateName == "off" && !lp.enabled && !lp.Fsm.RestartOnEnable && !Fsms.Find(radio, "CheckTag").Fsm.RestartOnEnable, "Vehicle lock/restart custody guards run every frame");
        Plugin.Enabled.Value = false; Step(r);
        Check(r.Slots[4].Content == null && radio.transform.parent == null && radio.GetComponent<Collider>().enabled, "Runtime disable ejects physical visible item");
        Check(!radio.GetComponent<Rigidbody>().isKinematic && radio.GetComponent<Rigidbody>().useGravity && lp.enabled && lp.Fsm.RestartOnEnable, "Eject restores physics and FSM ownership");
        Check(r.Slots[0].Content == host, "Disable retains vanilla weapon");
        Check(Fsms.Find(host, "LockPhysics").enabled && Fsms.Find(host, "CheckTag").Fsm.RestartOnEnable, "Disable returns native weapon FSM custody to the game");
    }

    private static void PickupPolicy()
    {
        var kinds = new Kind[6];
        for (int i = 0; i < 6; i++) { int free = InventoryPolicy.FirstFree(kinds, 6, -1); Check(free == i, "Pickup fills ordered slots"); kinds[free] = Kind.Weapon; }
        Check(InventoryPolicy.FirstFree(kinds, 6, -1) == -1, "Full slots have no pickup target");
        kinds[1] = Kind.Empty; Check(InventoryPolicy.FirstFree(kinds, 6, -1) == 1, "Drawn slot 1 does not skip empty slot 2");
        kinds[1] = Kind.Item; kinds[3] = Kind.Empty; Check(InventoryPolicy.FirstFree(kinds, 6, -1) == 3, "Item occupied slot skipped");
        Check(InventoryPolicy.FirstFree(kinds, 3, -1) == -1, "Locked extras excluded");
        kinds[0] = Kind.Empty; Check(InventoryPolicy.FirstFree(kinds, 6, 0) == 3, "Borrow host excluded");
        var r = Create(); Press(r, 4); Check(r.Selected == 4 && r.Borrow == null, "Empty extra selectable without borrowing");
    }

    private static PlayMakerFSM SaveFsm(string file)
    {
        var fsm = Fsm(new GameObject("SaveLoadGame"), "SaveLoadGame", "isPlay");
        fsm.FsmVariables.Strings["SaveFile"] = new FsmString { Value = file };
        fsm.FsmVariables.Floats["Timeline"] = new FsmFloat { Value = 42f };
        return fsm;
    }
    private static void SaveAndLoad()
    {
        const string file = "SaveGame1.es3";
        var r = Create(); var fsm = SaveFsm(file); ES3Settings.defaultSettings.path = file;
        var can = Item("can(Clone)1"); var host = Item("host(Clone)2", true); var extra = Item("extra(Clone)3", true);
        Put(r, 0, can); Put(r, 1, host); Put(r, 4, extra); Press(r, 4);
        Check(r.Borrow != null, "Save test begins during borrow");
        fsm.ActiveStateName = "wait";
        r.Save.OnState(r, fsm.Fsm, "wait");
        Check(r.Save.Normalised && r.Borrow == null && r.CurrentOp == null, "Save settles transactions and borrow before serialization");
        Check(can.transform.parent == null && extra.transform.parent == null && host.transform.parent == r.Refs.Slots[1], "Only vanilla weapons retain slot parents in the save");
        Check(can.GetComponent<Renderer>().enabled && can.GetComponent<Collider>().enabled && !can.GetComponent<Rigidbody>().isKinematic && can.GetComponent<Rigidbody>().useGravity, "Save contains visible physical generic items");
        can.transform.SetParent(r.Refs.Hand); can.GetComponent<Rigidbody>().isKinematic = true;
        r.Save.Tick(r);
        Check(can.transform.parent == null && !can.GetComponent<Rigidbody>().isKinematic, "Normalization maintains world parent and dynamic physics during the save");
        Check(extra.GetComponent<Renderer>().enabled && extra.GetComponent<Collider>().enabled && !extra.GetComponent<Collider>().isTrigger, "Save contains visible physical extra weapon");
        ES3.Flush(file);
        fsm.ActiveStateName = "isPlay";
        r.Save.OnState(r, fsm.Fsm, "isPlay");
        Check(!r.Save.Normalised && can.transform.parent == r.Refs.Slots[0] && extra.transform.parent == r.Refs.Slots[4], "Save end repockets synchronously");
        Step(r, 70); Drain(r);
        var settings = new ES3Settings(file) { location = ES3.Location.File };
        Check(ES3.KeyExists(Persistence.Key, settings), "Mapping exists in same game save after delayed verification");
        var map = JsonUtility.FromJson<SaveMap>(ES3.Load<string>(Persistence.Key, settings));
        Check(map.selected == 4 && map.slots.Length == 3 && map.slots.Single(s => s.slot == 5).name == extra.name, "Mapping preserves logical borrowed identity and selection");

        r = Create(); fsm = SaveFsm(file);
        r.Save.OnState(r, fsm.Fsm, "LoadGame");
        can = Item("can(Clone)1"); host = Item("host(Clone)2", true); extra = Item("extra(Clone)3", true);
        host.transform.SetParent(r.Refs.Slots[1]);
        r.Save.OnState(r, fsm.Fsm, "isPlay"); Step(r, 35); Drain(r);
        Check(r.Slots[0].Content == can && r.Slots[1].Content == host && r.Slots[4].Content == extra, "Load rebuilds slots from stable item names");
        Check(r.Selected == 4 && r.Borrow != null, "Selected extra weapon redrawn after load");

        r = Create(); fsm = SaveFsm(file); fsm.FsmVariables.GetFsmFloat("Timeline").Value = 43f;
        r.Save.OnState(r, fsm.Fsm, "LoadGame"); can = Item("can(Clone)1"); extra = Item("extra(Clone)3", true);
        r.Save.OnState(r, fsm.Fsm, "isPlay"); Step(r, 35);
        Check(can.transform.parent == null && extra.transform.parent == null, "Stale unknown ES3 key from a vanilla re-save never repockets items");

        r = Create(); fsm = SaveFsm(file); Plugin.Enabled.Value = false;
        r.Save.OnState(r, fsm.Fsm, "LoadGame");
        can = Item("can(Clone)1"); extra = Item("extra(Clone)3", true);
        r.Save.OnState(r, fsm.Fsm, "isPlay"); Step(r, 35);
        Check(can.transform.parent == null && extra.transform.parent == null && can.GetComponent<Collider>().enabled, "Load with disabled mod leaves mapped items physical in world");
        r.Save.OnState(r, fsm.Fsm, "wait"); ES3.Flush(file); r.Save.OnState(r, fsm.Fsm, "isPlay"); Step(r, 70);
        Check(!ES3.KeyExists(Persistence.Key, settings), "Saving while disabled removes stale mapping from cache and file");

        r = Create(); fsm = SaveFsm(file);
        ES3.Save(Persistence.Key, JsonUtility.ToJson(new SaveMap { slots = new[] { new SaveSlot { slot = 4, name = "stale" } } }), settings);
        var stale = Item("stale", true);
        r.Save.OnState(r, fsm.Fsm, "Start"); r.Save.OnState(r, fsm.Fsm, "setSeed"); r.Save.OnState(r, fsm.Fsm, "isPlay"); Step(r, 35);
        Check(stale.transform.parent == null && r.Slots[3].Content == null, "New game never imports previous character mapping");
    }

    private static void SaveRegistryOrdering()
    {
        var r = Create(); var save = SaveFsm("SaveGame2.es3"); ES3Settings.defaultSettings.path = "SaveGame2.es3";
        var registry = Fsm(new GameObject("NewGO_ArrayList"), "Save_NewGO_ArrayList");
        registry.FsmVariables.Strings["SaveFile"] = new FsmString { Value = "SaveGame2.es3" };
        var radio = Item("radio-save"); Put(r, 3, radio);
        r.Save.OnState(r, save.Fsm, "wait");
        r.Save.OnState(r, save.Fsm, "isPlay"); Step(r);
        Check(r.Save.Normalised && radio.transform.parent == null, "Save remains vanilla-safe until registry serialization actually ends");
        r.Save.OnState(r, registry.Fsm, "store cached file"); ES3.Flush("SaveGame2.es3");
        r.Save.AfterState(r, registry.Fsm, "store cached file");
        Check(!r.Save.Normalised && radio.transform.parent == r.Refs.Slots[3], "Registry flush restores inventory synchronously");
        Check(ES3.KeyExists(Persistence.Key, new ES3Settings("SaveGame2.es3") { location = ES3.Location.File }), "Mapping entered cache before registry flush");
    }

    private static void Pickup(Runner r, GameObject item)
    {
        var use = Fsms.Find(r.Refs.Weapons.gameObject, "UseWeapon") ?? Fsm(r.Refs.Weapons.gameObject, "UseWeapon");
        use.ActiveStateName = "over"; use.FsmVariables.Objects["Item"] = new FsmGameObject { Value = item };
        Time.frameCount++; Keybinds.Pressed.Add("Use");
        Check(r.UseOnWorldWeapon(use), "Mod owns UseWeapon's pickup");
        Keybinds.Pressed.Clear(); Drain(r);
    }
    private static void PickupsAndBackpacks()
    {
        var r = Create(); var guns = Enumerable.Range(0, 6).Select(i => Item("pickup" + i, true)).ToArray();
        Pickup(r, guns[0]); Press(r, 0);
        Pickup(r, guns[1]); Check(r.Slots[1].Content == guns[1] && r.Selected == 0, "Pickup with weapon 1 drawn fills empty slot 2 and retains drawn selection");
        for (int i = 2; i < 6; i++) Pickup(r, guns[i]);
        Check(r.Slots.Select(s => s.Content).SequenceEqual(guns), "Production pickup fills 1 through 6 in order");

        r = Create(); Put(r, 0, Item("first", true)); Press(r, 1);
        var selectedGun = Item("selected", true); Pickup(r, selectedGun);
        Check(r.Slots[1].Content == selectedGun && r.Selected == 1 && r.Refs.Weapons.ActiveStateName == "Slot 2", "Pickup draws newly occupied selected empty game slot");
        r = Create(); for (int i = 0; i < 4; i++) Put(r, i, Item("filled" + i, true));
        Press(r, 4); selectedGun = Item("selected-extra", true); Pickup(r, selectedGun);
        Check(r.Selected == 4 && r.Borrow != null && r.Borrow.Logical == 4, "Pickup into selected empty extra draws through borrow");

        r = Create(); Put(r, 3, Item("borrowed", true)); Press(r, 3); var next = Item("next", true); Pickup(r, next);
        Check(r.Slots[1].Content == next && r.Borrow != null && r.Selected == 3, "Pickup excludes empty physical borrow host and preserves current borrow");

        r = Create(); Plugin.RequireBackpack.Value = true;
        var quick = Child(r.Refs.Grab.transform, "QuickItems"); var holder = Child(quick, "Backpack_Item");
        foreach (var pair in new[] { Tuple.Create("small", 4), Tuple.Create("medium", 5), Tuple.Create("large", 6), Tuple.Create("huge", 6) })
        {
            while (holder.childCount > 0) holder.GetChild(0).SetParent(null);
            Item("backpack_" + pair.Item1).transform.SetParent(holder); Step(r);
            Check(r.Unlocked == pair.Item2, "Backpack unlocks " + pair.Item1);
        }
        var radio = Item("overflow"); Put(r, 5, radio);
        holder.GetChild(0).SetParent(null); Step(r, 100);
        Check(r.Slots[5].Content == null && radio.transform.parent == null, "Stable backpack removal ejects overflow");
    }

    public static void Main()
    {
        ES3Settings.TestDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Apocapocket-tests-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(ES3Settings.TestDirectory);
        try
        {
            ItemsAndPause(); BorrowAndQueue(); DropThrowAndAid(); RollbackAndCustody(); PickupPolicy(); PickupsAndBackpacks(); SaveAndLoad(); SaveRegistryOrdering();
            Console.WriteLine("PASS: " + checks + " checks executing production inventory, transaction and persistence code with headless adapters.");
        }
        finally { System.IO.Directory.Delete(ES3Settings.TestDirectory, true); }
    }
}
