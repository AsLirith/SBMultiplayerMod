using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.UI;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Connexion au serveur. Singleton global : il survit aux changements de scène.
/// Les événements LiteNetLib arrivent sur le thread réseau (UnsyncedEvents) ; le jeu n'a rien à « poller ».
/// Tout ce qui touche au jeu (chargement de map, skin, notifications de round) se fait dans GlobalTick.
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
    private string serverAddress = "";

    /// <summary>Déconnexion demandée depuis le menu : pas de reconnexion automatique.</summary>
    private bool userDisconnected;

    /// <summary>Dernière raison de déconnexion notifiée (pour ne pas répéter la même toutes les 5 s).</summary>
    private string lastDisconnectReason = "";

    private bool wasReady;

    public ConcurrentDictionary<int, RemotePlayer> Players { get; } = new();

    public RoundState Round { get; } = new();

    /// <summary>Round dont on a déjà chargé la map (pour ne la charger qu'une fois par round).</summary>
    private int loadedRoundId;

    /// <summary>Dernier (round, phase) annoncé par une notification.</summary>
    private (int, RoundPhase) notifiedRound;

    /// <summary>Le jeu a fini de démarrer (menu ou niveau affiché) : on peut lui imposer une map.</summary>
    private bool gameReady;

    private GlobalOverlay? overlay;
    private int localSkin;
    private int skinCheckTick;
    private KeyboardState previousKeys;

    public string CurrentMapKey => mapKey;

    public string ServerAddress => serverAddress;

    public string PlayerName => playerName;

    /// <summary>Vrai si on est sur la map du round en cours (classement, retry automatique…).</summary>
    public bool IsOnRoundMap
    {
        get
        {
            var r = Round.Current;
            return IsReady && r.IsRound && Msg.MapMatches(mapKey, r.Info.MapKey);
        }
    }

    public int? LocalId { get; private set; }

    public bool IsReady => server != null && LocalId.HasValue;

    public bool IsConnecting => server != null && !LocalId.HasValue;

    public bool IsUserDisconnected => userDisconnected;

    /// <summary>Connecté en admin sur ce serveur.</summary>
    public bool IsAdmin { get; private set; }

    /// <summary>Rotation de maps du serveur (reçue avec le statut admin).</summary>
    public IReadOnlyList<string> AdminMaps { get; private set; } = Array.Empty<string>();

    /// <summary>Incrémenté quand le statut admin change (le menu se reconstruit).</summary>
    public int AdminVersion { get; private set; }

    public void AdminLogin(string password)
    {
        if (IsReady)
        {
            Send(w => Msg.AdminLogin(w, password), DeliveryMethod.ReliableOrdered);
        }
    }

    public void Admin(AdminCommand command, int argument = 0)
    {
        if (IsReady && IsAdmin)
        {
            Send(w => Msg.Admin(w, command, argument), DeliveryMethod.ReliableOrdered);
        }
    }

    /// <summary>Objet (scène) qui a déclaré la map courante ; seul lui peut la remettre à "" en partant.</summary>
    public object? MapOwner { get; private set; }

    // ---------- Tick global (thread du jeu) ----------

    /// <summary>Appelé à chaque tick du jeu, quelle que soit la scène (voir GlobalTickPatch).</summary>
    public void GlobalTick(BytingLib.Scene? currentScene)
    {
        if (overlay == null && GameAccess.Game != null)
        {
            overlay = new GlobalOverlay();
            GameAccess.GlobalStuff.Add(overlay);
        }

        if (!gameReady)
        {
            gameReady = currentScene?.GetType().Name == "LevelSelectionScene" || GameAccess.IsIngameScene(currentScene);
            if (!gameReady)
            {
                return;
            }
            ModLog.Info($"Jeu prêt ({currentScene?.GetType().Name})");
        }

        HandleHotkeys();
        AdminMenu.Tick();
        TrackLocalSkin();
        EnsureConnected();
        FlushPendingFinish();
        AnnounceConnection();

        var r = Round.Current;
        AnnounceRound(r);

        // Nouveau round : on charge la map imposée (une seule fois par round).
        if (IsReady && r.Info.Phase == RoundPhase.Playing && r.Info.RoundId != loadedRoundId)
        {
            loadedRoundId = r.Info.RoundId;
            LoadRoundMap(r.Info.MapKey);
        }
    }

    private void HandleHotkeys()
    {
        KeyboardState keys = Keyboard.GetState();
        if (GameAccess.IsTyping() || ChatInput.IsOpen)
        {
            previousKeys = keys; // pas de raccourcis pendant la saisie de texte
            return;
        }
        if (Pressed(keys, ModConfig.Current.ChatKey))
        {
            ChatInput.Open();
        }
        if (Pressed(keys, ModConfig.Current.MenuKey))
        {
            MultiplayerMenu.Open();
        }
        if (Pressed(keys, ModConfig.Current.RejoinKey))
        {
            RejoinRound();
        }
        previousKeys = keys;
    }

    private bool Pressed(KeyboardState keys, string keyName) =>
        Enum.TryParse(keyName, ignoreCase: true, out Keys key) && keys.IsKeyDown(key) && !previousKeys.IsKeyDown(key);

    /// <summary>Le joueur peut changer de skin dans le menu du jeu : on le signale aux autres.</summary>
    private void TrackLocalSkin()
    {
        if (++skinCheckTick % 60 != 0 || GameAccess.Game == null)
        {
            return;
        }
        int skin = SkinCache.LocalSkinId(GameAccess.Game);
        if (skin != localSkin)
        {
            localSkin = skin;
            if (IsReady)
            {
                Send(w => Msg.SkinChanged(w, null, skin), DeliveryMethod.ReliableOrdered);
            }
        }
    }

    private void AnnounceConnection()
    {
        bool ready = IsReady;
        if (ready && !wasReady)
        {
            int others = Players.Count;
            Notifications.Good(Tr.Connected(serverAddress, others, ModConfig.Current.MenuKey));
            lastDisconnectReason = "";
        }
        wasReady = ready;
    }

    private void AnnounceRound(RoundState.Snapshot r)
    {
        var key = (r.Info.RoundId, r.Info.Phase);
        if (!IsReady || !r.IsRound || key == notifiedRound)
        {
            return;
        }
        notifiedRound = key;
        if (r.Info.Phase == RoundPhase.Playing)
        {
            Notifications.Info(Tr.RoundStarted(r.Info.RoundId, GameAccess.DescribeMap(r.Info.MapKey)));
        }
        else if (r.Info.Phase == RoundPhase.Results)
        {
            var winner = r.Leaderboard.FirstOrDefault();
            Notifications.Show(winner.Name == null
                ? Tr.RoundEndNoTimes
                : Tr.RoundEndWinner(winner.Name, Msg.FormatTime(winner.BestMs)), new Microsoft.Xna.Framework.Color(255, 210, 90));
        }
    }

    // ---------- Actions (menu, touches) ----------

    /// <summary>(Re)connexion avec l'adresse et le pseudo de la configuration.</summary>
    public void Connect()
    {
        userDisconnected = false;
        if (server != null)
        {
            DropConnection("reconnexion");
        }
        lastConnectAttempt = double.NegativeInfinity;
        lastDisconnectReason = "";
        EnsureConnected();
    }

    public void Disconnect()
    {
        userDisconnected = true;
        if (server != null)
        {
            DropConnection("déconnexion manuelle");
            Notifications.Info(Tr.DisconnectedByUser);
        }
    }

    private void DropConnection(string why)
    {
        NetPeer? peer = server;
        server = null; // l'événement de déconnexion de ce pair sera ignoré
        peer?.Disconnect();
        ModLog.Info($"Connexion fermée ({why})");
        ResetSessionState();
    }

    private void ResetSessionState()
    {
        LocalId = null;
        Players.Clear();
        Round.Reset();
        loadedRoundId = 0;
        notifiedRound = default;
        if (IsAdmin)
        {
            IsAdmin = false;
            AdminVersion++;
        }
    }

    public void RejoinRound()
    {
        var r = Round.Current;
        if (!IsReady || r.Info.Phase == RoundPhase.Free)
        {
            Notifications.Warn(Tr.NoRound);
        }
        else if (IsOnRoundMap)
        {
            Notifications.Info(Tr.AlreadyOnRoundMap);
        }
        else
        {
            LoadRoundMap(r.Info.MapKey);
        }
    }

    private static void LoadRoundMap(string key)
    {
        // Format : "o:<id>" ou "o:<id>:<version>" (maps officielles uniquement pour l'instant).
        string[] parts = key.Split(':');
        if (parts.Length >= 2 && parts[0] == "o" && uint.TryParse(parts[1], out uint id))
        {
            uint? version = parts.Length >= 3 && uint.TryParse(parts[2], out uint v) ? v : null;
            try
            {
                if (!GameAccess.StartOfficialLevel(id, version, quickRetry: false))
                {
                    Notifications.Warn(Tr.MapLoadFailedVersion(key));
                }
            }
            catch (Exception e)
            {
                ModLog.Error($"Impossible de charger la map {key}", e);
                Notifications.Warn(Tr.MapLoadFailed(key));
            }
        }
        else
        {
            ModLog.Warn($"Map de round non supportée : {key}");
            Notifications.Warn(Tr.MapNotSupported(key));
        }
    }

    // ---------- Temps ----------

    private (int RoundId, SE.LevelTimer Timer, SE.TimeDiffOnCheckpoints? Display, bool SendToServer)? pendingFinish;

    /// <summary>Arrivée franchie : le temps sera lu (et envoyé si besoin) à la fin du tick, chrono alors définitif.</summary>
    public void QueueFinish(int roundId, SE.LevelTimer timer, SE.TimeDiffOnCheckpoints? display, bool sendToServer) =>
        pendingFinish = (roundId, timer, display, sendToServer);

    private void FlushPendingFinish()
    {
        if (pendingFinish is not { } finish)
        {
            return;
        }
        pendingFinish = null;
        uint ms = finish.Timer.Counter.MS;
        int? delta = SplitTracker.Finished(mapKey, finish.RoundId, finish.Timer, finish.Display);
        if (!finish.SendToServer)
        {
            return; // hors round : affiché par les intermédiaires uniquement
        }
        ModLog.Info($"Arrivée en {Msg.FormatTime(ms)} (round {finish.RoundId}) → envoi au serveur");
        if (delta is not int d || d < 0)
        {
            Notifications.Good(Tr.NewBest(Msg.FormatTime(ms)));
        }
        else
        {
            Notifications.Info(Tr.TimeVsBest(Msg.FormatTime(ms), Msg.FormatTime((uint)(ms - d)), d / 1000f));
        }
        SendFinish(finish.RoundId, ms);
    }

    public void SendChat(string text)
    {
        if (IsReady)
        {
            Send(w => Msg.ChatToServer(w, text), DeliveryMethod.ReliableOrdered);
        }
    }

    public void SendFinish(int roundId, uint timeMs)
    {
        if (IsReady)
        {
            Send(w => Msg.Finish(w, roundId, timeMs), DeliveryMethod.ReliableOrdered);
        }
    }

    // ---------- Connexion ----------

    /// <summary>À appeler régulièrement depuis le thread du jeu : (re)connecte si nécessaire.</summary>
    public void EnsureConnected()
    {
        if (!ModConfig.Current.AutoConnect && lastConnectAttempt == double.NegativeInfinity && !userDisconnected)
        {
            // AutoConnect = false : on attend que le joueur se connecte depuis le menu.
            userDisconnected = true;
        }
        if (userDisconnected || server != null || NetClock.Now - lastConnectAttempt < ReconnectDelaySeconds)
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
            if (GameAccess.Game != null)
            {
                localSkin = SkinCache.LocalSkinId(GameAccess.Game);
            }
            var (host, port) = ModConfig.Current.ParseServer();
            serverAddress = $"{host}:{port}";
            ModLog.Info($"Connexion à {serverAddress} en tant que « {playerName} »…");
            server = net.Connect(host, port, ProtocolInfo.ConnectionData(ModConfig.Current.ServerPassword));
        }
        catch (Exception e)
        {
            ModLog.Error("Échec de la tentative de connexion", e);
            NotifyDisconnect(Tr.CannotConnect(ModConfig.Current.Server, e.GetType().Name));
            server = null;
        }
    }

    private void NotifyDisconnect(string reason)
    {
        if (reason != lastDisconnectReason)
        {
            lastDisconnectReason = reason;
            Notifications.Warn(reason);
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
            return "Player";
        }
    }

    // ---------- Événements LiteNetLib (thread réseau) ----------

    public void OnPeerConnected(NetPeer peer)
    {
        ModLog.Info($"Connecté au serveur {peer}");
        server = peer; // au cas où l'événement arrive avant le retour de Connect()
        int skin = localSkin;
        Send(w => Msg.Hello(w, playerName, skin), DeliveryMethod.ReliableOrdered);
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!ReferenceEquals(peer, server))
        {
            return; // ancienne connexion fermée volontairement (Connect/Disconnect depuis le menu)
        }
        // Code envoyé par le serveur (refus, kick…) : à lire une seule fois, la lecture consomme les octets.
        string code = info.AdditionalData.AvailableBytes > 0 ? info.AdditionalData.GetString() : "";
        string reason = code != "" ? TranslateReasonCode(code) : info.Reason switch
        {
            DisconnectReason.ConnectionFailed => Tr.ServerUnreachable(serverAddress),
            DisconnectReason.Timeout => Tr.ConnectionLost,
            DisconnectReason.RemoteConnectionClose => Tr.ServerClosed,
            DisconnectReason.ConnectionRejected => Tr.ConnectionRejected,
            _ => info.Reason.ToString(),
        };
        if (code is ServerReasons.BadPassword or ServerReasons.Kicked)
        {
            // Réessayer ne servirait à rien : on attend une action du joueur (menu F8).
            userDisconnected = true;
            ModLog.Warn($"Déconnecté du serveur : {reason}");
            NotifyDisconnect(reason);
        }
        else
        {
            ModLog.Warn($"Déconnecté du serveur : {reason}. Nouvelle tentative dans {ReconnectDelaySeconds} s.");
            NotifyDisconnect(Tr.RetryingEvery(reason, (int)ReconnectDelaySeconds));
        }
        server = null;
        ResetSessionState();
    }

    /// <summary>Le serveur envoie des codes (SERVER_FULL, VERSION_MISMATCH:4, KICKED…) que le client traduit.</summary>
    private static string TranslateReasonCode(string code) => code switch
    {
        ServerReasons.Full => Tr.ServerFull,
        ServerReasons.BadPassword => Tr.BadPassword,
        ServerReasons.Kicked => Tr.Kicked,
        _ when code.StartsWith(ServerReasons.VersionMismatch + ":") => Tr.VersionMismatch(code[(ServerReasons.VersionMismatch.Length + 1)..]),
        _ => code, // serveur plus ancien : message en clair
    };

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (!ReferenceEquals(peer, server))
        {
            return;
        }
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
                        AddPlayer(Msg.GetPlayerInfo(reader), notify: false);
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
                    AddPlayer(Msg.GetPlayerInfo(reader), notify: true);
                    break;
                case MessageType.PlayerLeft:
                {
                    int id = reader.GetInt();
                    if (Players.TryRemove(id, out RemotePlayer? p))
                    {
                        ModLog.Info($"{p.Name} (#{id}) est parti");
                        Notifications.Info(Tr.PlayerLeft(p.Name));
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
                case MessageType.SkinChanged:
                {
                    int id = reader.GetInt();
                    int skin = reader.GetInt();
                    if (Players.TryGetValue(id, out RemotePlayer? p))
                    {
                        p.Skin = skin;
                    }
                    break;
                }
                case MessageType.RoundInfo:
                {
                    RoundInfo info = Msg.GetRound(reader);
                    Round.SetInfo(info);
                    ModLog.Info($"Round {info.RoundId} : {info.Phase} sur {(info.MapKey == "" ? "-" : info.MapKey)}, {info.RemainingSeconds:0} s");
                    break;
                }
                case MessageType.Leaderboard:
                {
                    var (roundId, entries) = Msg.GetLeaderboard(reader);
                    Round.SetLeaderboard(roundId, entries);
                    break;
                }
                case MessageType.AdminStatus:
                {
                    var (isAdmin, maps) = Msg.GetAdminStatus(reader);
                    if (!isAdmin)
                    {
                        Notifications.Warn(Tr.AdminLoginFailed);
                    }
                    IsAdmin = isAdmin;
                    AdminMaps = maps;
                    AdminVersion++;
                    break;
                }
                case MessageType.ServerEvent:
                {
                    string code = reader.GetString(64);
                    string arg = reader.GetString(128);
                    Notifications.Show(Tr.ServerEvent(code, arg), new Microsoft.Xna.Framework.Color(255, 210, 90));
                    break;
                }
                case MessageType.Chat:
                {
                    reader.GetInt(); // id de l'émetteur (réservé : mute, couleurs…)
                    string name = reader.GetString(ProtocolInfo.MaxNameLength * 4);
                    string text = reader.GetString(ProtocolInfo.MaxChatLength * 4);
                    ChatLog.AddPlayer(name, text);
                    break;
                }
                case MessageType.CommandReply:
                {
                    string code = reader.GetString(64);
                    string arg = reader.GetString(256);
                    Notifications.Show(Tr.CommandReply(code, arg), new Microsoft.Xna.Framework.Color(255, 170, 80));
                    break;
                }
                case MessageType.MatchStandings:
                    Round.Standings = Msg.GetStandings(reader);
                    break;
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

    private void AddPlayer(PlayerInfo info, bool notify)
    {
        Players[info.Id] = new RemotePlayer(info);
        ModLog.Info($"{info.Name} (#{info.Id}) a rejoint{(info.MapKey == "" ? "" : $", sur {info.MapKey}")}");
        if (notify)
        {
            Notifications.Info(Tr.PlayerJoined(info.Name));
        }
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
