using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Connexion au serveur. Singleton global : il survit aux changements de scène.
/// Les événements LiteNetLib arrivent sur le thread réseau (UnsyncedEvents) ; le jeu n'a rien à « poller ».
/// </summary>
internal sealed class MultiplayerClient : INetEventListener
{
    private const double ReconnectDelaySeconds = 5;

    public static MultiplayerClient Instance { get; } = new();

    private readonly object sendGate = new();
    private readonly NetDataWriter writer = new();
    private NetManager? net;
    private volatile NetPeer? server;
    private double lastConnectAttempt = double.NegativeInfinity;
    private string mapKey = "";
    private string playerName = "Joueur";

    public ConcurrentDictionary<int, RemotePlayer> Players { get; } = new();

    public int? LocalId { get; private set; }

    public bool IsReady => server != null && LocalId.HasValue;

    /// <summary>Objet (scène) qui a déclaré la map courante ; seul lui peut la remettre à "" en partant.</summary>
    public object? MapOwner { get; private set; }

    /// <summary>À appeler régulièrement depuis le thread du jeu : (re)connecte si nécessaire.</summary>
    public void EnsureConnected()
    {
        if (!ModConfig.Current.AutoConnect || server != null || NetClock.Now - lastConnectAttempt < ReconnectDelaySeconds)
        {
            return;
        }
        lastConnectAttempt = NetClock.Now;

        try
        {
            if (net == null)
            {
                net = new NetManager(this)
                {
                    UnsyncedEvents = true,
                    AutoRecycle = true,
                    UpdateTime = 5,
                    DisconnectTimeout = 10_000,
                };
                net.Start();
                // Fermeture du jeu : on prévient le serveur au lieu de le laisser attendre le timeout.
                AppDomain.CurrentDomain.ProcessExit += (_, _) => net.Stop(sendDisconnectMessages: true);
            }

            playerName = ResolvePlayerName();
            var (host, port) = ModConfig.Current.ParseServer();
            ModLog.Info($"Connexion à {host}:{port} en tant que « {playerName} »…");
            server = net.Connect(host, port, ProtocolInfo.ConnectionKey);
        }
        catch (Exception e)
        {
            ModLog.Error("Échec de la tentative de connexion", e);
            server = null;
        }
    }

    public void SetMap(object owner, string key)
    {
        if (key == "" && !ReferenceEquals(owner, MapOwner))
        {
            return; // une scène plus récente a déjà déclaré sa map
        }
        MapOwner = key == "" ? null : owner;
        if (key == mapKey)
        {
            return;
        }
        mapKey = key;
        ModLog.Info($"Map courante : {(key == "" ? "(menu)" : key)}");
        if (IsReady)
        {
            Send(w => Msg.MapChanged(w, null, key), DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendState(in PlayerState state)
    {
        if (IsReady)
        {
            PlayerState s = state;
            Send(w => Msg.State(w, null, s), DeliveryMethod.Unreliable);
        }
    }

    private void Send(Func<NetDataWriter, NetDataWriter> build, DeliveryMethod method)
    {
        lock (sendGate)
        {
            server?.Send(build(writer), method);
        }
    }

    private static string ResolvePlayerName()
    {
        if (!string.IsNullOrWhiteSpace(ModConfig.Current.PlayerName))
        {
            return Msg.SanitizeName(ModConfig.Current.PlayerName);
        }
        try
        {
            return Msg.SanitizeName(Steamworks.SteamFriends.GetPersonaName());
        }
        catch (Exception e)
        {
            ModLog.Warn($"Pseudo Steam indisponible ({e.GetType().Name}), pseudo par défaut utilisé");
            return "Joueur";
        }
    }

    // ---------- Événements LiteNetLib (thread réseau) ----------

    public void OnPeerConnected(NetPeer peer)
    {
        ModLog.Info($"Connecté au serveur {peer}");
        server = peer; // au cas où l'événement arrive avant le retour de Connect()
        Send(w => Msg.Hello(w, playerName), DeliveryMethod.ReliableOrdered);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        string reason = info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetString() : info.Reason.ToString();
        ModLog.Warn($"Déconnecté du serveur : {reason}. Nouvelle tentative dans {ReconnectDelaySeconds} s.");
        server = null;
        LocalId = null;
        Players.Clear();
    }

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        try
        {
            var type = (MessageType)reader.GetByte();
            switch (type)
            {
                case MessageType.Welcome:
                {
                    LocalId = reader.GetInt();
                    int count = reader.GetInt();
                    Players.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        AddPlayer(Msg.GetPlayerInfo(reader));
                    }
                    ModLog.Info($"Bienvenue ! Notre id : #{LocalId}, {count} autre(s) joueur(s)");
                    // Le serveur ne connaît pas encore notre map (connexion en cours de partie, ou reconnexion).
                    if (mapKey != "")
                    {
                        Send(w => Msg.MapChanged(w, null, mapKey), DeliveryMethod.ReliableOrdered);
                    }
                    break;
                }
                case MessageType.PlayerJoined:
                    AddPlayer(Msg.GetPlayerInfo(reader));
                    break;
                case MessageType.PlayerLeft:
                {
                    int id = reader.GetInt();
                    if (Players.TryRemove(id, out RemotePlayer? p))
                    {
                        ModLog.Info($"{p.Name} (#{id}) est parti");
                    }
                    break;
                }
                case MessageType.MapChanged:
                {
                    int id = reader.GetInt();
                    string key = reader.GetString(256);
                    if (Players.TryGetValue(id, out RemotePlayer? p))
                    {
                        p.SetMap(key);
                        ModLog.Info($"{p.Name} (#{id}) → {(key == "" ? "menu" : key)}");
                    }
                    break;
                }
                case MessageType.State:
                {
                    int id = reader.GetInt();
                    PlayerState s = Msg.GetState(reader);
                    if (Players.TryGetValue(id, out RemotePlayer? p))
                    {
                        p.AddState(s);
                    }
                    break;
                }
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Paquet serveur invalide", e);
        }
    }

    private void AddPlayer(PlayerInfo info)
    {
        Players[info.Id] = new RemotePlayer(info);
        ModLog.Info($"{info.Name} (#{info.Id}) a rejoint{(info.MapKey == "" ? "" : $", sur {info.MapKey}")}");
    }

    public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) =>
        ModLog.Warn($"Erreur réseau {endPoint} : {socketError}");

    public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
    {
    }

    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
}
