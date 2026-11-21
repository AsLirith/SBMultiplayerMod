using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Client.UI;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Rooms;

/// <summary>
/// The tournaments: open, live and paused ones first, then upcoming ones, then the ended ones (the website's order).
/// Each row shows its status. The right panel shows the mode, the maps and the length, then the standings while the
/// tournament is live and once it's finished. They look like a leaderboard: place, name, then points and wins instead of
/// a time.
/// START joins an open tournament's waiting room, or brings a roster player back into a live one ("Rejoin"). The room
/// refuses anyone else. Approved hosts also get "Create tournament" and "My tournaments" under the list.
/// The first row is the tournament leaderboard. It ranks players by placing points over the finished tournaments, and has
/// a tab per mode like the game's Global / Friends tabs.
/// </summary>
internal sealed class TournamentsSource : ListSource
{
    /// <summary>How often the list is fetched again while the screen is open. Statuses and live standings change.</summary>
    private const double RefreshSeconds = 60;

    /// <summary>Standing lines that fit in the right panel under the tournament's details.</summary>
    private const int MaxRows = 10;

    /// <summary>Id of the leaderboard row (tournament ids are numbers).</summary>
    private const string LeaderboardId = "leaderboard";

    /// <summary>The leaderboard's tabs: every mode, then each tournament mode.</summary>
    private static readonly GameMode?[] LeaderboardModes =
        { null, GameMode.TimeAttack, GameMode.Rounds, GameMode.Cup, GameMode.Elimination, GameMode.HideAndSeek };

    /// <summary>The leaderboard's selected tab, kept while the game runs.</summary>
    private static GameMode? leaderboardMode;

    private double nextRefresh;

    /// <summary>The hosts' buttons under the list: shown once the website says we're an approved host.</summary>
    private Element? hostButtons;

    public override string RoomName => Tr.RoomTournaments;

    public override string EmptyText => Tournaments.ListState switch
    {
        Tournaments.State.Loading => Tr.PublicGamesSearching,
        Tournaments.State.Unavailable => Tr.TournamentsUnavailable,
        _ => Tr.NoTournaments,
    };

    /// <summary>The leaderboard first (once the list has arrived), then the tournaments.</summary>
    public override IReadOnlyList<ListEntry> Entries =>
        (Tournaments.ListState == Tournaments.State.Ready
            ? new[] { new ListEntry(LeaderboardId, Tr.TournamentLeaderboard, Tr.TournamentLeaderboard) }
            : Array.Empty<ListEntry>())
        .Concat(Tournaments.List.Select(t => new ListEntry(t.Id, $"{t.Name}  -  {Tr.TournamentStatus(t.Status, t.StartsAt)}", t.Name)))
        .ToList();

    public override void Open()
    {
        nextRefresh = NetClock.Now + RefreshSeconds;
        Tournaments.Refresh();
    }

    public override void Tick()
    {
        if (NetClock.Now >= nextRefresh)
        {
            nextRefresh = NetClock.Now + RefreshSeconds;
            Tournaments.Refresh();
        }
        if (hostButtons != null)
        {
            hostButtons.Visible = Tournaments.IsApprovedHost == true;
        }
    }

    private static Tournaments.Tournament? Find(ListEntry e) => Tournaments.List.FirstOrDefault(t => t.Id == e.Id);

    /// <summary>The website has standings while a tournament is live (so far) and once it's finished (final).</summary>
    private static bool HasStandings(Tournaments.Tournament t) => t.Status is "live" or "finished";

    public override string? Header(ListEntry? selected) =>
        selected?.Id == LeaderboardId ? Tr.LeaderboardHeader
        : selected != null && Find(selected) is { } t
            ? Tr.TournamentHeader(Tr.TournamentStatus(t.Status, t.StartsAt), t.Status == "upcoming" ? "" : Tr.TournamentDate(t.Date), t.Players, t.Rounds, t.Host)
            : null;

