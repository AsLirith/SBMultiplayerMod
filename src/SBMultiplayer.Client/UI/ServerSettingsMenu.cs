using System.Text.Json;
using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Admin screens to change the server's match settings in game, split in two popups so they fit on screen:
/// "Mode and maps" and "Timings and points". Both start from the server's current settings.
/// </summary>
internal static class ServerSettingsMenu
{
    private static readonly Color InputBackground = new(0x5F, 0x5A, 0x59);
    private static readonly GameMode[] Modes = { GameMode.Free, GameMode.TimeAttack, GameMode.Rounds, GameMode.Cup, GameMode.Elimination };
    private static bool isOpen;

    public static void OpenModeAndMaps() => Open(CreateModeAndMaps);

    public static void OpenTimingsAndPoints() => Open(CreateTimingsAndPoints);

    private static void Open(Func<Action, Scene> create)
    {
        if (isOpen || GameAccess.Game == null)
        {
            return;
        }
        if (MultiplayerClient.Instance.ServerSettings == null)
        {
            Notifications.Warn(Tr.AdminNeedsConnection);
            return;
        }
        try
        {
            GameAccess.ShowPopup(create);
        }
        catch (Exception e)
        {
            isOpen = false;
            ModLog.Error("Could not open the server settings", e);
            Notifications.Warn(Tr.MenuOpenFailed);
        }
    }

