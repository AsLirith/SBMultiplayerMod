using HarmonyLib;

namespace SBMultiplayer.Client;

internal static class Plugin
{
    public const string HarmonyId = "fr.aslirith.sbmultiplayer";

    public static Harmony Harmony { get; private set; } = null!;

    public static void Load()
    {
        ModLog.Info($"Runtime .NET {Environment.Version}, Harmony {typeof(Harmony).Assembly.GetName().Version}");
        Net.ModConfig.Load(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!);
        ModLog.Info($"Serveur configuré : {Net.ModConfig.Current.Server}");

        Harmony = new Harmony(HarmonyId);
        Harmony.PatchAll(typeof(Plugin).Assembly);

        foreach (var method in Harmony.GetPatchedMethods())
        {
            ModLog.Info($"Patché : {method.DeclaringType?.FullName}.{method.Name}");
        }
    }
}
