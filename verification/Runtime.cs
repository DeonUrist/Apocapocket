// Minimal headless PlayMaker/Unity adapter. Tests execute the production transaction code;
// this does not attempt to certify the game's rendering, physics, serialization or FSM actions.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BepInEx { }
namespace HarmonyLib { public class HarmonyPatch : Attribute { public HarmonyPatch(Type type, string method) { } } }
namespace HutongGames.PlayMaker.Actions { }

namespace UnityEngine
{
    public class Object { private static int next; private readonly int id = ++next; public int GetInstanceID() { return id; } }
    public class Component : Object
    {
        public GameObject gameObject;
        public Transform transform { get { return gameObject.transform; } }
        public T GetComponent<T>() where T : Component { return gameObject.GetComponent<T>(); }
        public T[] GetComponents<T>() where T : Component { return gameObject.GetComponents<T>(); }
        public T[] GetComponentsInChildren<T>(bool all) where T : Component { return gameObject.GetComponentsInChildren<T>(all); }
    }
    public class Behaviour : Component { public bool enabled = true; public bool isActiveAndEnabled { get { return enabled && gameObject.activeInHierarchy; } } }
    public class MonoBehaviour : Behaviour { }
    public class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int order) { } }
    public struct Scene { public bool IsValid() { return true; } }
    public class GameObject : Object
    {
        public string name;
        public int layer = 9;
        public bool activeSelf = true;
        public bool activeInHierarchy { get { return activeSelf && (transform.parent == null || transform.parent.gameObject.activeInHierarchy); } }
        public Scene scene;
        public Transform transform;
        private readonly List<Component> components = new List<Component>();
        public GameObject(string name)
        {
            this.name = name; transform = new Transform { gameObject = this }; components.Add(transform);
            Resources.All.Add(this); Resources.All.Add(transform);
        }
        public T AddComponent<T>() where T : Component, new()
        {
            var c = new T { gameObject = this }; components.Add(c); Resources.All.Add(c);
            var fsm = c as PlayMakerFSM;
            if (fsm != null) fsm.Fsm.Owner = fsm;
            var awake = typeof(T).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance);
            if (awake != null) awake.Invoke(c, null);
            return c;
        }
        public T GetComponent<T>() where T : Component { return components.OfType<T>().FirstOrDefault(); }
        public T GetComponentInChildren<T>(bool all) where T : Component { return GetComponentsInChildren<T>(all).FirstOrDefault(); }
        public T[] GetComponents<T>() where T : Component { return components.OfType<T>().ToArray(); }
        public T[] GetComponentsInChildren<T>(bool all) where T : Component
        {
            var found = new List<T>(GetComponents<T>());
            foreach (Transform child in transform) found.AddRange(child.gameObject.GetComponentsInChildren<T>(all));
            return found.ToArray();
        }
        public void SetActive(bool value) { activeSelf = value; }
    }
    public class Transform : Component, IEnumerable<Transform>
    {
        public string name { get { return gameObject.name; } }
        private readonly List<Transform> children = new List<Transform>();
        public Transform parent;
        public Vector3 position, localPosition;
        public Quaternion localRotation;
        public Vector3 forward { get { return new Vector3(0, 0, 1); } }
        public Vector3 right { get { return new Vector3(1, 0, 0); } }
        public int childCount { get { return children.Count; } }
        public Transform GetChild(int index) { return children[index]; }
        public void SetParent(Transform target, bool world = true)
        { if (parent != null) parent.children.Remove(this); parent = target; if (target != null) target.children.Add(this); }
        public Transform Find(string path)
        {
            Transform node = this;
            foreach (string part in path.Split('/')) { node = node.children.FirstOrDefault(c => c.name == part); if (node == null) return null; }
            return node;
        }
        public bool IsChildOf(Transform ancestor) { for (var p = parent; p != null; p = p.parent) if (p == ancestor) return true; return false; }
        public IEnumerator<Transform> GetEnumerator() { return children.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero { get { return new Vector3(); } }
        public static Vector3 up { get { return new Vector3(0, 1, 0); } }
        [System.Text.Json.Serialization.JsonIgnore]
        public Vector3 normalized { get { float size = (float)Math.Sqrt(x * x + y * y + z * z); return size == 0 ? zero : this * (1 / size); } }
        public static Vector3 operator +(Vector3 a, Vector3 b) { return new Vector3(a.x + b.x, a.y + b.y, a.z + b.z); }
        public static Vector3 operator *(Vector3 a, float b) { return new Vector3(a.x * b, a.y * b, a.z * b); }
    }
    public struct Quaternion { public float x, y, z, w; public static Quaternion identity { get { return new Quaternion { w = 1 }; } } }
    public class Rigidbody : Component { public bool isKinematic, useGravity; public float mass; public Vector3 velocity, angularVelocity; }
    public class Renderer : Behaviour { }
    public class Collider : Behaviour { public bool isTrigger; }
    public class Camera : Behaviour { public static Camera main; }
    public static class Time { public static int frameCount; public static float unscaledTime, timeScale = 1; }
    public static class Random { public static Vector3 insideUnitSphere { get { return Vector3.zero; } } }
    public static class Resources { public static readonly List<Object> All = new List<Object>(); public static T[] FindObjectsOfTypeAll<T>() where T : Object { return All.OfType<T>().ToArray(); } }
    public static class Input { public static bool GetButtonDown(string button) { return false; } }
    public static class JsonUtility
    {
        private static readonly System.Text.Json.JsonSerializerOptions options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        public static string ToJson<T>(T value) { return System.Text.Json.JsonSerializer.Serialize(value, options); }
        public static T FromJson<T>(string value) { return System.Text.Json.JsonSerializer.Deserialize<T>(value, options); }
    }
}
namespace UnityEngine.UI { public class Text : UnityEngine.Behaviour { public string text; } }
namespace UnityEngine.InputSystem
{
    public enum Key { None, Digit1, Digit2, Digit3 }
    public class KeyControl { public bool wasPressedThisFrame; }
    public class Keyboard { public static Keyboard current; public KeyControl this[Key key] { get { return new KeyControl(); } } }
}
namespace HutongGames.PlayMaker
{
    public class FsmGameObject { public UnityEngine.GameObject Value; }
    public class FsmString { public string Value; }
    public class FsmBool { public bool Value; }
    public class FsmInt { public int Value; }
    public class FsmFloat { public float Value; }
    public class FsmVariables
    {
        public static FsmVariables GlobalVariables = new FsmVariables();
        public Dictionary<string, FsmGameObject> Objects = new Dictionary<string, FsmGameObject>();
        public Dictionary<string, FsmBool> Bools = new Dictionary<string, FsmBool>();
        public Dictionary<string, FsmString> Strings = new Dictionary<string, FsmString>();
        public Dictionary<string, FsmFloat> Floats = new Dictionary<string, FsmFloat>();
        public FsmGameObject GetFsmGameObject(string key) { FsmGameObject v; Objects.TryGetValue(key, out v); return v; }
        public FsmBool GetFsmBool(string key) { FsmBool v; Bools.TryGetValue(key, out v); return v; }
        public FsmString GetFsmString(string key) { FsmString v; Strings.TryGetValue(key, out v); return v; }
        public FsmInt GetFsmInt(string key) { return null; }
        public FsmFloat GetFsmFloat(string key) { FsmFloat value; Floats.TryGetValue(key, out value); return value; }
    }
    public class Fsm
    {
        public bool Initialized = true, RestartOnEnable = true;
        public PlayMakerFSM Owner;
        public string Name { get { return Owner.FsmName; } }
        public UnityEngine.GameObject GameObject { get { return Owner.gameObject; } }
        public PlayMakerFSM FsmComponent { get { return Owner; } }
        public void SetState(string state) { Owner.ActiveStateName = state; if (Owner.OnState != null) Owner.OnState(state); }
    }
    public class FsmEvent { public string Name; }
    public class FsmState { public string Name; }
}
public class PlayMakerFSM : UnityEngine.Behaviour
{
    public string FsmName, ActiveStateName = "off";
    public HutongGames.PlayMaker.Fsm Fsm = new HutongGames.PlayMaker.Fsm();
    public HutongGames.PlayMaker.FsmVariables FsmVariables = new HutongGames.PlayMaker.FsmVariables();
    public Action<string> OnState, OnEvent;
    public void SendEvent(string ev) { if (OnEvent != null) OnEvent(ev); }
}
namespace Apocapocket
{
    internal sealed class ConfigValue<T> { public T Value; public ConfigValue(T value) { Value = value; } }
    internal sealed class LogAdapter
    {
        public readonly List<string> Warnings = new List<string>();
        public void LogWarning(object value) { Warnings.Add(value.ToString()); }
    }
    internal static class Plugin
    {
        internal static ConfigValue<bool> Enabled = new ConfigValue<bool>(true), RequireBackpack = new ConfigValue<bool>(false);
        internal static ConfigValue<string> Blacklist = new ConfigValue<string>("PartAdjusterTools;box_cardboard;crate_metal;crate_plastic");
        internal static LogAdapter Log = new LogAdapter();
        internal static int HandledFrame = -100;
        internal static void V(string message) { }
    }
    internal class Refs
    {
        internal PlayMakerFSM Grab, Weapons, Menu, GrabPause, InCar;
        internal UnityEngine.Transform Hand, HandItemUse;
        internal UnityEngine.Transform[] Slots = new UnityEngine.Transform[6];
        internal bool Valid { get { return Grab != null && Weapons != null && Hand != null && HandItemUse != null && Slots.All(t => t != null); } }
        internal static Refs Find() { return Runner.Instance.Refs; }
    }
    internal static class Fsms
    {
        internal static PlayMakerFSM Find(UnityEngine.GameObject go, string name) { return go == null ? null : go.GetComponents<PlayMakerFSM>().FirstOrDefault(f => f.FsmName == name); }
    }
    internal static class RestartGuard
    {
        internal static void Set(UnityEngine.GameObject item, bool guarded)
        { if (item != null) foreach (var f in item.GetComponents<PlayMakerFSM>()) if (f.FsmName == "CheckTag" || f.FsmName == "LockPhysics") f.Fsm.RestartOnEnable = !guarded; }
    }
    internal static class ApocasaverBridge
    {
        internal static bool TryGetPose(string name, out UnityEngine.Vector3 p, out UnityEngine.Quaternion q) { p = UnityEngine.Vector3.zero; q = UnityEngine.Quaternion.identity; return false; }
        internal static void SetPose(string name, UnityEngine.Vector3 p, UnityEngine.Quaternion q) { }
    }
    internal static class Icons
    {
        internal static UnityEngine.Transform[] Slots;
        internal static string PrefabName(UnityEngine.GameObject go) { return go.name.Split('(')[0]; }
        internal static void ResetUi() { }
        internal static void Tick(bool active) { }
    }
    internal static class Keybinds
    {
        internal static readonly HashSet<string> Pressed = new HashSet<string>();
        internal static void Tick() { }
        internal static bool Down(int index) { return false; }
        internal static bool? GameActionDown(string name) { return Pressed.Contains(name); }
    }
    internal static class ExtraSlots
    {
        internal static int Unlocked = 3;
        internal static int Active { get { return Unlocked; } }
        internal static UnityEngine.Transform Staging;
        internal static void ResetUi() { }
        internal static void HideAll() { }
        internal static void EnsureUi(UnityEngine.Transform a, UnityEngine.Transform b) { }
        internal static void UpdateUi(Func<int, UnityEngine.GameObject> item, int selected) { }
        internal static void ShowLogicalGameSlot(int index, UnityEngine.GameObject item, bool selected) { }
        internal static void ShowInGameSlot(int index, UnityEngine.GameObject item) { }
    }
}

