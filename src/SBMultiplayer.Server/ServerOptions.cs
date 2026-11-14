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

    /// <summary>Game mode: Free, TimeAttack, Rounds, Cup or Elimination (see the hosting guide).</summary>
    public GameMode Mode { get; set; } = GameMode.TimeAttack;

    /// <summary>Time Attack: round duration. Rounds: time limit of a round after the start.</summary>
    public double RoundSeconds { get; set; } = 300;

    /// <summary>Rounds mode: number of rounds played on each map before moving to the next one.</summary>
    public int RoundsPerMap { get; set; } = 3;

    /// <summary>Rounds mode: the round ends this many seconds after the first player finishes.</summary>
    public double FinishTimeoutSeconds { get; set; } = 15;

    /// <summary>Rounds mode: maximum wait for everyone to load the map and be ready on the start line.</summary>
    public double PrepareTimeoutSeconds { get; set; } = 20;

    /// <summary>Rounds mode: countdown before the start, once everyone is ready.</summary>
    public double CountdownSeconds { get; set; } = 3;

    public double ResultsSeconds { get; set; } = 15;

    /// <summary>Match points given by rank at the end of each round (1st, 2nd, 3rd…). Ranks beyond the table get 0.</summary>
    public List<int> PointsTable { get; set; } = new() { 10, 8, 6, 5, 4, 3, 2, 1 };

    /// <summary>Cup mode: points needed to become a finalist. A finalist who wins a round wins the cup.</summary>
    public int CupPointsLimit { get; set; } = 100;

    /// <summary>Elimination mode: players eliminated each round, the slowest ones (players who gave up count as last).</summary>
    public int EliminatedPerRound { get; set; } = 1;

    /// <summary>Folder where round results and match standings are written ("" = no export).</summary>
    public string ResultsDirectory { get; set; } = "results";

    /// <summary>Sends each player their own delayed board back, as a fake "Echo" player. For solo testing.</summary>
    public bool Echo { get; set; }

    public double EchoDelaySeconds { get; set; } = 2.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    public MatchSettings ToMatchSettings() => new()
    {
        Mode = Mode,
        Maps = new List<string>(Maps),
        RoundSeconds = RoundSeconds,
        ResultsSeconds = ResultsSeconds,
        RoundsPerMap = RoundsPerMap,
        FinishTimeoutSeconds = FinishTimeoutSeconds,
        PrepareTimeoutSeconds = PrepareTimeoutSeconds,
        CountdownSeconds = CountdownSeconds,
        PointsTable = new List<int>(PointsTable),
        CupPointsLimit = CupPointsLimit,
        EliminatedPerRound = EliminatedPerRound,
    };

    /// <summary>Applies settings received from an admin, clamped to sane ranges. Returns an error, or null.</summary>
    public string? Apply(MatchSettings s)
    {
        var maps = s.Maps.Select(m => m.Trim()).Where(m => m.Length > 0).ToList();
        string? invalid = maps.FirstOrDefault(m => !Msg.IsValidRotationKey(m));
        if (invalid != null)
        {
            return $"invalid map key \"{invalid}\"";
        }
        if (s.Mode != GameMode.Free && maps.Count == 0)
        {
            return "a map rotation is needed for this mode";
        }
        Mode = s.Mode;
        Maps = maps;
        RoundSeconds = Math.Clamp(s.RoundSeconds, 10, 3600);
        ResultsSeconds = Math.Clamp(s.ResultsSeconds, 3, 300);
        RoundsPerMap = Math.Clamp(s.RoundsPerMap, 1, 50);
        FinishTimeoutSeconds = Math.Clamp(s.FinishTimeoutSeconds, 1, 600);
        PrepareTimeoutSeconds = Math.Clamp(s.PrepareTimeoutSeconds, 3, 600);
        CountdownSeconds = Math.Clamp(s.CountdownSeconds, 1, 30);
        PointsTable = s.PointsTable.Where(p => p >= 0).Take(64).Select(p => Math.Min(p, 10_000)).ToList();
        CupPointsLimit = Math.Clamp(s.CupPointsLimit, 1, 100_000);
        EliminatedPerRound = Math.Clamp(s.EliminatedPerRound, 1, 16);
        return null;
    }

    /// <summary>Writes the current settings to a config file, keeping the comments of the template.</summary>
    public void Save(string path)
    {
        static string J(object v) => JsonSerializer.Serialize(v);
        static string N(double v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string text = Template
            .Replace("\"ServerName\": \"SBMultiplayer server\"", $"\"ServerName\": {J(ServerName)}")
            .Replace("\"Port\": 24800", $"\"Port\": {Port}")
            .Replace("\"MaxPlayers\": 32", $"\"MaxPlayers\": {MaxPlayers}")
            .Replace("\"Password\": \"\"", $"\"Password\": {J(Password)}")
            .Replace("\"AdminPassword\": \"\"", $"\"AdminPassword\": {J(AdminPassword)}")
            .Replace("\"Maps\": [ \"o:1\", \"o:2\", \"o:3\" ]", $"\"Maps\": [ {string.Join(", ", Maps.Select(J))} ]")
            .Replace("\"Mode\": \"TimeAttack\"", $"\"Mode\": \"{Mode}\"")
            .Replace("\"RoundSeconds\": 300", $"\"RoundSeconds\": {N(RoundSeconds)}")
            .Replace("\"ResultsSeconds\": 15", $"\"ResultsSeconds\": {N(ResultsSeconds)}")
            .Replace("\"RoundsPerMap\": 3", $"\"RoundsPerMap\": {RoundsPerMap}")
            .Replace("\"FinishTimeoutSeconds\": 15", $"\"FinishTimeoutSeconds\": {N(FinishTimeoutSeconds)}")
            .Replace("\"PrepareTimeoutSeconds\": 20", $"\"PrepareTimeoutSeconds\": {N(PrepareTimeoutSeconds)}")
            .Replace("\"CountdownSeconds\": 3", $"\"CountdownSeconds\": {N(CountdownSeconds)}")
            .Replace("\"CupPointsLimit\": 100", $"\"CupPointsLimit\": {CupPointsLimit}")
            .Replace("\"EliminatedPerRound\": 1", $"\"EliminatedPerRound\": {EliminatedPerRound}")
            .Replace("\"PointsTable\": [ 10, 8, 6, 5, 4, 3, 2, 1 ]", $"\"PointsTable\": [ {string.Join(", ", PointsTable)} ]")
            .Replace("\"ResultsDirectory\": \"results\"", $"\"ResultsDirectory\": {J(ResultsDirectory)}");
        File.WriteAllText(path, text);
    }

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

          // Map rotation: official levels as "o:<id>" (any version) or "o:<id>:<version>", and Workshop maps
          // as "c:M<workshop id>" (latest version). Easiest: set it in game (F8 > Admin > Mode and maps).
          // A player's map key is shown in the server console when they enter a level.
          // Empty list = free mode: no rounds, everyone rides wherever they want.
          "Maps": [ "o:1", "o:2", "o:3" ],

          // Game mode (can also be changed in game by an admin: F8 > Admin > Server settings):
          //  - "Free": no rounds, everyone rides wherever they want.
          //  - "TimeAttack": unlimited retries during the round, best time wins.
          //  - "Rounds": synchronized start, one attempt per round (respawning at checkpoints is allowed,
          //    going back to the start means giving up), points by race time.
          //  - "Cup": like Rounds; reaching CupPointsLimit makes you a finalist, and a finalist who wins a round wins the cup.
          //  - "Elimination": like Rounds, but the slowest players of each round are eliminated until one is left.
          "Mode": "TimeAttack",

          // Time Attack: round duration. Rounds: time limit after the start. In seconds.
          "RoundSeconds": 300,
          "ResultsSeconds": 15,

          // Rounds mode only: rounds per map, end of the round after the first finisher,
          // maximum wait for players to load the map, countdown before the start. In seconds.
          "RoundsPerMap": 3,
          "FinishTimeoutSeconds": 15,
          "PrepareTimeoutSeconds": 20,
          "CountdownSeconds": 3,

          // Match points by rank at the end of each round (1st, 2nd, 3rd...).
          "PointsTable": [ 10, 8, 6, 5, 4, 3, 2, 1 ],

          // Cup mode: points needed to become a finalist.
          "CupPointsLimit": 100,

          // Elimination mode: players eliminated each round, the slowest ones (players who gave up count as last).
          "EliminatedPerRound": 1,

          // Folder for round results (JSON) and match standings (CSV). Empty = no export.
          "ResultsDirectory": "results"
        }
        """;
}
