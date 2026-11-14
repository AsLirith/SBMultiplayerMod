using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LiteNetLib;
using LiteNetLib.Utils;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <summary>
/// Relay server: it does not simulate physics. It keeps the player list and their map, relays
/// board states, and runs the rounds (imposed map, timer, leaderboard, match points, admin commands).
/// Everything runs on a single thread (<see cref="Poll"/>).
/// </summary>
public sealed class GameServer : INetEventListener
{
    private const int EchoIdOffset = 1_000_000;

    /// <summary>Accepted margin between a run's duration and the time elapsed since the round started (latency, loading).</summary>
    private const double FinishToleranceSeconds = 3;

    private sealed class Player
    {
        public required NetPeer Peer { get; init; }
        public int Id => Peer.Id;
        public string Name { get; set; } = "";
        public string MapKey { get; set; } = "";
        public bool HasSaidHello { get; set; }
        public int Skin { get; set; }
        public bool IsAdmin { get; set; }
        public PlayerInfo Info => new(Id, Name, MapKey, Skin);
        public Queue<double> RecentChat { get; } = new();
    }

    private readonly ServerOptions options;
    private readonly NetManager net;
    private readonly NetDataWriter writer = new();
    private readonly Dictionary<int, Player> players = new();
    private readonly Queue<(double Due, int PeerId, PlayerState State)> echoQueue = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    // ---------- Round state ----------
    private RoundPhase phase = RoundPhase.Free;
    private int roundId;
    private int mapIndex = -1;
    private double phaseStart;
    private double phaseEnd;
    private bool paused;
    private double pausedRemaining;

    // ---------- Rounds mode (synchronized start) ----------
    private bool IsRoundsMode => options.Mode == GameMode.Rounds;
    private int roundInMap = 1;
    private double? startAt;
    private double? firstFinishAt;
    private readonly HashSet<int> ready = new();
    private readonly HashSet<int> participants = new();
    private readonly HashSet<int> gaveUp = new();

    /// <summary>Best time of each player in the current round (the name is kept if the player leaves).</summary>
    private readonly Dictionary<int, LeaderboardEntry> best = new();

    /// <summary>Match standings, by player name (players get a new id when they reconnect).</summary>
    private readonly Dictionary<string, StandingEntry> standings = new();

    public GameServer(ServerOptions options)
    {
        this.options = options;
        net = new NetManager(this)
        {
            AutoRecycle = true,
            UpdateTime = 5,
            DisconnectTimeout = 10_000,
        };
    }

    public int PlayerCount => players.Count;

    private bool HasRotation => options.Maps.Count > 0;

    private string CurrentMap => phase == RoundPhase.Free ? "" : options.Maps[mapIndex];

    private double Now => clock.Elapsed.TotalSeconds;

    public void Start()
    {
        if (!net.Start(options.Port))
        {
            throw new InvalidOperationException($"Cannot listen on UDP port {options.Port}");
        }
        Log($"\"{options.ServerName}\" started on UDP port {options.Port} (protocol v{ProtocolInfo.Version}, {options.MaxPlayers} players max"
            + (options.Password != "" ? ", password protected" : "")
            + (options.AdminPassword != "" ? ", in-game admin enabled" : "")
            + (options.Echo ? ", echo mode" : "") + ")");
        Log(HasRotation
            ? (IsRoundsMode
                ? $"Rounds mode - rotation: {string.Join(", ", options.Maps)} - {options.RoundsPerMap} rounds per map, {options.RoundSeconds:0} s limit, {options.ResultsSeconds:0} s results"
                : $"Time Attack - rotation: {string.Join(", ", options.Maps)} - {options.RoundSeconds:0} s rounds, {options.ResultsSeconds:0} s results")
            : "No maps configured: free mode (set Maps in the config or use --maps to enable rounds)");
    }

    public void Poll()
    {
        net.PollEvents();
        if (options.Echo)
        {
            FlushEcho();
        }
        UpdateRound();
    }

    public void Stop() => net.Stop(sendDisconnectMessages: true);

    // ---------- Console ----------

    public IEnumerable<string> DescribePlayers() =>
        players.Values.Select(p => $"#{p.Id} {p.Name}{(p.IsAdmin ? " [admin]" : "")} - map: {(p.MapKey == "" ? "(menu)" : p.MapKey)} - ping {p.Peer.Ping} ms");

