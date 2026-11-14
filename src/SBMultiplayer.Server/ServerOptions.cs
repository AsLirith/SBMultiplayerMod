using System.Text.Json;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <summary>
/// Server settings, loaded from a JSON file (comments allowed) with <c>--config</c>.
/// Command-line options override the file.
/// </summary>
public sealed class ServerOptions
{
    /// <summary>Name shown to players (reserved for a future server browser).</summary>
    public string ServerName { get; set; } = "SBMultiplayer server";

    public int Port { get; set; } = ProtocolInfo.DefaultPort;

    public int MaxPlayers { get; set; } = 32;

    /// <summary>Password required to join ("" = open server).</summary>
    public string Password { get; set; } = "";

    /// <summary>Password to log in as admin from the game's F8 menu ("" = no in-game admin).</summary>
    public string AdminPassword { get; set; } = "";

    /// <summary>Map rotation ("o:1", "o:57:2001"…). Empty = free mode, no rounds.</summary>
    public List<string> Maps { get; set; } = new();

    public double RoundSeconds { get; set; } = 300;

    public double ResultsSeconds { get; set; } = 15;

    /// <summary>Match points given by rank at the end of each round (1st, 2nd, 3rd…). Ranks beyond the table get 0.</summary>
    public List<int> PointsTable { get; set; } = new() { 10, 8, 6, 5, 4, 3, 2, 1 };

    /// <summary>Folder where round results and match standings are written ("" = no export).</summary>
    public string ResultsDirectory { get; set; } = "results";

    /// <summary>Sends each player their own delayed board back, as a fake "Echo" player. For solo testing.</summary>
    public bool Echo { get; set; }

    public double EchoDelaySeconds { get; set; } = 2.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ServerOptions Load(string path) =>
        JsonSerializer.Deserialize<ServerOptions>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Empty config file: {path}");

    /// <summary>Commented configuration template, written by <c>--init-config</c>.</summary>
    public const string Template = """
        // SBMultiplayer server configuration. Comments (//) are allowed.
        // Start the server with: dotnet SBMultiplayer.Server.dll --config server.json
        {
          "ServerName": "SBMultiplayer server",

          // UDP port (forward it on your router to accept players from the internet).
          "Port": 24800,
          "MaxPlayers": 32,

          // Password required to join. Empty = anyone can join.
          "Password": "",

          // Password to log in as admin from the game's F8 menu. Empty = no in-game admin.
          "AdminPassword": "",

          // Map rotation, as official level IDs: "o:<id>" (any version) or "o:<id>:<version>".
          // A player's map key is shown in the server console when they enter a level.
          // Empty list = free mode: no rounds, everyone rides wherever they want.
          "Maps": [ "o:1", "o:2", "o:3" ],

          // Round and results screen durations, in seconds.
          "RoundSeconds": 300,
          "ResultsSeconds": 15,

          // Match points by rank at the end of each round (1st, 2nd, 3rd...).
          "PointsTable": [ 10, 8, 6, 5, 4, 3, 2, 1 ],

          // Folder for round results (JSON) and match standings (CSV). Empty = no export.
          "ResultsDirectory": "results"
        }
        """;
}
