using BytingLib;
using BytingLib.UI;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// F8 menu. The main screen is a hub: status, the actions of the current game (when connected), then
/// "Join a game", "Host a game" and "Settings". It only shows what makes sense right now and rebuilds itself
/// when the connection state changes. Settings apply immediately and are saved in SBMultiplayer.json.
/// </summary>
internal static class MultiplayerMenu
{
    /// <summary>Steam games visible at once in "Join a game" (the list scrolls beyond that).</summary>
    private const int MaxSteamGames = 6;

    public static string Version => typeof(MultiplayerMenu).Assembly.GetName().Version?.ToString(3) ?? "?";

    private static MultiplayerClient Client => MultiplayerClient.Instance;

    public static void Open() => Show("main", BuildMain, HubState);

    // ---------- Main screen: hub ----------

    private static bool RoundJoinable =>
        Client.IsReady && Client.Round.Current.Info.Phase != RoundPhase.Free && !Client.IsOnRoundMap;

    /// <summary>Something to stop: a connection, an attempt or the automatic retries (a hosted game is stopped from its own screen).</summary>
    private static bool CanDisconnect => !HostedServer.IsRunning && !Client.IsUserDisconnected && Client.HasTarget;

    /// <summary>Everything the hub's content depends on: it's rebuilt when this changes.</summary>
    private static int HubState() =>
        (Client.IsReady ? 1 : 0)
        | (Client.IsSpectating ? 2 : 0)
        | (HostedServer.IsRunning ? 4 : 0)
        | (CanDisconnect ? 8 : 0)
        | (RoundJoinable ? 16 : 0)
        | (Client.IsReady && Client.RoomCode != null ? 32 : 0)
        | (UpdateCheck.NewerVersion != null ? 64 : 0)
        | (Tr.French ? 128 : 0);

    private static Element BuildMain(Action close, Screen screen)
    {
        ModConfig cfg = ModConfig.Current;
        var status = new Label(StatusText(), MenuWidth);
        int tick = 0;
        screen.Every(() =>
        {
            if (++tick % 15 == 0 && StatusText() is string text && status.Text != text)
            {
                status.Text = text;
            }
        });

        return Stack(
            Title(Tr.MenuTitle(Version)),
            UpdateCheck.NewerVersion is string newer ? Text(Tr.UpdateAvailable(newer)) : null,
            status,
            Client.IsReady && Client.RoomCode is string room ? Text(Tr.RoomCodeLine(room)) : null,
            // The current game.
            Client.IsReady
                ? Buttons(
                    RoundJoinable ? Button(Tr.JoinRound, () =>
                    {
                        CloseAll();
                        Client.RejoinRound();
                    }) : null,
                    Button(Client.IsSpectating ? Tr.PlayButton : Tr.SpectateButton, () =>
                    {
                        CloseAll();
                        Client.SetSpectating(!Client.IsSpectating);
                    }),
                    Button(Tr.AdminTitle, AdminMenu.Open))
                : null,
            Buttons(
                Button(Tr.JoinGameButton, OpenJoin),
                Button(HostedServer.IsRunning ? Tr.HostManageButton : Tr.HostSection, OpenHost),
                Button(Tr.SettingsSection, OpenSettings),
                CanDisconnect ? Button(Tr.Disconnect, Client.Disconnect) : null),
            Text(Tr.KeysHint(Tr.KeyName(cfg.MenuKey), Tr.KeyName(cfg.RejoinKey), Tr.KeyName(cfg.ChatKey), Tr.KeyName(cfg.ScoreboardKey))),
            Button(Tr.Close, close));
    }

    // ---------- Join a game: Steam games (friends, public), SBLB lobby code, server address ----------

    /// <summary>Incremented by "Refresh": the Steam list is read again (friends included).</summary>
    private static int joinRefreshes;

    private sealed record SteamGame(ulong HostId, string Name, string Label, bool HasPassword);

    private static void OpenJoin()
    {
        if (SteamP2P.Available)
        {
            SteamLobby.Refresh();
        }
        SblbLobbies.Refresh();
        Show("join", BuildJoin);
    }

    /// <summary>Changes when the SBLB lobby list changes. Also asks the website again every few seconds while the screen is open.</summary>
    private static int SblbListSignature()
    {
        SblbLobbies.Tick();
        var hash = new HashCode();
        hash.Add(SblbLobbies.Available);
        hash.Add(SblbLobbies.Searching && SblbLobbies.Lobbies.Count == 0);
        foreach (SblbLobbies.Lobby l in SblbLobbies.Lobbies)
        {
            hash.Add(l.Code);
            hash.Add(l.Players);
            hash.Add(l.MapKey);
            hash.Add(l.ModeText);
        }
        return hash.ToHashCode();
    }