    public string DescribeRound() => phase switch
    {
        RoundPhase.Free => "Free mode",
        _ => $"Round {roundId} - map {mapIndex + 1}/{options.Maps.Count} ({CurrentMap}) - {phase}{(paused ? " (paused)" : "")} - {Remaining:0} s left",
    };

    public IEnumerable<string> DescribeStandings() =>
        SortedStandings().Select((e, i) => $"{i + 1}. {e.Name} - {e.Points} pts ({e.RoundsPlayed} rounds)");

    /// <summary>Runs an admin command from the server console (no admin name). Returns an error message, or null.</summary>
    public string? RunConsoleCommand(AdminCommand command, int argument)
    {
        var (code, arg) = ExecuteAdmin(null, command, argument);
        return code == CommandResults.Done ? null : DescribeResult(code, arg);
    }

    /// <summary>English text of a command result, for the server console.</summary>
    private static string DescribeResult(string code, string arg) => code switch
    {
        CommandResults.NoRound => "No round in progress",
        CommandResults.Usage => $"Usage: {arg}",
        CommandResults.NoPlayer => $"No player {arg}",
        CommandResults.BadMap => $"Map index out of range (the rotation has {arg} maps)",
        _ => $"{code} {arg}",
    };

    // ---------- Rounds ----------

    private double Remaining => Math.Max(0, (paused ? pausedRemaining : phaseEnd - Now));

    private void UpdateRound()
    {
        if (!HasRotation)
        {
            return;
        }
        if (!players.Values.Any(p => p.HasSaidHello))
        {
            if (phase != RoundPhase.Free)
            {
                Log("Nobody left: rotation paused");
                phase = RoundPhase.Free;
                mapIndex = -1;
                paused = false;
            }
            return;
        }

        if (phase == RoundPhase.Free)
        {
            StartRound((mapIndex + 1) % options.Maps.Count, newMap: true);
        }
        else if (paused)
        {
            phaseEnd = Now + pausedRemaining;
        }
        else if (phase == RoundPhase.Preparing)
        {
            // Everyone on the start line (or waited long enough): countdown, then start.
            int connected = players.Values.Count(p => p.HasSaidHello);
            if ((ready.Count >= connected && ready.Count > 0) || Now >= phaseEnd)
            {
                BeginCountdown();
            }
        }
        else if (phase == RoundPhase.Playing && IsRoundsMode && RoundsRaceIsOver())
        {
            EndRound();
        }
        else if (Now >= phaseEnd)
        {
            if (phase == RoundPhase.Playing)
            {
                EndRound();
            }
            else
            {
                AdvanceAfterResults();
            }
        }
    }

    /// <summary>After the results: next round on the same map (Rounds mode), or next map.</summary>
    private void AdvanceAfterResults()
    {
        if (IsRoundsMode && roundInMap < Math.Max(1, options.RoundsPerMap))
        {
            roundInMap++;
            StartRound(mapIndex, newMap: false);
        }
        else
        {
            StartRound((mapIndex + 1) % options.Maps.Count, newMap: true);
        }
    }

    private void StartRound(int index, bool newMap)
    {
        if (newMap)
        {
            roundInMap = 1;
        }
        mapIndex = index;
        roundId++;
        paused = false;
        phaseStart = Now;
        best.Clear();
        ready.Clear();
        participants.Clear();
        gaveUp.Clear();
        startAt = null;
        firstFinishAt = null;
        if (IsRoundsMode)
        {
            phase = RoundPhase.Preparing;
            phaseEnd = Now + options.PrepareTimeoutSeconds;
            Log($"Round {roundId}: {CurrentMap} (round {roundInMap}/{options.RoundsPerMap} on this map) - waiting for players");
        }
        else
        {
            phase = RoundPhase.Playing;
            phaseEnd = Now + options.RoundSeconds;
            Log($"Round {roundId}: {CurrentMap} ({options.RoundSeconds:0} s)");
        }
        BroadcastRound();
        BroadcastLeaderboard();
    }

    /// <summary>Rounds mode: everyone ready (or timeout). The players ready now are the participants of the race.</summary>
    private void BeginCountdown()
    {
        phase = RoundPhase.Playing;
        participants.UnionWith(ready);
        startAt = Now + options.CountdownSeconds;
        phaseStart = startAt.Value;
        phaseEnd = startAt.Value + options.RoundSeconds;
        Log($"Round {roundId}: start in {options.CountdownSeconds:0} s ({participants.Count} participant(s))");
        BroadcastRound();
    }

