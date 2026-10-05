using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using HutongGames.PlayMaker;
using UnityEngine;

namespace Apocapocket
{
    [Serializable]
    internal sealed class SaveMap
    {
        public int version = 2;
        public int selected = -1;
        public bool hasTimeline;
        public float timeline;
        public SaveSlot[] slots = new SaveSlot[0];
    }
    [Serializable]
    internal sealed class SaveSlot
    {
        public int slot;
        public string name;
        public string kind;
        public Vector3 position;
        public Quaternion rotation;
        public bool hasPose;
        public int layer;
    }

    internal sealed class Persistence
    {
        internal const string Key = "Apocapocket.Slots";
        internal bool Loading, Normalised;
        internal bool Busy { get { return Loading || Normalised; } }
        private PlayMakerFSM _fsm;
        private bool _saving, _loadSeen, _seenPlay, _waitForRegistry, _cacheFlushed;
        private float _deadline, _flushAt = -1f, _loadAt = -1f;
        private string _json, _file;
        private string _requestedLoadFile;
        private int _selected = -1;
        private readonly List<string> _loadPaths = new List<string>();
        private readonly Dictionary<GameObject, Vector3> _worldPositions = new Dictionary<GameObject, Vector3>();
        private int _writeTries;

        internal void NewPlayer()
        {
            _fsm = null; _saving = false; Normalised = false; _worldPositions.Clear();
            // A scene can spawn its player after the LoadGame entry hook. Preserve the pending load.
            if (!Loading) { _loadAt = Time.unscaledTime + 0.5f; _seenPlay = false; }
        }

        internal void Tick(Runner run)
        {
            if (_fsm == null)
                foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                    if (f != null && f.FsmName == "SaveLoadGame" && f.gameObject.scene.IsValid()) { _fsm = f; break; }
            string state = Runner.State(_fsm);
            if ((state == "wait" || state == "SaveGame") && !_saving) BeginSave(run);
            if (_saving && state == "isPlay" && (!_waitForRegistry || _cacheFlushed)) EndSave(run);
            if (_saving && Time.unscaledTime > _deadline)
            { Plugin.Log.LogWarning("Save normalization timeout; returning inventory"); EndSave(run); }
            if (Normalised)
                foreach (var pair in _worldPositions)
                    if (pair.Key != null)
                    {
                        pair.Key.transform.SetParent(null, true);
                        pair.Key.transform.position = pair.Value;
                        Runner.ParkLock(pair.Key, false);
                        var rb = Runner.EnsureBody(pair.Key);
                        rb.isKinematic = false; rb.useGravity = true;
                        rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero;
                    }
            if (state == "isPlay" && !_seenPlay)
            {
                _seenPlay = true;
                if (Loading || _loadAt >= 0f) _loadAt = Time.unscaledTime + 0.5f;
            }
            if (_loadAt >= 0f && Time.unscaledTime >= _loadAt && state == "isPlay")
            { _loadAt = -1f; RestoreLoad(run); }
            if (_flushAt >= 0f && Time.unscaledTime >= _flushAt)
            {
                if (string.IsNullOrEmpty(_file)) _file = CurrentFile();
                if (WriteMapping(_file, _json)) _flushAt = -1f;
                else if (++_writeTries >= 3) { _flushAt = -1f; Plugin.Log.LogWarning("Slot mapping could not be verified in the game save"); }
                else _flushAt = Time.unscaledTime + 1f;
            }
        }