    public override string? StartLabel(ListEntry selected) => Find(selected) switch
    {
        { Status: "open", Code: not null } => Tr.JoinButton,
        { Status: "live", Code: not null } => Tr.RejoinButton,
        _ => null,
    };

    public override void Start(ListEntry selected)
    {
        if (Find(selected) is { Status: "open" or "live", Code: { } code } t)
        {
            // Our own tournament: rejoin as its room's admin when we know the password (our /mine list or this session).
            MultiplayerClient.Instance.JoinTournament(code, MyTournaments.KnownAdminPassword(t.Id));
        }
    }

    public override int DetailsSignature(ListEntry selected)
    {
        if (selected.Id == LeaderboardId)
        {
            var board = new HashCode();
            board.Add(leaderboardMode);
            board.Add(Tournaments.LeaderboardUnavailable(leaderboardMode));
            IReadOnlyList<Tournaments.LeaderRow>? rows = Tournaments.Leaderboard(leaderboardMode);
            board.Add(rows?.Count ?? -1);
            foreach (Tournaments.LeaderRow r in rows ?? Array.Empty<Tournaments.LeaderRow>())
            {
                board.Add(r);
            }
            return board.ToHashCode();
        }
        if (Find(selected) is not { } t)
        {
            return 0;
        }
        // Value fields only: the record itself is new after every refresh of the list, even when nothing changed.
        var hash = new HashCode();
        hash.Add(t.Status);
        hash.Add(t.Code);
        hash.Add(t.Players);
        hash.Add(t.Rounds);
        hash.Add(t.Date);
        hash.Add(t.Host);
        hash.Add(t.Mode);
        hash.Add(string.Join(",", t.Maps));
        hash.Add(t.RoundsPerMap);
        hash.Add(t.MinutesPerMap);
        hash.Add(t.CupPoints);
        hash.Add(t.EliminatedPerRound);
        if (HasStandings(t))
        {
            IReadOnlyList<Tournaments.Standing>? rows = Tournaments.Standings(t.Id);
            hash.Add(rows?.Count ?? -1);
            foreach (Tournaments.Standing s in rows ?? Array.Empty<Tournaments.Standing>())
            {
                hash.Add(s);
            }
        }
        return hash.ToHashCode();
    }

    public override Element Details(ListEntry selected)
    {
        if (selected.Id == LeaderboardId)
        {
            return LeaderboardDetails();
        }
        if (Find(selected) is not { } t)
        {
            return ListScreen.DetailText("");
        }
        string length = t.Mode is { } mode
            ? Tr.TournamentLength(TournamentLength.Estimate(mode, t.Maps.Count, t.RoundsPerMap, t.MinutesPerMap, t.EliminatedPerRound, t.Players), t.CupPoints)
            : "-";
        Element info = ListScreen.DetailRows(
            (Tr.DetailMode, t.Mode is { } m ? Tr.ModeName(m) : "-"),
            (Tr.DetailMaps, MapsText(t.Maps)),
            (Tr.DetailLength, length));
        Element? below = HasStandings(t) ? StandingsTable(Tournaments.Standings(t.Id))
            : t.Status == "incomplete" ? ListScreen.DetailText(Tr.TournamentNoStandings)
            : null;
        return new PanelStack(48f) { Anchor = Vector2.Zero, Skew = GameScreens.EdgeSkew }.AddMaybeNull(info, below);
    }

    /// <summary>"3  -  A1 Accelerate, A2 ..., ..." (shortened to the panel by its cell).</summary>
    private static string MapsText(IReadOnlyList<string> maps)
    {
        if (maps.Count == 0)
        {
            return "-";
        }
        string names = string.Join(", ", maps.Select(GameAccess.DescribeMap)).Replace("#", "");
        return maps.Count == 1 ? names : $"{maps.Count}  -  {names}";
    }

