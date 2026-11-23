using System.Net.Http;
using System.Text.Json;
using HarmonyLib;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Official leaderboard uploads with the mod loaded (allowed by the game's developers).
/// The game refuses runs while Harmony is loaded ("mod: 0Harmony"); we lift that one reason only when:
/// <list type="bullet">
/// <item>the player hasn't turned uploads off (<see cref="ModConfig.SubmitToLeaderboards"/>);</item>
/// <item>the developers' remote switch allows this version (<see cref="SwitchUrl"/>);</item>
/// <item>every Harmony patch in the game is ours (another Harmony mod keeps runs blocked);</item>
/// <item>the run wasn't affected by the mod (Hide and Seek, free camera unlocked by the spectator mode).</item>
/// </list>
/// Every other reason of the game (Modding Mode, DevKeys, changed gameplay settings...) is left untouched.
/// </summary>
internal static class LeaderboardUploads
{
    /// <summary>
    /// Remote switch: {"allowRunUploads": true, "minimumVersion": "1.4.0", "message": ""}.
    /// Unreachable (offline...): the last answer received is used (saved in SBMultiplayer.json).
    /// </summary>
    public const string SwitchUrl = "https://raw.githubusercontent.com/AsLirith/SBMultiplayerMod/main/leaderboards.json";

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    /// <summary>The spectator mode unlocked the free camera in the current level scene (cleared on a fresh start).</summary>
    public static bool FreeCameraGranted { get; set; }

    public static void Start()
    {
        Task.Run(async () =>
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SBMultiplayer-mod");
            while (true)
            {
                await Refresh(http);
                await Task.Delay(RefreshInterval);
            }
        });
    }

    private static async Task Refresh(HttpClient http)
    {
        try
        {
            string text = await http.GetStringAsync(SwitchUrl);
            using JsonDocument json = JsonDocument.Parse(text);
            JsonElement root = json.RootElement;
            bool allowed = !root.TryGetProperty("allowRunUploads", out JsonElement a) || a.ValueKind != JsonValueKind.False;
            string message = root.TryGetProperty("message", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            if (allowed && root.TryGetProperty("minimumVersion", out JsonElement v) && v.ValueKind == JsonValueKind.String
                && Version.TryParse(v.GetString(), out Version? minimum) && CurrentVersion < minimum)
            {
                allowed = false;
            }

            ModConfig cfg = ModConfig.Current;
            if (cfg.LeaderboardUploadsDisabledRemotely == !allowed && cfg.LeaderboardRemoteMessage == message)
            {
                return;
            }
            cfg.LeaderboardUploadsDisabledRemotely = !allowed;
            cfg.LeaderboardRemoteMessage = message;
            ModConfig.Save();
            ModLog.Info(allowed
                ? "Leaderboard uploads allowed by the remote switch"
                : $"Leaderboard uploads disabled by the remote switch{(message.Length > 0 ? ": " + message : "")}");
        }
        catch (Exception e)
        {
            ModLog.Info($"Leaderboard switch not read ({e.GetType().Name}), keeping the last answer: "
                + (ModConfig.Current.LeaderboardUploadsDisabledRemotely ? "disabled" : "allowed"));
        }
    }

    private static Version CurrentVersion
    {
        get
        {
            Version v = typeof(LeaderboardUploads).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>
    /// Why the mod keeps the run from the leaderboards, or null to let the game upload it (its other checks still apply).
    /// Called by the game when it shows its "modded" notice (level start) and when a run finishes.
    /// </summary>
    public static string? BlockReason()
    {
        try
        {
            if (!Compatibility.IsCompatible)
            {
                return UI.Tr.IncompatibleNotice(UI.MultiplayerMenu.Version);
            }
            string mod = $"SBMultiplayer v{UI.MultiplayerMenu.Version}";
            ModConfig cfg = ModConfig.Current;
            if (!cfg.SubmitToLeaderboards)
            {
                return $"mod: {mod} ({UI.Tr.UploadsOffInSettings})";
            }
            if (cfg.LeaderboardUploadsDisabledRemotely)
            {
                string message = cfg.LeaderboardRemoteMessage;
                return $"mod: {mod} ({UI.Tr.UploadsOffRemotely}{(message.Length > 0 ? ": " + message : "")})";
            }
            if (OtherHarmonyOwner() is string other)
            {
                return $"mod: {UI.Tr.OtherHarmonyMod} ({other})";
            }
            if (HideSeek.IsActive)
            {
                return $"mod: {mod} (Hide and Seek)";
            }
            if (FreeCameraGranted || Spectator.IsWatching)
            {
                return $"mod: {mod} ({UI.Tr.UploadsSpectatorCamera})";
            }
            return null;
        }
        catch (Exception e)
        {
            ModLog.Error("Leaderboard upload check", e);
            return $"mod: SBMultiplayer v{UI.MultiplayerMenu.Version}"; // in doubt, keep the run off the leaderboards
        }
    }

    /// <summary>Owner of a Harmony patch that isn't ours, or null.</summary>
    private static string? OtherHarmonyOwner()
    {
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            HarmonyLib.Patches? info = Harmony.GetPatchInfo(method);
            if (info == null)
            {
                continue;
            }
            foreach (string owner in info.Owners)
            {
                if (owner != Plugin.HarmonyId)
                {
                    return owner;
                }
            }
        }
        return null;
    }
}
