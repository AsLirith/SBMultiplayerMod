using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SBMultiplayer.Protocol;
using Steamworks;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Steam P2P: joining a friend's hosted game without opening any port. The game keeps speaking its usual
/// protocol (LiteNetLib/UDP); this class only tunnels the datagrams through Steam (SteamNetworkingMessages,
/// which handles NAT traversal and Steam's relays):
/// - host: one local UDP socket per remote friend, talking to the hosted server on 127.0.0.1;
/// - joiner: a local UDP "proxy" on 127.0.0.1 that the mod's client connects to.
/// Hosts advertise themselves through Steam rich presence; friends see them in F8 > Join a friend.
/// </summary>
internal static class SteamP2P
{
    private const int Channel = 0;
    /// <summary>Unreliable (LiteNetLib does its own reliability), no Nagle delay, restart broken sessions.</summary>
    private const int SendFlags = Constants.k_nSteamNetworkingSend_Unreliable | Constants.k_nSteamNetworkingSend_NoNagle | 32;
    private const string PresenceKey = "sbmulti";
    private const double IdlePeerSeconds = 30;

    private sealed class PeerTunnel(UdpClient socket)
    {
        public UdpClient Socket { get; } = socket;
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    }

    public readonly record struct HostingFriend(ulong SteamId, string Name, bool SameVersion);

    private static readonly object Gate = new();
    private static readonly Dictionary<ulong, PeerTunnel> Peers = new();
    private static readonly IntPtr[] Incoming = new IntPtr[64];

    private static Callback<SteamNetworkingMessagesSessionRequest_t>? sessionRequest;
    private static Thread? pump;
    private static volatile bool hosting;
    private static IPEndPoint? hostedServer;
    private static volatile bool joining;
    private static ulong joinTarget;
    private static UdpClient? proxy;
    private static IPEndPoint? localClient;

    public static bool IsJoining => joining;

    public static bool IsHosting => hosting;

