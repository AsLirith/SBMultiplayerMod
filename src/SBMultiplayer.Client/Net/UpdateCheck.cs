using System.Net.Http;
using System.Text.Json;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Checks once, at startup and in the background, whether a newer release of the mod is on GitHub.
/// Only the public "latest release" page of the repository is read; nothing about the player is sent.
/// Can be turned off with "CheckForUpdates": false in SBMultiplayer.json.
/// </summary>
internal static class UpdateCheck
{
    public const string ReleasesPage = "https://github.com/AsLirith/SBMultiplayerMod/releases/latest";
    private const string LatestReleaseApi = "https://api.github.com/repos/AsLirith/SBMultiplayerMod/releases/latest";

    /// <summary>Newer version available ("0.17.0"), or null (up to date, offline, or not checked yet).</summary>
    public static volatile string? NewerVersion;

    public static void Start()
    {
        if (!ModConfig.Current.CheckForUpdates)
        {
            return;
        }
        Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("SBMultiplayer-mod"); // required by the GitHub API
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                using JsonDocument json = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi));
                string tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
                Version? latest = Parse(tag);
                Version? current = typeof(UpdateCheck).Assembly.GetName().Version;
                if (latest != null && current != null && latest > new Version(current.Major, current.Minor, Math.Max(0, current.Build)))
                {
                    NewerVersion = latest.ToString(3);
                    ModLog.Info($"A newer version of the mod is available: {NewerVersion} ({ReleasesPage})");
                }
                else
                {
                    ModLog.Info($"Mod up to date (latest release: {tag})");
                }
            }
            catch (Exception e)
            {
                ModLog.Info($"Update check skipped ({e.GetType().Name})"); // offline, GitHub unreachable...
            }
        });
    }

    private static Version? Parse(string tag)
    {
        string s = tag.TrimStart('v', 'V');
        return Version.TryParse(s, out Version? v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }
}
