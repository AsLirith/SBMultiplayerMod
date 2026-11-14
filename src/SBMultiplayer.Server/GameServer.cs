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
}

/// <summary>
/// Serveur relais : il ne simule pas la physique. Il tient la liste des joueurs, leur map,
/// et relaie les états de chacun aux autres. Tout tourne sur un seul thread (PollEvents).
/// </summary>
public sealed class GameServer : INetEventListener
{
    private const int EchoIdOffset = 1_000_000;

    private sealed class Player
    {
        public required NetPeer Peer { get; init; }
        public int Id => Peer.Id;
        public string Name { get; set; } = "";
        public string MapKey { get; set; } = "";
        public bool HasSaidHello { get; set; }
        public PlayerInfo Info => new(Id, Name, MapKey);
    }

    private readonly ServerOptions options;
    private readonly NetManager net;
    private readonly NetDataWriter writer = new();
    private readonly Dictionary<int, Player> players = new();
    private readonly Queue<(double Due, int PeerId, PlayerState State)> echoQueue = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

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

    public void Start()
    {
        if (!net.Start(options.Port))
        {
            throw new InvalidOperationException($"Impossible d'écouter sur le port UDP {options.Port}");
        }
        Log($"Serveur démarré sur le port UDP {options.Port} (protocole v{ProtocolInfo.Version}, {options.MaxPlayers} joueurs max{(options.Echo ? ", mode écho" : "")})");
    }

    public void Poll()
    {
        net.PollEvents();
        if (options.Echo)
        {
            FlushEcho();
        }
    }

    public void Stop() => net.Stop(sendDisconnectMessages: true);

    public IEnumerable<string> DescribePlayers() =>
        players.Values.Select(p => $"#{p.Id} {p.Name} — map: {(p.MapKey == "" ? "(menu)" : p.MapKey)} — ping {p.Peer.Ping} ms");

    // ---------- Événements LiteNetLib ----------

    public void OnConnectionRequest(ConnectionRequest request)
    {
        if (players.Count >= options.MaxPlayers)
        {
            request.Reject(NetDataWriter.FromString("Serveur plein"));
            return;
        }
        if (request.AcceptIfKey(ProtocolInfo.ConnectionKey) == null)
        {
            Log($"Connexion refusée depuis {request.RemoteEndPoint} : version du mod différente");
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        players[peer.Id] = new Player { Peer = peer };
        Log($"Connexion de {peer} (#{peer.Id})");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!players.Remove(peer.Id, out Player? p))
        {
            return;
        }
        Log($"Déconnexion de {p.Name} (#{p.Id}) : {info.Reason}");
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
                default:
                    Log($"Message inattendu {type} de #{p.Id}");
                    break;
            }
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            Log($"Paquet invalide de #{p.Id} : {e.Message}");
        }
    }

    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) =>
        Log($"Erreur réseau {endPoint} : {socketError}");

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    // ---------- Logique ----------

    private void OnHello(Player p, NetPacketReader reader)
    {
        if (p.HasSaidHello)
        {
            return;
        }
        p.Name = Msg.SanitizeName(reader.GetString(ProtocolInfo.MaxNameLength * 4));
        p.HasSaidHello = true;

        var others = players.Values.Where(o => o.HasSaidHello && o != p).Select(o => o.Info).ToList();
        p.Peer.Send(Msg.Welcome(writer, p.Id, others), DeliveryMethod.ReliableOrdered);
        Broadcast(Msg.PlayerJoined(writer, p.Info), DeliveryMethod.ReliableOrdered, except: p);

        if (options.Echo)
        {
            p.Peer.Send(Msg.PlayerJoined(writer, new PlayerInfo(EchoIdOffset + p.Id, $"Echo {p.Name}", p.MapKey)), DeliveryMethod.ReliableOrdered);
        }
        Log($"{p.Name} (#{p.Id}) a rejoint la partie ({players.Count} connecté(s))");
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