        internal void OnState(Runner run, Fsm fsm, string state)
        {
            if (fsm.Name == "Continue" && state == "clicked" && fsm.GameObject != null && fsm.GameObject.name.StartsWith("load_game_", StringComparison.Ordinal))
            {
                int number;
                if (int.TryParse(fsm.GameObject.name.Substring("load_game_".Length), out number)) _requestedLoadFile = "SaveGame" + number + ".es3";
            }
            if (fsm.Name == "Save_NewGO_ArrayList" && state == "store cached file" && _saving)
            {
                var path = fsm.FsmComponent.FsmVariables.GetFsmString("SaveFile");
                if (path != null && !string.IsNullOrEmpty(path.Value)) _file = path.Value;
                // Confirmed by the shipped FSM dump: both StoreCachedFile actions run immediately after this prefix.
                StampTimeline();
                CacheMapping(_file, _json);
                return;
            }
            if (fsm.Name != "SaveLoadGame" || fsm.GameObject == null) return;
            _fsm = fsm.FsmComponent;
            if (state == "LoadGame" || state == "Start" || state == "setSeed")
            {
                if (!run.Ready) Apocapocket.Refs.Find(); // Register legacy parents before LoadAll can spawn their children.
                if (run.Ready) { run.Rollback("load"); run.EndBorrowNow(); }
                // Prevent the old character's delayed write from touching the new slot.
                _flushAt = -1f; _json = null; _file = null; _saving = false; Normalised = false;
                _worldPositions.Clear(); _loadPaths.Clear();
                if (state != "LoadGame") _requestedLoadFile = null;
                if (!string.IsNullOrEmpty(_requestedLoadFile)) _loadPaths.Add(_requestedLoadFile);
                foreach (var path in Candidates()) _loadPaths.Add(path);
                Loading = true; _loadSeen = state == "LoadGame"; _seenPlay = false;
                _loadAt = -1f;
                run.ResetForLoad();
            }
            else if ((state == "wait" || state == "SaveGame") && run.Ready)
            {
                if (!_saving) BeginSave(run);
                // Refresh at SaveGame entry, before SaveAll: defaultSettings now points to this slot.
                _file = CurrentFile();
                CacheMapping(_file, _json);
            }
            else if (state == "isPlay")
            {
                if (_saving && run.Ready && (!_waitForRegistry || _cacheFlushed)) EndSave(run);
                if (Loading) _loadAt = Time.unscaledTime + 0.5f;
            }
        }

        internal void AfterState(Runner run, Fsm fsm, string state)
        {
            if (!_saving || fsm.Name != "Save_NewGO_ArrayList" || state != "store cached file") return;
            _cacheFlushed = true;
            if (run.Ready && Runner.State(_fsm) == "isPlay") EndSave(run);
        }

        internal void BeginSave(Runner run)
        {
            if (_saving || !run.Ready) return;
            // Finish the preceding write before replacing its snapshot (rapid successive saves).
            if (_flushAt >= 0f) { WriteMapping(_file, _json); _flushAt = -1f; }
            _selected = run.Selected;
            run.Rollback("save normalization"); run.EndBorrowNow();
            _saving = true; Normalised = true; _deadline = Time.unscaledTime + 60f;
            _waitForRegistry = false; _cacheFlushed = false;
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                if (f != null && f.FsmName == "Save_NewGO_ArrayList" && f.gameObject.scene.IsValid()) { _waitForRegistry = true; break; }
            _file = CurrentFile(); _writeTries = 0;
            var entries = new List<SaveSlot>();
            for (int i = 0; i < 6; i++)
            {
                var slot = run.Slots[i];
                if (slot.Content == null) continue;
                if (Plugin.Enabled.Value)
                    entries.Add(new SaveSlot { slot = i + 1, name = slot.Content.name, kind = slot.Kind.ToString(), position = slot.Position, rotation = slot.Rotation, hasPose = slot.HasPose, layer = slot.Layer });
                if (slot.Kind == Kind.Item || i >= 3)
                {
                    run.MakeWorld(slot, i, false);
                    _worldPositions[slot.Content] = slot.Content.transform.position;
                }
            }
            _json = Plugin.Enabled.Value ? JsonUtility.ToJson(new SaveMap { selected = _selected, slots = entries.ToArray() }) : null;
            Plugin.V("Save mapping: " + _json);
            CacheMapping(_file, _json);
        }