    /// <summary>Deep copy of the server's settings, edited by the screen until "Apply".</summary>
    private static MatchSettings WorkingCopy() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(MultiplayerClient.Instance.ServerSettings!, MatchSettings.JsonInfo), MatchSettings.JsonInfo)!;

    // ---------- Mode and maps ----------

    private static Scene CreateModeAndMaps(Action close)
    {
        MatchSettings working = WorkingCopy();
        SE.UIAssets ui = GameAccess.UiAssets;
        var save = new ValueEvent<bool>(false);
        var modeLabel = new Label(Tr.SettingsModeLabel(Tr.ModeName(working.Mode)));
        var mapsInput = new TextInput(GameAccess.UpdateSpeed, string.Join(", ", working.Maps.Select(GameAccess.MapKeyToCode)),
            1800f, ui.FontSmall.Value.LineSpacing) { MaxTextLength = 2000 };

        var modeButtons = Modes.Select(mode => Button(ui, Tr.ModeName(mode), () =>
        {
            working.Mode = mode;
            modeLabel.Text = Tr.SettingsModeLabel(Tr.ModeName(mode));
        })).ToArray();

        void AddCurrentMap()
        {
            string current = MultiplayerClient.Instance.CurrentMapKey;
            if (current.StartsWith("o:", StringComparison.Ordinal))
            {
                string code = GameAccess.MapKeyToCode(current);
                mapsInput.Text = mapsInput.Text.Trim().Length == 0 ? code : $"{mapsInput.Text.TrimEnd(' ', ',')}, {code}";
            }
            else
            {
                Notifications.Warn(Tr.SettingsNoCurrentMap);
            }
        }

        void Apply()
        {
            var keys = new List<string>();
            foreach (string part in mapsInput.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? key = GameAccess.CodeToMapKey(part);
                if (key == null)
                {
                    Notifications.Warn(Tr.SettingsUnknownMap(part));
                    return;
                }
                keys.Add(key);
            }
            working.Maps = keys;
            MultiplayerClient.Instance.SendSettings(working, save.Value);
            close();
        }

        Element panel = new PanelStack(28f).SetStyle(ui.TextSmallStyle).Add(
            new Label(Tr.SettingsModeAndMapsTitle).SetStyle(ui.StrongStyle),
            modeLabel,
            new PanelStack(20f, vertical: false).Add(modeButtons),
            new Label(Tr.SettingsMapsField),
            new Panel(0f, 0f, InputBackground, null, new Padding(16f)).Add(mapsInput),
            Button(ui, Tr.SettingsAddCurrentMap, AddCurrentMap),
            new Label(Tr.SettingsRestartWarning),
            GameAccess.OptionsUi.GetCheckboxRow(Tr.SettingsSaveToFile, save),
            new PanelStack(24f, vertical: false).Add(Button(ui, Tr.SettingsApply, Apply), Button(ui, Tr.Back, close))
        ).SetNavigationStartToFirstNavigatable();

        return Wrap(panel, close);
    }

    // ---------- Timings and points ----------

    private static Scene CreateTimingsAndPoints(Action close)
    {
        MatchSettings working = WorkingCopy();
        SE.UIAssets ui = GameAccess.UiAssets;
        SE.OptionsUI options = GameAccess.OptionsUi;
        var save = new ValueEvent<bool>(false);

        var roundSeconds = new ValueEvent<float>((float)working.RoundSeconds);
        var resultsSeconds = new ValueEvent<float>((float)working.ResultsSeconds);
        var roundsPerMap = new ValueEvent<float>(working.RoundsPerMap);
        var finishTimeout = new ValueEvent<float>((float)working.FinishTimeoutSeconds);
        var prepareTimeout = new ValueEvent<float>((float)working.PrepareTimeoutSeconds);
        var countdown = new ValueEvent<float>((float)working.CountdownSeconds);
        var cupLimit = new ValueEvent<float>(working.CupPointsLimit);
        var eliminated = new ValueEvent<float>(working.EliminatedPerRound);
        var pointsInput = new TextInput(GameAccess.UpdateSpeed, string.Join(", ", working.PointsTable), 1100f, ui.FontSmall.Value.LineSpacing) { MaxTextLength = 300 };

        static string Seconds(string f) => MathF.Round(float.Parse(f)) + " s";
        static string Whole(string f) => MathF.Round(float.Parse(f)).ToString();

        void Apply()
        {
            var points = new List<int>();
            foreach (string part in pointsInput.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out int p) || p < 0)
                {
                    Notifications.Warn(Tr.SettingsBadPoints);
                    return;
                }
                points.Add(p);
            }
            working.PointsTable = points;
            working.RoundSeconds = MathF.Round(roundSeconds.Value);
            working.ResultsSeconds = MathF.Round(resultsSeconds.Value);
            working.RoundsPerMap = (int)MathF.Round(roundsPerMap.Value);
            working.FinishTimeoutSeconds = MathF.Round(finishTimeout.Value);
            working.PrepareTimeoutSeconds = MathF.Round(prepareTimeout.Value);
            working.CountdownSeconds = MathF.Round(countdown.Value);
            working.CupPointsLimit = (int)MathF.Round(cupLimit.Value);
            working.EliminatedPerRound = (int)MathF.Round(eliminated.Value);
            MultiplayerClient.Instance.SendSettings(working, save.Value);
            close();
        }

        Element panel = new PanelStack(22f).SetStyle(ui.TextSmallStyle).Add(
            new Label(Tr.SettingsTimingsTitle).SetStyle(ui.StrongStyle),
            options.GetSliderRow(Tr.SettingsRoundSeconds, roundSeconds, Seconds, 30f, 900f, 30f),
            options.GetSliderRow(Tr.SettingsResultsSeconds, resultsSeconds, Seconds, 5f, 60f, 5f),
            options.GetSliderRow(Tr.SettingsRoundsPerMap, roundsPerMap, Whole, 1f, 10f, 1f),
            options.GetSliderRow(Tr.SettingsFinishTimeout, finishTimeout, Seconds, 5f, 60f, 5f),
            options.GetSliderRow(Tr.SettingsPrepareTimeout, prepareTimeout, Seconds, 5f, 60f, 5f),
            options.GetSliderRow(Tr.SettingsCountdown, countdown, Seconds, 1f, 10f, 1f),
            options.GetSliderRow(Tr.SettingsCupLimit, cupLimit, Whole, 10f, 300f, 10f),
            options.GetSliderRow(Tr.SettingsEliminatedPerRound, eliminated, Whole, 1f, 5f, 1f),
            new PanelStack(32f, vertical: false).Add(
                new Label(Tr.SettingsPointsTable, 700f) { Anchor = new Vector2(0f, 0.5f) },
                new Panel(0f, 0f, InputBackground, null, new Padding(16f)).Add(pointsInput)),
            options.GetCheckboxRow(Tr.SettingsSaveToFile, save),
            new PanelStack(24f, vertical: false).Add(Button(ui, Tr.SettingsApply, Apply), Button(ui, Tr.Back, close))
        ).SetChildrenWidthToMaxChildWidth().SetNavigationStartToFirstNavigatable();

        return Wrap(panel, close);
    }

    // ---------- Helpers ----------

    private static Element Button(SE.UIAssets ui, string text, Action onClick) =>
        GameAccess.CreateButton(onClick).SetStyle(new Style { ButtonAnimation = ui.ButtonAnimation, ButtonPadding = new Padding(32f, 16f) }).Add(new Label(text));

    private static Scene Wrap(Element panel, Action close)
    {
        isOpen = true;
        var scene = new Scene();
        CanvasScale canvas = GameAccess.CreateCanvas();
        canvas.Add(GameAccess.CreateMainPanel(panel));
        scene.Add(canvas);
        scene.Add(new EscapeCloser(close));
        scene.OnDispose += () => isOpen = false;
        return scene;
    }

    private sealed class EscapeCloser(Action close) : IUpdate
    {
        private bool escapeWasDown = true;

        public void Update()
        {
            bool down = Keyboard.GetState().IsKeyDown(Keys.Escape);
            if (down && !escapeWasDown)
            {
                close();
            }
            escapeWasDown = down;
        }
    }
}