    /// <summary>Steam is running and its networking API usable.</summary>
    public static bool Available
    {
        get
        {
            try
            {
                return SteamUser.GetSteamID().IsValid();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static string PresenceValue => $"host:{ProtocolInfo.Version}";

    // ---------- Host ----------

    /// <summary>Makes the hosted game reachable by Steam friends. Call from the game thread.</summary>
    public static void StartHost(int hostedServerPort)
    {
        EnsureCallbacks();
        hostedServer = new IPEndPoint(IPAddress.Loopback, hostedServerPort);
        hosting = true;
        SteamNetworkingUtils.InitRelayNetworkAccess();
        SteamFriends.SetRichPresence(PresenceKey, PresenceValue);
        StartPump();
        ModLog.Info($"Steam P2P: hosting, visible to friends ({SteamFriends.GetPersonaName()})");
    }

    public static void StopHost()
    {
        if (!hosting)
        {
            return;
        }
        hosting = false;
        lock (Gate)
        {
            foreach (var (id, tunnel) in Peers)
            {
                tunnel.Socket.Dispose();
                CloseSession(id);
            }
            Peers.Clear();
        }
        try
        {
            SteamFriends.SetRichPresence(PresenceKey, "");
        }
        catch (Exception)
        {
            // Steam already shut down (game closing)
        }
        ModLog.Info("Steam P2P: hosting stopped");
    }

    /// <summary>A friend opens a session with us: accepted while we host a game.</summary>
    private static void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t request)
    {
        SteamNetworkingIdentity identity = request.m_identityRemote;
        if (hosting)
        {
            SteamNetworkingMessages.AcceptSessionWithUser(ref identity);
            ModLog.Info($"Steam P2P: session accepted from {identity.GetSteamID64()}");
        }
    }

    // ---------- Joiner ----------

    /// <summary>Opens a local proxy towards a friend's game. Returns the local port to connect to.</summary>
    public static int StartJoin(ulong hostSteamId)
    {
        StopJoin();
        EnsureCallbacks();
        SteamNetworkingUtils.InitRelayNetworkAccess();
        var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        lock (Gate)
        {
            proxy = socket;
            localClient = null;
            joinTarget = hostSteamId;
            joining = true;
        }
        StartPump();
        int port = ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
        ModLog.Info($"Steam P2P: joining {hostSteamId} through 127.0.0.1:{port}");
        return port;
    }

    public static void StopJoin()
    {
        if (!joining)
        {
            return;
        }
        lock (Gate)
        {
            joining = false;
            proxy?.Dispose();
            proxy = null;
            CloseSession(joinTarget);
        }
        ModLog.Info("Steam P2P: left the friend's game");
    }

    /// <summary>Steam friends currently playing STUNTBOOST and hosting an SBMultiplayer game.</summary>
    public static List<HostingFriend> FriendsHosting()
    {
        var result = new List<HostingFriend>();
        try
        {
            AppId_t app = SteamUtils.GetAppID();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                CSteamID friend = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                if (!SteamFriends.GetFriendGamePlayed(friend, out FriendGameInfo_t game) || game.m_gameID.AppID() != app)
                {
                    continue;
                }
                SteamFriends.RequestFriendRichPresence(friend); // refreshed for the next look
                string presence = SteamFriends.GetFriendRichPresence(friend, PresenceKey);
                if (presence.StartsWith("host:", StringComparison.Ordinal))
                {
                    result.Add(new HostingFriend(friend.m_SteamID, SteamFriends.GetFriendPersonaName(friend), presence == PresenceValue));
                }
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"Steam P2P: could not list friends ({e.Message})");
        }
        return result.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------- Tunnel ----------

    private static void EnsureCallbacks()
    {
        // Steam callbacks are dispatched by the game's own SteamAPI.RunCallbacks (game thread).
        sessionRequest ??= Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
    }

    private static void StartPump()
    {
        if (pump is { IsAlive: true })
        {
            return;
        }
        pump = new Thread(Pump) { IsBackground = true, Name = "SBMultiplayer Steam P2P" };
        pump.Start();
    }

    private static void Pump()
    {
        while (hosting || joining)
        {
            try
            {
                PumpOnce();
            }
            catch (Exception e)
            {
                ModLog.Error("Steam P2P tunnel error", e);
                Thread.Sleep(100);
            }
            Thread.Sleep(1);
        }
    }

    private static void PumpOnce()
    {
        // Steam → local sockets.
        int count = SteamNetworkingMessages.ReceiveMessagesOnChannel(Channel, Incoming, Incoming.Length);
        for (int i = 0; i < count; i++)
        {
            SteamNetworkingMessage_t message = SteamNetworkingMessage_t.FromIntPtr(Incoming[i]);
            var data = new byte[message.m_cbSize];
            Marshal.Copy(message.m_pData, data, 0, data.Length);
            ulong from = message.m_identityPeer.GetSteamID64();
            SteamNetworkingMessage_t.Release(Incoming[i]);
            Deliver(from, data);
        }

        // Local sockets → Steam.
        lock (Gate)
        {
            if (hosting)
            {
                foreach (var (id, tunnel) in Peers)
                {
                    while (tunnel.Socket.Available > 0)
                    {
                        IPEndPoint? any = null;
                        byte[] data = tunnel.Socket.Receive(ref any);
                        SendTo(id, data);
                    }
                }
                foreach (ulong idle in Peers.Where(p => (DateTime.UtcNow - p.Value.LastActivity).TotalSeconds > IdlePeerSeconds).Select(p => p.Key).ToList())
                {
                    Peers[idle].Socket.Dispose();
                    Peers.Remove(idle);
                    CloseSession(idle);
                }
            }
            if (joining && proxy != null)
            {
                while (proxy.Available > 0)
                {
                    IPEndPoint? from = null;
                    byte[] data = proxy.Receive(ref from);
                    localClient = from; // the mod's client, on 127.0.0.1
                    SendTo(joinTarget, data);
                }
            }
        }
    }

    private static void Deliver(ulong from, byte[] data)
    {
        lock (Gate)
        {
            if (hosting && hostedServer != null)
            {
                if (!Peers.TryGetValue(from, out PeerTunnel? tunnel))
                {
                    tunnel = new PeerTunnel(new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)));
                    Peers[from] = tunnel;
                    ModLog.Info($"Steam P2P: new player {from}");
                }
                tunnel.LastActivity = DateTime.UtcNow;
                tunnel.Socket.Send(data, data.Length, hostedServer);
            }
            else if (joining && from == joinTarget && proxy != null && localClient != null)
            {
                proxy.Send(data, data.Length, localClient);
            }
        }
    }

    private static void SendTo(ulong steamId, byte[] data)
    {
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID64(steamId);
        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            SteamNetworkingMessages.SendMessageToUser(ref identity, handle.AddrOfPinnedObject(), (uint)data.Length, SendFlags, Channel);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void CloseSession(ulong steamId)
    {
        try
        {
            var identity = new SteamNetworkingIdentity();
            identity.SetSteamID64(steamId);
            SteamNetworkingMessages.CloseSessionWithUser(ref identity);
        }
        catch (Exception)
        {
            // Steam already shut down
        }
    }
}
