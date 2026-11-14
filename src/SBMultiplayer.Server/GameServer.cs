using System.Diagnostics;
using LiteNetLib;
using LiteNetLib.Utils;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

public sealed class ServerOptions
{
    public int Port { get; set; } = ProtocolInfo.DefaultPort;

    public int MaxPlayers { get; set; } = 32;

    /// <summary>Renvoie à chaque joueur sa propre planche, retardée, sous un faux joueur « Echo ». Pour tester seul.</summary>
    public bool Echo { get; set; }

    public double EchoDelaySeconds { get; set; } = 2.0;

    /// <summary>Rotation de maps ("o:1", "o:57:2001"…). Vide = mode libre, sans rounds.</summary>
    public List<string> Maps { get; set; } = new();

    public double RoundSeconds { get; set; } = 300;

    public double ResultsSeconds { get; set; } = 15;
}

/// <summary>
/// Serveur relais : il ne simule pas la physique. Il tient la liste des joueurs, leur map,
/// relaie les états, et orchestre les rounds (map imposée, chrono, classement).
/// Tout tourne sur un seul thread (Poll).
/// </summary>
public sealed class GameServer : INetEventListener
{
    private const int EchoIdOffset = 1_000_000;

    /// <summary>Marge acceptée entre la durée d'une run et le temps écoulé depuis le début du round (latence, chargement).</summary>
    private const double FinishToleranceSeconds = 3;

    private sealed class Player
    {
        public required NetPeer Peer { get; init; }
        public int Id => Peer.Id;
        public string Name { get; set; } = "";
        public string MapKey { get; set; } = "";
        public bool HasSaidHello { get; set; }
        public int Skin { get; set; }
        public PlayerInfo Info => new(Id, Name, MapKey, Skin);
    }

    private readonly ServerOptions options;
    private readonly NetManager net;
    private readonly NetDataWriter writer = new();
    private readonly Dictionary<int, Player> players = new();
    private readonly Queue<(double Due, int PeerId, PlayerState State)> echoQueue = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    // ---------- État du round ----------
    private RoundPhase phase = RoundPhase.Free;
    private int roundId;
    private int mapIndex = -1;
    private double phaseStart;
    private double phaseEnd;

    /// <summary>Meilleur temps de chaque joueur sur le round en cours (le nom est gardé si le joueur part).</summary>
    private readonly Dictionary<int, LeaderboardEntry> best = new();

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