    /// <summary>Rounds mode: all participants finished or gave up, or the finish timeout expired.</summary>
    private bool RoundsRaceIsOver()
    {
        if (startAt == null || Now < startAt)
        {
            return false;
        }
        if (firstFinishAt is double first && Now >= first + options.FinishTimeoutSeconds)
        {
            return true;
        }
        var stillRacing = participants.Where(id => players.ContainsKey(id) && !best.ContainsKey(id) && !gaveUp.Contains(id));
        return participants.Count > 0 && !stillRacing.Any();
    }

    private void OnReady(Player p, int readyRoundId)
    {
        if (!IsRoundsMode || readyRoundId != roundId || phase != RoundPhase.Preparing || !Msg.MapMatches(p.MapKey, CurrentMap))
        {
            return;
        }
        if (ready.Add(p.Id))
        {
            BroadcastRound(); // updates the "ready x/y" counter
        }
    }

    private void OnGiveUp(Player p, int giveUpRoundId)
    {
        if (IsRoundsMode && giveUpRoundId == roundId && phase == RoundPhase.Playing && participants.Contains(p.Id)
            && !best.ContainsKey(p.Id) && gaveUp.Add(p.Id))
        {
            Log($"{p.Name} gave up");
        }
    }

    private void EndRound()
    {
        phase = RoundPhase.Results;
        paused = false;
        phaseStart = Now;
        phaseEnd = Now + options.ResultsSeconds;
        AwardPoints();
        BroadcastRound();
        BroadcastStandings();
        LogResults();
        ExportResults();
    }

    private void AwardPoints()
    {
        List<LeaderboardEntry> board = SortedLeaderboard();
        for (int rank = 0; rank < board.Count; rank++)
        {
            int points = rank < options.PointsTable.Count ? options.PointsTable[rank] : 0;
            string name = board[rank].Name;
            standings.TryGetValue(name, out StandingEntry previous);
            standings[name] = new StandingEntry(name, previous.Points + points, previous.RoundsPlayed + 1);
        }
    }

    private RoundInfo CurrentRoundInfo() =>
        new(roundId, CurrentMap, phase, (float)Remaining, Math.Max(0, mapIndex), options.Maps.Count, paused,
            options.Mode,
            StartIn: startAt is double s ? (float)(s - Now) : 0f,
            RoundInMap: roundInMap,
            RoundsPerMap: IsRoundsMode ? Math.Max(1, options.RoundsPerMap) : 1,
            ReadyCount: ready.Count,
            PlayerCount: players.Values.Count(p => p.HasSaidHello));

    private void BroadcastRound() => Broadcast(Msg.Round(writer, CurrentRoundInfo()), DeliveryMethod.ReliableOrdered, except: null);

    private List<LeaderboardEntry> SortedLeaderboard() => best.Values.OrderBy(e => e.BestMs).ToList();

    private List<StandingEntry> SortedStandings() =>
        standings.Values.OrderByDescending(e => e.Points).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private void BroadcastLeaderboard() =>
        Broadcast(Msg.Leaderboard(writer, roundId, SortedLeaderboard()), DeliveryMethod.ReliableOrdered, except: null);

    private void BroadcastStandings() =>
        Broadcast(Msg.Standings(writer, SortedStandings()), DeliveryMethod.ReliableOrdered, except: null);

    private void BroadcastEvent(string code, string argument) =>
        Broadcast(Msg.Event(writer, code, argument), DeliveryMethod.ReliableOrdered, except: null);

    private void OnFinish(Player p, int finishRoundId, uint timeMs)
    {
        string reject = "";
        double elapsed = Now - phaseStart;
        if (phase != RoundPhase.Playing || finishRoundId != roundId)
        {
            reject = "outside of a round";
        }
        else if (IsRoundsMode && (!participants.Contains(p.Id) || gaveUp.Contains(p.Id) || best.ContainsKey(p.Id)))
        {
            reject = "not racing in this round (one attempt per round)";
        }
        else if (!Msg.MapMatches(p.MapKey, CurrentMap))
        {
            reject = $"wrong map ({p.MapKey})";
        }
        else if (timeMs == 0 || timeMs / 1000.0 > elapsed + FinishToleranceSeconds)
        {
            // A run can't last longer than the round has been running.
            reject = $"impossible time ({Msg.FormatTime(timeMs)} while the round started {elapsed:0.0} s ago)";
        }

        if (reject != "")
        {
            Log($"Time rejected for {p.Name}: {reject}");
            return;
        }

        firstFinishAt ??= Now;
        bool improved = !best.TryGetValue(p.Id, out LeaderboardEntry previous) || timeMs < previous.BestMs;
        Log($"{p.Name}: {Msg.FormatTime(timeMs)}{(improved ? " (personal best)" : "")}");
        if (improved)
        {
            best[p.Id] = new LeaderboardEntry(p.Id, p.Name, timeMs);
            BroadcastLeaderboard();
        }
    }

