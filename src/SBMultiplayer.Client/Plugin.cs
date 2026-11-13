using HarmonyLib;

namespace SBMultiplayer.Client;

internal static class Plugin
{
    public const string HarmonyId = "fr.aslirith.sbmultiplayer";

    public static Harmony Harmony { get; private set; } = null!;

    public static void Load()
    {
        ModLog.Info($"Runtime .NET {Environment.Version}, Harmony {typeof(Harmony).Assembly.GetName().Version}");

        Harmony = new Harmony(HarmonyId);
        Harmony.PatchAll(typeof(Plugin).Assembly);

        foreach (var method in Harmony.GetPatchedMethods())
        {
            ModLog.Info($"Patché : {method.DeclaringType?.FullName}.{method.Name}");
        }
    }
}
