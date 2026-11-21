using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.UI;

namespace SBMultiplayer.Client.Rooms;

/// <summary>One row of a list screen.</summary>
/// <param name="Id">Stable identity (the selection survives the list's updates).</param>
/// <param name="Label">Text of the row.</param>
/// <param name="Title">Big title shown in the top bar while the row is selected.</param>
internal sealed record ListEntry(string Id, string Label, string Title);

/// <summary>What a list screen shows: its rows, the details of the selected one, and what START does.</summary>
internal abstract class ListSource
{
    /// <summary>Small text of the top bar, above the title ("public games").</summary>
    public abstract string RoomName { get; }

    public abstract IReadOnlyList<ListEntry> Entries { get; }

    /// <summary>Shown instead of the rows when there are none (searching, nothing listed, unavailable).</summary>
    public abstract string EmptyText { get; }

    /// <summary>Big title while there are no rows.</summary>
    public virtual string EmptyTitle => Tr.EmptyListTitle;

    /// <summary>Changes whenever the rows (or the empty text) change: the list is rebuilt then.</summary>
    public virtual int Signature
    {
        get
        {
            var hash = new HashCode();
            hash.Add(EmptyText);
            foreach (ListEntry e in Entries)
            {
                hash.Add(e);
            }
            return hash.ToHashCode();
        }
    }

    /// <summary>Called when the screen opens, then every tick while it's shown (refreshes the data).</summary>
    public virtual void Open() { }

    public virtual void Tick() { }

    /// <summary>A line above the rows (e.g. the selected tournament's date and host). Null: none.</summary>
    public virtual string? Header(ListEntry? selected) => null;

    /// <summary>Content of the right panel for the selected row.</summary>
    public abstract Element Details(ListEntry selected);

    /// <summary>Changes when the selected row's details change (e.g. standings finished loading).</summary>
    public virtual int DetailsSignature(ListEntry selected) => 0;

    /// <summary>Text of the START button for this row; null hides it.</summary>
    public virtual string? StartLabel(ListEntry selected) => null;

    public virtual void Start(ListEntry selected) { }

    /// <summary>A fixed row under the list (e.g. a code to type). Built once: it isn't rebuilt with the rows.</summary>
    public virtual Element? Footer() => null;
}

/// <summary>
/// A list screen in the style of the game's Challenges screen. The rows are on the left, and the selected row's details
/// are in the dark panel on the right, where the leaderboard is. Its name is the big title, and START sits at the
/// bottom right. It's a scene of its own, built from the game's parts. The game's Challenges screen and save are never
/// touched.
/// </summary>
internal static class ListScreen
{
    /// <summary>Rows visible at once; more and the list scrolls.</summary>
    private const int VisibleRows = 6;

    public const float ListWidth = 1500f;
    private const float RowHeight = 128f;
    private const float RowGap = 16f;
    public const float DetailsWidth = 970f; // Leaderboard.Width

    /// <summary>The shown list screen's tooltip: text cut to fit (<see cref="Cell"/>) shows in full on hover.</summary>
    public static Tooltip? ShownTooltip { get; private set; }

    /// <summary>
    /// Shows <paramref name="full"/> on hover when <paramref name="shown"/> is a cut version of it, as the game's
    /// leaderboard does for long names.
    /// </summary>
    public static T HoverIfCut<T>(T element, string shown, string full) where T : Element
    {
        if (shown != full && ShownTooltip is { } tooltip)
        {
            element.TooltipEscaped(tooltip, full);
        }
        return element;
    }

    /// <summary>The selected row of each screen, kept while the game runs.</summary>
    private static readonly Dictionary<Type, string> Selected = new();

    public static void Open(ListSource source) => GameScreens.ChangeScene(() => Create(source));

