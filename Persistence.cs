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
        public int version = 3;
        public int selected = -1;
        public bool hasTimeline;
        public float timeline;
        // JsonUtility silently skipped the SaveSlot[] field in 2.0.0-2.0.2 (the saved JSON had no "slots" at all), so every
        // load restored nothing. Entries are hand-encoded strings now (string[] always serialises).
        public string[] entries = new string[0];
    }
    internal sealed class SaveSlot
    {
        public int slot;
        public string name;
        public string kind;
        public Vector3 position;
        public Quaternion rotation;
        public bool hasPose;
        public int layer;
        public bool hasWorld;        // the item was laid in the world for the save: where (to recognise it on load)
        public Vector3 world;

        private static readonly System.Globalization.CultureInfo C = System.Globalization.CultureInfo.InvariantCulture;
        private static string F(float f) { return f.ToString("R", C); }
        internal string Encode()
        {
            return string.Join("|", new[] { slot.ToString(C), kind ?? "", hasPose ? "1" : "0", layer.ToString(C),
                F(position.x), F(position.y), F(position.z), F(rotation.x), F(rotation.y), F(rotation.z), F(rotation.w),
                hasWorld ? "1" : "0", F(world.x), F(world.y), F(world.z), Uri.EscapeDataString(name ?? "") });
        }
        internal static SaveSlot Decode(string s)
        {
            try
            {
                var a = (s ?? "").Split('|');
                if (a.Length != 16) return null;
                Func<int, float> f = i => float.Parse(a[i], System.Globalization.NumberStyles.Float, C);
                return new SaveSlot
                {
                    slot = int.Parse(a[0], C), kind = a[1], hasPose = a[2] == "1", layer = int.Parse(a[3], C),
                    position = new Vector3(f(4), f(5), f(6)), rotation = new Quaternion(f(7), f(8), f(9), f(10)),
                    hasWorld = a[11] == "1", world = new Vector3(f(12), f(13), f(14)), name = Uri.UnescapeDataString(a[15])
                };
            }
            catch { return null; }
        }
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
            var entries = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                var slot = run.Slots[i];
                if (slot.Content == null) continue;
                var e = new SaveSlot { slot = i + 1, name = slot.Content.name, kind = slot.Kind.ToString(), position = slot.Position, rotation = slot.Rotation, hasPose = slot.HasPose, layer = slot.Layer };
                if (slot.Kind == Kind.Item || i >= 3)
                {
                    run.MakeWorld(slot, i, false);
                    _worldPositions[slot.Content] = slot.Content.transform.position;
                    e.hasWorld = true; e.world = slot.Content.transform.position;
                }
                if (Plugin.Enabled.Value) entries.Add(e.Encode());
            }
            _json = Plugin.Enabled.Value ? JsonUtility.ToJson(new SaveMap { selected = _selected, entries = entries.ToArray() }) : null;
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
            Loading = false;
            string json = null;
            // The loaded slot = ES3Settings.defaultSettings.path once the game is in play (what Apocasaver, Immersive Map and
            // Apocapatrol use). 2.0.0-2.0.3 tried NewGO_ArrayList/SaveLoadGame "SaveFile" first; those variables are stale
            // (SaveGame1/another slot), so the mapping was read from the WRONG save and nothing was restored.
            var loadPaths = new List<string>();
            string current = null;
            try { current = ES3Settings.defaultSettings.path; } catch { }
            if (!string.IsNullOrEmpty(current)) loadPaths.Add(current);
            if (!string.IsNullOrEmpty(_requestedLoadFile) && !loadPaths.Contains(_requestedLoadFile)) loadPaths.Add(_requestedLoadFile);
            Plugin.V("Load: reading slot mapping from " + string.Join(", ", loadPaths.ToArray()));
            foreach (var path in loadPaths)
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
            if (map == null || map.version != 3 || map.entries == null)
            {
                // 2.0.0-2.0.2 mappings never contained their slot list: nothing to restore, the items lie in the world.
                Plugin.Log.LogInfo("Load: slot mapping version " + (map != null ? map.version : 0) + " has no slot list; items stay where they were saved");
                run.AdoptVanilla(true); run.EjectLockedAfterLoad(); return;
            }
            // Stale-mapping guard (a later save written without Apocapocket can keep this key in the ES3 cache): an entry only
            // owns an item that still lies where the save put it. (2.0.0-2.0.2 compared the save's Timeline with the LIVE clock
            // 0.5 s after load, which always differed.)
            // Candidates by exact name among ALL loaded scene objects that look like saved items (Rigidbody or Collider).
            // 2.0.0-2.0.4 required an ItemName/saveItemVar/weaponType FSM, so items with only an ID FSM (empty alcohol canister)
            // were "missing" and stayed in the world. Prefab assets (no loaded scene), hidden objects and stripped icon copies
            // (no Rigidbody/Collider) are excluded.
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in map.entries) { var e = SaveSlot.Decode(raw); if (e != null && !string.IsNullOrEmpty(e.name)) wanted.Add(e.name); }
            var objects = new Dictionary<string, List<GameObject>>(StringComparer.Ordinal);
            foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            {
                if (t == null) continue;
                var go = t.gameObject;
                if (!wanted.Contains(go.name) || !go.scene.IsValid() || !go.scene.isLoaded) continue;
                if ((go.hideFlags & (HideFlags.HideInHierarchy | HideFlags.DontSave)) != 0) continue;
                if (go.GetComponent<Rigidbody>() == null && go.GetComponent<Collider>() == null) continue;
                List<GameObject> list;
                if (!objects.TryGetValue(go.name, out list)) objects[go.name] = list = new List<GameObject>();
                if (!list.Contains(go)) list.Add(go);
            }
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in map.entries)
            {
                var entry = SaveSlot.Decode(raw);
                if (entry == null || entry.slot < 1 || entry.slot > 6 || string.IsNullOrEmpty(entry.name) || !used.Add(entry.name)) continue;
                int index = entry.slot - 1;
                List<GameObject> found;
                if (!objects.TryGetValue(entry.name, out found) || found.Count == 0) { Plugin.Log.LogWarning("Load: missing pocket item " + entry.name); continue; }
                string why;
                var item = PickCandidate(entry, found, run.Refs.Slots[index], out why);
                if (item == null) { Plugin.Log.LogWarning("Load: " + entry.name + " (slot " + entry.slot + ") not restored: " + why); continue; }
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
        /// Which of the same-named scene objects an entry owns. Items laid in the world for the save (hasWorld) must lie within
        /// 2 m (XZ) of that spot - also the stale-mapping guard; more than one that close = ambiguous, nothing is taken.
        /// Weapons that stayed in a game slot (no world position): the one under that slot, else the only candidate.
        internal static GameObject PickCandidate(SaveSlot entry, List<GameObject> found, Transform slotParent, out string why)
        {
            why = null;
            if (entry.hasWorld)
            {
                GameObject best = null; int near = 0;
                foreach (var go in found) if (FlatDistance(go.transform.position, entry.world) <= 2f) { near++; best = go; }
                if (near == 1) return best;
                why = near == 0 ? "it moved since the slot mapping was saved (save written without Apocapocket?); left in the world"
                                : near + " objects with that name lie at the saved spot (ambiguous)";
                return null;
            }
            if (slotParent != null) foreach (var go in found) if (go.transform.parent == slotParent) return go;
            if (found.Count == 1) return found[0];
            why = found.Count + " objects with that name and none in its slot (ambiguous)";
            return null;
        }

        /// Horizontal distance: the items were saved at the drop point and may have fallen / settled a little before the restore.
        private static float FlatDistance(Vector3 a, Vector3 b) { a.y = 0f; b.y = 0f; return Vector3.Distance(a, b); }

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
