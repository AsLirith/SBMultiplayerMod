using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SBMultiplayer.Client.UI;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Tournaments on the community website: the list (newest first), each tournament's final standings when it's selected,
/// and the host calls (create, edit, cancel, open, resume). Requests run off the game thread. The screens only read what
/// has arrived, or await the host calls.
/// The API sits under <see cref="ModConfig.TournamentsApiBase"/> (default: the website's /multiplayer/api):
/// GET /tournaments and GET /tournaments/{id}/standings. The host routes all send the header x-steam-ticket
/// (<see cref="SteamTicket"/>): GET /tournaments/host, GET /tournaments/mine, POST /tournaments (create),
/// POST /tournaments/{id} (edit), POST /tournaments/{id}/cancel, /open, /resume and /close. Errors come as { error: "text" }.
/// </summary>
internal static class Tournaments
{
    /// <summary>
    /// A tournament. Status is upcoming, open, live, paused, finished or incomplete. Code is its room code, only while it's
    /// open or live. AdminPassword is its room's admin password. It only comes in our own list (<see cref="MineAsync"/>),
    /// and only while the tournament is open or live.
    /// </summary>
    public sealed record Tournament(string Id, string Name, string Date, string Host, int Rounds, int Players,
        string Status, DateTimeOffset? StartsAt, GameMode? Mode, IReadOnlyList<string> Maps,
        int RoundsPerMap, int MinutesPerMap, int CupPoints, int EliminatedPerRound, string? Code, string? AdminPassword = null)
    {
        /// <summary>Never the admin password (a record would print every member if it were ever logged).</summary>
        public override string ToString() => $"Tournament {Id} ({Status})";
    }

    /// <summary>What a host fills in to create or edit a tournament. Maps are keys like "o:1" or "c:M123".</summary>
    public sealed record Draft(string Name, DateTimeOffset StartsAt, GameMode Mode, List<string> Maps,
        int RoundsPerMap, int MinutesPerMap, int CupPoints, int EliminatedPerRound);

    /// <summary>The answer to a host call: Error is the text to show when it failed.</summary>
    public sealed record HostResult(bool Ok, string? Error, string? Id = null, string? Code = null, string? AdminPassword = null);

    /// <summary>A final standing. Flagged: how many of the player's finishes failed the time checks.</summary>
    public sealed record Standing(int Place, string Name, int Points, int Wins, int Flagged);

    /// <summary>A row of the tournament leaderboard: placing points over the finished tournaments, and tournaments won.</summary>
    public sealed record LeaderRow(int Place, string Name, int Points, int Wins, int Tournaments);

    public enum State { Loading, Ready, Unavailable }

    /// <summary>The website refuses bodies bigger than this.</summary>
    private const int MaxBodyBytes = 4096;