    private static Element StandingsTable(IReadOnlyList<Tournaments.Standing>? rows)
    {
        if (rows == null)
        {
            return ListScreen.DetailText(Tr.PublicGamesSearching);
        }
        if (rows.Count == 0)
        {
            return ListScreen.DetailText(Tr.StandingsUnavailable);
        }
        // Rows like the game's leaderboard (LeaderboardEntryPanelPlayer): a tilted band per player, with "1. Name" on the
        // left (the name cut to fit with "-") and the score on the right. Our own row is lighter. The bands follow the
        // right panel's edge, as the game's do. A player with flagged runs (time checks) gets a thinner band under
        // theirs. It counts as part of a row in the panel's budget.
        const float FlagRow = 0.6f;
        string mine = MyName();
        var bands = new List<Element>();
        float used = 0f;
        int shown = 0;
        foreach (Tournaments.Standing s in rows)
        {
            float cost = s.Flagged > 0 ? 1f + FlagRow : 1f;
            if (used + cost > MaxRows)
            {
                break;
            }
            used += cost;
            shown++;
            Color color = s.Name == mine ? GameScreens.LeaderboardMyRow : GameScreens.LeaderboardRow;
            string place = $"{s.Place}. ";
            bands.Add(Band(place, s.Name, Tr.TournamentScore(s.Points, s.Wins), color));
            if (s.Flagged > 0)
            {
                float height = RowHeight * FlagRow;
                bands.Add(GameScreens.RightPanelBand(color, RowWidth, height).Add(
                    new Panel(-1f, -1f, null, null, new Padding(24f + Measure(place), 0f, 0f, 0f)).Add(
                        SmallCell(Tr.TournamentFlagged(s.Flagged), NameWidth, 0.7f))));
            }
        }
        return Board(bands, rows.Count > shown ? Tr.MorePlayers(rows.Count - shown) : null);
    }

    private const float RowHeight = 84f; // LeaderboardList.RowHeight
    private const float RowWidth = 880f;
    private const float NameWidth = 440f; // the game's room for place and name

    /// <summary>A leaderboard row: "1. Name" on the left (the name cut to fit with "-"), the score on the right.</summary>
    private static Element Band(string place, string name, string score, Color color) =>
        GameScreens.RightPanelBand(color, RowWidth, RowHeight).Add(
            new PanelStack(0f, vertical: false, new Padding(24f, 0f, 0f, 0f)) { Anchor = new Vector2(0f, 0.5f) }.Add(
                new Label(place, 0f, RowHeight) { Anchor = new Vector2(0f, 0.5f) },
                NameLabel(name, NameWidth - Measure(place), RowHeight)),
            new Label(score, 0f, RowHeight) { Anchor = new Vector2(1f, 0.5f), Padding = new Padding(0f, 0f, 40f, 0f) });

    /// <summary>
    /// Rows under each other along the panel's edge, with an optional element above them and a line under them, in the
    /// leaderboard font.
    /// </summary>
    private static Element Board(IEnumerable<Element> bands, string? below, Element? above = null)
    {
        // The stack shifts each band by the panel's tilt, so they run along its edge.
        var list = new PanelStack(0f) { Anchor = Vector2.Zero, Skew = GameScreens.EdgeSkew }.Add(bands.ToArray());
        var stack = new PanelStack(14f) { Anchor = Vector2.Zero, Skew = GameScreens.EdgeSkew }
            .AddMaybeNull(new Panel(ListScreen.DetailsWidth, 1f), above, list);
        if (below != null)
        {
            stack.Add(new Label(below, ListScreen.DetailsWidth));
        }
        return stack.SetStyle(new Style { Font = GameScreens.Ui.FontLeaderboardLatin });
    }

