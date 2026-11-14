using System.Text.Json;
using BytingLib;
using BytingLib.UI;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Admin screens to change the server's match settings in game: "Mode and maps" and "Timings and points".
/// Both start from the server's current settings; "Timings and points" only shows what the mode uses.
/// </summary>
internal static class ServerSettingsMenu
{
    private static readonly GameMode[] Modes = { GameMode.Free, GameMode.TimeAttack, GameMode.Rounds, GameMode.Cup, GameMode.Elimination };

    public static void OpenModeAndMaps()
    {
        if (MultiplayerClient.Instance.ServerSettings == null)
        {
            Notifications.Warn(Tr.AdminNeedsConnection);
            return;
        }
        Show("settings-mode", BuildModeAndMaps);
    }

    public static void OpenTimingsAndPoints()
    {
        if (MultiplayerClient.Instance.ServerSettings == null)
        {
            Notifications.Warn(Tr.AdminNeedsConnection);
            return;
        }
        Show("settings-timings", BuildTimingsAndPoints);
    }

    /// <summary>Deep copy of the server's settings, edited by the screen until "Apply".</summary>
    private static MatchSettings WorkingCopy() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(MultiplayerClient.Instance.ServerSettings!, MatchSettings.JsonInfo), MatchSettings.JsonInfo)!;

    // ---------- Mode and maps ----------

    private static Element BuildModeAndMaps(Action close, Scene scene)
    {
        MatchSettings working = WorkingCopy();
        var save = new ValueEvent<bool>(false);
        var modeLabel = new Label(Tr.SettingsModeLabel(Tr.ModeName(working.Mode)));
        var modeDescription = new Label(Tr.ModeDescription(working.Mode));
        TextInput maps = Input(string.Join(", ", working.Maps.Select(GameAccess.MapKeyToCode)), 1800f, 2000);

        var modeButtons = Modes.Select(mode => Button(Tr.ModeName(mode), () =>
        {
            working.Mode = mode;
            modeLabel.Text = Tr.SettingsModeLabel(Tr.ModeName(mode));
            modeDescription.Text = Tr.ModeDescription(mode);
        })).ToArray();

        void AddCurrentMap()
        {
            string current = MultiplayerClient.Instance.CurrentMapKey;
            if (current.StartsWith("l:", StringComparison.Ordinal))
            {
                Notifications.Warn(Tr.SettingsLocalMap);
                return;
            }
            if (!current.StartsWith("o:", StringComparison.Ordinal) && Msg.WorkshopId(current) == null)
            {
                Notifications.Warn(Tr.SettingsNoCurrentMap);
                return;
            }
            string code = GameAccess.MapKeyToCode(current);
            maps.Text = maps.Text.Trim().Length == 0 ? code : $"{maps.Text.TrimEnd(' ', ',')}, {code}";
        }

        void Apply()
        {
            var keys = new List<string>();
            foreach (string part in maps.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

        return Stack(
            Title(Tr.SettingsModeAndMapsTitle),
            Section(Tr.SettingsModeSection),
            Line(modeButtons),
            modeLabel,
            modeDescription,
            Section(Tr.SettingsMapsSection),
            Text(Tr.SettingsMapsField),
            Field(maps),
            Button(Tr.SettingsAddCurrentMap, AddCurrentMap),
            Section(Tr.SettingsApplySection),
            Text(Tr.SettingsRestartWarning),
            GameAccess.OptionsUi.GetCheckboxRow(Tr.SettingsSaveToFile, save),
            Line(Button(Tr.SettingsApply, Apply), Button(Tr.Back, close)));
    }

    // ---------- Timings and points ----------

    private static Element BuildTimingsAndPoints(Action close, Scene scene)
    {
        MatchSettings working = WorkingCopy();
        SE.OptionsUI o = GameAccess.OptionsUi;
        GameMode mode = working.Mode;
        bool synchronized = mode.IsSynchronized();
        var save = new ValueEvent<bool>(false);

        var roundSeconds = new ValueEvent<float>((float)working.RoundSeconds);
        var resultsSeconds = new ValueEvent<float>((float)working.ResultsSeconds);
        var roundsPerMap = new ValueEvent<float>(working.RoundsPerMap);
        var finishTimeout = new ValueEvent<float>((float)working.FinishTimeoutSeconds);
        var prepareTimeout = new ValueEvent<float>((float)working.PrepareTimeoutSeconds);
        var countdown = new ValueEvent<float>((float)working.CountdownSeconds);
        var cupLimit = new ValueEvent<float>(working.CupPointsLimit);
        var eliminated = new ValueEvent<float>(working.EliminatedPerRound);
        var matchEnd = new ValueEvent<float>((float)working.MatchEndSeconds);
        TextInput points = Input(string.Join(", ", working.PointsTable), 1000f, 300);

        void Apply()
        {
            var table = new List<int>();
            foreach (string part in points.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out int p) || p < 0)
                {
                    Notifications.Warn(Tr.SettingsBadPoints);
                    return;
                }
                table.Add(p);
            }
            working.PointsTable = table;
            working.RoundSeconds = MathF.Round(roundSeconds.Value);
            working.ResultsSeconds = MathF.Round(resultsSeconds.Value);
            working.RoundsPerMap = (int)MathF.Round(roundsPerMap.Value);
            working.FinishTimeoutSeconds = MathF.Round(finishTimeout.Value);
            working.PrepareTimeoutSeconds = MathF.Round(prepareTimeout.Value);
            working.CountdownSeconds = MathF.Round(countdown.Value);
            working.CupPointsLimit = (int)MathF.Round(cupLimit.Value);
            working.EliminatedPerRound = (int)MathF.Round(eliminated.Value);
            working.MatchEndSeconds = MathF.Round(matchEnd.Value);
            MultiplayerClient.Instance.SendSettings(working, save.Value);
            close();
        }

        if (mode == GameMode.Free)
        {
            return Stack(Title(Tr.SettingsTimingsTitle), Text(Tr.SettingsFreeModeNothing), Button(Tr.Back, close));
        }

        // Only the settings used by the current mode.
        return Stack(
            Title(Tr.SettingsTimingsTitle),
            Text(Tr.SettingsModeLabel(Tr.ModeName(mode))),
            Section(Tr.SettingsDurationsSection),
            o.GetSliderRow(synchronized ? Tr.SettingsTimeLimit : Tr.SettingsRoundSeconds, roundSeconds, Seconds, 30f, 900f, 30f),
            o.GetSliderRow(Tr.SettingsResultsSeconds, resultsSeconds, Seconds, 5f, 60f, 5f),
            o.GetSliderRow(Tr.SettingsMatchEnd, matchEnd, Seconds, 5f, 60f, 5f),
            synchronized ? o.GetSliderRow(Tr.SettingsFinishTimeout, finishTimeout, Seconds, 5f, 60f, 5f) : null,
            synchronized ? o.GetSliderRow(Tr.SettingsPrepareTimeout, prepareTimeout, Seconds, 5f, 60f, 5f) : null,
            synchronized ? o.GetSliderRow(Tr.SettingsCountdown, countdown, Seconds, 1f, 10f, 1f) : null,
            Section(Tr.SettingsMatchSection),
            synchronized ? o.GetSliderRow(Tr.SettingsRoundsPerMap, roundsPerMap, Whole, 1f, 10f, 1f) : null,
            mode == GameMode.Cup ? o.GetSliderRow(Tr.SettingsCupLimit, cupLimit, Whole, 10f, 300f, 10f) : null,
            mode == GameMode.Elimination ? o.GetSliderRow(Tr.SettingsEliminatedPerRound, eliminated, Whole, 1f, 5f, 1f) : null,
            mode != GameMode.Elimination ? Row(Tr.SettingsPointsTable, Field(points)) : null,
            Section(Tr.SettingsApplySection),
            o.GetCheckboxRow(Tr.SettingsSaveToFile, save),
            Line(Button(Tr.SettingsApply, Apply), Button(Tr.Back, close))
        ).SetChildrenWidthToMaxChildWidth();
    }
}