    private void LogResults()
    {
        var board = SortedLeaderboard();
        Log($"Round {roundId} results ({CurrentMap}):" + (board.Count == 0 ? " no times" : ""));
        for (int i = 0; i < board.Count; i++)
        {
            Log($"  {i + 1}. {board[i].Name} - {Msg.FormatTime(board[i].BestMs)}");
        }
    }

    /// <summary>Writes the round results (JSON) and the match standings (CSV) for tournament organizers.</summary>
    private void ExportResults()
    {
        if (string.IsNullOrWhiteSpace(options.ResultsDirectory))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(options.ResultsDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string safeMap = string.Concat(CurrentMap.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var board = SortedLeaderboard();
            var round = new
            {
                Round = roundId,
                Map = CurrentMap,
                EndedAt = DateTime.Now.ToString("o", CultureInfo.InvariantCulture),
                Results = board.Select((e, i) => new
                {
                    Rank = i + 1,
                    e.Name,
                    TimeMs = e.BestMs,
                    Time = Msg.FormatTime(e.BestMs),
                    Points = i < options.PointsTable.Count ? options.PointsTable[i] : 0,
                }),
            };
            File.WriteAllText(Path.Combine(options.ResultsDirectory, $"{stamp}_round{roundId}_{safeMap}.json"),
                JsonSerializer.Serialize(round, ExportJson));

            var csv = new StringBuilder("Rank,Name,Points,RoundsPlayed\n");
            int rank = 1;
            foreach (StandingEntry e in SortedStandings())
            {
                csv.Append(rank++).Append(',').Append(CsvField(e.Name)).Append(',').Append(e.Points).Append(',').Append(e.RoundsPlayed).Append('\n');
            }
            File.WriteAllText(Path.Combine(options.ResultsDirectory, "match_standings.csv"), csv.ToString());
        }
        catch (Exception e)
        {
            Log($"Could not export results: {e.Message}");
        }
    }

    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        // Keep "+01:00" and accented names readable in the exported files.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string CsvField(string s) => s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

    // ---------- Admin ----------

    private void OnAdminLogin(Player p, string password)
    {
        bool ok = options.AdminPassword != "" && password == options.AdminPassword;
        if (ok && !p.IsAdmin)
        {
            p.IsAdmin = true;
            Log($"{p.Name} (#{p.Id}) logged in as admin");
            BroadcastEvent(ServerEvents.AdminLoggedIn, p.Name);
        }
        else if (!ok)
        {
            Log($"Failed admin login from {p.Name} (#{p.Id})");
        }
        p.Peer.Send(Msg.AdminStatus(writer, p.IsAdmin, p.IsAdmin ? options.Maps : Array.Empty<string>()), DeliveryMethod.ReliableOrdered);
    }

    private void OnAdminCommand(Player p, AdminCommand command, int argument)
    {
        if (!p.IsAdmin)
        {
            Log($"Admin command {command} refused for {p.Name} (#{p.Id}): not logged in");
            Reply(p, CommandResults.NotAdmin, "");
            return;
        }
        var (code, arg) = ExecuteAdmin(p, command, argument);
        if (code != CommandResults.Done)
        {
            Reply(p, code, arg);
        }
    }

    /// <summary>Runs an admin command. Returns a <see cref="CommandResults"/> code and its argument.</summary>
    private (string Code, string Arg) ExecuteAdmin(Player? admin, AdminCommand command, int argument)
    {
        string by = admin?.Name ?? "server";
        bool inRound = phase != RoundPhase.Free;
        switch (command)
        {
            case AdminCommand.NextPhase:
                if (!inRound)
                {
                    return (CommandResults.NoRound, "");
                }
                if (phase == RoundPhase.Preparing)
                {
                    BeginCountdown(); // don't wait for the missing players
                }
                else if (phase == RoundPhase.Playing)
                {
                    EndRound();
                }
                else
                {
                    AdvanceAfterResults();
                }
                BroadcastEvent(ServerEvents.Skipped, by);
                break;

            case AdminCommand.RestartRound:
                if (!inRound)
                {
                    return (CommandResults.NoRound, "");
                }
                StartRound(mapIndex, newMap: false);
                BroadcastEvent(ServerEvents.Restarted, by);
                break;

            case AdminCommand.TogglePause:
                if (!inRound)
                {
                    return (CommandResults.NoRound, "");
                }
                if (paused)
                {
                    paused = false;
                    phaseEnd = Now + pausedRemaining;
                }
                else
                {
                    paused = true;
                    pausedRemaining = Math.Max(0, phaseEnd - Now);
                }
                BroadcastRound();
                BroadcastEvent(paused ? ServerEvents.Paused : ServerEvents.Resumed, by);
                break;

            case AdminCommand.Extend:
                if (phase != RoundPhase.Playing)
                {
                    return (CommandResults.NoRound, "");
                }
                if (argument <= 0)
                {
                    return (CommandResults.Usage, "/extend <seconds>");
                }
                if (paused)
                {
                    pausedRemaining += argument;
                }
                else
                {
                    phaseEnd += argument;
                }
                BroadcastRound();
                BroadcastEvent(ServerEvents.Extended, argument.ToString(CultureInfo.InvariantCulture));
                break;

            case AdminCommand.GotoMap:
                if (argument < 0 || argument >= options.Maps.Count)
                {
                    return (CommandResults.BadMap, options.Maps.Count.ToString(CultureInfo.InvariantCulture));
                }
                StartRound(argument, newMap: true);
                BroadcastEvent(ServerEvents.MapChosen, by);
                break;

            case AdminCommand.Kick:
                if (!players.TryGetValue(argument, out Player? target))
                {
                    return (CommandResults.NoPlayer, $"#{argument}");
                }
                Log($"{target.Name} (#{target.Id}) kicked by {by}");
                BroadcastEvent(ServerEvents.PlayerKicked, target.Name);
                target.Peer.Disconnect(NetDataWriter.FromString(ServerReasons.Kicked));
                break;

            case AdminCommand.ResetMatch:
                standings.Clear();
                BroadcastStandings();
                BroadcastEvent(ServerEvents.MatchReset, by);
                break;

            default:
                return (CommandResults.Unknown, command.ToString());
        }
        Log($"Admin: {command}{(argument != 0 ? $" {argument}" : "")} by {by}");
        return (CommandResults.Done, "");
    }

    // ---------- Chat ----------

    private const int ChatBurst = 5;
    private const double ChatWindowSeconds = 5;

    private void OnChat(Player p, string raw)
    {
        string text = Msg.SanitizeChat(raw);
        if (text.Length == 0)
        {
            return;
        }

        // Anti-spam: at most ChatBurst messages per ChatWindowSeconds.
        while (p.RecentChat.Count > 0 && Now - p.RecentChat.Peek() > ChatWindowSeconds)
        {
            p.RecentChat.Dequeue();
        }
        if (p.RecentChat.Count >= ChatBurst)
        {
            Reply(p, CommandResults.TooFast, "");
            return;
        }
        p.RecentChat.Enqueue(Now);

        if (text.StartsWith('/'))
        {
            OnChatCommand(p, text);
            return;
        }
        Log($"[chat] {p.Name}: {text}");
        Broadcast(Msg.ChatToClient(writer, p.Id, p.Name, text), DeliveryMethod.ReliableOrdered, except: null);
    }

    private void Reply(Player p, string code, string argument) =>
        p.Peer.Send(Msg.Reply(writer, code, argument), DeliveryMethod.ReliableOrdered);

    /// <summary>Chat commands. Map numbers are 1-based for players (the console uses 0-based indexes).</summary>
    private void OnChatCommand(Player p, string text)
    {
        string[] parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1] : "";

        switch (command)
        {
            case "help":
                Reply(p, CommandResults.Help, p.IsAdmin ? "admin" : "player");
                return;
            case "login":
                OnAdminLogin(p, arg);
                return;
        }

        AdminCommand? admin = command switch
        {
            "next" or "skip" => AdminCommand.NextPhase,
            "restart" => AdminCommand.RestartRound,
            "pause" or "resume" => AdminCommand.TogglePause,
            "extend" => AdminCommand.Extend,
            "map" => AdminCommand.GotoMap,
            "kick" => AdminCommand.Kick,
            "resetmatch" => AdminCommand.ResetMatch,
            _ => null,
        };
        if (admin is not AdminCommand cmd)
        {
            Reply(p, CommandResults.Unknown, "/" + command);
            return;
        }
        if (!p.IsAdmin)
        {
            Reply(p, CommandResults.NotAdmin, "");
            return;
        }

        int argument = 0;
        switch (cmd)
        {
            case AdminCommand.Extend:
                argument = arg == "" ? 60 : int.TryParse(arg, out int seconds) ? seconds : -1;
                break;
            case AdminCommand.GotoMap:
                if (!int.TryParse(arg, out int number))
                {
                    Reply(p, CommandResults.Usage, "/map <number>");
                    return;
                }
                argument = number - 1;
                break;
            case AdminCommand.Kick:
                Player? target = FindPlayer(arg);
                if (target == null)
                {
                    Reply(p, arg == "" ? CommandResults.Usage : CommandResults.NoPlayer, arg == "" ? "/kick <name>" : arg);
                    return;
                }
                argument = target.Id;
                break;
        }

        var (code, resultArg) = ExecuteAdmin(p, cmd, argument);
        if (code != CommandResults.Done)
        {
            Reply(p, code, resultArg);
        }
    }

