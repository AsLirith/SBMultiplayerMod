using System.Text.RegularExpressions;
using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// The host's tournament form, to create a tournament or edit an upcoming one. It has the name, the start time (a day
/// over the next 60 days, an hour and a quarter hour, in the host's local time, sent as UTC), the mode, the maps (on a
/// screen of their own, like the server's map rotation) and only the chosen mode's length setting. Below come the length
/// it gives and Create/Save.
/// The website's checks are repeated here, so most mistakes are shown before anything is sent. Its refusals (busy, not
/// approved...) are shown on the form. The form is reached from the Tournaments screen ("Create tournament", approved
/// hosts only) and from <see cref="MyTournaments"/> ("Edit").
/// </summary>
internal static class TournamentEditor
{
    private const string ScreenId = "tournament-editor";
    private const string MapsScreenId = "tournament-maps";

    // The website's limits (it checks them again).
    private const int MaxNameLength = 40;
    private const int MaxMaps = 20;
    private const int DaysAhead = 60;
    private const int MaxRoundsPerMap = 50;
    private const int MaxMinutesPerMap = 30;
    private const int MaxCupPoints = 100000;
    private const int MaxEliminated = 16;

    /// <summary>The map keys the website accepts.</summary>
    private static readonly Regex WebsiteMapKey = new(@"^(o:\d{1,6}|c:M\d{1,20})$", RegexOptions.CultureInvariant);

    /// <summary>Map rows visible at once on the maps screen (the list scrolls beyond that).</summary>
    private const int VisibleMaps = 7;

    /// <summary>Every mode but Free can be a tournament.</summary>
    private static readonly GameMode[] Modes = GameModes.Known.Where(m => m != GameMode.Free).ToArray();

    /// <summary>What the host is filling in. Kept outside the screen: the steppers rebuild it.</summary>
    private sealed class Draft
    {
        /// <summary>The tournament being edited; null for a new one.</summary>
        public string? Id { get; init; }
        public string Name { get; set; } = "";
        /// <summary>The start day, in local time (date only).</summary>
        public DateTime Day { get; set; }
        public int Hour { get; set; }
        public int Minute { get; set; }
        public GameMode Mode { get; set; }
        public List<string> Maps { get; init; } = new();
        public ValueEvent<float> RoundsPerMap { get; init; } = new(3f);
        public ValueEvent<float> MinutesPerMap { get; init; } = new(5f);
        public ValueEvent<float> CupPoints { get; init; } = new(200f);
        public ValueEvent<float> Eliminated { get; init; } = new(1f);
        /// <summary>The Cup slider's range, widened to fit a saved value outside the usual one.</summary>
        public float CupMin { get; init; } = 10f;
        public float CupMax { get; init; } = 1000f;
        public string? Error { get; set; }
        /// <summary>Sent to the website, waiting for its answer.</summary>
        public bool Busy { get; set; }
        /// <summary>The name that was sent (for the message once saved).</summary>
        public string SentName { get; set; } = "";
        /// <summary>The website's answer, set off the game thread and taken by the screen's tick.</summary>
        public volatile Tournaments.HostResult? Result;

        // The maps screen.
        public string AddText { get; set; } = "";
        public string? MapsError { get; set; }
        public ScrollMemory Scroll { get; } = new();
    }

    private static Draft? draft;

    /// <summary>Opens the form for a new tournament, with the owner's defaults: Cup, official levels 1 to 6, 3 rounds per map, 200 points.</summary>
    public static void OpenCreate()
    {
        if (IsOpen(ScreenId))
        {
            return;
        }
        draft = new Draft
        {
            Day = DateTime.Today.AddDays(1),
            Hour = 20,
            Minute = 0,
            Mode = GameMode.Cup,
            Maps = Enumerable.Range(1, 6).Select(i => $"o:{i}").ToList(),
        };
        Show(ScreenId, Build, Signature);
    }