        private void EndSave(Runner run)
        {
            if (!_saving) return;
            StampTimeline();
            _saving = false; Normalised = false;
            for (int i = 0; i < 6; i++)
            {
                if (run.Slots[i].Content == null) continue;
                if (Plugin.Enabled.Value && i < run.Unlocked) run.Pocket(run.Slots[i], run.Refs.Slots[i]);
                else if (i >= 3 || run.Slots[i].Kind == Kind.Item) { run.MakeWorld(run.Slots[i], i, false); run.Slots[i] = new SlotState(); }
                else { RestartGuard.Set(run.Slots[i].Content, false); Runner.EnableFsm(run.Slots[i].Content, "LockPhysics", true); }
            }
            _worldPositions.Clear();
            if (Plugin.Enabled.Value && _selected >= 0 && _selected < run.Unlocked && run.Slots[_selected].Kind != Kind.Item) run.QueueSelection(_selected);
            if (string.IsNullOrEmpty(_file)) _file = CurrentFile();
            _flushAt = Time.unscaledTime + 1f;
            Plugin.V("Save finished; inventory returned; mapping verification pending for " + _file);
        }

        private void RestoreLoad(Runner run)
        {
            foreach (var path in Candidates()) if (!_loadPaths.Contains(path)) _loadPaths.Add(path);
            Loading = false;
            string json = null;
            foreach (var path in _loadSeen ? _loadPaths : new List<string>())
            {
                if (TryRead(path, out json)) { _file = path; break; }
                // Do not use another slot's mapping when the actual loaded save has no mod key.
                if (File.Exists(Settings(path, ES3.Location.File).FullPath)) break;
            }
            _loadPaths.Clear();
            _requestedLoadFile = null;
            if (json == null) { run.AdoptVanilla(true); run.EjectLockedAfterLoad(); return; }
            SaveMap map;
            try { map = JsonUtility.FromJson<SaveMap>(json); }
            catch (Exception e) { Plugin.Log.LogWarning("Invalid save slot mapping: " + e.Message); run.AdoptVanilla(true); return; }
            if (map == null || map.version != 2 || map.slots == null) { Plugin.Log.LogWarning("Unsupported slot mapping version"); return; }
            var timeline = _fsm != null ? _fsm.FsmVariables.GetFsmFloat("Timeline") : null;
            if (map.hasTimeline && timeline != null && Math.Abs(map.timeline - timeline.Value) > 0.00001f)
            {
                // Vanilla ES3 caches can retain unknown keys after saving without this DLL.
                // A changed vanilla save revision means this old mapping no longer owns those world items.
                Plugin.V("Ignoring stale mapping from a save subsequently written without Apocapocket");
                run.AdoptVanilla(true); run.EjectLockedAfterLoad(); return;
            }
            var objects = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                if (f != null && f.gameObject.scene.IsValid() && (f.FsmName == "ItemName" || f.FsmName == "saveItemVar" || f.FsmName == "weaponType")) objects[f.gameObject.name] = f.gameObject;
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in map.slots)
            {
                if (entry == null || entry.slot < 1 || entry.slot > 6 || string.IsNullOrEmpty(entry.name) || !used.Add(entry.name)) continue;
                GameObject item;
                if (!objects.TryGetValue(entry.name, out item)) { Plugin.Log.LogWarning("Load: missing pocket item " + entry.name); continue; }
                int index = entry.slot - 1;
                if (run.Slots[index].Content != null && run.Slots[index].Content != item) { Plugin.Log.LogWarning("Load: duplicate slot " + entry.slot); continue; }
                var slot = Runner.Capture(item);
                slot.Position = entry.position; slot.Rotation = entry.rotation; slot.HasPose = entry.hasPose; slot.Layer = entry.layer;
                if (!Plugin.Enabled.Value)
                {
                    if (index >= 3 || slot.Kind == Kind.Item) { Plugin.Log.LogInfo("Load: " + item.name + " from slot " + entry.slot + " to the world (mod disabled)"); run.MakeWorld(slot, index, false); }
                    continue;
                }
                run.Slots[index] = slot; run.Pocket(slot, run.Refs.Slots[index]);
            }
            run.AdoptVanilla(false);
            run.EjectLockedAfterLoad();
            if (Plugin.Enabled.Value && map.selected >= 0 && map.selected < run.Unlocked) run.QueueSelection(map.selected);
            Plugin.V("Load mapping: " + json);
            _loadSeen = false;
        }

