using System.Text.Json;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Configuration read from SBMultiplayer.json (next to the DLL), created with default values if missing.
/// Environment variables take precedence (handy to run two instances): SBMULTI_SERVER, SBMULTI_NAME.
/// </summary>
internal sealed class ModConfig
{
    /// <summary>Server address, "host:port" or "host".</summary>
    public string Server { get; set; } = $"127.0.0.1:{ProtocolInfo.DefaultPort}";

    /// <summary>Displayed name. Empty = Steam name.</summary>
    public string PlayerName { get; set; } = "";

    /// <summary>When hosting, let Steam friends join through Steam P2P (no port to open).</summary>
    public bool SteamP2PHost { get; set; } = true;

    /// <summary>UDP port used when hosting a game from the F8 menu.</summary>
    public int HostPort { get; set; } = ProtocolInfo.DefaultPort;

    /// <summary>Server password (empty if the server is open).</summary>
    public string ServerPassword { get; set; } = "";

    /// <summary>Admin password, remembered to log in as admin again easily.</summary>
    public string AdminPassword { get; set; } = "";

    public bool AutoConnect { get; set; } = true;

    /// <summary>Max distance (in game units, 1 unit = 1 cm) to show a player's name. 0 = never.</summary>
    public float NameTagMaxDistance { get; set; } = 30f;

    /// <summary>Height of the name above the board, in game units.</summary>
    public float NameTagHeight { get; set; } = 0.8f;

    /// <summary>Name text size (1 = the game's font size at 1080p).</summary>
    public float NameTagScale { get; set; } = 0.4f;

    /// <summary>Round panel (time left, standings) at the top right.</summary>
    public bool ShowRoundPanel { get; set; } = true;

    /// <summary>Round panel size (1 = the original v0.4 size, found too big).</summary>
    public float RoundPanelScale { get; set; } = 0.6f;

    /// <summary>Shows the other players' ghosts.</summary>
    public bool ShowOtherPlayers { get; set; } = true;

    /// <summary>Opacity of the other players' ghosts (0 to 1).</summary>
    public float GhostOpacity { get; set; } = 0.5f;

    /// <summary>Shows names above nearby ghosts.</summary>
    public bool ShowNameTags { get; set; } = true;

    /// <summary>Checkpoint splits (the game's display), compared with our best run of the round.</summary>
    public bool ShowSplits { get; set; } = true;

    /// <summary>Language of the mod's interface: "auto" (the game's), "en" or "fr".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>Multiplayer menu key (a MonoGame key name: F8, F10, Insert…).</summary>
    public string MenuKey { get; set; } = "F8";

    /// <summary>Key to join the current round's map.</summary>
    public string RejoinKey { get; set; } = "F7";

    /// <summary>Key to write in the chat.</summary>
    public string ChatKey { get; set; } = "Tab";

    /// <summary>Shows the chat (messages and events) at the bottom left.</summary>
    public bool ShowChat { get; set; } = true;

    /// <summary>
    /// Notifications also at the top center of the screen. By default they only go to the chat
    /// (they come back to the top if the chat is hidden).
    /// </summary>
    public bool NotificationsOnTop { get; set; }

    /// <summary>Chat text size (1 = original size).</summary>
    public float ChatScale { get; set; } = 1f;

    /// <summary>Debug: logs the position every second.</summary>
    public bool DebugProbe { get; set; }

    /// <summary>Debug: local ghost following the player 1.5 s behind (step 2).</summary>
    public bool DebugDelayedSelfGhost { get; set; }

    public static ModConfig Current { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string path = "";

    // File values, when an environment variable overrides them: not overwritten when saving.
    private static (string Server, string Name)? fileValuesBeforeEnv;

    /// <summary>Saves the configuration (called when a setting changes in the menu).</summary>
    public static void Save()
    {
        try
        {
            ModConfig toWrite = Current;
            if (fileValuesBeforeEnv is { } fileValues)
            {
                toWrite = (ModConfig)Current.MemberwiseClone();
                toWrite.Server = fileValues.Server;
                toWrite.PlayerName = fileValues.Name;
            }
            File.WriteAllText(path, JsonSerializer.Serialize(toWrite, JsonOptions));
        }
        catch (Exception e)
        {
            ModLog.Error($"Could not save the configuration ({path})", e);
        }
    }

    /// <summary>The address or name were changed in the menu: they replace the environment override.</summary>
    public static void ForgetEnvironmentOverride() => fileValuesBeforeEnv = null;

    public static void Load(string modDir)
    {
        path = Path.Combine(modDir, "SBMultiplayer.json");
        try
        {
            if (File.Exists(path))
            {
                Current = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path), JsonOptions) ?? new ModConfig();
            }
            else
            {
                File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));
                ModLog.Info($"Default configuration created: {path}");
            }
        }
        catch (Exception e)
        {
            ModLog.Error($"Unreadable configuration ({path}), using default values", e);
            Current = new ModConfig();
        }

        string envServer = Environment.GetEnvironmentVariable("SBMULTI_SERVER") ?? "";
        string envName = Environment.GetEnvironmentVariable("SBMULTI_NAME") ?? "";
        if (envServer.Length > 0 || envName.Length > 0)
        {
            fileValuesBeforeEnv = (Current.Server, Current.PlayerName);
        }
        if (Environment.GetEnvironmentVariable("SBMULTI_SERVER") is { Length: > 0 } server)
        {
            Current.Server = server;
        }
        if (Environment.GetEnvironmentVariable("SBMULTI_NAME") is { Length: > 0 } name)
        {
            Current.PlayerName = name;
        }
    }

    public (string Host, int Port) ParseServer()
    {
        string s = Server.Trim();
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out int port))
        {
            return (s[..colon], port);
        }
        return (s, ProtocolInfo.DefaultPort);
    }
}
