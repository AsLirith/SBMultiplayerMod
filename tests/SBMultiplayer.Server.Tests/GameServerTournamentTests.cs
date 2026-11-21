using System.IO;
using LiteNetLib.Utils;
using SBMultiplayer.Protocol;
using SBMultiplayer.Server;

namespace SBMultiplayer.Server.Tests;

public class GameServerTournamentTests
{
    static GameServerTournamentTests()
    {
        GameServer.LogSink = _ => { };
    }

    /// <summary>A tournament room driven by a fake clock and fake players.</summary>
    private sealed class Room
    {
        private readonly NetDataWriter w = new();
        public double T;
        public ServerOptions Options { get; }
        public GameServer Server { get; }
        public List<TournamentReport> Reports { get; } = new();

        public Room(GameMode mode, int maps = 2, int roundsPerMap = 1, int minutes = 1, int eliminatedPerRound = 1, string? hostSteamId = null)
        {
            Options = new ServerOptions
            {
                AdminPassword = "pw",
                ResultsDirectory = "",
                Tournament = new TournamentSettings("t1", mode, Enumerable.Range(1, maps).Select(i => $"o:{i}").ToList(),
                    roundsPerMap, minutes, 100, eliminatedPerRound, hostSteamId),
            };
            Server = new GameServer(Options, listenUdp: false, clock: () => T);
            Server.TournamentReported += Reports.Add;
            Server.Start();
        }

        public FakePeer Join(int id, string? steamId, string? steamName, string helloName = "Client Name")
        {
            var p = new FakePeer(id, steamId, steamName);
            Server.OnWebPeerConnected(p);
            Send(p, Msg.Hello(w, helloName, 0, ProtocolInfo.OwnCapabilities));
            return p;
        }

        public FakePeer JoinAdmin(int id, string steamId, string steamName)
        {
            FakePeer p = Join(id, steamId, steamName);
            Send(p, Msg.AdminLogin(w, "pw"));
            return p;
        }

        public void Send(FakePeer p, NetDataWriter message) => Server.OnWebMessage(p, message.CopyData());

        public void Command(FakePeer p, AdminCommand command, int argument = 0) => Send(p, Msg.Admin(w, command, argument));

        public void Leave(FakePeer p) => Server.OnWebPeerDisconnected(p, "left");

        public void Run(double seconds, double step = 0.5)
        {
            for (double done = 0; done < seconds; done += step)
            {
                T += step;
                Server.Poll();
            }
        }

        /// <summary>Polls until a report of this kind arrives. Gives up after a simulated hour.</summary>
        public TournamentReport RunUntil(TournamentReportKind kind, int after = 0)
        {
            for (int i = 0; i < 7200; i++)
            {
                T += 0.5;
                Server.Poll();
                var found = Reports.Where(r => r.Kind == kind).Skip(after).FirstOrDefault();
                if (found != null)
                {
                    return found;
                }
            }
            throw new Xunit.Sdk.XunitException($"No {kind} report");
        }

        /// <summary>Puts these players on the current round's start line.</summary>
        public void ReadyUp(params FakePeer[] peers)
        {
            foreach (FakePeer p in peers)
            {
                RoundInfo info = p.Rounds.Last();
                Send(p, Msg.MapChanged(w, null, info.MapKey));
                Send(p, Msg.Ready(w, info.RoundId));
            }
        }

        /// <summary>Synchronized modes. After the countdown, these players finish in this order, each with a valid replay.</summary>
        public void FinishInOrder(params FakePeer[] peers) => FinishInOrder(11, peers);

        /// <param name="wait">Seconds to run first. The default is a 3 s countdown and 8 s of racing, under the 10 s after which a silent racer is stalled.</param>
        public void FinishInOrder(double wait, params FakePeer[] peers)
        {
            Run(wait);
            for (int i = 0; i < peers.Length; i++)
            {
                RoundInfo info = peers[i].Rounds.Last();
                uint ms = (uint)(5000 + 500 * i);
                int runId = 100 + i;
                Send(peers[i], Msg.Finish(w, info.RoundId, ms, lostMs: 0, runId: runId));
                byte[] replay = GoodReplay(uint.Parse(info.MapKey.Split(':')[1]), ms);
                Send(peers[i], Msg.ReplayChunk(w, runId, replay.Length, 0, replay, replay.Length));
            }
        }

