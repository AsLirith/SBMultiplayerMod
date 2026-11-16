using BytingLib;
using BytingLib.UI;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Admin screens: login, then round and match controls, with sub-screens for the map choice, the players
/// (kick) and the server settings. Rebuilt automatically when the admin status or the player list changes.
/// </summary>
internal static class AdminMenu
{
    private const int MaxMapButtons = 24;
    private const int EchoIdOffset = 1_000_000;

    private static MultiplayerClient Client => MultiplayerClient.Instance;

    public static void Open() => Show("admin", Build, () => Client.AdminVersion * 31 + (Client.IsReady ? 1 : 0));

    private static Element Build(Action close, Scene scene)
    {
        if (!Client.IsReady)
        {
            return Stack(Title(Tr.AdminTitle), Text(Tr.AdminNeedsConnection), Button(Tr.Back, close));
        }
        if (!Client.IsAdmin)
        {
            TextInput password = Input(ModConfig.Current.AdminPassword, 64);
            void Login()
            {
                ModConfig.Current.AdminPassword = password.Text;
                ModConfig.Save();
                Client.AdminLogin(password.Text);
            }
            password.OnEnter += () => { Login(); return true; };
            return Stack(
                Title(Tr.AdminTitle),
                Row(Tr.AdminPasswordField, Field(password)),
                Buttons(Button(Tr.AdminLoginButton, Login), Button(Tr.Back, close)));
        }

        return Stack(
            Title(Tr.AdminTitle),
            Text(StatusLine()),
            Section(Tr.AdminRoundSection),
            Buttons(
                Button(Tr.AdminNext, () => Client.Admin(AdminCommand.NextPhase)),
                Button(Tr.AdminRestart, () => Client.Admin(AdminCommand.RestartRound)),
                Button(Tr.AdminPause, () => Client.Admin(AdminCommand.TogglePause)),
                Button(Tr.AdminExtend, () => Client.Admin(AdminCommand.Extend, 60))),
            Section(Tr.AdminMatchSection),
            Buttons(
                Button(Tr.AdminChangeMap, OpenMaps),
                Button(Tr.AdminPlayersButton, OpenPlayers),
                Client.ServerSettings?.Mode == GameMode.HideAndSeek ? Button(Tr.AdminNextHunter, OpenNextHunter) : null,
                Button(Tr.AdminResetMatch, () => Confirm(Tr.ConfirmResetMatch, Tr.AdminResetMatch,
                    () => Client.Admin(AdminCommand.ResetMatch)))),
            Section(Tr.AdminServerSection),
            Buttons(
                Button(Tr.SettingsModeAndMapsTitle, ServerSettingsMenu.OpenModeAndMaps),
                Button(Tr.SettingsTimingsTitle, ServerSettingsMenu.OpenTimingsAndPoints)),
            Button(Tr.Back, close));
    }

    /// <summary>"Rounds - A1 Accelerate (1/3) - round 2/3".</summary>
    private static string StatusLine()
    {
        RoundInfo info = Client.Round.Current.Info;
        string mode = Tr.ModeName(Client.ServerSettings?.Mode ?? info.Mode);
        if (info.Phase == RoundPhase.Free)
        {
            return Tr.SettingsModeLabel(mode);
        }
        string round = info.RoundsPerMap > 1 ? $" - {Tr.RoundOf(info.RoundInMap, info.RoundsPerMap)}" : "";
        return $"{mode} - {GameAccess.DescribeMap(info.MapKey)} ({info.MapIndex + 1}/{info.MapCount}){round}";
    }

    // ---------- Change map ----------

    private static void OpenMaps() => Show("admin-maps", (close, _) =>
    {
        IReadOnlyList<string> maps = Client.AdminMaps;
        if (maps.Count == 0)
        {
            return Stack(Title(Tr.AdminChangeMap), Text(Tr.AdminNoRotation), Button(Tr.Back, close));
        }
        var buttons = maps.Take(MaxMapButtons).Select((key, i) => Button($"{i + 1}. {GameAccess.DescribeMap(key)}", () =>
        {
            Client.Admin(AdminCommand.GotoMap, i);
            close();
        })).ToList();
        return Stack(Title(Tr.AdminChangeMap), Grid(buttons, 3), Button(Tr.Back, close));
    }, () => Client.AdminVersion);

    // ---------- Hide and Seek: next hunter ----------

    private static void OpenNextHunter() => Show("admin-hunter", (close, _) =>
    {
        var everyone = Client.Players.Values.Where(p => p.Id < EchoIdOffset).Select(p => (p.Id, p.Name)).ToList();
        if (Client.LocalId is int me)
        {
            everyone.Add((me, Client.PlayerName));
        }
        var buttons = everyone.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p => Button(p.Name, () =>
        {
            Client.Admin(AdminCommand.SetHunter, p.Id);
            Notifications.Info(Tr.AdminHunterSet(p.Name));
            close();
        })).ToList();
        return Stack(
            Title(Tr.AdminNextHunter),
            Grid(buttons, 3),
            Buttons(Button(Tr.AdminRandomHunter, () =>
            {
                Client.Admin(AdminCommand.SetHunter, -1);
                Notifications.Info(Tr.AdminHunterRandom);
                close();
            }), Button(Tr.Back, close)));
    }, () => Client.Players.Count * 31 + Client.AdminVersion);

    // ---------- Players (kick) ----------

    private static void OpenPlayers() => Show("admin-players", (close, _) =>
    {
        var others = Client.Players.Values.Where(p => p.Id < EchoIdOffset).OrderBy(p => p.Name).ToList();
        Element list = others.Count == 0
            ? Text(Tr.AdminNoOtherPlayers)
            : Grid(others.Select(p => Button(Tr.AdminKick(p.Name), () => Confirm(Tr.ConfirmKick(p.Name), Tr.AdminKick(p.Name),
                    () => Client.Admin(AdminCommand.Kick, p.Id)))).ToList(), 3);
        return Stack(Title(Tr.AdminPlayersButton), list, Button(Tr.Back, close));
    }, () => Client.Players.Count * 31 + Client.AdminVersion);
}
