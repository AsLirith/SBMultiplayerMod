using System.Reflection;
using BytingLib;
using SE;

namespace SBMultiplayer.Client;

/// <summary>
/// Startup check: the mod relies on internal members of the game (found by reflection or patched with Harmony).
/// A game update can rename or remove them; instead of running half-broken, the mod then stays inactive,
/// says so in the game's "modded" notice, and logs exactly what is missing.
/// Keep this list in sync with the reflection used in the code.
/// </summary>
internal static class Compatibility
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>False if something required is missing: the mod is inactive.</summary>
    public static bool IsCompatible { get; private set; } = true;

    private static Type? Find(string name) => typeof(GlobalGame).Assembly.GetType(name);

    public static List<string> Check()
    {
        var missing = new List<string>();

        void Type(string name)
        {
            if (Find(name) == null)
            {
                missing.Add($"type {name}");
            }
        }

        void Member(Type? type, string name, string? typeName = null)
        {
            if (type == null)
            {
                missing.Add($"{typeName ?? "?"}.{name} (type missing)");
            }
            else if (type.GetMember(name, All).Length == 0)
            {
                missing.Add($"{type.FullName}.{name}");
            }
        }

        void MemberOf(string typeName, string name) => Member(Find(typeName), name, typeName);

        // Types reached by name (internal classes).
        foreach (string t in new[] { "SE.Scene3D", "SE.Scene3DIngame", "SE.Scene3DDependencies", "SE.ClickToStartSceneCloser",
                     "SE.FlyCameraToggler", "SE.CustomMapCatalogue", "SE.CustomMapCatalogue+Entry", "SE.LevelSelectionScene" })
        {
            Type(t);
        }

        // GlobalGame: game loop hook, scenes, levels, UI helpers, Workshop, skins.
        foreach (string m in new[] { "GetTopmostScene", "LevelFinished", "currentScene", "currentLevel", "uiAssets", "optionsUI", "stuff",
                     "StartLevel", "SetCurrentChallenge", "GetMaxNonSpoileryMedal", "ShowPopup", "CreateCanvas", "CreateButton",
                     "IsNavigatingInTextInput", "customMaps", "DownloadWorkshopCode", "StartCustomMapEntry", "flyCameraToggler",
                     "globalContentLoader", "globalAssets", "SaveState", "levelOrder", "inputOutgame" })
        {
            Member(typeof(GlobalGame), m);
        }

        Member(typeof(GamePrototype), "updateSpeed");
        Member(typeof(Board), "GetGhostState");
        Member(typeof(Board), "playerContainer");
        Member(typeof(PlayerContainer), "level");
        Member(typeof(GlobalAssets), "defaultSkin");
        Member(typeof(UIAssets), "CreateMainPanel");
        Member(typeof(TimeDiffOnCheckpoints), "opponent");
        Member(typeof(TimeDiffOnCheckpoints), "ShowTimeDiff");
        Member(typeof(RunUploadBlockers), "GetReasons");
        Member(typeof(CheckpointManager), "IsInFirstCheckpoint");
        MemberOf("SE.Scene3D", "d");
        MemberOf("SE.Scene3D", "TransparencyBatch");
        MemberOf("SE.Scene3DIngame", "IsReplay");
        MemberOf("SE.Scene3DDependencies", "camera");
        MemberOf("SE.Scene3DDependencies", "projection");
        MemberOf("SE.ClickToStartSceneCloser", "Update");
        MemberOf("SE.ClickToStartSceneCloser", "Close");
        MemberOf("SE.FlyCameraToggler", "CanFly");
        // Hide and Seek: freezing a mouse with the game's free camera, grounded check, grind input, line of sight.
        MemberOf("SE.FlyCameraToggler", "SetFly");
        MemberOf("SE.FlyCameraToggler", "EndFlying");
        Member(typeof(Player), "State");
        Member(typeof(InputOutgameRaw), "GrindOrStartSkatingRaw");
        Member(typeof(Entities), "GetNearestTriangleRaycastSolid");
        Member(typeof(Goal), "GoalReached");
        foreach (string m in new[] { "RestartLevel", "RewindToCheckpoint", "RewindToggle", "RewindToStart", "TimeTravelForward" })
        {
            Member(typeof(CheckpointManager), m);
        }
        MemberOf("SE.CustomMapCatalogue", "ReadMap");
        MemberOf("SE.CustomMapCatalogue", "Maps");
        MemberOf("SE.CustomMapCatalogue+Entry", "WorkshopItemId");
        MemberOf("SE.CustomMapCatalogue+Entry", "Name");

        IsCompatible = missing.Count == 0;
        return missing;
    }

    /// <summary>Marks the mod inactive (e.g. a Harmony patch failed even though every member was found).</summary>
    public static void MarkIncompatible() => IsCompatible = false;

    /// <summary>Game version as stamped in its assembly, for the log.</summary>
    public static string GameVersion
    {
        get
        {
            Assembly game = typeof(GlobalGame).Assembly;
            return game.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? game.GetName().Version?.ToString() ?? "?";
        }
    }
}