    /// <summary>Finds a player by "#id", exact name, or unique name prefix (case-insensitive).</summary>
    private Player? FindPlayer(string query)
    {
        if (query == "")
        {
            return null;
        }
        var named = players.Values.Where(o => o.HasSaidHello).ToList();
        if (query.StartsWith('#') && int.TryParse(query[1..], out int id))
        {
            return named.FirstOrDefault(o => o.Id == id);
        }
        var exact = named.FirstOrDefault(o => string.Equals(o.Name, query, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }
        var prefix = named.Where(o => o.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefix.Count == 1 ? prefix[0] : null;
    }

    // ---------- LiteNetLib events ----------

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (players.Count >= options.MaxPlayers)
        {
            request.Reject(NetDataWriter.FromString(ServerReasons.Full));
            return;
        }
        // Manual checks (instead of AcceptIfKey) so the player is told why they were refused.
        string key = request.Data.AvailableBytes > 0 ? request.Data.GetString(64) : "";
        if (key != ProtocolInfo.ConnectionKey)
        {
            Log($"Connection refused from {request.RemoteEndPoint}: different mod version ({key})");
            request.Reject(NetDataWriter.FromString($"{ServerReasons.VersionMismatch}:{ProtocolInfo.Version}"));
            return;
        }
        string password = request.Data.AvailableBytes > 0 ? request.Data.GetString(128) : "";
        if (options.Password != "" && password != options.Password)
        {
            Log($"Connection refused from {request.RemoteEndPoint}: wrong password");
            request.Reject(NetDataWriter.FromString(ServerReasons.BadPassword));
            return;
        }
        request.Accept();
    }

    public void OnPeerConnected(NetPeer peer)
    {
        players[peer.Id] = new Player { Peer = peer };
        Log($"Connection from {peer} (#{peer.Id})");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!players.Remove(peer.Id, out Player? p))
        {
            return;
        }
        Log($"Disconnection of {p.Name} (#{p.Id}): {info.Reason}");
        if (p.HasSaidHello)
        {
            Broadcast(Msg.PlayerLeft(writer, p.Id), DeliveryMethod.ReliableOrdered, except: null);
        }
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (!players.TryGetValue(peer.Id, out Player? p) || reader.AvailableBytes < 1)
        {
            return;
        }

        try
        {
            var type = (MessageType)reader.GetByte();
            if (!p.HasSaidHello && type != MessageType.Hello)
            {
                return;
            }

            switch (type)
            {
                case MessageType.Hello:
                    OnHello(p, reader);
                    break;
                case MessageType.MapChanged:
                    OnMapChanged(p, reader.GetString(256));
                    break;
                case MessageType.State:
                    OnState(p, Msg.GetState(reader));
                    break;
                case MessageType.SkinChanged:
                    p.Skin = reader.GetInt();
                    Broadcast(Msg.SkinChanged(writer, p.Id, p.Skin), DeliveryMethod.ReliableOrdered, except: p);
                    if (options.Echo)
                    {
                        p.Peer.Send(Msg.SkinChanged(writer, EchoIdOffset + p.Id, p.Skin), DeliveryMethod.ReliableOrdered);
                    }
                    break;
                case MessageType.Finish:
                    OnFinish(p, reader.GetInt(), reader.GetUInt());
                    break;
                case MessageType.AdminLogin:
                    OnAdminLogin(p, reader.GetString(128));
                    break;
                case MessageType.AdminCommand:
                    OnAdminCommand(p, (AdminCommand)reader.GetByte(), reader.GetInt());
                    break;
                case MessageType.Chat:
                    OnChat(p, reader.GetString(ProtocolInfo.MaxChatLength * 4));
                    break;
                case MessageType.Ready:
                    OnReady(p, reader.GetInt());
                    break;
                case MessageType.GiveUp:
                    OnGiveUp(p, reader.GetInt());
                    break;
                default:
                    Log($"Unexpected message {type} from #{p.Id}");
                    break;
            }
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            Log($"Invalid packet from #{p.Id}: {e.Message}");
        }
    }

    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) =>
        Log($"Network error {endPoint}: {socketError}");

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    // ---------- Players ----------

    private void OnHello(Player p, NetPacketReader reader)
    {
        if (p.HasSaidHello)
        {
            return;
        }
        p.Name = Msg.SanitizeName(reader.GetString(ProtocolInfo.MaxNameLength * 4));
        p.Skin = reader.GetInt();
        p.HasSaidHello = true;

        var others = players.Values.Where(o => o.HasSaidHello && o != p).Select(o => o.Info).ToList();
        p.Peer.Send(Msg.Welcome(writer, p.Id, others), DeliveryMethod.ReliableOrdered);
        Broadcast(Msg.PlayerJoined(writer, p.Info), DeliveryMethod.ReliableOrdered, except: p);

        if (options.Echo)
        {
            p.Peer.Send(Msg.PlayerJoined(writer, new PlayerInfo(EchoIdOffset + p.Id, $"Echo {p.Name}", p.MapKey, p.Skin)), DeliveryMethod.ReliableOrdered);
        }
        Log($"{p.Name} (#{p.Id}) joined the game ({players.Count} connected)");

        // Round in progress: the newcomer gets the imposed map, the leaderboard and the match standings.
        if (phase != RoundPhase.Free)
        {
            p.Peer.Send(Msg.Round(writer, CurrentRoundInfo()), DeliveryMethod.ReliableOrdered);
            p.Peer.Send(Msg.Leaderboard(writer, roundId, SortedLeaderboard()), DeliveryMethod.ReliableOrdered);
        }
        if (standings.Count > 0)
        {
            p.Peer.Send(Msg.Standings(writer, SortedStandings()), DeliveryMethod.ReliableOrdered);
        }
    }

    private void OnMapChanged(Player p, string mapKey)
    {
        p.MapKey = mapKey;
        Broadcast(Msg.MapChanged(writer, p.Id, mapKey), DeliveryMethod.ReliableOrdered, except: p);
        if (options.Echo)
        {
            p.Peer.Send(Msg.MapChanged(writer, EchoIdOffset + p.Id, mapKey), DeliveryMethod.ReliableOrdered);
        }
        Log($"{p.Name} (#{p.Id}) → {(mapKey == "" ? "menu" : mapKey)}");
    }

    private void OnState(Player p, PlayerState state)
    {
        // Everyone receives every state; clients only show players on their own map.
        Broadcast(Msg.State(writer, p.Id, state), DeliveryMethod.Unreliable, except: p);
        if (options.Echo)
        {
            echoQueue.Enqueue((Now + options.EchoDelaySeconds, p.Id, state));
        }
    }

    private void FlushEcho()
    {
        while (echoQueue.Count > 0 && echoQueue.Peek().Due <= Now)
        {
            var (_, peerId, state) = echoQueue.Dequeue();
            if (players.TryGetValue(peerId, out Player? p))
            {
                p.Peer.Send(Msg.State(writer, EchoIdOffset + peerId, state), DeliveryMethod.Unreliable);
            }
        }
    }

    private void Broadcast(NetDataWriter w, DeliveryMethod method, Player? except)
    {
        foreach (Player o in players.Values)
        {
            if (o != except && o.HasSaidHello)
            {
                o.Peer.Send(w, method);
            }
        }
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