    private static volatile IReadOnlyList<Tournament> list = Array.Empty<Tournament>();
    private static volatile State listState = State.Loading;
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Standing>?> standings = new(); // null: loading
    /// <summary>Standings to ask for again the next time they're shown (kept on screen until the new ones arrive).</summary>
    private static readonly ConcurrentDictionary<string, byte> staleStandings = new();
    /// <summary>The tournament leaderboard of each mode ("" for all modes), and when it arrived. Rows null: unavailable.</summary>
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<LeaderRow>? Rows, double At)> leaderboards = new();
    private static readonly ConcurrentDictionary<string, byte> fetchingLeaderboards = new();
    /// <summary>A leaderboard this old is asked for again when shown (the one known stays on screen meanwhile).</summary>
    private const double LeaderboardSeconds = 60;
    private static int loading;
    private static int checkingHost;
    private static int approvedHost = -1; // -1 unknown, 0 no, 1 yes

    public static IReadOnlyList<Tournament> List => list;

    public static State ListState => listState;

    /// <summary>Whether we may host tournaments (our Steam ID is approved on the website): null until known. Refreshed with the list.</summary>
    public static bool? IsApprovedHost => Volatile.Read(ref approvedHost) switch { 1 => true, 0 => false, _ => null };

    /// <summary>Asks for the list again (opening the screen), then whether we're an approved host.</summary>
    public static void Refresh()
    {
        if (Interlocked.Exchange(ref loading, 1) == 1)
        {
            return;
        }
        if (list.Count == 0)
        {
            listState = State.Loading;
        }
        Task.Run(async () =>
        {
            try
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(await Get("/tournaments"));
                    IReadOnlyList<Tournament> fresh = ParseList(doc.RootElement);
                    MarkStaleStandings(list, fresh);
                    list = fresh;
                    listState = State.Ready;
                }
                catch (Exception e)
                {
                    ModLog.Warn($"Tournament list unavailable: {e.Message}");
                    listState = list.Count > 0 ? State.Ready : State.Unavailable;
                }
            }
            finally
            {
                Interlocked.Exchange(ref loading, 0);
            }
            CheckHost(); // after the list, but outside its guard: a slow host check never drops a list refresh
        });
    }

    /// <summary>Asks the website whether we're an approved host (<see cref="IsApprovedHost"/>), unless already asking.</summary>
    private static void CheckHost()
    {
        if (Interlocked.Exchange(ref checkingHost, 1) == 1)
        {
            return;
        }
        Task.Run(async () =>
        {
            try
            {
                await RefreshHost();
            }
            catch (Exception e)
            {
                ModLog.Warn($"Tournament host status unavailable: {e.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref checkingHost, 0);
            }
        });
    }

    /// <summary>
    /// Runs after a list refresh. Standings that may have changed are marked to be asked for again: a live tournament's, and
    /// any whose status changed (e.g. live to finished). Those of tournaments no longer listed are dropped. Finished
    /// standings are final and kept.
    /// </summary>
    private static void MarkStaleStandings(IReadOnlyList<Tournament> before, IReadOnlyList<Tournament> after)
    {
        var oldStatus = new Dictionary<string, string>();
        foreach (Tournament t in before)
        {
            oldStatus[t.Id] = t.Status;
        }
        var listed = new HashSet<string>();
        foreach (Tournament t in after)
        {
            listed.Add(t.Id);
            if (t.Status == "live" || !oldStatus.TryGetValue(t.Id, out string? was) || was != t.Status)
            {
                staleStandings[t.Id] = 0;
            }
        }
        foreach (string id in standings.Keys.Where(id => !listed.Contains(id)).ToList())
        {
            standings.TryRemove(id, out _);
            staleStandings.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// The standings of a tournament. Null while loading (the first call starts the request), empty if unavailable.
    /// Standings marked stale by a list refresh are asked for again. The old ones are returned until the new ones arrive.
    /// </summary>
    public static IReadOnlyList<Standing>? Standings(string id)
    {
        if (standings.TryGetValue(id, out IReadOnlyList<Standing>? known))
        {
            if (known != null && staleStandings.TryRemove(id, out _))
            {
                FetchStandings(id, keepOnFailure: true);
            }
            return known;
        }
        staleStandings.TryRemove(id, out _);
        standings[id] = null;
        FetchStandings(id, keepOnFailure: false);
        return null;
    }

    /// <param name="keepOnFailure">A refetch: on failure, the standings already shown stay.</param>
    private static void FetchStandings(string id, bool keepOnFailure) => Task.Run(async () =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await Get($"/tournaments/{Uri.EscapeDataString(id)}/standings"));
            standings[id] = doc.RootElement.EnumerateArray()
                .Select((s, i) => new Standing(Int(s, "place") is int p && p > 0 ? p : i + 1, Clean(Str(s, "name")),
                    Int(s, "points"), Int(s, "wins"), Int(s, "flagged")))
                .ToList();
        }
        catch (Exception e)
        {
            ModLog.Warn($"Standings of tournament {id} unavailable: {e.Message}");
            if (!keepOnFailure)
            {
                standings[id] = Array.Empty<Standing>();
            }
        }
    });

    /// <summary>
    /// The tournament leaderboard of <paramref name="mode"/> (null: every mode). Null while it's first loading, empty when
    /// it's unavailable or nothing has finished yet (<see cref="LeaderboardUnavailable"/> tells which). Asked for again when old.
    /// </summary>
    public static IReadOnlyList<LeaderRow>? Leaderboard(GameMode? mode)
    {
        string key = mode?.ToString() ?? "";
        bool known = leaderboards.TryGetValue(key, out var entry);
        if ((!known || NetClock.Now - entry.At > LeaderboardSeconds) && fetchingLeaderboards.TryAdd(key, 0))
        {
            FetchLeaderboard(key);
        }
        return known ? entry.Rows ?? Array.Empty<LeaderRow>() : null;
    }

    /// <summary>Whether the last answer for this mode's leaderboard was a failure.</summary>
    public static bool LeaderboardUnavailable(GameMode? mode) =>
        leaderboards.TryGetValue(mode?.ToString() ?? "", out var entry) && entry.Rows == null;

    private static void FetchLeaderboard(string key) => Task.Run(async () =>
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(await Get(key == "" ? "/tournaments/leaderboard" : $"/tournaments/leaderboard?mode={Uri.EscapeDataString(key)}"));
            IReadOnlyList<LeaderRow> rows = doc.RootElement.EnumerateArray()
                .Select((r, i) => new LeaderRow(Int(r, "place") is int p && p > 0 ? p : i + 1, Clean(Str(r, "name")),
                    Int(r, "points"), Int(r, "wins"), Int(r, "tournaments")))
                .ToList();
            leaderboards[key] = (rows, NetClock.Now);
        }
        catch (Exception e)
        {
            ModLog.Warn($"Tournament leaderboard ({(key == "" ? "all modes" : key)}) unavailable: {e.Message}");
            // The rows already shown stay; a first failure shows as unavailable.
            leaderboards.AddOrUpdate(key, (null, NetClock.Now), (_, old) => (old.Rows, NetClock.Now));
        }
        finally
        {
            fetchingLeaderboards.TryRemove(key, out _);
        }
    });

    /// <summary>The tournaments we host (any status), with their rooms' admin passwords. Null if unavailable.</summary>
    public static Task<IReadOnlyList<Tournament>?> MineAsync() => Task.Run<IReadOnlyList<Tournament>?>(async () =>
    {
        try
        {
            (int status, string body) = await HostSend(HttpMethod.Get, "/tournaments/mine", null);
            if (status == 403)
            {
                Volatile.Write(ref approvedHost, 0);
            }
            if (status is >= 200 and < 300)
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                return ParseList(doc.RootElement, withAdminPassword: true);
            }
            ModLog.Warn(status == 0 ? "Own tournaments unavailable: no Steam ticket" : $"Own tournaments unavailable: HTTP {status}");
        }
        catch (Exception e)
        {
            ModLog.Warn($"Own tournaments unavailable: {e.Message}");
        }
        return null;
    });

    public static Task<HostResult> CreateAsync(Draft d) =>
        HostCall("/tournaments", DraftJson(d), root => IdText(root) is { Length: > 0 } id
            ? new HostResult(true, null, Id: id)
            : new HostResult(false, Tr.TournamentHostError));

    public static Task<HostResult> UpdateAsync(string id, Draft d) =>
        HostCall($"/tournaments/{Uri.EscapeDataString(id)}", DraftJson(d), _ => new HostResult(true, null, Id: id));

    public static Task<HostResult> CancelAsync(string id) =>
        HostCall($"/tournaments/{Uri.EscapeDataString(id)}/cancel", null, _ => new HostResult(true, null, Id: id));

    /// <summary>Opens the waiting room: on success, Code is the room code and AdminPassword the room's admin password.</summary>
    public static Task<HostResult> OpenAsync(string id) =>
        HostCall($"/tournaments/{Uri.EscapeDataString(id)}/open", null, root => RoomResult(id, root));

    /// <summary>Starts a paused tournament's room again (same answer as <see cref="OpenAsync"/>).</summary>
    public static Task<HostResult> ResumeAsync(string id) =>
        HostCall($"/tournaments/{Uri.EscapeDataString(id)}/resume", null, root => RoomResult(id, root));

    /// <summary>Closes an open tournament's waiting room (back to upcoming, or to paused after a resume).</summary>
    public static Task<HostResult> CloseRoomAsync(string id) =>
        HostCall($"/tournaments/{Uri.EscapeDataString(id)}/close", null, _ => new HostResult(true, null, Id: id));

    private static HostResult RoomResult(string id, JsonElement root)
    {
        string code = root.ValueKind == JsonValueKind.Object ? Str(root, "code") : "";
        return code == ""
            ? new HostResult(false, Tr.TournamentHostError)
            : new HostResult(true, null, Id: id, Code: code, AdminPassword: Str(root, "adminPassword"));
    }

    private static async Task RefreshHost()
    {
        (int status, string body) = await HostSend(HttpMethod.Get, "/tournaments/host", null);
        int before = Volatile.Read(ref approvedHost);
        if (status != 200 || before != 1)
        {
            // Logged until we're known to be a host, then only on trouble. The check repeats with every list refresh.
            ModLog.Info(status == 0 ? "Tournament host check: no Steam ticket" : $"Tournament host check: HTTP {status} {(body.Length > 200 ? body[..200] : body).ReplaceLineEndings(" ")}");
        }
        if (status == 403)
        {
            Volatile.Write(ref approvedHost, 0);
        }
        else if (status is >= 200 and < 300)
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            bool approved = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("approved", out JsonElement v) && v.ValueKind == JsonValueKind.True;
            Volatile.Write(ref approvedHost, approved ? 1 : 0);
        }
        // Otherwise (no Steam, website or Steam unreachable): keep what we knew.
    }

    /// <summary>A POST host route; success refreshes the list.</summary>
    private static Task<HostResult> HostCall(string path, string? json, Func<JsonElement, HostResult> onSuccess) => Task.Run(async () =>
    {
        try
        {
            if (json != null && Encoding.UTF8.GetByteCount(json) > MaxBodyBytes)
            {
                return new HostResult(false, Tr.TournamentTooBig);
            }
            (int status, string body) = await HostSend(HttpMethod.Post, path, json);
            if (status == 0)
            {
                return new HostResult(false, Tr.TournamentNeedsSteam);
            }
            if (status == 403)
            {
                Volatile.Write(ref approvedHost, 0);
            }
            if (status is >= 200 and < 300)
            {
                // Update and cancel need nothing from the body (cancel answers 204): an empty or non-JSON one is still a success.
                using JsonDocument doc = ParseOrEmpty(body);
                HostResult result = onSuccess(doc.RootElement);
                if (result.Ok)
                {
                    Refresh();
                }
                return result;
            }
            ModLog.Warn($"Tournament host call {path} refused: HTTP {status}");
            return new HostResult(false, ErrorText(body));
        }
        catch (Exception e)
        {
            ModLog.Warn($"Tournament host call {path} failed: {e.Message}");
            return new HostResult(false, Tr.TournamentHostError);
        }
    });

    /// <summary>
    /// Sends a host request with our Steam ticket. A refused ticket (401) is replaced and the request is sent again, once.
    /// Status 0 means there was no Steam ticket and nothing was sent.
    /// </summary>
    private static async Task<(int Status, string Body)> HostSend(HttpMethod method, string path, string? json)
    {
        for (int attempt = 0; ; attempt++)
        {
            string? ticket = await SteamTicket.GetHexAsync();
            if (ticket == null)
            {
                return (0, "");
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var request = new HttpRequestMessage(method, ModConfig.Current.TournamentsApiBase() + path);
            request.Headers.Add("x-steam-ticket", ticket);
            if (json != null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }
            using HttpResponseMessage response = await http.SendAsync(request);
            int status = (int)response.StatusCode;
            string body = await response.Content.ReadAsStringAsync();
            if (status == 401 && attempt == 0)
            {
                SteamTicket.Invalidate(); // expired or refused: ask Steam for a new one
                continue;
            }
            return (status, body);
        }
    }

    private static string DraftJson(Draft d) => JsonSerializer.Serialize(new
    {
        name = d.Name,
        startsAt = d.StartsAt.ToUnixTimeMilliseconds(),
        mode = d.Mode.ToString(),
        maps = d.Maps,
        roundsPerMap = d.RoundsPerMap,
        minutesPerMap = d.MinutesPerMap,
        cupPoints = d.CupPoints,
        eliminatedPerRound = d.EliminatedPerRound,
    });

    /// <summary>The body as JSON, or an empty object when it's empty or not JSON.</summary>
    private static JsonDocument ParseOrEmpty(string body)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
            }
        }
        return JsonDocument.Parse("{}");
    }

    /// <summary>The website's { error } text (without "#"), or the generic message.</summary>
    private static string ErrorText(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && Clean(Str(doc.RootElement, "error")).Trim() is { Length: > 0 } text)
            {
                return text;
            }
        }
        catch (JsonException)
        {
        }
        return Tr.TournamentHostError;
    }

    /// <param name="withAdminPassword">Our own list: it carries the admin passwords (never read from the public one).</param>
    private static IReadOnlyList<Tournament> ParseList(JsonElement root, bool withAdminPassword = false) => root.EnumerateArray()
        .Where(t => t.ValueKind == JsonValueKind.Object)
        .Select(t => new Tournament(IdText(t), Clean(Str(t, "name")), Str(t, "date"), Clean(Str(t, "host")), Int(t, "rounds"), Int(t, "players"),
            Str(t, "status"), StartsAt(t), Mode(t), Maps(t),
            Int(t, "roundsPerMap"), Int(t, "minutesPerMap"), Int(t, "cupPoints"), Int(t, "eliminatedPerRound"),
            Str(t, "code") is { Length: > 0 } code ? code : null,
            withAdminPassword && Str(t, "adminPassword") is { Length: > 0 } password ? password : null))
        .Where(t => t.Id != "")
        .ToList();

    private static DateTimeOffset? StartsAt(JsonElement t)
    {
        if (!t.TryGetProperty("startsAt", out JsonElement v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out long ms))
        {
            return null;
        }
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static GameMode? Mode(JsonElement t) =>
        Str(t, "mode") is { Length: > 0 } name && !char.IsDigit(name[0])
            && Enum.TryParse(name, ignoreCase: true, out GameMode mode) && Enum.IsDefined(mode) ? mode : null;

    private static IReadOnlyList<string> Maps(JsonElement t) =>
        t.TryGetProperty("maps", out JsonElement v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.String).Select(m => m.GetString() ?? "").Where(m => m != "").ToList()
            : Array.Empty<string>();

    /// <summary>An "id" given as a string or a number.</summary>
    private static string IdText(JsonElement item) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out JsonElement v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";

    private static async Task<string> Get(string path)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        return await http.GetStringAsync(ModConfig.Current.TournamentsApiBase() + path);
    }

    /// <summary>
    /// Text from the website as the game's font can draw it. No '#' (the font's markup), and no invisible or unknown
    /// characters. A Steam name can start with one, and it would be drawn as "??". The room server drops the same ones
    /// from names.
    /// </summary>
    private static string Clean(string text) => Msg.SanitizeChat(text).Replace("#", "");

    private static string Str(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement item, string name) =>
        item.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;
}