    private static Scene Create(ListSource source)
    {
        var screen = new PopupMenu.Screen();
        Type key = source.GetType();

        ListEntry? Current()
        {
            IReadOnlyList<ListEntry> entries = source.Entries;
            if (entries.Count == 0)
            {
                return null;
            }
            return Selected.TryGetValue(key, out string? id) && entries.FirstOrDefault(e => e.Id == id) is { } e ? e : entries[0];
        }

        void Select(ListEntry entry)
        {
            Selected[key] = entry.Id;
            GameScreens.ClickSound();
        }

        void Start()
        {
            if (Current() is { } entry && source.StartLabel(entry) != null)
            {
                source.Start(entry);
            }
        }

        source.Open();
        Scene scene = GameScreens.CreateScene(MultiplayerRooms.Open, tooltip =>
        {
            screen.Reset();
            ShownTooltip = tooltip;
            Label title = GameScreens.Title(Current()?.Title ?? source.EmptyTitle);
            var startLabel = new LabelMarkup(Current() is { } first ? source.StartLabel(first) ?? "" : "", GameScreens.Creator);
            Element startButton = GameScreens.SkewedButton(Start, -1f, 160f, Vector2.Zero)
                .SetStyle(new Style { ButtonPadding = Padding.GetRight(32f) })
                .Add(startLabel);
            Element startPanel = new Panel(0f, 0f).Add(startButton);
            Element header = new Label("", ListWidth - 128f).SetStyle(GameScreens.Ui.TextSmallStyle);

            // Title, START and the header line follow the selection and the data, without rebuilding anything.
            screen.Every(() =>
            {
                ListEntry? entry = Current();
                string titleText = entry?.Title ?? source.EmptyTitle;
                if (title.Text != titleText)
                {
                    GameScreens.SetTitle(title, titleText);
                }
                string? start = entry == null ? null : source.StartLabel(entry);
                startPanel.Visible = start != null;
                if (start != null && startLabel.Text != start)
                {
                    startLabel.Text = start;
                }
                string headerText = source.Header(entry) ?? "";
                header.Visible = headerText != "";
                if (header is Label h && h.Text != headerText)
                {
                    h.Text = headerText;
                }
            });

            Element rows = PopupMenu.Live(screen,
                () => HashCode.Combine(source.Signature, Current()?.Id),
                () => BuildRows(source, Current(), Select));
            Element details = PopupMenu.Live(screen,
                () => Current() is { } e ? HashCode.Combine(e, source.DetailsSignature(e)) : 0,
                () => Current() is { } e ? source.Details(e) : new Panel(DetailsWidth, 1f));
            details.Anchor = Vector2.Zero; // from the panel's top, like the game's leaderboard: short details don't float mid-panel

            Element list = new PanelStack(RowGap, vertical: true, new Padding(64f), null, GameScreens.PanelBackground) { Width = ListWidth }
                .SetStyle(new Style { Font = GameScreens.Ui.FontStrong })
                .AddMaybeNull(header, rows, source.Footer());

            return new Element?[]
            {
                new Panel(-1f, -1f, null, null, new Padding(300f, 450f, 1450f, 460f)).Add(list),
                GameScreens.RightPanel(details),
                GameScreens.TopPanel(source.RoomName, title, tooltip),
                GameScreens.BottomPanel(
                        new Panel { Width = -1f, Height = 0f, Anchor = Vector2.Zero },
                        new PanelStack { Anchor = Vector2.Zero, Width = 750f, Height = 326f, Padding = new Padding(0f, 0f, 150f, 0f) }.Add(startPanel))
                    .Add(GameScreens.BackButton(MultiplayerRooms.Open)),
            };
        }, out _);
        scene.Add(new Updater(source, screen));
        scene.OnDispose += screen.Reset;
        return scene;
    }

    /// <summary>Room for a row's text: the row minus the panel's and button's padding (and the scroll bar if any).</summary>
    private static float RowTextWidth(int rows) => ListWidth - 128f - 16f - 120f - (rows > VisibleRows ? 88f : 0f);

