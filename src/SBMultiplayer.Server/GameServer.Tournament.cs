using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <summary>
/// Tournament rooms (<see cref="ServerOptions.Tournament"/>). The room waits until the host starts, then locks the
/// roster of Steam accounts. Every round ends with a report and a save to resume from. One match ends the tournament.
/// </summary>
public sealed partial class GameServer
{
    /// <summary>Steam accounts and their display names. Null outside a tournament room.</summary>
    private TournamentRoster? roster;

    private bool tournamentStarted;

    /// <summary>No more rounds: the final standings are out, or the host ended it early.</summary>
    private bool tournamentOver;

    /// <summary>Rounds won, by roster name.</summary>
    private readonly Dictionary<string, int> wins = new(StringComparer.Ordinal);

    /// <summary>Runs that failed a time check, by roster name.</summary>
    private readonly Dictionary<string, int> flagged = new(StringComparer.Ordinal);

    /// <summary>Elimination: roster names in the order they were knocked out.</summary>
    private readonly List<string> eliminatedOrder = new();

    private int roundsCompleted;

    /// <summary>Where the next round starts. Set at the start, after a restore, and when players return to an empty room.</summary>
    private int nextMapIndex;
    private int nextRoundInMap = 1;

    /// <summary>Hide and Seek: each player's points at the start of the round. Used to find the round's top scorer.</summary>
    private readonly Dictionary<string, int> pointsAtRoundStart = new(StringComparer.Ordinal);

    /// <summary>Raised on the game thread at the start, after every round, at the end, and on an early end.</summary>
    public event Action<TournamentReport>? TournamentReported;

    private bool IsTournament => roster != null;

    private bool TournamentRunning => tournamentStarted && !tournamentOver;