    /// <summary>Opens the form on an upcoming tournament, with its saved values.</summary>
    public static void OpenEdit(Tournaments.Tournament t)
    {
        if (IsOpen(ScreenId))
        {
            return;
        }
        DateTime start = t.StartsAt is { } at ? at.ToLocalTime().DateTime : DateTime.Today.AddDays(1).AddHours(20);
        float cup = t.CupPoints > 0 ? Math.Min(t.CupPoints, MaxCupPoints) : 200f;
        draft = new Draft
        {
            Id = t.Id,
            Name = t.Name,
            Day = start.Date,
            Hour = start.Hour,
            Minute = start.Minute,
            Mode = t.Mode is { } mode && Modes.Contains(mode) ? mode : GameMode.Cup,
            Maps = t.Maps.ToList(),
            RoundsPerMap = new(t.RoundsPerMap > 0 ? Math.Min(t.RoundsPerMap, MaxRoundsPerMap) : 3f),
            MinutesPerMap = new(t.MinutesPerMap > 0 ? Math.Min(t.MinutesPerMap, MaxMinutesPerMap) : 5f),
            CupPoints = new(cup),
            CupMin = Math.Min(10f, cup),
            CupMax = Math.Max(1000f, cup),
            Eliminated = new(t.EliminatedPerRound > 0 ? Math.Min(t.EliminatedPerRound, MaxEliminated) : 1f),
        };
        Show(ScreenId, Build, Signature);
    }

    /// <summary>The form is rebuilt when the maps change (on the maps screen).</summary>
    private static int Signature() => draft is { } d ? HashCode.Combine(string.Join(",", d.Maps), d.Maps.Count) : 0;

    private static int Num(ValueEvent<float> value) => (int)MathF.Round(value.Value);

