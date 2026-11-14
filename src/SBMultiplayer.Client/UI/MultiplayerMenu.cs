using BytingLib;
using BytingLib.UI;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// F8 menu: connection (server, password, name) and links to the "Display", "Interface" and "Admin" screens.
/// Each screen stays short; settings apply immediately and are saved in SBMultiplayer.json.
/// </summary>
internal static class MultiplayerMenu
{
    public static string Version => typeof(MultiplayerMenu).Assembly.GetName().Version?.ToString(3) ?? "?";

    public static void Open() => Show("main", BuildMain);

    // ---------- Main screen: connection ----------

    private static Element BuildMain(Action close, Scene scene)
    {
        ModConfig cfg = ModConfig.Current;
        var status = new Label(StatusText());
        scene.Add(new StatusUpdater(status));

        TextInput server = Input(cfg.Server, 1100f, 80);
        TextInput password = Input(cfg.ServerPassword, 1100f, 64);
        TextInput name = Input(cfg.PlayerName, 1100f, ProtocolInfo.MaxNameLength);

        void ApplyAndConnect()
        {
            cfg.Server = server.Text.Trim();
            cfg.ServerPassword = password.Text;
            cfg.PlayerName = name.Text.Trim();
            ModConfig.ForgetEnvironmentOverride();
            ModConfig.Save();
            MultiplayerClient.Instance.Connect();
        }
        foreach (TextInput input in new[] { server, password, name })
        {
            input.OnEnter += () => { ApplyAndConnect(); return true; };
        }

        return Stack(
            Title(Tr.MenuTitle(Version)),
            status,
            Section(Tr.ConnectionSection),
            Row(Tr.ServerField, Field(server)),
            Row(Tr.PasswordField, Field(password)),
            Row(Tr.NameField, Field(name)),
            Line(
                Button(Tr.Connect, ApplyAndConnect),
                Button(Tr.Disconnect, () => MultiplayerClient.Instance.Disconnect()),
                Button(Tr.JoinRound, () =>
                {
                    close();
                    MultiplayerClient.Instance.RejoinRound();
                }),
                Button(MultiplayerClient.Instance.IsSpectating ? Tr.PlayButton : Tr.SpectateButton, () =>
                {
                    close();
                    MultiplayerClient.Instance.SetSpectating(!MultiplayerClient.Instance.IsSpectating);
                })),
            Section(Tr.HostSection),
            BuildHostRow(close),
            Section(Tr.SettingsSection),
            Line(
                Button(Tr.DisplayTitle, OpenDisplay),
                Button(Tr.InterfaceTitle, OpenInterface),
                Button(Tr.AdminTitle, AdminMenu.Open)),
            Text(Tr.KeysHint(cfg.MenuKey, cfg.RejoinKey, cfg.ChatKey)),
            Button(Tr.Back, close));
    }

    // ---------- Host a game ----------

    private static Element BuildHostRow(Action close)
    {
        if (HostedServer.IsRunning)
        {
            var addresses = HostedServer.LocalAddresses().Select(a => $"{a}:{HostedServer.Port}").ToList();
            return Stack(
                Text(Tr.HostRunning(HostedServer.Port)),
                Text(Tr.HostAddresses(addresses.Count > 0 ? string.Join(", ", addresses) : "?", HostedServer.Port)),
                Button(Tr.HostStop, () =>
                {
                    close();
                    MultiplayerClient.Instance.StopHosting();
                }));
        }
        TextInput port = Input(ModConfig.Current.HostPort.ToString(), 300f, 5);
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
            MultiplayerClient.Instance.HostGame(p);
        }
        port.OnEnter += () => { Host(); return true; };
        return Line(Text(Tr.HostPortLabel), Field(port), Button(Tr.HostButton, Host));
    }

    // ---------- Display: how other players are shown ----------

    private static void OpenDisplay() => Show("display", (close, _) =>
    {
        SE.OptionsUI o = GameAccess.OptionsUi;
        return Stack(
            Title(Tr.DisplayTitle),
            o.GetCheckboxRow(Tr.ShowOtherPlayers, ModSettings.ShowOtherPlayers),
            o.GetSliderRow(Tr.OtherPlayersOpacity, ModSettings.GhostOpacity, Percent, 0.1f, 1f, 0.1f),
            o.GetCheckboxRow(Tr.ShowNameTags, ModSettings.ShowNameTags),
            o.GetSliderRow(Tr.NameTagDistance, ModSettings.NameTagMaxDistance, f => MathF.Round(float.Parse(f)) + " cm", 10f, 300f, 10f),
            Button(Tr.Back, close)
        ).SetChildrenWidthToMaxChildWidth();
    });

    // ---------- Interface: HUD elements of the mod ----------

    private static void OpenInterface() => Show("interface", (close, _) =>
    {
        SE.OptionsUI o = GameAccess.OptionsUi;
        return Stack(
            Title(Tr.InterfaceTitle),
            o.GetCheckboxRow(Tr.ShowSplits, ModSettings.ShowSplits),
            Section(Tr.ChatTitle),
            o.GetCheckboxRow(Tr.ShowChat, ModSettings.ShowChat),
            o.GetSliderRow(Tr.ChatSize, ModSettings.ChatScale, Percent, 0.5f, 2f, 0.1f),
            o.GetCheckboxRow(Tr.NotificationsOnTop, ModSettings.NotificationsOnTop),
            Section(Tr.RoundPanel),
            o.GetCheckboxRow(Tr.RoundPanel, ModSettings.ShowRoundPanel),
            o.GetSliderRow(Tr.RoundPanelSize, ModSettings.RoundPanelScale, Percent, 0.3f, 1.2f, 0.1f),
            Button(Tr.Back, close)
        ).SetChildrenWidthToMaxChildWidth();
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
        return c.IsUserDisconnected ? Tr.StatusDisconnected : Tr.StatusOffline;
    }

    private sealed class StatusUpdater(Label status) : IUpdate
    {
        private int tick;

        public void Update()
        {
            if (++tick % 15 == 0)
            {
                string text = StatusText();
                if (status.Text != text)
                {
                    status.Text = text;
                }
            }
        }
    }
}