    /// <summary>The website's public lobbies (no password), or nothing when the website doesn't list them.</summary>
    private static Element BuildSblbList(Action closeJoin)
    {
        if (!SblbLobbies.Available)
        {
            return new PanelStack(0f); // empty: the live part keeps a place to come back to
        }
        IReadOnlyList<SblbLobbies.Lobby> lobbies = SblbLobbies.Lobbies;
        if (lobbies.Count == 0)
        {
            return Text(SblbLobbies.Searching ? Tr.PublicGamesSearching : Tr.NoSblbLobbies);
        }
        return new PanelStack(26f).Add(
            Text(Tr.SblbLobbiesHint),
            ScrollList(lobbies.Select(l => Button(DescribeLobby(l), () =>
            {
                closeJoin();
                Client.JoinRoom(l.Code, null); // listed lobbies have no password
            })).ToList(), MaxSteamGames));
    }

    /// <summary>"MAPLE-42 - Rounds - A5 Jump - 3/32".</summary>
    private static string DescribeLobby(SblbLobbies.Lobby l)
    {
        var parts = new List<string> { l.Code };
        string mode = l.Mode is GameMode m ? Tr.ModeName(m) : l.ModeText;
        if (mode != "")
        {
            parts.Add(mode);
        }
        if (l.MapKey != "")
        {
            parts.Add(GameAccess.DescribeMap(l.MapKey));
        }
        parts.Add($"{l.Players}/{l.MaxPlayers}");
        return string.Join(" - ", parts);
    }

    /// <summary>Changes when the list of public games really changes (not at every search), or on "Refresh".</summary>
    private static int SteamListSignature()
    {
        var hash = new HashCode();
        hash.Add(joinRefreshes);
        hash.Add(SteamLobby.Searching && SteamLobby.Games.Count == 0); // "Searching..." becomes "no game"
        foreach (SteamLobby.PublicGame g in SteamLobby.Games)
        {
            hash.Add(g.HostId);
            hash.Add(g.Players);
            hash.Add(g.MapKey);
            hash.Add(g.Mode);
        }
        return hash.ToHashCode();
    }

    /// <summary>Friends hosting a game first (with their public listing's details if they have one), then the other public games.</summary>
    private static List<SteamGame> SteamGames()
    {
        var games = new List<SteamGame>();
        var publicGames = SteamLobby.Games;
        var friendIds = new HashSet<ulong>();
        foreach (SteamP2P.HostingFriend f in SteamP2P.FriendsHosting())
        {
            friendIds.Add(f.SteamId);
            SteamLobby.PublicGame listed = publicGames.FirstOrDefault(g => g.HostId == f.SteamId);
            string label = listed.HostId != 0 ? DescribeGame(listed)
                : f.SameVersion ? f.Name
                : $"{f.Name} - {Tr.OtherVersion}";
            games.Add(new SteamGame(f.SteamId, f.Name, $"{label} - {Tr.FriendTag}", listed.HostId != 0 && listed.HasPassword));
        }
        games.AddRange(publicGames.Where(g => !friendIds.Contains(g.HostId))
            .Select(g => new SteamGame(g.HostId, g.Name, DescribeGame(g), g.HasPassword)));
        return games;
    }