    public void Start()
    {
        if (!net.Start(options.Port))
        {
            throw new InvalidOperationException($"Cannot listen on UDP port {options.Port}");
        }
        Log($"Server started on UDP port {options.Port} (protocol v{ProtocolInfo.Version}, {options.MaxPlayers} players max{(options.Echo ? ", echo mode" : "")})");
        Log(HasRotation
            ? $"Rotation: {string.Join(", ", options.Maps)} - {options.RoundSeconds:0} s rounds, {options.ResultsSeconds:0} s results"
            : "No maps configured: free mode (use --maps to enable rounds)");
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

    public IEnumerable<string> DescribePlayers() =>
        players.Values.Select(p => $"#{p.Id} {p.Name} - map: {(p.MapKey == "" ? "(menu)" : p.MapKey)} - ping {p.Peer.Ping} ms");

    public string DescribeRound() => phase switch
    {
        RoundPhase.Free => "Free mode",
        _ => $"Round {roundId} - map {mapIndex + 1}/{options.Maps.Count} ({CurrentMap}) - {phase} - {Math.Max(0, phaseEnd - Now):0} s left",
    };

    /// <summary>Commande console : termine la phase en cours (round → résultats → map suivante).</summary>
    public void SkipPhase()
    {
        if (phase != RoundPhase.Free)
        {
            phaseEnd = Now;
        }
    }

    // ---------- Rounds ----------

    private void UpdateRound()
    {
        bool anyone = players.Values.Any(p => p.HasSaidHello);
        if (!HasRotation)
        {
            return;
        }
        if (!anyone)
        {
            if (phase != RoundPhase.Free)
            {
                Log("Nobody left: rotation paused");
                phase = RoundPhase.Free;
                mapIndex = -1;
            }
            return;
        }

        if (phase == RoundPhase.Free)
        {
            StartRound((mapIndex + 1) % options.Maps.Count);
        }
        else if (Now >= phaseEnd)
        {
            if (phase == RoundPhase.Playing)
            {
                phase = RoundPhase.Results;
                phaseStart = Now;
                phaseEnd = Now + options.ResultsSeconds;
                BroadcastRound();
                LogResults();
            }
            else
            {
                StartRound((mapIndex + 1) % options.Maps.Count);
            }
        }
    }

    private void StartRound(int index)
    {
        mapIndex = index;
        roundId++;
        phase = RoundPhase.Playing;
        phaseStart = Now;
        phaseEnd = Now + options.RoundSeconds;
        best.Clear();
        Log($"Round {roundId}: {CurrentMap} ({options.RoundSeconds:0} s)");
        BroadcastRound();
        BroadcastLeaderboard();
    }

    private RoundInfo CurrentRoundInfo() =>
        new(roundId, CurrentMap, phase, (float)Math.Max(0, phaseEnd - Now), Math.Max(0, mapIndex), options.Maps.Count);

    private void BroadcastRound() => Broadcast(Msg.Round(writer, CurrentRoundInfo()), DeliveryMethod.ReliableOrdered, except: null);

    private List<LeaderboardEntry> SortedLeaderboard() => best.Values.OrderBy(e => e.BestMs).ToList();

    private void BroadcastLeaderboard() =>
        Broadcast(Msg.Leaderboard(writer, roundId, SortedLeaderboard()), DeliveryMethod.ReliableOrdered, except: null);

    private void OnFinish(Player p, int finishRoundId, uint timeMs)
    {
        string reject = "";
        double elapsed = Now - phaseStart;
        if (phase != RoundPhase.Playing || finishRoundId != roundId)
        {
            reject = "outside of a round";
        }
        else if (!Msg.MapMatches(p.MapKey, CurrentMap))
        {
            reject = $"wrong map ({p.MapKey})";
        }
        else if (timeMs == 0 || timeMs / 1000.0 > elapsed + FinishToleranceSeconds)
        {
            // Une run ne peut pas durer plus longtemps que le round n'a duré.
            reject = $"impossible time ({Msg.FormatTime(timeMs)} while the round started {elapsed:0.0} s ago)";
        }

        if (reject != "")
        {
            Log($"Time rejected for {p.Name}: {reject}");
            return;
        }

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

    // ---------- Événements LiteNetLib ----------

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (players.Count >= options.MaxPlayers)
        {
            request.Reject(NetDataWriter.FromString(ServerReasons.Full));
            return;
        }
        // Vérification manuelle de la clé (au lieu de AcceptIfKey) pour expliquer le refus au joueur.
        string key = request.Data.AvailableBytes > 0 ? request.Data.GetString(64) : "";
        if (key == ProtocolInfo.ConnectionKey)
        {
            request.Accept();
            return;
        }
        Log($"Connection refused from {request.RemoteEndPoint}: different mod version ({key})");
        request.Reject(NetDataWriter.FromString($"{ServerReasons.VersionMismatch}:{ProtocolInfo.Version}"));
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

    // ---------- Logique joueurs ----------

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

        // Round en cours : le nouveau venu reçoit la map imposée et le classement.
        if (phase != RoundPhase.Free)
        {
            p.Peer.Send(Msg.Round(writer, CurrentRoundInfo()), DeliveryMethod.ReliableOrdered);
            p.Peer.Send(Msg.Leaderboard(writer, roundId, SortedLeaderboard()), DeliveryMethod.ReliableOrdered);
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
        // Les joueurs reçoivent tous les états ; c'est le client qui n'affiche que ceux de sa map.
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

    private double Now => clock.Elapsed.TotalSeconds;

    private static void Log(string message) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
