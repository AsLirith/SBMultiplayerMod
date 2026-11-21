using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Client.UI;

namespace SBMultiplayer.Client.Rooms;

/// <summary>Public games: Steam friends hosting a game, then the public games listed on Steam. START joins.</summary>
internal sealed class PublicGamesSource : ListSource
{
    private const double RefreshSeconds = 10;
    private double nextRefresh;

    private sealed record Game(ulong HostId, string Name, SteamLobby.PublicGame? Listing, bool Friend, bool SameVersion);

    private List<Game> games = new();

    public override string RoomName => Tr.ListPublicGames;

    public override string EmptyText =>
        !SteamP2P.Available ? Tr.SteamUnavailable
        : SteamLobby.Searching ? Tr.PublicGamesSearching
        : Tr.NoSteamGames;

    public override IReadOnlyList<ListEntry> Entries =>
        games.Select(g => new ListEntry(g.HostId.ToString(), g.Friend ? $"{g.Name}  ({Tr.FriendTag})" : g.Name, g.Name)).ToList();

    public override void Open()
    {
        nextRefresh = 0;
        Tick();
    }

    public override void Tick()
    {
        if (SteamP2P.Available && NetClock.Now >= nextRefresh)
        {
            nextRefresh = NetClock.Now + RefreshSeconds;
            SteamLobby.Refresh();
        }
        games = Read();
    }

    /// <summary>Friends first (with their public listing's details if they have one), then the other public games.</summary>
    private static List<Game> Read()
    {
        var result = new List<Game>();
        if (!SteamP2P.Available)
        {
            return result;
        }
        IReadOnlyList<SteamLobby.PublicGame> listed = SteamLobby.Games;
        var friends = new HashSet<ulong>();
        foreach (SteamP2P.HostingFriend f in SteamP2P.FriendsHosting())
        {
            friends.Add(f.SteamId);
            SteamLobby.PublicGame? listing = listed.Any(g => g.HostId == f.SteamId) ? listed.First(g => g.HostId == f.SteamId) : null;
            result.Add(new Game(f.SteamId, listing?.Name ?? f.Name, listing, true, f.SameVersion));
        }
        result.AddRange(listed.Where(g => !friends.Contains(g.HostId)).Select(g => new Game(g.HostId, g.Name, g, false, g.SameVersion)));
        return result;
    }

    private Game? Find(ListEntry e) => games.FirstOrDefault(g => g.HostId.ToString() == e.Id);

    public override int DetailsSignature(ListEntry selected) => Find(selected)?.GetHashCode() ?? 0;

    public override Element Details(ListEntry selected)
    {
        if (Find(selected) is not { } g)
        {
            return ListScreen.DetailText("");
        }
        var rows = new List<(string, string)>();
        if (g.Listing is { } l)
        {
            rows.Add((Tr.DetailMode, Tr.ModeName(l.Mode)));
            rows.Add((Tr.DetailMap, l.MapKey == "" ? "-" : GameAccess.DescribeMap(l.MapKey)));
            rows.Add((Tr.DetailPlayers, $"{l.Players}/{l.MaxPlayers}"));
            rows.Add((Tr.DetailPassword, l.HasPassword ? Tr.Yes : Tr.No));
        }
        rows.Add((Tr.DetailHost, g.Friend ? Tr.SteamFriendHost : Tr.SteamPublicHost));
        if (!g.SameVersion)
        {
            rows.Add((Tr.DetailVersion, g.Listing is { } v ? Tr.OtherVersionWith(v.ModVersion) : Tr.OtherVersion));
        }
        return ListScreen.DetailRows(rows.ToArray());
    }

    public override string? StartLabel(ListEntry selected) => Find(selected) == null ? null : Tr.JoinButton;

    public override void Start(ListEntry selected)
    {
        if (Find(selected) is not { } g)
        {
            return;
        }
        if (g.Listing?.HasPassword == true)
        {
            MultiplayerMenu.AskPassword(Tr.PasswordNeeded(g.Name), password => MultiplayerClient.Instance.JoinFriend(g.HostId, g.Name, password));
        }
        else
        {
            MultiplayerClient.Instance.JoinFriend(g.HostId, g.Name); // an unlisted friend's game may refuse, and then the password is asked
        }
    }
}

/// <summary>Public SBLB lobbies (website rooms without a password), plus a code field for private ones. START joins.</summary>
internal sealed class SblbLobbiesSource : ListSource
{
    public override string RoomName => Tr.ListSblbLobbies;

    public override string EmptyText =>
        !SblbLobbies.Available ? Tr.SblbLobbiesUnavailable
        : SblbLobbies.Searching && SblbLobbies.Lobbies.Count == 0 ? Tr.PublicGamesSearching
        : Tr.NoSblbLobbies;

    public override IReadOnlyList<ListEntry> Entries =>
        SblbLobbies.Lobbies.Select(l => new ListEntry(l.Code, $"{l.Code}  -  {ModeText(l)}", l.Code)).ToList();

    private static string ModeText(SblbLobbies.Lobby l) => l.Mode is { } m ? Tr.ModeName(m) : l.ModeText;

    public override void Open() => SblbLobbies.Refresh();

    public override void Tick() => SblbLobbies.Tick();

    private static SblbLobbies.Lobby? Find(ListEntry e) =>
        SblbLobbies.Lobbies.Any(l => l.Code == e.Id) ? SblbLobbies.Lobbies.First(l => l.Code == e.Id) : null;

    public override int DetailsSignature(ListEntry selected) => Find(selected)?.GetHashCode() ?? 0;

    public override Element Details(ListEntry selected)
    {
        if (Find(selected) is not { } l)
        {
            return ListScreen.DetailText("");
        }
        return ListScreen.DetailRows(
            (Tr.DetailCode, l.Code),
            (Tr.DetailMode, ModeText(l)),
            (Tr.DetailMap, l.MapKey == "" ? "-" : GameAccess.DescribeMap(l.MapKey)),
            (Tr.DetailPlayers, $"{l.Players}/{l.MaxPlayers}"));
    }

    public override string? StartLabel(ListEntry selected) => Find(selected) == null ? null : Tr.JoinButton;

    public override void Start(ListEntry selected) => Join(selected.Id, typed: false);

    /// <param name="typed">A code the player typed: it may be a tournament's room, which the website is asked about first.</param>
    private static void Join(string code, bool typed)
    {
        code = code.Trim();
        if (code == "")
        {
            Notifications.Warn(Tr.NoRoomChosen);
            return;
        }
        if (typed)
        {
            MultiplayerClient.Instance.JoinTypedCode(code);
            return;
        }
        MultiplayerClient.Instance.JoinRoom(code, null); // a private lobby refuses, and then the password is asked
    }

    /// <summary>Like the game's "enter your own challenge" row: a code field, for private lobbies (not listed).</summary>
    public override Element? Footer()
    {
        TextInput code = PopupMenu.Input(ModConfig.Current.LastRoomCode, 16, 1100f);
        code.OnEnter += () =>
        {
            Join(code.Text, typed: true);
            return true;
        };
        return new PanelStack(0f).Add(
            new Panel(-1f, 128f, GameScreens.PanelSub).Add(code).SetStyle(GameScreens.Ui.TextSmallStyle),
            new Panel(-1f, 96f).Add(new Label(Tr.LobbyCodeHint).SetStyle(GameScreens.Ui.TextSmallStyle)));
    }
}