    private static Element Build(Action close, Screen screen)
    {
        Draft d = draft!;
        TextInput name = Input(d.Name, MaxNameLength);
        name.OnTextInputChanged += text =>
        {
            // "#" (drawn as an icon by the game's font) and control characters are taken out as they're typed. The field
            // is rebuilt with the cleaned name, and the typing goes on in it.
            string clean = new string(text.Where(c => !char.IsControl(c) && c != '#').ToArray());
            d.Name = clean;
            if (clean != text)
            {
                screen.Refresh();
            }
        };

        screen.Every(() =>
        {
            if (d.Result is not { } result)
            {
                return;
            }
            d.Result = null;
            d.Busy = false;
            Tournaments.Refresh();
            MyTournaments.Reload();
            if (result.Ok)
            {
                Notifications.Good(d.Id == null ? Tr.TournamentCreated(d.SentName) : Tr.TournamentSaved(d.SentName));
                close();
                return;
            }
            d.Error = result.Error ?? Tr.TournamentHostError;
            screen.Refresh();
        });

        void Change(Action change)
        {
            change();
            d.Error = null;
            screen.Refresh();
        }

        void StepDay(int delta) => Change(() =>
        {
            DateTime first = DateTime.Today;
            DateTime last = first.AddDays(DaysAhead - 1);
            DateTime day = d.Day.AddDays(delta);
            d.Day = day < first ? last : day > last ? first : day;
        });

        void StepMinute(int delta) => Change(() =>
        {
            // On the quarter hours; a saved time between them goes to the next (or previous) one.
            int quarter = delta > 0 ? d.Minute / 15 + 1 : (d.Minute + 14) / 15 - 1;
            d.Minute = (quarter + 4) % 4 * 15;
        });

        void StepMode(int delta) => Change(() =>
        {
            int i = Array.IndexOf(Modes, d.Mode);
            d.Mode = Modes[((i < 0 ? 0 : i) + delta + Modes.Length) % Modes.Length];
        });

        void Submit()
        {
            if (d.Busy)
            {
                return;
            }
            string cleanName = CleanName(d.Name);
            DateTimeOffset startsAt = LocalStart(d);
            d.Error = cleanName == "" ? Tr.TournamentNameNeeded
                : startsAt <= DateTimeOffset.UtcNow ? Tr.TournamentStartInPast
                : d.Maps.Count == 0 ? Tr.TournamentNoMaps
                : d.Maps.Count > MaxMaps ? Tr.TournamentMaxMaps(MaxMaps)
                : d.Maps.FirstOrDefault(m => !WebsiteMapKey.IsMatch(m)) is { } bad ? Tr.SettingsUnknownMap(GameAccess.MapKeyToCode(bad))
                : null;
            if (d.Error != null)
            {
                screen.Refresh();
                return;
            }
            var sent = new Tournaments.Draft(cleanName, startsAt.ToUniversalTime(), d.Mode, d.Maps.ToList(),
                Math.Clamp(Num(d.RoundsPerMap), 1, MaxRoundsPerMap),
                Math.Clamp(Num(d.MinutesPerMap), 1, MaxMinutesPerMap),
                Math.Clamp(Num(d.CupPoints), 1, MaxCupPoints),
                Math.Clamp(Num(d.Eliminated), 1, MaxEliminated));
            d.Busy = true;
            d.SentName = cleanName;
            Task<Tournaments.HostResult> call = d.Id == null ? Tournaments.CreateAsync(sent) : Tournaments.UpdateAsync(d.Id, sent);
            call.ContinueWith(task => d.Result = task.IsCompletedSuccessfully ? task.Result : new Tournaments.HostResult(false, Tr.TournamentHostError),
                TaskScheduler.Default);
            screen.Refresh();
        }

        // Only the chosen mode's length settings: its own, plus rounds per map for the modes that rotate maps by rounds.
        // Those are Rounds, Hide and Seek, and also Cup and Elimination, which change maps every few rounds.
        Element? roundsPerMap = d.Mode == GameMode.TimeAttack ? null
            : Slider(Tr.SettingsRoundsPerMap, d.RoundsPerMap, Whole, 1f, MaxRoundsPerMap, 1f);
        Element? length = d.Mode switch
        {
            GameMode.TimeAttack => Slider(Tr.TournamentMinutesPerMap, d.MinutesPerMap, Whole, 1f, MaxMinutesPerMap, 1f),
            GameMode.Cup => Slider(Tr.TournamentCupPoints, d.CupPoints, Whole, d.CupMin, d.CupMax, 10f),
            GameMode.Elimination => Slider(Tr.TournamentKnockedOut, d.Eliminated, Whole, 1f, MaxEliminated, 1f),
            _ => null, // Rounds, Hide and Seek: rounds per map is their length
        };
        screen.HoldEscape = d.Busy; // the answer must be shown (an error) or acted on

        float mapsWidth = WidthBeside(Tr.TournamentChangeMaps);
        return Stack(
            Title(d.Id == null ? Tr.CreateTournamentButton : Tr.EditTournamentTitle),
            Row(Tr.TournamentNameField, Field(name)),
            Row(Tr.TournamentStartsAt, Line(
                Stepper(Tr.TournamentDay(d.Day), 520f, StepDay),
                Stepper(d.Hour.ToString("00"), 110f, delta => Change(() => d.Hour = (d.Hour + delta + 24) % 24)),
                Stepper(d.Minute.ToString("00"), 110f, StepMinute))),
            Text(Tr.TournamentLocalTimeHint),
            Row(Tr.DetailMode, Stepper(Tr.ModeName(d.Mode), 620f, StepMode)),
            Text(Tr.ModeDescription(d.Mode)),
            Row(Tr.DetailMaps, Line(
                Value(Fit(MapsSummary(d.Maps), mapsWidth), mapsWidth, left: true),
                Button(Tr.TournamentChangeMaps, OpenMaps))),
            roundsPerMap,
            length,
            Row(Tr.DetailLength, Live(screen,
                () => HashCode.Combine(Num(d.RoundsPerMap), Num(d.MinutesPerMap), Num(d.CupPoints), Num(d.Eliminated)),
                () => Value(Fit(LengthText(d), FieldWidth), FieldWidth, left: true))),
            d.Error != null ? ErrorText(d.Error) : null,
            d.Busy ? Text(Tr.TournamentSending) : null,
            Buttons(
                d.Busy ? null : Button(d.Id == null ? Tr.TournamentCreate : Tr.TournamentSave, Submit),
                d.Busy ? null : Button(Tr.Back, close)));
    }