    /// <summary>
    /// The leaderboard row's panel: the mode tabs, then the top players of the selected one (place, name, points and
    /// tournaments won), our own row lighter.
    /// </summary>
    private static Element LeaderboardDetails()
    {
        GameMode? mode = leaderboardMode;
        Element tabs = GameScreens.FilterTabs(LeaderboardModes
            .Select(m => (m is { } one ? Tr.ModeName(one) : Tr.LeaderboardAllModes, m == mode, (Action)(() => leaderboardMode = m)))
            .ToList(), RowWidth, perRow: 3);
        IReadOnlyList<Tournaments.LeaderRow>? rows = Tournaments.Leaderboard(mode);
        string? message = rows == null ? Tr.PublicGamesSearching
            : rows.Count > 0 ? null
            : Tournaments.LeaderboardUnavailable(mode) ? Tr.LeaderboardUnavailable
            : mode is { } named ? Tr.LeaderboardEmptyMode(Tr.ModeName(named))
            : Tr.LeaderboardEmpty;
        string mine = MyName();
        IEnumerable<Element> bands = (rows ?? Array.Empty<Tournaments.LeaderRow>()).Take(MaxRows)
            .Select(r => Band($"{r.Place}. ", r.Name, Tr.TournamentScore(r.Points, r.Wins),
                r.Name == mine ? GameScreens.LeaderboardMyRow : GameScreens.LeaderboardRow));
        return Board(bands, message, tabs);
    }

    /// <summary>Our name as the room server writes it in the standings (the Steam name, cleaned): "" without Steam.</summary>
    private static string MyName()
    {
        try
        {
            return Msg.SanitizeName(Steamworks.SteamFriends.GetPersonaName()).Replace("#", "");
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Width of a text in the leaderboard font (0 if the font can't draw it).</summary>
    private static float Measure(string text)
    {
        try
        {
            return GameScreens.Ui.FontLeaderboardLatin.Value.MeasureString(text).X;
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    /// <summary>A standings name, cut to <paramref name="width"/> with "-" and shown in full on hover (the game's way).</summary>
    private static Element NameLabel(string name, float width, float height)
    {
        string shown = CutName(name, width);
        return ListScreen.HoverIfCut(new Label(shown, 0f, height) { Anchor = new Vector2(0f, 0.5f) }, shown, name);
    }

    /// <summary>A name shortened to <paramref name="width"/>, ending with "-" when cut (as the game's leaderboard does).</summary>
    private static string CutName(string name, float width)
    {
        if (Measure(name) <= width)
        {
            return name;
        }
        string cut = name;
        while (cut.Length > 1 && Measure(cut + "-") > width)
        {
            cut = cut[..^1];
        }
        return cut.TrimEnd() + "-";
    }

    /// <summary>
    /// A cell in the leaderboard font, drawn at <paramref name="scale"/> and one scaled line high. The text is measured at
    /// that size and shortened to fit <paramref name="width"/>, so any language fits.
    /// </summary>
    private static Element SmallCell(string text, float width, float scale)
    {
        var font = GameScreens.Ui.FontLeaderboardLatin;
        float height = font.Value.LineSpacing * scale;
        string shown = text;
        try
        {
            while (shown.Length > 2 && font.Value.MeasureString(shown).X * scale > width)
            {
                shown = shown[..^1];
            }
        }
        catch (Exception)
        {
            // a character the font doesn't have: left as it is
        }
        return new Label(shown.TrimEnd(), width, height, setSizeToText: false) { Anchor = new Vector2(0f, 0.5f) }
            .SetStyle(new Style { FontScale = new Vector2(scale) });
    }

    /// <summary>
    /// Approved hosts: "Create tournament" and "My tournaments" (their screens: <see cref="TournamentEditor"/> and
    /// <see cref="MyTournaments"/>). Built hidden; <see cref="Tick"/> shows it once the website has said yes.
    /// </summary>
    public override Element? Footer()
    {
        const float Gap = 32f;
        float width = (ListScreen.ListWidth - 128f - Gap) / 2f;
        Element HostButton(string text, Action open) =>
            GameScreens.PlainButton(open, width, 110f).Add(GameScreens.Fit(new Label(text), text, GameScreens.Ui.FontStrong, width - 120f));
        hostButtons = new PanelStack(Gap, vertical: false).Add(
            HostButton(Tr.CreateTournamentButton, TournamentEditor.OpenCreate),
            HostButton(Tr.MyTournamentsButton, MyTournaments.Open));
        hostButtons.Visible = Tournaments.IsApprovedHost == true;
        return hostButtons;
    }
}
