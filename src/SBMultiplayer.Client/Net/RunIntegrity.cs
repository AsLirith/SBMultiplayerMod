using System.Collections;
using System.Reflection;
using BytingLib;
using HarmonyLib;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Protocol;
using SE;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// What the server needs to check a finish time (servers with <see cref="ProtocolInfo.Features.RunProof"/>):
/// the game settings that the official leaderboards refuse, and the replay the game recorded for the run.
/// Optional: if the game changed, the checks are skipped and the time is sent as before (the server decides).
/// </summary>
internal static class RunIntegrity
{
    // ---------- Non-standard settings ----------

    private static AccessTools.FieldRef<GlobalGame, SettingsManager<Settings>>? settingsManagerRef;
    private static PropertyInfo? saveStateProperty;
    private static bool settingsUnavailable;

    /// <summary>
    /// Settings that disqualify a run from the official leaderboards (the game's own list), except the mod itself.
    /// Read from the game every time, even when its "submit with cheats" or "dev_" exceptions would skip them.
    /// </summary>
    public static List<string> NonStandardSettings()
    {
        var list = new List<string>();
        if (GameAccess.Game is not { } game || settingsUnavailable)
        {
            return list;
        }
        try
        {
            settingsManagerRef ??= AccessTools.FieldRefAccess<GlobalGame, SettingsManager<Settings>>("settingsManager");
            saveStateProperty ??= AccessTools.Property(typeof(GlobalGame), "SaveState");
            Settings settings = settingsManagerRef(game).Settings;
            var saveState = (SaveState)saveStateProperty.GetValue(game)!;

            if (saveState.Gameplay.ModdingMode.Value)
            {
                list.Add("ModdingMode");
            }
            if (settings.DevKeys)
            {
                list.Add("DevKeys");
            }
            if (settings.PhysicsVersion.HasValue)
            {
                list.Add($"PhysicsVersion: {settings.PhysicsVersion}");
            }
            if (settings.Player.HasTutorialAbilities)
            {
                list.Add("HasTutorialAbilities");
            }
            // The gameplay settings the game checks (kept in sync with the game: read from its own table).
            var table = (IEnumerable)AccessTools.Field(typeof(RunUploadBlockers), "gameplaySettings").GetValue(null)!;
            var defaults = (Settings._Player)AccessTools.Field(typeof(RunUploadBlockers), "defaultPlayerSettings").GetValue(null)!;
            foreach (object entry in table)
            {
                var (name, get) = ((string, Func<Settings._Player, object>))entry;
                object value = get(settings.Player);
                if (!value.Equals(get(defaults)))
                {
                    list.Add($"{name}: {value}");
                }
            }
        }
        catch (Exception e)
        {
            settingsUnavailable = true;
            ModLog.Warn($"Game settings check unavailable: {e.Message}");
        }
        return list;
    }

    // ---------- Replay of the run ----------

    /// <summary>Last replay the game finished writing at a finish line: file and time.</summary>
    public static (string Path, uint Ms, double At)? LastFinishedReplay { get; set; }

    private static int nextRunId = 1;

    public static int NewRunId() => nextRunId++;

    /// <summary>
    /// The replay of the run that just finished in <paramref name="ms"/> (the game wrote it during this tick), or null.
    /// </summary>
    public static byte[]? TakeReplay(uint ms)
    {
        if (LastFinishedReplay is not { } r)
        {
            return null;
        }
        LastFinishedReplay = null;
        if (NetClock.Now - r.At > 2 || Math.Abs((long)r.Ms - ms) > 20)
        {
            ModLog.Warn($"Replay of the run not found (last one: {Msg.FormatTime(r.Ms)}, run: {Msg.FormatTime(ms)})");
            return null;
        }
        try
        {
            var info = new FileInfo(r.Path);
            if (!info.Exists || info.Length > Msg.MaxReplayBytes)
            {
                ModLog.Warn($"Replay of the run unusable: {r.Path}");
                return null;
            }
            return File.ReadAllBytes(r.Path);
        }
        catch (Exception e)
        {
            ModLog.Warn($"Could not read the replay of the run: {e.Message}");
            return null;
        }
    }
}

/// <summary>Notes which replay file the game finished at a finish line (it writes one per attempt).</summary>
[HarmonyPatch]
internal static class ReplayRecorderPatch
{
    private static readonly Type? RecorderType = AccessTools.TypeByName("SE.ReplayRecorder`1") is { } open
        && AccessTools.TypeByName("SE.ReplayFrame") is { } frame
            ? open.MakeGenericType(frame)
            : null;

    private static readonly FieldInfo? PathField = RecorderType == null ? null : AccessTools.Field(RecorderType, "ReplayFilePath");
    private static readonly FieldInfo? DisposeField = RecorderType == null ? null : AccessTools.Field(RecorderType, "disposeRecorder");

    private static bool Prepare() => RecorderType != null && PathField != null && DisposeField != null && TargetMethod() != null;

    private static MethodBase? TargetMethod() =>
        RecorderType == null ? null : AccessTools.Method(RecorderType, "StopRecording", new[] { typeof(LevelTimeData), typeof(bool) });

    /// <summary>Only the call that actually writes the file counts (later calls do nothing).</summary>
    private static void Prefix(object __instance, out bool __state) => __state = DisposeField!.GetValue(__instance) != null;

    private static void Postfix(object __instance, LevelTimeData levelTimeData, bool goalReached, bool __state)
    {
        try
        {
            if (__state && goalReached && PathField!.GetValue(__instance) is string path)
            {
                RunIntegrity.LastFinishedReplay = (path, levelTimeData.MS, NetClock.Now);
            }
        }
        catch (Exception e)
        {
            ModLog.Error("ReplayRecorderPatch", e);
        }
    }
}
