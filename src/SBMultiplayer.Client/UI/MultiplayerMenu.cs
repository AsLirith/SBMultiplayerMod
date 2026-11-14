using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Écran « Multijoueur » (touche F8 par défaut), construit avec les éléments d'interface du jeu
/// (mêmes sliders, cases à cocher et boutons que ses écrans d'options), ouvert en popup.
/// </summary>
internal static class MultiplayerMenu
{
    private static readonly Color InputBackground = new(0x5F, 0x5A, 0x59); // Colors.DarkBrighter du jeu

    private static bool isOpen;

    public static string Version => typeof(MultiplayerMenu).Assembly.GetName().Version?.ToString(3) ?? "?";

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
            ModLog.Error("Impossible d'ouvrir le menu multijoueur", e);
            Notifications.Warn("Impossible d'ouvrir le menu multijoueur (voir SBMultiplayer.log)");
        }
    }

    private static Scene Create(Action close)
    {
        isOpen = true;
        ModConfig cfg = ModConfig.Current;
        SE.UIAssets ui = GameAccess.UiAssets;
        SE.OptionsUI options = GameAccess.OptionsUi;
        var buttonStyle = new Style { ButtonAnimation = ui.ButtonAnimation, ButtonPadding = new Padding(32f, 16f) };

        var scene = new Scene();
        CanvasScale canvas = GameAccess.CreateCanvas();

        var status = new Label(StatusText());
        float inputHeight = ui.FontSmall.Value.LineSpacing;
        var serverInput = new TextInput(GameAccess.UpdateSpeed, cfg.Server, 1100f, inputHeight) { MaxTextLength = 80 };
        var nameInput = new TextInput(GameAccess.UpdateSpeed, cfg.PlayerName, 1100f, inputHeight) { MaxTextLength = ProtocolInfo.MaxNameLength };

        void ApplyAndConnect()
        {
            cfg.Server = serverInput.Text.Trim();
            cfg.PlayerName = nameInput.Text.Trim();
            ModConfig.ForgetEnvironmentOverride();
            ModConfig.Save();
            MultiplayerClient.Instance.Connect();
        }
        serverInput.OnEnter += () => { ApplyAndConnect(); return true; };
        nameInput.OnEnter += () => { ApplyAndConnect(); return true; };

        Element Button(string text, Action onClick) => GameAccess.CreateButton(onClick).SetStyle(buttonStyle).Add(new Label(text));

        Element panel = new PanelStack(28f).SetStyle(ui.TextSmallStyle).AddMaybeNull(
            new Label($"Multijoueur  (SBMultiplayer v{Version})").SetStyle(ui.StrongStyle),
            status,
            Row("Serveur (adresse:port)", Field(serverInput)),
            Row("Pseudo (vide = pseudo Steam)", Field(nameInput)),
            new PanelStack(24f, vertical: false).Add(
                Button("Se connecter", ApplyAndConnect),
                Button("Se déconnecter", () => MultiplayerClient.Instance.Disconnect()),
                Button("Rejoindre le round", () =>
                {
                    close();
                    MultiplayerClient.Instance.RejoinRound();
                })),
            options.GetCheckboxRow("Afficher les autres joueurs", ModSettings.ShowOtherPlayers),
            options.GetSliderRow("Opacité des autres joueurs", ModSettings.GhostOpacity,
                f => MathF.Round(float.Parse(f) * 100f) + "%", 0.1f, 1f, 0.1f),
            options.GetCheckboxRow("Afficher les pseudos", ModSettings.ShowNameTags),
            options.GetSliderRow("Distance des pseudos", ModSettings.NameTagMaxDistance,
                f => MathF.Round(float.Parse(f)) + " cm", 10f, 300f, 10f),
            options.GetCheckboxRow("Panneau du round", ModSettings.ShowRoundPanel),
            options.GetSliderRow("Taille du panneau du round", ModSettings.RoundPanelScale,
                f => MathF.Round(float.Parse(f) * 100f) + "%", 0.3f, 1.2f, 0.1f),
            new Label($"{cfg.MenuKey} : ce menu      {cfg.RejoinKey} : rejoindre la map du round"),
            Button("Retour", close)
        ).SetChildrenWidthToMaxChildWidth().SetNavigationStartToFirstNavigatable();

        canvas.Add(GameAccess.CreateMainPanel(panel));
        scene.Add(canvas);
        scene.Add(new MenuUpdater(status, close));
        scene.OnDispose += () => isOpen = false;
        return scene;
    }

    private static Element Row(string label, Element field) =>
        new PanelStack(32f, vertical: false).Add(new Label(label, 700f) { Anchor = new Vector2(0f, 0.5f) }, field);

    private static Element Field(TextInput input) =>
        new Panel(0f, 0f, InputBackground, null, new Padding(16f)).Add(input);

    private static string StatusText()
    {
        MultiplayerClient c = MultiplayerClient.Instance;
        if (c.IsReady)
        {
            int n = c.Players.Count;
            return $"Connecté à {c.ServerAddress} en tant que {c.PlayerName} ({n} autre{(n > 1 ? "s" : "")} joueur{(n > 1 ? "s" : "")})";
        }
        if (c.IsConnecting)
        {
            return $"Connexion à {c.ServerAddress}...";
        }
        return c.IsUserDisconnected ? "Déconnecté" : "Hors ligne - nouvelle tentative automatique";
    }

    /// <summary>Met à jour le statut et ferme le menu avec Échap.</summary>
    private sealed class MenuUpdater(Label status, Action close) : IUpdate
    {
        private int tick;
        private bool escapeWasDown = true; // ignore l'Échap éventuellement encore enfoncé à l'ouverture

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
            bool escapeDown = Keyboard.GetState().IsKeyDown(Keys.Escape);
            if (escapeDown && !escapeWasDown)
            {
                close();
            }
            escapeWasDown = escapeDown;
        }
    }
}