public class ES3Settings
{
    public static ES3Settings defaultSettings = new ES3Settings("SaveGame1.es3");
    public static string TestDirectory;
    public string path;
    public ES3.Location location;
    public string FullPath { get { return System.IO.Path.Combine(TestDirectory, path); } }
    public ES3Settings(string path) { this.path = path; }
}
public static class ES3
{
    public enum Location { Cache, File }
    private static readonly Dictionary<string, Dictionary<string, string>> cache = new Dictionary<string, Dictionary<string, string>>();
    private static Dictionary<string, string> Read(ES3Settings settings)
    {
        if (settings.location == Location.Cache)
        { Dictionary<string, string> data; if (!cache.TryGetValue(settings.FullPath, out data)) cache[settings.FullPath] = data = new Dictionary<string, string>(); return data; }
        return System.IO.File.Exists(settings.FullPath) ? System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(System.IO.File.ReadAllText(settings.FullPath)) : new Dictionary<string, string>();
    }
    public static void Save<T>(string key, T value, ES3Settings settings)
    { var data = Read(settings); data[key] = (string)(object)value; if (settings.location == Location.File) System.IO.File.WriteAllText(settings.FullPath, System.Text.Json.JsonSerializer.Serialize(data)); }
    public static T Load<T>(string key, ES3Settings settings) { return (T)(object)Read(settings)[key]; }
    public static bool KeyExists(string key, ES3Settings settings) { return Read(settings).ContainsKey(key); }
    public static void DeleteKey(string key, ES3Settings settings)
    { var data = Read(settings); data.Remove(key); if (settings.location == Location.File && System.IO.File.Exists(settings.FullPath)) System.IO.File.WriteAllText(settings.FullPath, System.Text.Json.JsonSerializer.Serialize(data)); }
    public static void Flush(string file)
    { var data = Read(new ES3Settings(file) { location = Location.Cache }); System.IO.File.WriteAllText(new ES3Settings(file).FullPath, System.Text.Json.JsonSerializer.Serialize(data)); }
}