    private static Element BuildRows(ListSource source, ListEntry? selected, Action<ListEntry> select)
    {
        IReadOnlyList<ListEntry> entries = source.Entries;
        if (entries.Count == 0)
        {
            return new Label(source.EmptyText, ListWidth - 128f).SetStyle(GameScreens.Ui.TextSmallStyle);
        }
        var rows = new List<Element>();
        foreach (ListEntry entry in entries)
        {
            bool isSelected = entry.Id == selected?.Id;
            Element label = GameScreens.Fit(new Label(entry.Label), entry.Label, GameScreens.Ui.FontStrong, RowTextWidth(entries.Count));
            Element button = GameScreens.PlainButton(() => select(entry), -1f, RowHeight).Add(label);
            if (isSelected)
            {
                button.SetNavigationStart();
            }
            // The game outlines the selected challenge with a white frame around its button.
            rows.Add(new PanelStack(64f, vertical: false, new Padding(8f), null, isSelected ? Color.White : null).Add(button));
        }
        if (rows.Count <= VisibleRows)
        {
            return new PanelStack(RowGap).Add(rows.ToArray());
        }
        const float BarWidth = 64f;
        float pitch = RowHeight + 16f + RowGap;
        var scroll = new PanelScrollWithBar(GameScreens.PanelBackground, GameScreens.PanelSub, BarWidth, ListWidth - 128f, VisibleRows * pitch);
        scroll.SetButtonHoverStyle(new Style { FontColor = GameScreens.Primary, TextureColor = GameScreens.Primary });
        scroll.PanelScroll.Add(new PanelStack(RowGap) { Anchor = Vector2.Zero }
            .Add(new Label("", ListWidth - 128f - BarWidth - 24f, 1f, setSizeToText: false))
            .Add(rows.ToArray()));
        return scroll;
    }

    /// <summary>Keeps the data fresh and runs the screen's live parts.</summary>
    private sealed class Updater(ListSource source, PopupMenu.Screen screen) : IUpdate
    {
        public void Update()
        {
            try
            {
                source.Tick();
                screen.Tick();
                PopupMenu.RestoreSelection(screen);
            }
            catch (Exception e)
            {
                ModLog.Error($"List screen {source.GetType().Name}", e);
            }
        }
    }

    // ---------- Details helpers ----------

    /// <summary>Lines of "label   value" in the leaderboard's font, for the right panel.</summary>
    public static Element DetailRows(params (string Label, string Value)[] rows)
    {
        var stack = new PanelStack(18f) { Anchor = Vector2.Zero, Skew = GameScreens.EdgeSkew }.Add(new Panel(DetailsWidth, 1f));
        foreach ((string label, string value) in rows)
        {
            stack.Add(new PanelStack(32f, vertical: false) { Anchor = Vector2.Zero }.Add(
                Cell(label, 420f),
                Cell(value, DetailsWidth - 460f)));
        }
        return stack.SetStyle(new Style { Font = GameScreens.Ui.FontLeaderboardLatin });
    }

    /// <summary>
    /// A column of a detail line: a fixed width, one line of the leaderboard font high, the text shortened to fit.
    /// The size has to be fixed, because a label told to fill its row collapses inside a stack.
    /// </summary>
    /// <param name="alignRight">Right-aligned (numbers, scores); left-aligned otherwise.</param>
    public static Element Cell(string text, float width, bool alignRight = false)
    {
        var font = GameScreens.Ui.FontLeaderboardLatin;
        float height = font.Value.LineSpacing;
        string shown = text;
        try
        {
            while (shown.Length > 2 && font.Value.MeasureString(shown + "..").X > width)
            {
                shown = shown[..^1];
            }
            if (shown != text)
            {
                shown = shown.TrimEnd() + "..";
            }
        }
        catch (Exception)
        {
            // a character the font doesn't have: left as it is
        }
        return HoverIfCut(new Label(shown, width, height, setSizeToText: false) { Anchor = new Vector2(alignRight ? 1f : 0f, 0.5f) }, shown, text);
    }

    /// <summary>Wrapped text in the right panel (a message instead of details).</summary>
    public static Element DetailText(string text) =>
        new PanelStack(0f) { Anchor = Vector2.Zero }.Add(new Panel(DetailsWidth, 1f), new Label(text, DetailsWidth))
            .SetStyle(new Style { Font = GameScreens.Ui.FontLeaderboardLatin });
}