    private static Element BuildJoin(Action close, Screen screen)
    {
        ModConfig cfg = ModConfig.Current;
        TextInput code = Input(cfg.LastRoomCode, 16, WidthBeside(Tr.JoinButton));
        TextInput server = Input(cfg.Server, 80, WidthBeside(Tr.Connect));

        void JoinLobby()
        {
            string c = code.Text.Trim();
            if (c == "")
            {
                Notifications.Warn(Tr.NoRoomChosen);
                return;
            }
            close();
            Client.JoinRoom(c, null); // a protected lobby refuses: the password is asked then
        }

        void ConnectTo(string address)
        {
            if (address.Trim() == "")
            {
                Notifications.Warn(Tr.NoServerAddress);
                return;
            }
            close();
            Client.ConnectTo(address);
        }

        code.OnEnter += () => { JoinLobby(); return true; };
        server.OnEnter += () => { ConnectTo(server.Text); return true; };

        var recent = cfg.RecentServers
            .Where(r => !r.Equals(cfg.Server.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(r => Button(r, () => ConnectTo(r)))
            .ToArray();
        return Stack(
            Title(Tr.JoinGameButton),
            // Only the Steam list is rebuilt when it changes: the fields below keep their text and cursor.
            SteamP2P.Available ? Live(screen, SteamListSignature, () => BuildSteamList(close)) : null,
            Live(screen, SblbListSignature, () => BuildSblbList(close)),
            Row(Tr.RoomCodeField, Line(Field(code), Button(Tr.JoinButton, JoinLobby))),
            Row(Tr.ServerField, Line(Field(server), Button(Tr.Connect, () => ConnectTo(server.Text)))),
            recent.Length > 0 ? Buttons(recent) : null,
            Buttons(
                Button(Tr.Refresh, () =>
                {
                    joinRefreshes++;
                    if (SteamP2P.Available)
                    {
                        SteamLobby.Refresh();
                    }
                    SblbLobbies.Refresh();
                }),
                Button(Tr.Back, close)));
    }

    private static Element BuildSteamList(Action closeJoin)
    {
        List<SteamGame> games = SteamGames();
        string line = games.Count > 0 ? Tr.SteamGamesHint : SteamLobby.Searching ? Tr.PublicGamesSearching : Tr.NoSteamGames;
        Element text = Text(line);
        return games.Count == 0 ? text : new PanelStack(26f).Add(
            text,
            ScrollList(games.Select(g => Button(g.Label, () => JoinSteamGame(g, closeJoin))).ToList(), MaxSteamGames));
    }

    private static void JoinSteamGame(SteamGame game, Action closeJoin)
    {
        void Join(string? password)
        {
            closeJoin();
            Client.JoinFriend(game.HostId, game.Name, password);
        }
        if (game.HasPassword)
        {
            AskPassword(Tr.PasswordNeeded(game.Name), Join);
        }
        else
        {
            Join(null); // an unlisted friend's game may still refuse: the password is asked then
        }
    }

    /// <summary>Asks for the password of a protected game (known from its listing, or after a refusal).</summary>
    public static void AskPassword(string message, Action<string> onOk) => Show("password", (close, _) =>
    {
        TextInput password = Input("", 64);
        void Ok()
        {
            string text = password.Text;
            close();
            onOk(text);
        }
        password.OnEnter += () => { Ok(); return true; };
        return Stack(
            Title(Tr.PasswordTitle),
            Text(message),
            Row(Tr.PasswordTitle, Field(password)),
            Buttons(Button(Tr.Connect, Ok), Button(Tr.Cancel, close)));
    });

    // ---------- Host a game: on this PC, or an SBLB lobby on the community website ----------

    private static void OpenHost() => Show("host", (close, _) => Stack(Title(Tr.HostSection), BuildHostRow(close), Button(Tr.Back, close)));

    private static Element BuildHostRow(Action close)
    {
        if (HostedServer.IsRunning)
        {
            var addresses = HostedServer.LocalAddresses().Select(a => $"{a}:{HostedServer.Port}").ToList();
            return Stack(
                Text(Tr.HostRunning(HostedServer.Port)),
                Text(Tr.HostAddresses(addresses.Count > 0 ? string.Join(", ", addresses) : "?", HostedServer.Port)),
                ModConfig.Current.SteamP2PHost && SteamP2P.Available ? Text(Tr.SteamHostVisible) : null,
                SteamP2P.IsHosting ? Check(Tr.PublicGameCheckbox, ModSettings.PublicGame) : null,
                Button(Tr.HostStop, () => Confirm(Tr.ConfirmStopHosting, Tr.HostStop, () =>
                {
                    close();
                    Client.StopHosting();
                })));
        }
        TextInput port = Input(ModConfig.Current.HostPort.ToString(), 5, 300f);
        TextInput lobbyPassword = Input("", 64, WidthBeside(Tr.CreateRoomButton));
        void Host()
        {
            if (!int.TryParse(port.Text.Trim(), out int p) || p < 1 || p > 65535)
            {
                Notifications.Warn(Tr.HostBadPort);
                return;
            }
            ModConfig.Current.HostPort = p;
            ModConfig.Save();
            close();
            Client.HostGame(p);
        }
        void CreateLobby()
        {
            close();
            Client.CreateRoom(lobbyPassword.Text);
        }
        port.OnEnter += () => { Host(); return true; };
        lobbyPassword.OnEnter += () => { CreateLobby(); return true; };
        return Stack(
            Row(Tr.HostPortLabel, Line(Field(port), Button(Tr.HostButton, Host))),
            SteamP2P.Available ? Check(Tr.SteamHostCheckbox, ModSettings.SteamP2PHost) : null,
            SteamP2P.Available ? Check(Tr.PublicGameCheckbox, ModSettings.PublicGame) : null,
            Text(Tr.CreateRoomHint),
            Text(Tr.SblbPublicHint),
            Row(Tr.RoomPasswordField, Line(Field(lobbyPassword), Button(Tr.CreateRoomButton, CreateLobby))));
    }

    /// <summary>"Bob's game - Hide and Seek - C9 Castle - 3/32 - password".</summary>
    private static string DescribeGame(SteamLobby.PublicGame g)
    {
        var parts = new List<string> { g.Name, Tr.ModeName(g.Mode) };
        if (g.MapKey != "")
        {
            parts.Add(GameAccess.DescribeMap(g.MapKey));
        }
        parts.Add($"{g.Players}/{g.MaxPlayers}");
        if (g.HasPassword)
        {
            parts.Add(Tr.PasswordTag);
        }
        if (!g.SameVersion)
        {
            parts.Add(Tr.OtherVersionWith(g.ModVersion));
        }
        return string.Join(" - ", parts);
    }

    // ---------- Settings: name, then the themed screens ----------

    private static void OpenSettings() => Show("settings", (close, screen) =>
    {
        ModConfig cfg = ModConfig.Current;
        TextInput name = Input(cfg.PlayerName, ProtocolInfo.MaxNameLength);
        void SaveName()
        {
            string text = name.Text.Trim();
            if (text != cfg.PlayerName)
            {
                cfg.PlayerName = text;
                ModConfig.ForgetEnvironmentOverride();
                ModConfig.Save();
            }
        }
        name.OnEnter += () => { SaveName(); return true; };
        screen.OnLeave(SaveName); // leaving the screen keeps what was typed
        return Stack(
            Title(Tr.SettingsSection),
            Row(Tr.NameField, Field(name)),
            Text(Tr.NameHint),
            Buttons(
                Button(Tr.DisplayTitle, OpenDisplay),
                Button(Tr.InterfaceTitle, OpenInterface),
                Button(Tr.KeysTitle, KeyBindings.Open)),
            Button(Tr.Back, close));
    }, () => Tr.French ? 1 : 0);

    // ---------- Display: how other players are shown ----------

    private static void OpenDisplay() => Show("display", (close, _) =>
    {
        return Stack(
            Title(Tr.DisplayTitle),
            Check(Tr.ShowOtherPlayers, ModSettings.ShowOtherPlayers),
            Slider(Tr.OtherPlayersOpacity, ModSettings.GhostOpacity, Percent, 0.1f, 1f, 0.1f),
            Check(Tr.ShowNameTags, ModSettings.ShowNameTags),
            Slider(Tr.NameTagDistance, ModSettings.NameTagMaxDistance, f => MathF.Round(float.Parse(f)) + " cm", 10f, 300f, 10f),
            Check(Tr.ShowNameTagDistance, ModSettings.ShowNameTagDistance),
            Button(Tr.Back, close)
        );
    });

    // ---------- Interface: HUD elements of the mod, language, updates ----------

    private static void OpenInterface() => Show("interface", (close, screen) =>
    {
        ModConfig cfg = ModConfig.Current;
        string language = cfg.Language.Trim().ToLowerInvariant() is "en" or "fr" ? cfg.Language.Trim().ToLowerInvariant() : "auto";
        return Stack(
            Title(Tr.InterfaceTitle),
            Check(Tr.ShowSplits, ModSettings.ShowSplits),
            Section(Tr.ChatTitle),
            Check(Tr.ShowChat, ModSettings.ShowChat),
            Slider(Tr.ChatSize, ModSettings.ChatScale, Percent, 0.5f, 2f, 0.1f),
            Check(Tr.NotificationsOnTop, ModSettings.NotificationsOnTop),
            Section(Tr.RoundPanel),
            Check(Tr.ShowRoundPanel, ModSettings.ShowRoundPanel),
            Slider(Tr.RoundPanelSize, ModSettings.RoundPanelScale, Percent, 0.3f, 1.2f, 0.1f),
            Section(Tr.GeneralSection),
            Row(Tr.LanguageField, Button(Tr.LanguageName(language), () =>
            {
                cfg.Language = language switch { "auto" => "en", "en" => "fr", _ => "auto" };
                ModConfig.Save();
                screen.Refresh(); // rebuilt in the new language
            })),
            Check(Tr.GameMenuButtonSetting, ModSettings.GameMenuButton),
            Check(Tr.CheckForUpdates, ModSettings.CheckForUpdates),
            Button(Tr.Back, close)
        );
    });

    private static string StatusText()
    {
        MultiplayerClient c = MultiplayerClient.Instance;
        if (c.IsReady)
        {
            return Tr.StatusConnected(c.ServerAddress, c.PlayerName, c.Players.Count);
        }
        if (c.IsConnecting)
        {
            return Tr.StatusConnecting(c.ServerAddress);
        }
        return c.IsUserDisconnected || !c.HasTarget ? Tr.StatusDisconnected : Tr.StatusOffline;
    }
}
