using HarmonyLib;

namespace SBMultiplayer.Client;

internal static class Plugin
{
    public const string HarmonyId = "fr.aslirith.sbmultiplayer";

    public static Harmony Harmony { get; private set; } = null!;

    public static void Load()
    {
        ModLog.Info($"SBMultiplayer v{UI.MultiplayerMenu.Version} - game {Compatibility.GameVersion} - .NET {Environment.Version}, Harmony {typeof(Harmony).Assembly.GetName().Version}");

        // Prototype: replay verification run (no multiplayer at all).
        if (Verify.ReplayVerifier.ReportPath != null)
        {
            Harmony = new Harmony(HarmonyId);
            try
            {
                Verify.ReplayVerifier.Install(Harmony);
            }
            catch (Exception e)
            {
                ModLog.Error("Replay verification: could not install", e);
            }
            return;
        }

        Net.ModConfig.Load(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!);
        ModLog.Info($"Configured server: {Net.ModConfig.Current.Server}");

        Harmony = new Harmony(HarmonyId);

        // A game update may have removed what the mod relies on: check first, and stay inactive if needed.
        List<string> missing = Compatibility.Check();
        if (missing.Count > 0)
        {
            ModLog.Error("Mod inactive: incompatible game version. Missing:\n  " + string.Join("\n  ", missing));
            ShowIncompatibleNotice();
            return;
        }

        try
        {
            Harmony.PatchAll(typeof(Plugin).Assembly);
        }
        catch (Exception e)
        {
            ModLog.Error("Mod inactive: a patch could not be applied (incompatible game version?)", e);
            Harmony.UnpatchAll(HarmonyId);
            Compatibility.MarkIncompatible();
            ShowIncompatibleNotice();
            return;
        }

        foreach (var method in Harmony.GetPatchedMethods())
        {
            ModLog.Info($"Patched: {method.DeclaringType?.FullName}.{method.Name}");
        }
        Net.UpdateCheck.Start();
        Net.WebJoin.Start();
    }

    /// <summary>Only the "modded" notice patch, to tell the player that the mod needs an update.</summary>
    private static void ShowIncompatibleNotice()
    {
        try
        {
            Harmony.CreateClassProcessor(typeof(Patches.ModdedNoticePatch)).Patch();
        }
        catch (Exception e)
        {
            ModLog.Error("Could not show the incompatibility notice", e);
        }
    }
}