        public NetDataWriter W => w;
    }

    /// <summary>A replay that passes the checks. It reaches the finish on this official level, in this time.</summary>
    private static byte[] GoodReplay(uint levelId, uint ms)
    {
        using var stream = new MemoryStream();
        using var bw = new BinaryWriter(stream);
        bw.Write(ReplayFile.MaxFormat);
        bw.Write(1u);      // level version
        bw.Write(levelId);
        bw.Write(0u);      // user id
        bw.Write(0L);      // recording date
        bw.Write(1u);      // physics version
        bw.Write(1u);      // section 1: level time
        bw.Write(ms);
        bw.Write(0u);      // restarts
        bw.Write(ms);      // total time
        bw.Write(0u);      // no checkpoints
        bw.Write(9u);      // section 9: goal reached
        bw.Write(1u);
        bw.Write(true);
        bw.Write(0u);      // section 0: the recorded stream starts
        bw.Flush();
        return stream.ToArray();
    }

    private static TournamentReportKind[] Kinds(Room room) => room.Reports.Select(r => r.Kind).ToArray();

    private static bool IsRacing(RoundInfo r) => r.Phase is RoundPhase.Preparing or RoundPhase.Playing;

    [Fact]
    public void Options_CopiedFromTournament()
    {
        var options = new ServerOptions
        {
            RunCheck = RunCheck.Off,
            ResultsDirectory = "results",
            Tournament = new TournamentSettings("t9", GameMode.TimeAttack, new List<string> { "o:5", "o:6" }, 4, 3, 77, 2),
        };
        _ = new GameServer(options, listenUdp: false, clock: () => 0);
        Assert.Equal(GameMode.TimeAttack, options.Mode);
        Assert.Equal(new[] { "o:5", "o:6" }, options.Maps);
        Assert.Equal(4, options.RoundsPerMap);
        Assert.Equal(77, options.CupPointsLimit);
        Assert.Equal(2, options.EliminatedPerRound);
        Assert.Equal(180, options.RoundSeconds);
        Assert.Equal(RunCheck.Enforce, options.RunCheck);
        Assert.Equal(Path.Combine("results", "tournament-t9"), options.ResultsDirectory);

        var noExport = new ServerOptions
        {
            ResultsDirectory = "",
            Tournament = new TournamentSettings("t9", GameMode.Rounds, new List<string> { "o:5" }, 1, 3, 100, 1),
        };
        _ = new GameServer(noExport, listenUdp: false, clock: () => 0);
        Assert.Equal("", noExport.ResultsDirectory);
    }

    [Fact]
    public void WaitingRoom_NoRoundUntilStart()
    {
        var room = new Room(GameMode.Rounds);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        FakePeer b = room.Join(2, "2", "B");
        room.Run(60);
        Assert.DoesNotContain(a.Rounds, IsRacing);
        Assert.DoesNotContain(b.Rounds, IsRacing);
        Assert.Empty(room.Reports);
    }

    [Fact]
    public void Start_LocksRoster_RefusesNewcomer()
    {
        var room = new Room(GameMode.Rounds);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Join(2, "2", "B");
        Assert.Null(room.Server.RefuseReason(ProtocolInfo.ConnectionKey, "", checkPassword: false, steamId: "999"));

        room.Command(a, AdminCommand.StartTournament);

        Assert.Equal(ServerReasons.TournamentStarted, room.Server.RefuseReason(ProtocolInfo.ConnectionKey, "", checkPassword: false, steamId: "999"));
        Assert.Null(room.Server.RefuseReason(ProtocolInfo.ConnectionKey, "", checkPassword: false, steamId: "2"));
        Assert.Equal(new[] { TournamentReportKind.Started }, Kinds(room));
        Assert.Contains(ServerEvents.TournamentStarted, a.Events);
        Assert.Contains(a.Rounds, IsRacing);
        Assert.Equal(new[] { "1", "2" }, room.Reports[0].Save!.Roster.Select(e => e.SteamId).OrderBy(s => s));
    }

