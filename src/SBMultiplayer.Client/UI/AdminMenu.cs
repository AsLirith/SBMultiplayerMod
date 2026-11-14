using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Admin panel, opened from the F8 menu: admin login, then round controls, map choice and kicks.
/// Rebuilt automatically when the admin status changes.
/// </summary>
internal static class AdminMenu
{
    private const int MaxMapButtons = 12;
    private static readonly Color InputBackground = new(0x5F, 0x5A, 0x59);

    private static bool isOpen;
    private static bool reopenRequested;

    public static void Open()
    {
        if (isOpen || GameAccess.Game == null)
        {
            return;
        }
        try
        {
            GameAccess.ShowPopup(Create);
        }
        catch (Exception e)
        {
            isOpen = false;
            ModLog.Error("Could not open the admin panel", e);
            Notifications.Warn(Tr.MenuOpenFailed);
        }
    }

    /// <summary>Called every tick: reopens the panel after a rebuild request (admin status changed).</summary>
    public static void Tick()
    {
        if (reopenRequested && !isOpen)
        {
            reopenRequested = false;
            Open();
        }
    }

    private static Scene Create(Action close)
    {
        isOpen = true;
        MultiplayerClient client = MultiplayerClient.Instance;
        SE.UIAssets ui = GameAccess.UiAssets;
        var buttonStyle = new Style { ButtonAnimation = ui.ButtonAnimation, ButtonPadding = new Padding(32f, 16f) };
        Element Button(string text, Action onClick) => GameAccess.CreateButton(onClick).SetStyle(buttonStyle).Add(new Label(text));
        Element Line(params Element[] items) => new PanelStack(24f, vertical: false).Add(items);

        var content = new List<Element?> { new Label(Tr.AdminTitle).SetStyle(ui.StrongStyle) };

        if (!client.IsReady)
        {
            content.Add(new Label(Tr.AdminNeedsConnection));
        }
        else if (!client.IsAdmin)
        {
            var passwordInput = new TextInput(GameAccess.UpdateSpeed, ModConfig.Current.AdminPassword, 900f, ui.FontSmall.Value.LineSpacing) { MaxTextLength = 64 };
            void Login()
            {
                ModConfig.Current.AdminPassword = passwordInput.Text;
                ModConfig.Save();
                client.AdminLogin(passwordInput.Text);
            }
            passwordInput.OnEnter += () => { Login(); return true; };
            content.Add(Line(
                new Label(Tr.AdminPasswordField, 600f) { Anchor = new Vector2(0f, 0.5f) },
                new Panel(0f, 0f, InputBackground, null, new Padding(16f)).Add(passwordInput)));
            content.Add(Button(Tr.AdminLoginButton, Login));
        }
        else
        {
            content.Add(Line(
                Button(Tr.AdminNext, () => client.Admin(AdminCommand.NextPhase)),
                Button(Tr.AdminRestart, () => client.Admin(AdminCommand.RestartRound)),
                Button(Tr.AdminPause, () => client.Admin(AdminCommand.TogglePause)),
                Button(Tr.AdminExtend, () => client.Admin(AdminCommand.Extend, 60))));
            content.Add(Line(
                Button(Tr.AdminResetMatch, () => client.Admin(AdminCommand.ResetMatch)),
                Button(Tr.SettingsModeAndMapsTitle, ServerSettingsMenu.OpenModeAndMaps),
                Button(Tr.SettingsTimingsTitle, ServerSettingsMenu.OpenTimingsAndPoints)));

            IReadOnlyList<string> maps = client.AdminMaps;
            if (maps.Count == 0)
            {
                content.Add(new Label(Tr.AdminNoRotation));
            }
            else
            {
                content.Add(new Label(Tr.AdminMaps));
                // Map buttons, 3 per line.
                for (int start = 0; start < Math.Min(maps.Count, MaxMapButtons); start += 3)
                {
                    var row = new List<Element>();
                    for (int i = start; i < Math.Min(start + 3, Math.Min(maps.Count, MaxMapButtons)); i++)
                    {
                        int index = i;
                        row.Add(Button($"{i + 1}. {GameAccess.DescribeMap(maps[i])}", () => client.Admin(AdminCommand.GotoMap, index)));
                    }
                    content.Add(Line(row.ToArray()));
                }
            }

            var others = client.Players.Values.Where(p => p.Id < 1_000_000).OrderBy(p => p.Name).ToList(); // hides the echo player
            if (others.Count > 0)
            {
                content.Add(new Label(Tr.AdminPlayers));
                for (int start = 0; start < others.Count; start += 3)
                {
                    content.Add(Line(others.Skip(start).Take(3)
                        .Select(p => Button(Tr.AdminKick(p.Name), () => client.Admin(AdminCommand.Kick, p.Id))).ToArray()));
                }
            }
        }
        content.Add(Button(Tr.Back, close));

        Element panel = new PanelStack(28f).SetStyle(ui.TextSmallStyle).AddMaybeNull(content.ToArray())
            .SetNavigationStartToFirstNavigatable();

        var scene = new Scene();
        CanvasScale canvas = GameAccess.CreateCanvas();
        canvas.Add(GameAccess.CreateMainPanel(panel));
        scene.Add(canvas);
        scene.Add(new Updater(close, client.AdminVersion, client.Players.Count));
        scene.OnDispose += () => isOpen = false;
        return scene;
    }

    /// <summary>Closes with Escape, and rebuilds the panel when the admin status or the player list changes.</summary>
    private sealed class Updater(Action close, int builtAdminVersion, int builtPlayerCount) : IUpdate
    {
        private bool escapeWasDown = true;

        public void Update()
        {
            MultiplayerClient client = MultiplayerClient.Instance;
            if (client.AdminVersion != builtAdminVersion || (client.IsAdmin && client.Players.Count != builtPlayerCount))
            {
                reopenRequested = true;
                close();
                return;
            }
            bool escapeDown = Keyboard.GetState().IsKeyDown(Keys.Escape);
            if (escapeDown && !escapeWasDown)
            {
                close();
            }
            escapeWasDown = escapeDown;
        }
    }
}