        private IEnumerable<string> Candidates()
        {
            var list = new List<string>();
            // During load, NewGO_ArrayList is the slot selected by the load button; SaveLoadGame may still be stale.
            foreach (var f in Resources.FindObjectsOfTypeAll<PlayMakerFSM>())
                if (f != null && f.gameObject.scene.IsValid() && f.gameObject.name == "NewGO_ArrayList")
                { var v = f.FsmVariables.GetFsmString("SaveFile"); if (v != null && !string.IsNullOrEmpty(v.Value) && !list.Contains(v.Value)) list.Add(v.Value); }
            var sv = _fsm != null ? _fsm.FsmVariables.GetFsmString("SaveFile") : null;
            if (sv != null && !string.IsNullOrEmpty(sv.Value) && !list.Contains(sv.Value)) list.Add(sv.Value);
            var path = ES3Settings.defaultSettings.path;
            if (!string.IsNullOrEmpty(path) && !list.Contains(path)) list.Add(path);
            return list;
        }
        private string CurrentFile()
        {
            string path = ES3Settings.defaultSettings.path;
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".es3", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFileName(path), "SaveSettings.es3", StringComparison.OrdinalIgnoreCase)) return path;
            foreach (var candidate in Candidates()) return candidate;
            return null;
        }
        private static ES3Settings Settings(string path, ES3.Location location)
        { var s = new ES3Settings(path); s.location = location; return s; }
        private void StampTimeline()
        {
            if (_json == null || _fsm == null) return;
            var timeline = _fsm.FsmVariables.GetFsmFloat("Timeline");
            if (timeline == null) return;
            var map = JsonUtility.FromJson<SaveMap>(_json);
            map.hasTimeline = true; map.timeline = timeline.Value;
            _json = JsonUtility.ToJson(map);
        }
        private static void CacheMapping(string file, string json)
        {
            if (string.IsNullOrEmpty(file)) return;
            try
            {
                var settings = Settings(file, ES3.Location.Cache);
                if (json == null) ES3.DeleteKey(Key, settings); else ES3.Save<string>(Key, json, settings);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Save slot mapping to cache: " + e.Message); }
        }
        private static bool WriteMapping(string file, string json)
        {
            if (string.IsNullOrEmpty(file)) return false;
            try
            {
                CacheMapping(file, json);
                var settings = Settings(file, ES3.Location.File);
                if (!File.Exists(settings.FullPath)) return false;
                if (json == null) ES3.DeleteKey(Key, settings); else ES3.Save<string>(Key, json, settings);
                bool exists = ES3.KeyExists(Key, settings);
                bool verified = json == null ? !exists : exists && ES3.Load<string>(Key, settings) == json;
                if (verified) Plugin.V("Save slot mapping verified in " + settings.FullPath);
                return verified;
            }
            catch (Exception e) { Plugin.Log.LogWarning("Write/verify slot mapping: " + e.Message); return false; }
        }
        private static bool TryRead(string file, out string json)
        {
            json = null;
            if (string.IsNullOrEmpty(file)) return false;
            try
            {
                var settings = Settings(file, ES3.Location.File);
                if (File.Exists(settings.FullPath) && ES3.KeyExists(Key, settings)) { json = ES3.Load<string>(Key, settings); return true; }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Read slot mapping: " + e.Message); }
            return false;
        }
    }

    [HarmonyPatch(typeof(Fsm), "EnterState")]
    internal static class SaveStatePatch
    {
        static void Prefix(Fsm __instance, FsmState __0)
        {
            var run = Runner.Instance;
            if (run != null && __0 != null) run.Save.OnState(run, __instance, __0.Name);
        }
        static void Postfix(Fsm __instance, FsmState __0)
        {
            var run = Runner.Instance;
            if (run != null && __0 != null) run.Save.AfterState(run, __instance, __0.Name);
        }
    }
    [HarmonyPatch(typeof(Fsm), "ProcessEvent")]
    internal static class SaveEventPatch
    {
        static void Prefix(FsmEvent fsmEvent)
        {
            var run = Runner.Instance;
            if (run != null && fsmEvent != null && fsmEvent.Name == "SaveGame") run.Save.BeginSave(run);
        }
    }
}