    /// <summary>The tournament's settings replace the room's. Time checks are always on.</summary>
    private void ApplyTournamentSettings()
    {
        if (options.Tournament is not TournamentSettings t)
        {
            return;
        }
        options.Mode = t.Mode;
        options.Maps = new List<string>(t.Maps);
        options.RoundsPerMap = t.RoundsPerMap;
        options.CupPointsLimit = t.CupPointsLimit;
        options.EliminatedPerRound = t.EliminatedPerRound;
        if (t.Mode == GameMode.TimeAttack)
        {
            options.RoundSeconds = t.MinutesPerMap * 60;
        }
        options.RunCheck = RunCheck.Enforce;
        if (!string.IsNullOrWhiteSpace(options.ResultsDirectory))
        {
            string safeId = string.Concat(t.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            options.ResultsDirectory = Path.Combine(options.ResultsDirectory, $"tournament-{safeId}");
        }
        roster = new TournamentRoster();
        tournamentHost = string.IsNullOrWhiteSpace(t.HostSteamId) ? null : t.HostSteamId;
    }

    /// <summary>The host's Steam ID. The host always gets in, and watches if they aren't on the roster.</summary>
    private string? tournamentHost;

    /// <summary>May this Steam account come in? Once the roster is locked: the roster and the host.</summary>
    private bool CanEnterTournament(string? steamId) =>
        !string.IsNullOrEmpty(steamId) && (roster!.CanEnter(steamId) || steamId == tournamentHost);

    /// <summary>In the room but not on the locked roster, so they can only watch. Spectators at Start, or the host.</summary>
    private bool IsWatcher(Player p) => roster is { Locked: true } && !roster.Contains(p.Peer.SteamId ?? "");

    /// <summary>
    /// Continues a tournament from its last save, with the same locked roster, standings and position. Call it right
    /// after <see cref="Start"/>, before anyone joins. The host still presses Start.
    /// </summary>
    public void RestoreTournament(TournamentSave save)
    {
        if (roster == null)
        {
            throw new InvalidOperationException("Not a tournament room");
        }
        roster = TournamentRoster.FromSave(save.Roster);
        standings.Clear();
        foreach (StandingEntry e in save.Standings)
        {
            standings[e.Name] = e;
        }
        eliminatedOrder.Clear();
        eliminatedOrder.AddRange(save.EliminatedOrder);
        eliminated.Clear();
        eliminated.UnionWith(save.EliminatedOrder);
        eliminated.UnionWith(save.Standings.Where(e => e.Status == StandingStatus.Eliminated).Select(e => e.Name));
        wins.Clear();
        foreach (var (name, n) in save.Wins)
        {
            wins[name] = n;
        }
        flagged.Clear();
        foreach (var (name, n) in save.Flagged)
        {
            flagged[name] = n;
        }
        roundsCompleted = save.RoundsCompleted;
        nextMapIndex = Math.Clamp(save.NextMapIndex, 0, Math.Max(0, options.Maps.Count - 1));
        nextRoundInMap = Math.Max(1, save.NextRoundInMap);
        Log($"Tournament restored: {roster.Entries.Count} players, next round {nextRoundInMap} on map {nextMapIndex + 1}, {roundsCompleted} rounds played");
    }

    /// <summary>Only a verified Steam account gets in, under its roster name. Returns false if refused.</summary>
    private bool AdmitToTournament(Player p, string clientName)
    {
        if (roster == null)
        {
            return true;
        }
        string? steamId = p.Peer.SteamId;
        if (string.IsNullOrEmpty(steamId))
        {
            Log($"#{p.Id} refused: no verified Steam account");
            p.Peer.Disconnect(ServerReasons.NeedsSteam);
            return false;
        }
        if (!CanEnterTournament(steamId))
        {
            Log($"#{p.Id} refused: not on the tournament's roster");
            p.Peer.Disconnect(ServerReasons.TournamentStarted);
            return false;
        }
        // Same Steam account again (game restarted, network change). The new connection takes over.
        foreach (Player old in players.Values.Where(o => o != p && o.HasSaidHello && o.Peer.SteamId == steamId).ToList())
        {
            Log($"{old.Name} (#{old.Id}) reconnected as #{p.Id}: the old connection is closed");
            p.IsAdmin |= old.IsAdmin; // the host keeps admin powers
            old.Peer.Disconnect(ServerReasons.Replaced);
            RemovePlayer(old.Id, "replaced by a new connection");
        }
        if (roster.Locked && !roster.Contains(steamId))
        {
            // The host isn't on the roster. They watch and never race or score.
            p.Name = roster.WatcherName(p.Peer.SteamName ?? clientName);
            p.IsSpectator = true;
            Log($"{p.Name} (#{p.Id}) is not on the roster: watching only");
            return true;
        }
        p.Name = roster.Join(steamId, p.Peer.SteamName ?? clientName);
        return true;
    }

    private (string Code, string Arg) StartTournament(string by)
    {
        if (tournamentOver)
        {
            return (CommandResults.NoRound, ""); // ended early, so it can't start
        }
        if (tournamentStarted)
        {
            return (CommandResults.Done, ""); // already started
        }
        if (!roster!.Locked)
        {
            // The players present are the roster. Spectators aren't. They can stay and watch but never race.
            var present = players.Values.Where(p => p.IsPlaying && !string.IsNullOrEmpty(p.Peer.SteamId))
                .Select(p => p.Peer.SteamId!).ToList();
            if (present.Count == 0)
            {
                return (CommandResults.NoPlayer, "");
            }
            roster.Lock(present);
        }
        tournamentStarted = true;
        Log($"Tournament started by {by}: {string.Join(", ", roster.Entries.Select(e => e.Name))}");
        Report(TournamentReportKind.Started, CurrentSave(nextMapIndex, nextRoundInMap), Places());
        BroadcastEvent(ServerEvents.TournamentStarted, by, $"Tournament started by {by}");
        StartTournamentRound();
        return (CommandResults.Done, "");
    }

    private (string Code, string Arg) EndTournament(string by)
    {
        if (tournamentOver)
        {
            return (CommandResults.Done, "");
        }
        tournamentOver = true;
        Log($"Tournament ended early by {by}: nothing is recorded");
        if (phase != RoundPhase.Free)
        {
            phase = RoundPhase.Free;
            mapIndex = -1;
            paused = false;
            BroadcastRound();
        }
        Report(TournamentReportKind.EndedEarly, null, new List<TournamentPlace>());
        BroadcastEvent(ServerEvents.TournamentEnded, by, $"Tournament ended early by {by}");
        return (CommandResults.Done, "");
    }

    private void StartTournamentRound()
    {
        roundInMap = nextRoundInMap;
        StartRound(nextMapIndex, newMap: false);
    }

    /// <summary>Everyone left. When someone comes back, the round in progress is replayed, or the next one if it was over.</summary>
    private void RememberTournamentPosition()
    {
        if (!TournamentRunning || phase == RoundPhase.Free)
        {
            return;
        }
        (nextMapIndex, nextRoundInMap) = phase == RoundPhase.Results ? NextPosition() : (mapIndex, roundInMap);
    }

    /// <summary>The map and round that follow the current one.</summary>
    private (int MapIndex, int RoundInMap) NextPosition() =>
        IsRoundsMode && roundInMap < Math.Max(1, options.RoundsPerMap)
            ? (mapIndex, roundInMap + 1)
            : ((mapIndex + 1) % options.Maps.Count, 1);

    private void SnapshotTournamentPoints()
    {
        if (!IsTournament)
        {
            return;
        }
        pointsAtRoundStart.Clear();
        foreach (var (name, e) in standings)
        {
            pointsAtRoundStart[name] = e.Points;
        }
    }

    /// <summary>Runs after every round. Credits the winner, sends a report with a save, and sends the final standings at the end.</summary>
    private void OnTournamentRoundEnded()
    {
        if (!TournamentRunning)
        {
            return;
        }
        roundsCompleted++;
        if (RoundWinner() is string winner)
        {
            wins[winner] = wins.GetValueOrDefault(winner) + 1;
        }
        var (nextMap, nextRound) = NextPosition();
        TournamentSave save = CurrentSave(nextMap, nextRound);
        Report(TournamentReportKind.Round, save, Places());
        if (matchOver)
        {
            tournamentOver = true;
            List<TournamentPlace> final = Places();
            Log($"Tournament finished: {string.Join(", ", final.Take(3).Select(f => $"{f.Place}. {f.Name}"))}");
            Report(TournamentReportKind.Final, save, final);
            string first = final.Count > 0 ? final[0].Name : "";
            BroadcastEvent(ServerEvents.TournamentFinished, first, $"Tournament finished! Winner: {first}");
        }
    }

    /// <summary>First on the round's board. In Hide and Seek, the round's top scorer. Null if nobody.</summary>
    private string? RoundWinner()
    {
        if (IsHideAndSeek)
        {
            var top = standings.Values
                .Select(e => (e.Name, Gained: e.Points - pointsAtRoundStart.GetValueOrDefault(e.Name)))
                .Where(x => x.Gained > 0)
                .OrderByDescending(x => x.Gained).ThenBy(x => x.Name, StringComparer.Ordinal)
                .ToList();
            return top.Count > 0 ? top[0].Name : null;
        }
        List<LeaderboardEntry> board = SortedLeaderboard();
        return board.Count > 0 ? board[0].Name : null;
    }

    /// <summary>
    /// Elimination: roster players still in who aren't in this race. They disconnected, are spectating, or reconnected
    /// or came back from spectating after the start. They count as the round's slowest non-finishers.
    /// </summary>
    private List<string> NonRacingRosterPlayers(IEnumerable<string> racerNames)
    {
        var racing = new HashSet<string>(racerNames, StringComparer.Ordinal);
        if (roster is not { Locked: true })
        {
            return new List<string>();
        }
        return roster.Entries.Select(e => e.Name)
            .Where(n => !eliminated.Contains(n) && !racing.Contains(n))
            .ToList();
    }

    /// <summary>Elimination: roster players not knocked out yet, whether connected or not.</summary>
    private List<string> RosterNamesStillIn() =>
        roster!.Entries.Select(e => e.Name).Where(n => !eliminated.Contains(n)).ToList();

    /// <summary>Elimination: this round's knock-outs, worst first. Players who missed the round, then those who didn't finish, then the slowest.</summary>
    private void RecordKnockOuts(List<string> absent, List<string> dnf, List<string> out_, List<string> finishers)
    {
        if (!IsTournament)
        {
            return;
        }
        var order = absent
            .Concat(out_.Where(dnf.Contains))
            .Concat(out_.Where(n => !dnf.Contains(n)).OrderByDescending(finishers.IndexOf));
        foreach (string name in order)
        {
            if (!eliminatedOrder.Contains(name))
            {
                eliminatedOrder.Add(name);
            }
        }
    }

    /// <summary>The admin reset the match, so the round results go too. Flagged runs stay counted.</summary>
    private void ResetTournamentMatch()
    {
        eliminatedOrder.Clear();
        wins.Clear();
    }

    private void CountFlaggedRun(Player p)
    {
        if (IsTournament)
        {
            flagged[p.Name] = flagged.GetValueOrDefault(p.Name) + 1;
        }
    }

    private TournamentSave CurrentSave(int nextMap, int nextRound) => new(
        roster!.Entries.ToList(),
        standings.Values.ToList(),
        eliminatedOrder.ToList(),
        new Dictionary<string, int>(wins),
        new Dictionary<string, int>(flagged),
        nextMap, nextRound, roundsCompleted);

    /// <summary>Standings in final order, with Steam IDs. Roster players who never scored are listed as well.</summary>
    private List<TournamentPlace> Places()
    {
        var all = standings.Values.ToList();
        foreach (RosterEntry e in roster!.Entries)
        {
            if (!standings.ContainsKey(e.Name))
            {
                all.Add(new StandingEntry(e.Name, 0, 0, eliminated.Contains(e.Name) ? StandingStatus.Eliminated : StandingStatus.Normal));
            }
        }
        var places = new List<TournamentPlace>();
        foreach (StandingEntry e in TournamentPlacing.Order(options.Mode, all, eliminatedOrder))
        {
            if (roster.SteamIdOf(e.Name) is string steamId)
            {
                places.Add(new TournamentPlace(places.Count + 1, steamId, e.Name, e.Points, wins.GetValueOrDefault(e.Name), e.RoundsPlayed,
                    e.Status, flagged.GetValueOrDefault(e.Name)));
            }
        }
        return places;
    }

    private void Report(TournamentReportKind kind, TournamentSave? save, List<TournamentPlace> places)
    {
        try
        {
            TournamentReported?.Invoke(new TournamentReport(options.Tournament!.Id, kind, save, places));
        }
        catch (Exception e)
        {
            // A failed report must not stop the game loop.
            Log($"Tournament report ({kind}) failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