    /// <summary>"&lt; value &gt;": the value between two buttons, at a fixed width so the buttons don't move.</summary>
    private static Element Stepper(string value, float width, Action<int> step) => Line(
        Button(Tr.StepPrevious, () => step(-1)),
        Value(Fit(value, width), width, left: false),
        Button(Tr.StepNext, () => step(1)));

    private static Element Value(string text, float width, bool left) =>
        new Label(text, width) { Anchor = new Vector2(left ? 0f : 0.5f, 0.5f) };

    /// <summary>A refusal or a mistake to fix, in the game's highlight color.</summary>
    internal static Element ErrorText(string text) => Text(text.Replace("#", "")).SetStyle(new Style { FontColor = Highlight });

    /// <summary>"6  -  A1, A2, A3, A4, A5, A6".</summary>
    private static string MapsSummary(IReadOnlyList<string> maps) =>
        maps.Count == 0 ? Tr.TournamentNoMaps : $"{maps.Count}  -  {string.Join(", ", maps.Select(GameAccess.MapKeyToCode))}".Replace("#", "");

    /// <summary>The length line: the same text as the Tournaments screen, plus the players an Elimination estimate assumes.</summary>
    private static string LengthText(Draft d)
    {
        LengthEstimate estimate = TournamentLength.Estimate(d.Mode, d.Maps.Count, Num(d.RoundsPerMap), Num(d.MinutesPerMap),
            Num(d.Eliminated), TournamentLength.DefaultPlayers);
        string text = Tr.TournamentLength(estimate, Num(d.CupPoints));
        return d.Mode == GameMode.Elimination ? $"{text} ({Tr.TournamentWithPlayers(TournamentLength.DefaultPlayers)})" : text;
    }

    /// <summary>The name as the website takes it: no control characters, no "#" (the game's font draws it as an icon).</summary>
    private static string CleanName(string name)
    {
        string clean = new string(name.Where(c => !char.IsControl(c) && c != '#').ToArray()).Trim();
        return clean.Length > MaxNameLength ? clean[..MaxNameLength].TrimEnd() : clean;
    }

    /// <summary>The chosen day and time in the host's time zone.</summary>
    private static DateTimeOffset LocalStart(Draft d) =>
        new(new DateTime(d.Day.Year, d.Day.Month, d.Day.Day, d.Hour, d.Minute, 0, DateTimeKind.Local));

    /// <summary>
    /// A map key as the website takes it ("o:12", "c:M123"), from a key of the game or of a rotation: the level version
    /// ("o:12:3") and the Workshop version ("c:M123.abc...") are dropped. Null if it can't be one.
    /// </summary>
    private static string? WebsiteKey(string key)
    {
        if (Msg.WorkshopId(key) is ulong workshopId)
        {
            key = $"c:M{workshopId}";
        }
        else if (key.Split(':') is { Length: >= 2 } parts && parts[0] == "o")
        {
            key = $"o:{parts[1]}";
        }
        return WebsiteMapKey.IsMatch(key) ? key : null;
    }

    // ---------- Maps ----------

    private static void OpenMaps()
    {
        if (draft is { } d)
        {
            d.MapsError = null;
            Show(MapsScreenId, BuildMaps);
        }
    }

