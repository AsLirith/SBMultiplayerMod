using SBMultiplayer.Client.UI;
using SBMultiplayer.Protocol;
using Steamworks;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Public game list, through Steam lobbies (no server of our own needed). A host who ticks "List my game
/// publicly" creates a public lobby that only carries the game's description (name, mode, map, players);
/// nobody actually joins the lobby: players connect to the host with Steam P2P (<see cref="SteamP2P"/>).
/// Game thread only (Steam callbacks are dispatched by the game's own SteamAPI.RunCallbacks).
/// </summary>
internal static class SteamLobby
{
    /// <summary>Lobby data key that marks SBMultiplayer lobbies (and its value).</summary>
    private const string MarkerKey = "sbmulti";
    private const string MarkerValue = "1";
    private const double DataRefreshSeconds = 5;
    private const double RetryAfterFailureSeconds = 60;

    public readonly record struct PublicGame(ulong HostId, string Name, GameMode Mode, string MapKey, int Players, int MaxPlayers,
        bool HasPassword, bool SameVersion, string ModVersion);

    private static CallResult<LobbyCreated_t>? createResult;
    private static CallResult<LobbyMatchList_t>? listResult;
    private static CSteamID lobby = CSteamID.Nil;
    private static bool creating;
    private static double nextDataAt;
    private static double nextCreateAt;

    /// <summary>Last list of public games (other players' ones).</summary>
    public static IReadOnlyList<PublicGame> Games { get; private set; } = Array.Empty<PublicGame>();

    /// <summary>Incremented when a new list arrives (the menu rebuilds itself).</summary>
    public static int ListVersion { get; private set; }

    public static bool Searching { get; private set; }

    /// <summary>Our game is listed right now.</summary>
    public static bool IsListed => lobby != CSteamID.Nil;

    private static bool Wanted => ModConfig.Current.PublicGame && HostedServer.IsRunning && SteamP2P.IsHosting;

    /// <summary>Called every global tick: creates, updates or removes our public lobby.</summary>
    public static void Tick()
    {
        try
        {
            double now = NetClock.Now;
            if (Wanted && lobby == CSteamID.Nil && !creating && now >= nextCreateAt)
            {
                creating = true;
                createResult ??= CallResult<LobbyCreated_t>.Create(OnCreated);
                // Two slots: only the host is a member, players never join the lobby itself.
                createResult.Set(SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypePublic, 2));
            }
            else if (!Wanted && lobby != CSteamID.Nil)
            {
                Leave();
            }
            if (lobby != CSteamID.Nil && now >= nextDataAt)
            {
                nextDataAt = now + DataRefreshSeconds;
                UpdateData();
            }
        }
        catch (Exception e)
        {
            creating = false;
            nextCreateAt = NetClock.Now + RetryAfterFailureSeconds;
            ModLog.Error("Steam lobby: error", e);
        }
    }

    private static void OnCreated(LobbyCreated_t result, bool ioFailure)
    {
        creating = false;
        if (ioFailure || result.m_eResult != EResult.k_EResultOK)
        {
            nextCreateAt = NetClock.Now + RetryAfterFailureSeconds;
            ModLog.Warn($"Steam lobby: could not list the game publicly ({(ioFailure ? "I/O failure" : result.m_eResult.ToString())})");
            Notifications.Warn(Tr.PublicListingFailed);
            return;
        }
        lobby = new CSteamID(result.m_ulSteamIDLobby);
        if (!Wanted)
        {
            Leave();
            return;
        }
        UpdateData();
        nextDataAt = NetClock.Now + DataRefreshSeconds;
        ModLog.Info($"Steam lobby: game listed publicly (lobby {lobby.m_SteamID})");
        Notifications.Good(Tr.PublicListingOn);
    }

    private static void UpdateData()
    {
        SBMultiplayer.Server.GameServer.ServerSummary? s = HostedServer.Summary; // (Steamworks also has a GameServer class)
        if (s == null)
        {
            return;
        }
        Set(MarkerKey, MarkerValue);
        Set("proto", ProtocolInfo.Version.ToString());
        Set("version", MultiplayerMenu.Version);
        Set("host", SteamUser.GetSteamID().m_SteamID.ToString());
        Set("name", s.Name);
        Set("mode", s.Mode.ToString());
        Set("map", s.MapKey);
        Set("players", s.Players.ToString());
        Set("max", s.MaxPlayers.ToString());
        Set("pw", s.HasPassword ? "1" : "0");
    }

    private static void Set(string key, string value) => SteamMatchmaking.SetLobbyData(lobby, key, value);

    private static void Leave()
    {
        try
        {
            SteamMatchmaking.LeaveLobby(lobby);
        }
        catch (Exception)
        {
            // Steam already shut down (game closing)
        }
        lobby = CSteamID.Nil;
        ModLog.Info("Steam lobby: game no longer listed");
    }

    /// <summary>Called when the game closes.</summary>
    public static void Shutdown()
    {
        if (lobby != CSteamID.Nil)
        {
            Leave();
        }
    }

    /// <summary>Asks Steam for the public SBMultiplayer games (result in <see cref="Games"/>).</summary>
    public static void Refresh()
    {
        if (Searching || !SteamP2P.Available)
        {
            return;
        }
        try
        {
            Searching = true;
            ListVersion++;
            listResult ??= CallResult<LobbyMatchList_t>.Create(OnList);
            SteamMatchmaking.AddRequestLobbyListStringFilter(MarkerKey, MarkerValue, ELobbyComparison.k_ELobbyComparisonEqual);
            SteamMatchmaking.AddRequestLobbyListDistanceFilter(ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter(100);
            listResult.Set(SteamMatchmaking.RequestLobbyList());
        }
        catch (Exception e)
        {
            Searching = false;
            ModLog.Error("Steam lobby: could not search for games", e);
        }
    }

    private static void OnList(LobbyMatchList_t result, bool ioFailure)
    {
        Searching = false;
        var games = new List<PublicGame>();
        if (!ioFailure)
        {
            ulong me = SteamUser.GetSteamID().m_SteamID;
            for (int i = 0; i < result.m_nLobbiesMatching; i++)
            {
                CSteamID id = SteamMatchmaking.GetLobbyByIndex(i);
                string Get(string key) => SteamMatchmaking.GetLobbyData(id, key);
                if (!ulong.TryParse(Get("host"), out ulong host) || host == me)
                {
                    continue;
                }
                games.Add(new PublicGame(host, Get("name"),
                    Enum.TryParse(Get("mode"), out GameMode mode) ? mode : GameMode.Free,
                    Get("map"),
                    int.TryParse(Get("players"), out int players) ? players : 0,
                    int.TryParse(Get("max"), out int max) ? max : 0,
                    Get("pw") == "1",
                    Get("proto") == ProtocolInfo.Version.ToString(),
                    Get("version")));
            }
        }
        Games = games.OrderByDescending(g => g.SameVersion).ThenByDescending(g => g.Players).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        ListVersion++;
        ModLog.Info($"Steam lobby: {Games.Count} public game(s) found");
    }
}
