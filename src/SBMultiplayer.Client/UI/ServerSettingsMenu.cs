using System.Text.Json;
using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Admin screens to change the server's match settings in game: "Mode and settings" (the game mode, then the
/// timings and points this mode uses, on the same screen) and "Map rotation" (a list to reorder, with levels
/// added by code). Both start from the server's current settings and send only what changed on "Apply".
/// </summary>
internal static class ServerSettingsMenu
{
    /// <summary>Rotation rows visible at once (the list scrolls beyond that).</summary>
    private const int VisibleMaps = 7;

    private static MultiplayerClient Client => MultiplayerClient.Instance;

    /// <summary>Modes both this mod and the server know (an older server may lack the newest ones).</summary>
    private static IEnumerable<GameMode> Modes => GameModes.Known.Where(Client.ServerCapabilities.Supports);

    /// <summary>Deep copy of the server's settings, edited by the screen until "Apply".</summary>
    private static MatchSettings Copy(MatchSettings settings) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, MatchSettings.JsonInfo), MatchSettings.JsonInfo)!;

    private static bool ServerSettingsKnown()
    {
        if (Client.ServerSettings != null)
        {
            return true;
        }
        Notifications.Warn(Tr.AdminNeedsConnection);
        return false;
    }

    // ---------- Mode and settings ----------

    /// <summary>What the admin is editing. Kept outside the screen: choosing another mode rebuilds it.</summary>
    private sealed class MatchDraft(MatchSettings server)
    {
        public MatchSettings Baseline { get; } = Copy(server); // only the differences are sent
        public MatchSettings Working { get; } = Copy(server);
        public ValueEvent<bool> Save { get; } = new(false);
        public ValueEvent<float> RoundSeconds { get; } = new((float)server.RoundSeconds);
        public ValueEvent<float> ResultsSeconds { get; } = new((float)server.ResultsSeconds);
        public ValueEvent<float> RoundsPerMap { get; } = new(server.RoundsPerMap);
        public ValueEvent<float> FinishTimeout { get; } = new((float)server.FinishTimeoutSeconds);
        public ValueEvent<float> PrepareTimeout { get; } = new((float)server.PrepareTimeoutSeconds);
        public ValueEvent<float> Countdown { get; } = new((float)server.CountdownSeconds);
        public ValueEvent<float> CupLimit { get; } = new(server.CupPointsLimit);
        public ValueEvent<float> Eliminated { get; } = new(server.EliminatedPerRound);
        public ValueEvent<float> MatchEnd { get; } = new((float)server.MatchEndSeconds);
        public ValueEvent<float> HideSeconds { get; } = new((float)server.HideSeconds);
        public ValueEvent<bool> Infection { get; } = new(server.Infection);
        public ValueEvent<bool> DnfFillsQuota { get; } = new(server.DnfFillsQuota);
        public string Points { get; set; } = string.Join(", ", server.PointsTable);
    }

    private static MatchDraft? matchDraft;

    public static void OpenMatch()
    {
        if (ServerSettingsKnown())
        {
            matchDraft = new MatchDraft(Client.ServerSettings!);
            Show("settings-match", BuildMatch);
        }
    }

    private static Element BuildMatch(Action close, Screen screen)
    {
        MatchDraft d = matchDraft!;
        MatchSettings working = d.Working;
        GameMode mode = working.Mode;
        bool synchronized = mode.IsSynchronized();
        bool hideAndSeek = mode == GameMode.HideAndSeek;
        bool elimination = mode == GameMode.Elimination;
        bool rounds = mode != GameMode.Free;
        bool canDnfQuota = Client.ServerCapabilities.Has(ProtocolInfo.Features.DnfFillsQuota);
        bool canRunCheck = Client.ServerCapabilities.Has(ProtocolInfo.Features.RunProof) && rounds && !hideAndSeek;
        TextInput points = Input(d.Points, 300);
        points.OnTextInputChanged += text => d.Points = text;

        void Apply()
        {
            var table = new List<int>();
            foreach (string part in d.Points.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, out int p) || p < 0)
                {
                    Notifications.Warn(Tr.SettingsBadPoints);
                    return;
                }
                table.Add(p);
            }
            working.PointsTable = table;
            working.RoundSeconds = MathF.Round(d.RoundSeconds.Value);
            working.ResultsSeconds = MathF.Round(d.ResultsSeconds.Value);
            working.RoundsPerMap = (int)MathF.Round(d.RoundsPerMap.Value);
            working.FinishTimeoutSeconds = MathF.Round(d.FinishTimeout.Value);
            working.PrepareTimeoutSeconds = MathF.Round(d.PrepareTimeout.Value);
            working.CountdownSeconds = MathF.Round(d.Countdown.Value);
            working.CupPointsLimit = (int)MathF.Round(d.CupLimit.Value);
            working.EliminatedPerRound = (int)MathF.Round(d.Eliminated.Value);
            working.MatchEndSeconds = MathF.Round(d.MatchEnd.Value);
            working.HideSeconds = MathF.Round(d.HideSeconds.Value);
            working.Infection = d.Infection.Value;
            working.DnfFillsQuota = d.DnfFillsQuota.Value;
            Client.SendSettings(working, d.Baseline, d.Save.Value);
            close();
        }

        // The chosen mode is highlighted; choosing another one shows its own settings below.
        var modeButtons = Modes.Select(m => Button(Tr.ModeName(m), () =>
        {
            working.Mode = m;
            screen.Refresh();
        }, selected: m == mode)).ToArray();

        return Stack(
            Title(Tr.SettingsMatchTitle),
            Buttons(modeButtons),
            Text(Tr.ModeDescription(mode)),
            // Only the settings used by the chosen mode.
            hideAndSeek ? Slider(Tr.SettingsHideSeconds, d.HideSeconds, Seconds, 10f, 180f, 5f) : null,
            rounds ? Slider(hideAndSeek ? Tr.SettingsHuntSeconds : synchronized ? Tr.SettingsTimeLimit : Tr.SettingsRoundSeconds, d.RoundSeconds, Seconds, 30f, 900f, 30f) : null,
            rounds ? Slider(Tr.SettingsResultsSeconds, d.ResultsSeconds, Seconds, 5f, 60f, 5f) : null,
            rounds ? Slider(Tr.SettingsMatchEnd, d.MatchEnd, Seconds, 5f, 60f, 5f) : null,
            synchronized && !hideAndSeek ? Slider(Tr.SettingsFinishTimeout, d.FinishTimeout, Seconds, 5f, 60f, 5f) : null,
            synchronized ? Slider(Tr.SettingsPrepareTimeout, d.PrepareTimeout, Seconds, 5f, 60f, 5f) : null,
            synchronized ? Slider(Tr.SettingsCountdown, d.Countdown, Seconds, 1f, 10f, 1f) : null,
            synchronized ? Slider(Tr.SettingsRoundsPerMap, d.RoundsPerMap, Whole, 1f, 10f, 1f) : null,
            mode == GameMode.Cup ? Slider(Tr.SettingsCupLimit, d.CupLimit, Whole, 10f, 300f, 10f) : null,
            elimination ? Slider(Tr.SettingsEliminatedPerRound, d.Eliminated, Whole, 1f, 5f, 1f) : null,
            elimination && canDnfQuota ? Check(Tr.SettingsDnfFillsQuota, d.DnfFillsQuota) : null,
            elimination && canDnfQuota ? Text(Tr.SettingsDnfFillsQuotaHint) : null,
            hideAndSeek ? Check(Tr.SettingsInfection, d.Infection) : null,
            rounds && !elimination && !hideAndSeek ? Row(Tr.SettingsPointsTable, Field(points)) : null,
            // Off, Report, Enforce, Off...
            canRunCheck ? Row(Tr.SettingsRunCheck, Button(Tr.RunCheckName(working.RunCheck), () =>
            {
                working.RunCheck = (RunCheck)(((int)working.RunCheck + 1) % 3);
                screen.Refresh();
            })) : null,
            canRunCheck ? Text(Tr.RunCheckDescription(working.RunCheck)) : null,
            mode != d.Baseline.Mode ? Text(Tr.SettingsRestartWarning) : null,
            Check(Tr.SettingsSaveToFile, d.Save),
            Buttons(Button(Tr.SettingsApply, Apply), Button(Tr.Back, close)));
    }

    // ---------- Map rotation ----------

    private sealed class RotationDraft(MatchSettings server)
    {
        public MatchSettings Baseline { get; } = Copy(server);
        public List<string> Maps { get; } = server.Maps.ToList();
        public ValueEvent<bool> Save { get; } = new(false);
        public string AddText { get; set; } = "";
        public ScrollMemory Scroll { get; } = new();
    }

    private static RotationDraft? rotationDraft;

    public static void OpenRotation()
    {
        if (ServerSettingsKnown())
        {
            rotationDraft = new RotationDraft(Client.ServerSettings!);
            Show("settings-maps", BuildRotation);
        }
    }

    private static Element BuildRotation(Action close, Screen screen)
    {
        RotationDraft d = rotationDraft!;
        List<string> maps = d.Maps;
        TextInput add = Input(d.AddText, 2000, WidthBeside(Tr.SettingsAddButton));
        add.OnTextInputChanged += text => d.AddText = text;

        // Adds the codes typed (or pasted) in the field; nothing is added if one of them is unknown.
        bool AddTyped()
        {
            var keys = new List<string>();
            foreach (string part in d.AddText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? key = GameAccess.CodeToMapKey(part);
                if (key == null)
                {
                    Notifications.Warn(Tr.SettingsUnknownMap(part));
                    return false;
                }
                keys.Add(key);
            }
            if (keys.Count > 0)
            {
                maps.AddRange(keys);
                d.AddText = "";
                d.Scroll.ToEnd();
                screen.Refresh();
            }
            return true;
        }

        void AddCurrentMap()
        {
            string current = Client.CurrentMapKey;
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
            maps.Add(current);
            d.Scroll.ToEnd();
            screen.Refresh();
        }

        void Move(int from, int to)
        {
            (maps[from], maps[to]) = (maps[to], maps[from]);
            screen.Refresh();
        }

        void Apply()
        {
            if (!AddTyped())
            {
                return; // codes still in the field: they're part of the rotation, or the mistake is shown
            }
            MatchSettings working = Copy(d.Baseline);
            working.Maps = maps.ToList();
            Client.SendSettings(working, d.Baseline, d.Save.Value);
            close();
        }

        add.OnEnter += () => { AddTyped(); return true; };

        // One row per map: "3. A5 Name" then its buttons, aligned in columns.
        float buttons = ButtonWidth(Tr.MoveUp) + ButtonWidth(Tr.MoveDown) + ButtonWidth(Tr.RemoveButton) + 3 * 22f;
        float nameWidth = ScrollRowWidth - buttons;
        var rows = maps.Select((key, i) => Line(
            new Label(Fit($"{i + 1}. {GameAccess.DescribeMap(key)}", nameWidth), nameWidth) { Anchor = new Vector2(0f, 0.5f) },
            Button(Tr.MoveUp, () => Move(i, i == 0 ? maps.Count - 1 : i - 1)),
            Button(Tr.MoveDown, () => Move(i, i == maps.Count - 1 ? 0 : i + 1)),
            Button(Tr.RemoveButton, () =>
            {
                maps.RemoveAt(i);
                screen.Refresh();
            }))).ToList();

        return Stack(
            Title(Tr.SettingsMapsSection),
            maps.Count == 0 ? Text(Tr.SettingsNoMaps) : ScrollList(rows, VisibleMaps, screen, d.Scroll),
            Row(Tr.SettingsAddMaps, Line(Field(add), Button(Tr.SettingsAddButton, () => AddTyped()))),
            Text(Tr.SettingsMapsHint),
            Buttons(
                Button(Tr.SettingsAddCurrentMap, AddCurrentMap),
                maps.Count > 0 ? Button(Tr.RemoveAllButton, () =>
                {
                    maps.Clear();
                    screen.Refresh();
                }) : null),
            Text(Tr.SettingsRestartWarning),
            Check(Tr.SettingsSaveToFile, d.Save),
            Buttons(Button(Tr.SettingsApply, Apply), Button(Tr.Back, close)));
    }
}