    [Fact]
    public void Hello_WithoutSteamId_IsDisconnected()
    {
        var room = new Room(GameMode.Rounds);
        FakePeer p = room.Join(1, null, null);
        Assert.Equal(ServerReasons.NeedsSteam, p.DisconnectReason);
        Assert.Empty(p.Rounds);
    }

    [Fact]
    public void Hello_UsesRosterName_NotClientName()
    {
        var room = new Room(GameMode.Rounds);
        room.Join(1, "1", "Steam Guy", helloName: "Client Name");
        string line = Assert.Single(room.Server.DescribePlayers());
        Assert.Contains("Steam Guy", line);
        Assert.DoesNotContain("Client Name", line);
    }

    [Fact]
    public void TimeAttack_EndsAfterLastMap()
    {
        var room = new Room(GameMode.TimeAttack, maps: 2, minutes: 1);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Join(2, "2", "B");
        room.Command(a, AdminCommand.StartTournament);
        room.Run(900);
        Assert.Equal(new[] { TournamentReportKind.Started, TournamentReportKind.Round, TournamentReportKind.Round, TournamentReportKind.Final },
            Kinds(room));
        Assert.Equal(1, room.Reports[1].Save!.NextMapIndex);
        Assert.Contains(ServerEvents.TournamentFinished, a.Events);
        Assert.Equal(new[] { "1", "2" }, room.Reports[3].Standings.Select(s => s.SteamId).OrderBy(s => s));
    }

    [Fact]
    public void Rounds_FinalAfterMapsTimesRounds()
    {
        var room = new Room(GameMode.Rounds, maps: 2, roundsPerMap: 2);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Join(2, "2", "B");
        room.Command(a, AdminCommand.StartTournament);
        room.Run(1200);
        Assert.Equal(new[]
        {
            TournamentReportKind.Started, TournamentReportKind.Round, TournamentReportKind.Round, TournamentReportKind.Round,
            TournamentReportKind.Round, TournamentReportKind.Final,
        }, Kinds(room));
        Assert.Equal(4, room.Reports[^1].Save!.RoundsCompleted);
        // After the final results the room holds. No new round.
        Assert.False(IsRacing(a.Rounds.Last()));
    }

    [Fact]
    public void FinalRound_ReportsFinalBeforeResultsEnd()
    {
        var room = new Room(GameMode.TimeAttack, maps: 1, minutes: 1);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Command(a, AdminCommand.StartTournament);
        for (int i = 0; i < 1000; i++)
        {
            room.Run(0.5);
            if (room.Reports.Any(r => r.Kind == TournamentReportKind.Round))
            {
                Assert.Contains(room.Reports, r => r.Kind == TournamentReportKind.Final);
                Assert.Equal(RoundPhase.Results, a.Rounds.Last().Phase);
                return;
            }
        }
        Assert.Fail("No round report");
    }

