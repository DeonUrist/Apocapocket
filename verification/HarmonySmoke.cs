using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;

internal static class HarmonySmoke
{
    private static readonly string[] ParameterlessCallbacks = {
        "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy", "Reset", "OnGUI"
    };
    private sealed class InvalidLifecycleFixture { private void Start(object operation) { } }

    private static string InvalidCallback(Type type)
    {
        foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            if (ParameterlessCallbacks.Contains(method.Name) && method.GetParameters().Length != 0)
                return type.FullName + "." + method.Name + " is a reserved Unity callback and must not take parameters";
        return null;
    }
    private static bool IsUnityComponent(Type type)
    {
        for (var parent = type.BaseType; parent != null; parent = parent.BaseType)
            if (parent.FullName == "UnityEngine.MonoBehaviour") return true;
        return false;
    }

    public static void Main(string[] args)
    {
        var plugin = Assembly.LoadFrom(args[0]);
        if (InvalidCallback(typeof(InvalidLifecycleFixture)) == null) throw new Exception("Lifecycle regression detector failed its negative control");
        foreach (var type in plugin.GetTypes().Where(IsUnityComponent))
        {
            string error = InvalidCallback(type);
            if (error != null) throw new Exception(error);
            Console.WriteLine("Checked Unity lifecycle signatures: " + type.FullName);
        }
        var metadata = (BepInPlugin)plugin.GetType("Apocapocket.Plugin").GetCustomAttributes(typeof(BepInPlugin), false)[0];
        if (metadata.GUID != "com.denis.apocalypter.apocapocket" || metadata.Version != new Version(2, 0, 0)) throw new Exception("Invalid plugin identity/version");
        var harmony = new Harmony("apocapocket.verification");
        harmony.PatchAll(plugin);
        var methods = Harmony.GetAllPatchedMethods().Where(m => Harmony.GetPatchInfo(m).Owners.Contains(harmony.Id)).ToArray();
        if (methods.Length != 4) throw new Exception("Expected four patches; got " + methods.Length);
        foreach (var method in methods) Console.WriteLine("Patched " + method.DeclaringType.FullName + "." + method.Name);
        harmony.UnpatchSelf();
        Console.WriteLine("PASS: Unity lifecycle signatures, 2.0.0 plugin metadata and all Harmony patches on the actual game assemblies.");
    }
}