    /// <summary>The tournament's maps: a list to reorder, levels added by code or "the map I'm on" (as the server's map rotation).</summary>
    private static Element BuildMaps(Action close, Screen screen)
    {
        Draft d = draft!;
        List<string> maps = d.Maps;
        TextInput add = Input(d.AddText, 2000, WidthBeside(Tr.SettingsAddButton));
        add.OnTextInputChanged += text => d.AddText = text;

        bool Fail(string error)
        {
            d.MapsError = error;
            screen.Refresh();
            return false;
        }

        bool Room(int adding) => maps.Count + adding <= MaxMaps || Fail(Tr.TournamentMaxMaps(MaxMaps));

        // Adds the codes typed (or pasted) in the field; nothing is added if one of them is unknown.
        bool AddTyped()
        {
            var keys = new List<string>();
            foreach (string part in d.AddText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string? key = GameAccess.CodeToMapKey(part) is { } known ? WebsiteKey(known) : null;
                if (key == null)
                {
                    return Fail(Tr.SettingsUnknownMap(part));
                }
                keys.Add(key);
            }
            if (keys.Count > 0)
            {
                if (!Room(keys.Count))
                {
                    return false;
                }
                maps.AddRange(keys);
                d.AddText = "";
                d.MapsError = null;
                d.Scroll.ToEnd();
                screen.Refresh();
            }
            return true;
        }

        void AddCurrentMap()
        {
            string current = MultiplayerClient.Instance.CurrentMapKey;
            if (current.StartsWith("l:", StringComparison.Ordinal))
            {
                Fail(Tr.SettingsLocalMap);
                return;
            }
            if (WebsiteKey(current) is not { } key)
            {
                Fail(Tr.SettingsNoCurrentMap);
                return;
            }
            if (Room(1))
            {
                maps.Add(key);
                d.MapsError = null;
                d.Scroll.ToEnd();
                screen.Refresh();
            }
        }

        void Move(int from, int to)
        {
            (maps[from], maps[to]) = (maps[to], maps[from]);
            screen.Refresh();
        }

        void Done()
        {
            if (AddTyped())
            {
                close(); // codes still in the field are added first, or the mistake is shown
            }
        }

        add.OnEnter += () => { AddTyped(); return true; };

        // One row per map: "3. A5 Name" then its buttons, aligned in columns.
        float buttons = ButtonWidth(Tr.MoveUp) + ButtonWidth(Tr.MoveDown) + ButtonWidth(Tr.RemoveButton) + 3 * 22f;
        float nameWidth = ScrollRowWidth - buttons;
        var rows = maps.Select((key, i) => Line(
            new Label(Fit($"{i + 1}. {GameAccess.DescribeMap(key)}".Replace("#", ""), nameWidth), nameWidth) { Anchor = new Vector2(0f, 0.5f) },
            Button(Tr.MoveUp, () => Move(i, i == 0 ? maps.Count - 1 : i - 1)),
            Button(Tr.MoveDown, () => Move(i, i == maps.Count - 1 ? 0 : i + 1)),
            Button(Tr.RemoveButton, () =>
            {
                maps.RemoveAt(i);
                d.MapsError = null;
                screen.Refresh();
            }))).ToList();

        return Stack(
            Title(Tr.TournamentMapsTitle),
            maps.Count == 0 ? Text(Tr.TournamentNoMaps) : ScrollList(rows, VisibleMaps, screen, d.Scroll),
            Row(Tr.SettingsAddMaps, Line(Field(add), Button(Tr.SettingsAddButton, () => AddTyped()))),
            Text(Tr.SettingsMapsHint),
            d.MapsError != null ? ErrorText(d.MapsError) : null,
            Buttons(
                Button(Tr.SettingsAddCurrentMap, AddCurrentMap),
                maps.Count > 0 ? Button(Tr.RemoveAllButton, () =>
                {
                    maps.Clear();
                    d.MapsError = null;
                    screen.Refresh();
                }) : null),
            Buttons(Button(Tr.Back, Done)));
    }
}