    [Fact]
    public void Elimination_DisconnectedRosterPlayerKnockedOut_StaysOut()
    {
        var room = new Room(GameMode.Elimination, maps: 2, roundsPerMap: 5);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        FakePeer b = room.Join(2, "2", "B");
        FakePeer c = room.Join(3, "3", "C");
        room.Command(a, AdminCommand.StartTournament);
        room.Leave(c);
        room.ReadyUp(a, b);
        room.FinishInOrder(a, b);

        TournamentReport first = room.RunUntil(TournamentReportKind.Round);
        Assert.Equal(StandingStatus.Eliminated, first.Standings.Single(s => s.Name == "C").Status);
        Assert.Equal("3", first.Standings.Single(s => s.Name == "C").SteamId);
        Assert.Equal(new[] { "C" }, first.Save!.EliminatedOrder);
        Assert.All(first.Standings.Where(s => s.Name != "C"), s => Assert.NotEqual(StandingStatus.Eliminated, s.Status));

        // C comes back with the same name, still out.
        while (a.Rounds.Last().Phase != RoundPhase.Preparing)
        {
            room.Run(0.5);
        }
        FakePeer c2 = room.Join(4, "3", "C renamed");
        Assert.Null(c2.DisconnectReason);
        Assert.Contains("C", room.Server.DescribePlayers().Single(l => l.StartsWith("#4 ")));
        room.ReadyUp(a, b, c2);

        TournamentReport second = room.RunUntil(TournamentReportKind.Round, after: 1);
        Assert.Equal(StandingStatus.Eliminated, second.Standings.Single(s => s.Name == "C").Status);
        Assert.Equal(new[] { "C" }, second.Save!.EliminatedOrder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Elimination_MissingRosterPlayer_IsTheSlowest_WithinTheQuota(bool spectating)
    {
        var room = new Room(GameMode.Elimination, maps: 1, roundsPerMap: 5, eliminatedPerRound: 1);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        FakePeer b = room.Join(2, "2", "B");
        FakePeer c = room.Join(3, "3", "C");
        FakePeer d = room.Join(4, "4", "D");
        room.Command(a, AdminCommand.StartTournament);
        if (spectating)
        {
            room.Send(d, Msg.Spectate(room.W, null, true));
        }
        else
        {
            room.Leave(d);
        }
        room.ReadyUp(a, b, c);
        room.FinishInOrder(a, b, c);

        TournamentReport report = room.RunUntil(TournamentReportKind.Round);
        Assert.Equal(new[] { "D" }, report.Save!.EliminatedOrder);
        Assert.Equal(StandingStatus.Eliminated, report.Standings.Single(s => s.Name == "D").Status);
        Assert.All(report.Standings.Where(s => s.Name != "D"), s => Assert.Equal(StandingStatus.Normal, s.Status));
        Assert.Equal(new[] { "A", "B", "C" }, report.Standings.Where(s => s.Name != "D").OrderBy(s => s.Name).Select(s => s.Name));
        Assert.All(report.Standings.Where(s => s.Name != "D"), s => Assert.Equal(1, s.Points));
    }

    [Fact]
    public void StartAfterEnd_IsRefused()
    {
        var room = new Room(GameMode.Rounds);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Command(a, AdminCommand.EndTournament);
        room.Command(a, AdminCommand.StartTournament);
        room.Run(60);
        Assert.Equal(new[] { TournamentReportKind.EndedEarly }, Kinds(room));
        Assert.Contains(CommandResults.NoRound, a.Replies);
        Assert.DoesNotContain(a.Rounds, IsRacing);
    }

    [Fact]
    public void Start_SpectatorsAreNotOnTheRoster_AndStayWatchers()
    {
        var room = new Room(GameMode.Rounds);
        FakePeer host = room.JoinAdmin(1, "1", "Host");
        FakePeer watcher = room.Join(2, "2", "W");
        room.Send(watcher, Msg.Spectate(room.W, null, true));
        FakePeer b = room.Join(3, "3", "B");
        room.Command(host, AdminCommand.StartTournament);

        Assert.Equal(new[] { "1", "3" }, room.Reports[0].Save!.Roster.Select(e => e.SteamId).OrderBy(x => x));
        Assert.DoesNotContain(room.Reports[0].Standings, s => s.SteamId == "2");

        // Still in the room, but can't come back to race. Not counted as ready.
        Assert.Null(watcher.DisconnectReason);
        room.Send(watcher, Msg.Spectate(room.W, null, false));
        room.ReadyUp(watcher, b);
        Assert.Equal(1, b.Rounds.Last().ReadyCount);

        // Once gone, a spectator can't return.
        room.Leave(watcher);
        Assert.Equal(ServerReasons.TournamentStarted,
            room.Server.RefuseReason(ProtocolInfo.ConnectionKey, "", checkPassword: false, steamId: "2"));
    }

    [Fact]
    public void Start_SpectatingHost_IsNotOnTheRoster_ButCanAlwaysComeBackToWatch()
    {
        var room = new Room(GameMode.Rounds, hostSteamId: "1");
        FakePeer host = room.JoinAdmin(1, "1", "Host");
        room.Send(host, Msg.Spectate(room.W, null, true));
        FakePeer b = room.Join(2, "2", "B");
        room.Command(host, AdminCommand.StartTournament);

        Assert.Equal(new[] { "2" }, room.Reports[0].Save!.Roster.Select(e => e.SteamId));

        // The host drops and comes back. They get in, watching only, and can log in as admin again.
        room.Leave(host);
        Assert.Null(room.Server.RefuseReason(ProtocolInfo.ConnectionKey, "", checkPassword: false, steamId: "1"));
        FakePeer back = room.JoinAdmin(3, "1", "Host");
        Assert.Null(back.DisconnectReason);
        room.Send(back, Msg.Spectate(room.W, null, false));
        room.ReadyUp(back, b);
        Assert.Equal(1, b.Rounds.Last().ReadyCount);
        room.Command(back, AdminCommand.EndTournament);
        Assert.Contains(room.Reports, r => r.Kind == TournamentReportKind.EndedEarly);
    }

    [Fact]
    public void Start_WithOnlySpectators_IsRefused()
    {
        var room = new Room(GameMode.Rounds);
        FakePeer host = room.JoinAdmin(1, "1", "Host");
        room.Send(host, Msg.Spectate(room.W, null, true));
        room.Command(host, AdminCommand.StartTournament);

        Assert.Contains(CommandResults.NoPlayer, host.Replies);
        Assert.Empty(room.Reports);
    }

    [Fact]
    public void SameSteamIdTwice_OldConnectionReplaced_SameNameAndStanding()
    {
        var room = new Room(GameMode.TimeAttack, maps: 2, minutes: 1);
        room.Server.RestoreTournament(new TournamentSave(
            new List<RosterEntry> { new("1", "A"), new("2", "B") },
            new List<StandingEntry> { new("A", 10, 1) },
            new List<string>(), new Dictionary<string, int>(), new Dictionary<string, int>(), 1, 1, 1));
        FakePeer first = room.JoinAdmin(1, "1", "A");
        FakePeer second = room.Join(2, "1", "Renamed");

        Assert.Equal(ServerReasons.Replaced, first.DisconnectReason);
        Assert.Null(second.DisconnectReason);
        string line = Assert.Single(room.Server.DescribePlayers());
        Assert.StartsWith("#2 A ", line);

        // The host is still admin on the new connection, with no new login.
        Assert.Contains("[admin]", line);
        Assert.True(second.AdminStatuses.Last());
        room.Command(second, AdminCommand.StartTournament);
        TournamentReport report = room.RunUntil(TournamentReportKind.Round);
        TournamentPlace a = Assert.Single(report.Standings, s => s.SteamId == "1");
        Assert.Equal("A", a.Name);
        Assert.Equal(10, a.Points);
    }

    [Fact]
    public void Elimination_ReconnectedMidRound_IsTheSlowest_WithinTheQuota()
    {
        var room = new Room(GameMode.Elimination, maps: 1, roundsPerMap: 5, eliminatedPerRound: 1);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        FakePeer b = room.Join(2, "2", "B");
        FakePeer c = room.Join(3, "3", "C");
        room.Command(a, AdminCommand.StartTournament);
        room.ReadyUp(a, b, c);
        room.Run(4); // the race has started with A, B and C

        // C's game reconnects mid-round. The new connection takes over, with a new id, and isn't in this race.
        FakePeer c2 = room.Join(4, "3", "C");
        Assert.Equal(ServerReasons.Replaced, c.DisconnectReason);

        room.FinishInOrder(4, a, b); // 5 s into the race, B finishes last
        Assert.Equal(RoundPhase.Playing, a.Rounds.Last().Phase); // both times came in during the race
        TournamentReport report = room.RunUntil(TournamentReportKind.Round);
        Assert.Equal(new[] { "C" }, report.Save!.EliminatedOrder);
        Assert.Equal(StandingStatus.Normal, report.Standings.Single(s => s.Name == "B").Status);
        Assert.Equal(StandingStatus.Normal, report.Standings.Single(s => s.Name == "A").Status);
    }

    [Theory]
    [InlineData(false)] // the mod sends no replay at all
    [InlineData(true)]  // a replay is announced but never arrives
    public void NoReplay_DoesNotScore_AndIsCountedAsFlagged(bool announced)
    {
        var room = new Room(GameMode.TimeAttack, maps: 1, minutes: 1);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Command(a, AdminCommand.StartTournament);
        RoundInfo round = a.Rounds.Last(IsRacing);
        room.Send(a, Msg.MapChanged(room.W, null, round.MapKey));
        room.Run(20);
        room.Send(a, Msg.Finish(room.W, round.RoundId, 10_000, lostMs: 0, runId: announced ? 7 : 0));
        room.Run(20); // longer than the replay timeout

        Assert.Contains(CommandResults.TimeRejected, a.Replies);
        TournamentReport report = room.RunUntil(TournamentReportKind.Round);
        TournamentPlace placeA = report.Standings.Single(s => s.Name == "A");
        Assert.Equal(0, placeA.Points);
        Assert.Equal(1, placeA.Flagged);
    }

    [Fact]
    public void Restore_ContinuesFromSavedRound()
    {
        var room = new Room(GameMode.Rounds, maps: 2, roundsPerMap: 3);
        var save = new TournamentSave(
            new List<RosterEntry> { new("1", "A"), new("2", "B") },
            new List<StandingEntry> { new("A", 10, 4), new("B", 8, 4) },
            new List<string>(),
            new Dictionary<string, int> { ["A"] = 2 },
            new Dictionary<string, int> { ["B"] = 1 },
            NextMapIndex: 1, NextRoundInMap: 2, RoundsCompleted: 4);
        room.Server.RestoreTournament(save);

        Assert.Equal(ServerReasons.TournamentStarted, room.Server.RefuseReason(ProtocolInfo.ConnectionKey, "", checkPassword: false, steamId: "999"));
        FakePeer a = room.JoinAdmin(1, "1", "Another name");
        Assert.Contains("A", room.Server.DescribePlayers().Single());
        room.Run(30);
        Assert.DoesNotContain(a.Rounds, IsRacing);

        room.Command(a, AdminCommand.StartTournament);
        RoundInfo firstRound = a.Rounds.First(IsRacing);
        Assert.Equal(1, firstRound.MapIndex);
        Assert.Equal(2, firstRound.RoundInMap);

        TournamentReport started = room.Reports.Single(r => r.Kind == TournamentReportKind.Started);
        Assert.Equal(new[] { "1", "2" }, started.Save!.Roster.Select(e => e.SteamId));

        TournamentReport round = room.RunUntil(TournamentReportKind.Round);
        Assert.Equal(5, round.Save!.RoundsCompleted);
        Assert.Equal(1, round.Save.NextMapIndex);
        Assert.Equal(3, round.Save.NextRoundInMap);
        TournamentPlace placeA = round.Standings.Single(s => s.Name == "A");
        Assert.Equal(10, placeA.Points);
        Assert.Equal(2, placeA.Wins);
        Assert.Equal(1, round.Standings.Single(s => s.Name == "B").Flagged);
    }

    [Fact]
    public void EndTournament_ReportsEndedEarly_AndStopsRounds()
    {
        var room = new Room(GameMode.TimeAttack, maps: 2, minutes: 1);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Command(a, AdminCommand.StartTournament);
        room.Run(10);
        room.Command(a, AdminCommand.EndTournament);

        Assert.Equal(TournamentReportKind.EndedEarly, room.Reports[^1].Kind);
        Assert.Contains(ServerEvents.TournamentEnded, a.Events);
        Assert.Equal(RoundPhase.Free, a.Rounds.Last().Phase);

        int roundsBefore = a.Rounds.Count;
        room.Run(600);
        Assert.DoesNotContain(a.Rounds.Skip(roundsBefore), IsRacing);
        Assert.Equal(new[] { TournamentReportKind.Started, TournamentReportKind.EndedEarly }, Kinds(room));
    }

    [Fact]
    public void EmptyRoom_ReplaysTheRoundWhenSomeoneComesBack()
    {
        var room = new Room(GameMode.Rounds, maps: 2, roundsPerMap: 2);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Command(a, AdminCommand.StartTournament);
        room.RunUntil(TournamentReportKind.Round); // round 1 on map 0 played
        while (a.Rounds.Last().Phase != RoundPhase.Preparing)
        {
            room.Run(0.5);
        }
        RoundInfo inProgress = a.Rounds.Last();
        Assert.Equal((0, 2), (inProgress.MapIndex, inProgress.RoundInMap));

        room.Leave(a);
        room.Run(120);
        FakePeer back = room.Join(2, "1", "A");
        room.Run(1);
        RoundInfo resumed = back.Rounds.First(IsRacing);
        Assert.Equal((0, 2), (resumed.MapIndex, resumed.RoundInMap));
        Assert.Single(room.Reports, r => r.Kind == TournamentReportKind.Round);
    }

    [Fact]
    public void NonAdmin_CannotStart()
    {
        var room = new Room(GameMode.Rounds);
        FakePeer a = room.Join(1, "1", "A");
        room.Command(a, AdminCommand.StartTournament);
        Assert.Contains(CommandResults.NotAdmin, a.Replies);
        room.Run(30);
        Assert.Empty(room.Reports);
        Assert.DoesNotContain(a.Rounds, IsRacing);
        Assert.Null(room.Server.RefuseReason(ProtocolInfo.ConnectionKey, "", checkPassword: false, steamId: "999"));
    }

    [Fact]
    public void Tournament_ForcesEnforce_EvenAfterSettingsChange()
    {
        var room = new Room(GameMode.Rounds);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        MatchSettings settings = room.Options.ToMatchSettings();
        settings.RunCheck = RunCheck.Report;
        settings.RoundSeconds = 123;
        room.Send(a, Msg.Settings(room.W, settings, saveToFile: false));

        Assert.Equal(123, room.Options.RoundSeconds); // the change applied
        Assert.Equal(RunCheck.Enforce, room.Options.RunCheck);
        Assert.Equal(RunCheck.Enforce, a.Settings.Last().RunCheck);
    }

    /// <summary>The smallest replay the server reads, a header and no sections. It never reaches the finish.</summary>
    private static byte[] BadReplay()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(ReplayFile.MaxFormat); // format
        bw.Write(1u);                   // level version
        bw.Write(1u);                   // level id
        bw.Write(0u);                   // user id
        bw.Write(0L);                   // recording date
        bw.Write(1u);                   // physics version
        bw.Write(0u);                   // section 0: the recorded stream starts
        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void FlaggedRun_DoesNotScore_AndIsCounted()
    {
        var room = new Room(GameMode.TimeAttack, maps: 1, minutes: 1);
        FakePeer a = room.JoinAdmin(1, "1", "A");
        room.Join(2, "2", "B");
        room.Command(a, AdminCommand.StartTournament);
        RoundInfo round = a.Rounds.Last(IsRacing);
        room.Send(a, Msg.MapChanged(room.W, null, round.MapKey));
        room.Run(30);

        room.Send(a, Msg.Finish(room.W, round.RoundId, 20_000, lostMs: 0, runId: 7));
        byte[] replay = BadReplay();
        room.Send(a, Msg.ReplayChunk(room.W, 7, replay.Length, 0, replay, replay.Length));
        Assert.Contains(CommandResults.TimeRejected, a.Replies);

        TournamentReport report = room.RunUntil(TournamentReportKind.Round);
        TournamentPlace placeA = report.Standings.Single(s => s.Name == "A");
        Assert.Equal(0, placeA.Points);
        Assert.Equal(1, placeA.Flagged);
        Assert.Equal(1, report.Save!.Flagged["A"]);
        Assert.Equal(0, report.Standings.Single(s => s.Name == "B").Flagged);
    }
}
