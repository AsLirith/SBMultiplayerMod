using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Shared plumbing for the mod's menus: popups built with the game's UI elements (same buttons, sliders and
/// checkboxes as its options screens), closed with Escape, rebuilt in place when their content changes
/// (the popup stays open: no flicker, no sound), plus small layout helpers so every screen looks the same.
/// </summary>
internal static class PopupMenu
{
    public static readonly Color InputBackground = new(0x5F, 0x5A, 0x59); // Colors.DarkBrighter of the game

    // More of the game's Colors (internal to the game): Primary, PanelBackground, PanelSub.
    internal static readonly Color Highlight = new(0xE5, 0x50, 0x00);
    private static readonly Color PanelBackground = new(0x16, 0x16, 0x16);
    private static readonly Color PanelSub = Color.White * 0.05f;

    /// <summary>
    /// What a screen's builder can hook into: code run every tick while the screen is on top, code run when its
    /// content goes away (closed or rebuilt), and a way to ask for a rebuild.
    /// </summary>
    public sealed class Screen
    {
        private readonly List<Action> ticks = new();
        private readonly List<Action> leaving = new();

        internal bool RefreshRequested { get; private set; }

        /// <summary>Where the keyboard/gamepad selection (or the text cursor) goes back to after a rebuild.</summary>
        internal Restore? PendingRestore { get; set; }

        /// <summary>Runs every tick while the screen is the topmost one.</summary>
        public void Every(Action tick) => ticks.Add(tick);

        /// <summary>Runs when the content built by this call goes away: the screen closes or is rebuilt.</summary>
        public void OnLeave(Action action) => leaving.Add(action);

        /// <summary>Rebuilds the screen in place (after a click that changes what it shows).</summary>
        public void Refresh() => RefreshRequested = true;

        internal void Tick()
        {
            foreach (Action tick in ticks.ToArray())
            {
                tick();
            }
        }

        internal void Reset()
        {
            foreach (Action action in leaving.ToArray())
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    ModLog.Error("Menu: leave action failed", e);
                }
            }
            leaving.Clear();
            ticks.Clear();
            RefreshRequested = false;
        }
    }

    internal sealed record Restore(Canvas Canvas, Element Root, Vector2 Position, bool Typing)
    {
        public int Attempts { get; set; }
    }

    private sealed record Definition(string Id, Func<Action, Screen, Element> Build, Func<int>? Signature);

    private static readonly HashSet<string> OpenIds = new();

    /// <summary>The open popups with their close action, in opening order (the game stacks them as a chain).</summary>
    private static readonly List<(string Id, Action Close)> OpenPopups = new();

    private static SE.UIAssets Ui => GameAccess.UiAssets;

    /// <summary>Opens a popup over the current scene (ignored if this popup is already open).</summary>
    /// <param name="build">Builds the content; called again for every rebuild.</param>
    /// <param name="signature">When its value changes, the content is rebuilt in place (e.g. admin status, player list).</param>
    public static void Show(string id, Func<Action, Screen, Element> build, Func<int>? signature = null) =>
        Show(new Definition(id, build, signature));

    public static bool IsOpen(string id) => OpenIds.Contains(id);

    /// <summary>One of the mod's menus is open.</summary>
    public static bool AnyOpen => OpenIds.Count > 0;

    /// <summary>Closes every menu of the mod, topmost first (the menu key pressed while a menu is open).</summary>
    public static void CloseAll()
    {
        foreach ((string id, Action close) in Enumerable.Reverse(OpenPopups).ToArray())
        {
            try
            {
                close();
            }
            catch (Exception e)
            {
                ModLog.Error($"Could not close the menu {id}", e);
            }
        }
    }

    /// <summary>Small "are you sure?" popup in front of an action that can't be undone.</summary>
    public static void Confirm(string question, string yesLabel, Action onYes) => Show("confirm", (close, _) => Stack(
        Text(question),
        Buttons(
            Button(yesLabel, () =>
            {
                close();
                onYes();
            }),
            Button(Tr.Cancel, close))));

    private static void Show(Definition def)
    {
        if (OpenIds.Contains(def.Id) || GameAccess.Game == null)
        {
            return;
        }
        try
        {
            GameAccess.ShowPopup(close => Create(def, close));
        }
        catch (Exception e)
        {
            OpenIds.Remove(def.Id);
            OpenPopups.RemoveAll(p => p.Id == def.Id);
            ModLog.Error($"Could not open the menu {def.Id}", e);
            Notifications.Warn(Tr.MenuOpenFailed);
        }
    }

    private static Scene Create(Definition def, Action close)
    {
        var screen = new Screen();
        var scene = new Scene();
        Element content = def.Build(close, screen)
            .SetStyle(Ui.TextSmallStyle)
            .SetNavigationStartToFirstNavigatable();
        CanvasScale canvas = GameAccess.CreateCanvas();
        canvas.Add(GameAccess.CreateMainPanel(content));
        scene.Add(canvas);
        scene.Add(new Updater(def, close, screen, content));
        OpenIds.Add(def.Id);
        OpenPopups.Add((def.Id, close));
        scene.OnDispose += () =>
        {
            OpenIds.Remove(def.Id);
            OpenPopups.RemoveAll(p => p.Id == def.Id);
            screen.Reset();
        };
        return scene;
    }

    private sealed class Updater(Definition def, Action close, Screen screen, Element content) : IUpdate
    {
        private int builtSignature = def.Signature?.Invoke() ?? 0;
        private bool escapeWasDown = true; // ignores an Escape still held when the popup opens

        public void Update()
        {
            int signature = def.Signature?.Invoke() ?? 0;
            if (screen.RefreshRequested || signature != builtSignature)
            {
                builtSignature = signature;
                if (!Rebuild())
                {
                    return;
                }
            }
            bool escapeDown = Keyboard.GetState().IsKeyDown(Keys.Escape);
            if (escapeDown && !escapeWasDown && !KeyBindings.BlocksEscape)
            {
                close();
                return;
            }
            escapeWasDown = escapeDown;
            screen.Tick();
            RestoreSelection(screen);
        }

        /// <summary>Builds the content again and swaps it into the open popup.</summary>
        private bool Rebuild()
        {
            screen.Reset();
            try
            {
                Element fresh = def.Build(close, screen).SetStyle(Ui.TextSmallStyle);
                screen.PendingRestore = Replace(content, fresh) ?? screen.PendingRestore;
                content = fresh;
                return true;
            }
            catch (Exception e)
            {
                ModLog.Error($"Could not rebuild the menu {def.Id}", e);
                close();
                return false;
            }
        }
    }

    /// <summary>
    /// A part of a screen that rebuilds itself when <paramref name="signature"/> changes (a list that updates live),
    /// without touching the rest of the screen: text being typed elsewhere keeps its cursor.
    /// </summary>
    public static Element Live(Screen screen, Func<int> signature, Func<Element> build)
    {
        Element current = build();
        int built = signature();
        Element holder = new PanelStack(0f).Add(current);
        screen.Every(() =>
        {
            int now = signature();
            if (now == built)
            {
                return;
            }
            built = now;
            Element fresh = build();
            screen.PendingRestore = Replace(current, fresh) ?? screen.PendingRestore;
            current = fresh;
        });
        return holder;
    }

    /// <summary>
    /// Swaps an element of an open popup for a new one. The canvas must forget the old elements (selection,
    /// text focus, mouse capture); the returned value tells where to put the selection back, if it was in there.
    /// </summary>
    private static Restore? Replace(Element old, Element fresh)
    {
        Element parent = old.Parent ?? throw new InvalidOperationException("Element not shown");
        Canvas? canvas = old.GetParents().OfType<Canvas>().FirstOrDefault();
        Restore? restore = null;
        if (canvas != null)
        {
            bool Inside(Element? e) => e != null && (ReferenceEquals(e, old) || e.GetParents().Contains(old));
            Element? focus = canvas.Input.FocusElement;
            Element? selected = canvas.NavigateElement;
            bool typing = Inside(focus);
            Element? anchor = typing ? focus : Inside(selected) ? selected : null;
            if (anchor?.AbsoluteRect is { } rect)
            {
                restore = new Restore(canvas, fresh, new Vector2((rect.Left + rect.Right) / 2f, (rect.Top + rect.Bottom) / 2f), typing);
            }
            if (typing)
            {
                canvas.Input.FocusElement = null;
            }
            if (Inside(selected))
            {
                canvas.NavigateElement = null;
            }
            canvas.SetUpdateCatch(null);
        }
        int index = parent.Children.IndexOf(old);
        parent.Remove(old);
        parent.Insert(index, fresh);
        old.Dispose();
        return restore;
    }

    /// <summary>After a rebuild, once the new elements are laid out: selects the one where the selection was.</summary>
    private static void RestoreSelection(Screen screen)
    {
        if (screen.PendingRestore is not { } restore)
        {
            return;
        }
        Element? target = null;
        float best = float.MaxValue;
        foreach (Element e in restore.Root.GetAllVisibleChildren())
        {
            if (e.AbsoluteRect is not { } rect || !e.CanBeNavigated || (restore.Typing && e is not TextInput))
            {
                continue;
            }
            float distance = rect.DistanceToPoint(restore.Position).LengthSquared();
            if (distance < best)
            {
                best = distance;
                target = e;
            }
        }
        if (target == null)
        {
            if (++restore.Attempts > 30)
            {
                screen.PendingRestore = null; // nothing to select in the new content
            }
            return;
        }
        screen.PendingRestore = null;
        if (restore.Typing)
        {
            restore.Canvas.Navigate(target); // a text field: it takes the focus again
        }
        else if (restore.Canvas.NavigateElement == null)
        {
            restore.Canvas.NavigateElement = target;
        }
    }

    // ---------- Layout helpers ----------
    // One layout for every screen (canvas units: the game's UI is laid out on 3840 x 2160):
    // - every screen is MenuWidth wide; plain text wraps at that width;
    // - settings rows (checkbox, slider, field) have their label on the left, right-aligned on RowLabelWidth like
    //   the game's options, always on a single line (the game's rows have a fixed height): too long = shortened;
    // - text fields are FieldWidth wide, buttons never wider than the screen.

    public const float MenuWidth = 3300f;
    public const float RowLabelWidth = 1450f;
    public const float FieldWidth = MenuWidth - RowLabelWidth - LabelGap - 40f; // 40: the field's own padding

    /// <summary>Space between a settings label and its checkbox, slider or field.</summary>
    public const float LabelGap = 220f;

    /// <summary>The game's own gap inside a settings row (e.g. between a slider and its value).</summary>
    private const float RowGap = 32f;

    /// <summary>Invisible element that widens the gap after the label up to LabelGap (the rest of the row keeps RowGap).</summary>
    private static Element LabelSpacer() => new Label("", LabelGap - 2 * RowGap, 1f, setSizeToText: false);

    private static float Measure(string text) => Ui.FontSmall.Value.MeasureString(text).X;

    /// <summary>Shortens the text with "..." so that it fits on one line of <paramref name="width"/>.</summary>
    public static string Fit(string text, float width)
    {
        try
        {
            if (Measure(text) <= width)
            {
                return text;
            }
            int length = text.Length;
            while (length > 1 && Measure(text[..length].TrimEnd() + "...") > width)
            {
                length--;
            }
            return text[..length].TrimEnd() + "...";
        }
        catch (Exception)
        {
            return text; // character missing from the font: leave it to the game
        }
    }

    /// <summary>
    /// Vertical stack of the screen's elements (nulls are skipped). Always MenuWidth wide (an invisible line on top):
    /// settings rows are aligned on its left edge, so their label column is at the same place on every screen.
    /// </summary>
    public static Element Stack(params Element?[] children) =>
        new PanelStack(26f).Add(new Label("", MenuWidth, 1f, setSizeToText: false)).AddMaybeNull(children);

    public static Element Title(string text) => new Label(text).SetStyle(Ui.StrongStyle);

    /// <summary>
    /// Section header: regular font, like the rest of the text, with some space above it to separate the groups.
    /// (The game's small bold font is hard to read.)
    /// </summary>
    public static Element Section(string text) =>
        new Label(text) { Padding = Padding.GetTop(18f) }.SetStyle(new Style { Font = Ui.FontSmall });

    /// <summary>Plain text, wrapped at the screen's width.</summary>
    public static Element Text(string text) => new Label(text, MenuWidth);

    /// <summary>Estimated width of each button (text + padding), so button groups can be split into lines that fit.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Element, object> ButtonWidths = new();

    /// <param name="selected">The current choice of a group of buttons: its text takes the game's highlight color.</param>
    public static Element Button(string text, Action onClick, bool selected = false)
    {
        string label = Fit(text, MenuWidth - 100f);
        Element caption = new Label(label);
        if (selected)
        {
            caption.SetStyle(new Style { FontColor = Highlight });
        }
        Element button = GameAccess.CreateButton(onClick)
            .SetStyle(new Style { ButtonAnimation = Ui.ButtonAnimation, ButtonPadding = new Padding(32f, 14f) })
            .Add(caption);
        try
        {
            ButtonWidths.AddOrUpdate(button, Measure(label) + 64f);
        }
        catch (Exception)
        {
            // unknown width: counted as a wide button
        }
        return button;
    }

    /// <summary>Horizontal line of elements (buttons…).</summary>
    public static Element Line(params Element?[] items) => new PanelStack(22f, vertical: false).AddMaybeNull(items);

    /// <summary>A group of buttons on one line; split into several lines only if it would be wider than the screen (nulls are skipped).</summary>
    public static Element Buttons(params Element?[] buttons)
    {
        const float Gap = 22f;
        var lines = new List<Element>();
        var current = new List<Element>();
        float width = 0f;
        foreach (Element b in buttons.Where(b => b != null).Cast<Element>())
        {
            float w = ButtonWidths.TryGetValue(b, out object? known) ? (float)known : MenuWidth / 3f;
            if (current.Count > 0 && width + Gap + w > MenuWidth)
            {
                lines.Add(Line(current.ToArray()));
                current.Clear();
                width = 0f;
            }
            width += (current.Count > 0 ? Gap : 0f) + w;
            current.Add(b);
        }
        if (current.Count > 0)
        {
            lines.Add(Line(current.ToArray()));
        }
        return lines.Count == 1 ? lines[0] : new PanelStack(18f).Add(lines);
    }

    /// <summary>The lines of a grid of elements, <paramref name="perRow"/> per line (for <see cref="ScrollList"/>).</summary>
    public static List<Element> GridRows(IReadOnlyList<Element> items, int perRow)
    {
        var rows = new List<Element>();
        for (int i = 0; i < items.Count; i += perRow)
        {
            rows.Add(Line(items.Skip(i).Take(perRow).ToArray()));
        }
        return rows;
    }

    /// <summary>Width left for the rows of a <see cref="ScrollList"/> (its scroll bar takes the rest).</summary>
    public const float ScrollRowWidth = MenuWidth - ScrollBarWidth - 24f;

    private const float ScrollBarWidth = 64f;

    /// <summary>
    /// Vertical list of rows (buttons or lines of buttons). Longer than <paramref name="visible"/> rows: it gets
    /// that height and scrolls (mouse wheel, scroll bar, or keyboard selection), like the game's long screens.
    /// </summary>
    /// <param name="screen">With <paramref name="memory"/>: the scroll position survives the screen's rebuilds.</param>
    public static Element ScrollList(IReadOnlyList<Element> rows, int visible, Screen? screen = null, ScrollMemory? memory = null)
    {
        const float Gap = 18f;
        if (rows.Count <= visible)
        {
            if (memory != null)
            {
                memory.Position = 0f;
            }
            return new PanelStack(Gap).Add(rows.ToArray());
        }
        // Same width for every list, so short rows are centered like everywhere else.
        // Anchored at the top of the scroll area: the default (centered) hides the top of a list taller than it.
        Element stack = new PanelStack(Gap) { Anchor = Vector2.Zero }
            .Add(new Label("", ScrollRowWidth, 1f, setSizeToText: false)).Add(rows.ToArray());
        float rowHeight = Ui.FontSmall.Value.LineSpacing + 28f; // a button: one line of text + its padding
        float height = visible * (rowHeight + Gap);
        var scroll = new PanelScrollWithBar(PanelBackground, PanelSub, ScrollBarWidth, MenuWidth, height);
        scroll.SetButtonHoverStyle(new Style { FontColor = Highlight, TextureColor = Highlight });
        scroll.PanelScroll.Add(stack);
        if (screen != null && memory != null)
        {
            // Back where the list was (clamped to the new length); set directly: nothing is laid out yet.
            float max = (rows.Count - visible) * (rowHeight + Gap);
            float position = Math.Clamp(memory.Position, 0f, max);
            memory.Position = position;
            if (position > 0f)
            {
                scroll.PanelScroll.Padding = new Padding(0f, -position, 0f, 0f);
            }
            screen.OnLeave(() =>
            {
                if (!float.IsPositiveInfinity(memory.Position)) // (unless the next build was just told to show the end)
                {
                    memory.Position = scroll.PanelScroll.Scroll;
                }
            });
        }
        return scroll;
    }

    /// <summary>Label on the left (right-aligned, like the game's settings rows), field on the right.</summary>
    public static Element Row(string label, Element field) =>
        new PanelStack(RowGap, vertical: false) { Anchor = Vector2.Zero }
            .Add(new Label(Fit(label, RowLabelWidth), RowLabelWidth) { Anchor = new Vector2(1f, 0.5f) }, LabelSpacer(), field);

    /// <summary>Checkbox row of the game's options, with the common label width.</summary>
    public static Element Check(string label, IValue<bool> value)
    {
        Element row = GameAccess.OptionsUi.GetCheckboxRow(Fit(label, RowLabelWidth), value, RowLabelWidth);
        row.Insert(1, LabelSpacer());
        return row;
    }

    /// <summary>Slider row of the game's options, with the common label width.</summary>
    public static Element Slider(string label, IValue<float> value, Func<string, string> valueText, float min, float max, float step)
    {
        SE.SliderIntRow row = GameAccess.OptionsUi.GetSliderRow(Fit(label, RowLabelWidth), value, valueText, min, max, step);
        if (row.Children.FirstOrDefault() is Label rowLabel)
        {
            rowLabel.InitialWidth = RowLabelWidth;
            rowLabel.Width = RowLabelWidth;
        }
        row.Insert(1, LabelSpacer()); // label | spacer | slider | value
        return row;
    }

    /// <summary>Text field (FieldWidth wide unless told otherwise, e.g. MenuWidth for a field alone on its line).</summary>
    public static TextInput Input(string text, int maxLength, float width = FieldWidth) =>
        new(GameAccess.UpdateSpeed, text, width, Ui.FontSmall.Value.LineSpacing) { MaxTextLength = maxLength };

    /// <summary>Scroll position of a <see cref="ScrollList"/>, kept by the screen's own state across rebuilds.</summary>
    public sealed class ScrollMemory
    {
        public float Position { get; set; }

        /// <summary>The next build shows the end of the list (a row was just added there).</summary>
        public void ToEnd() => Position = float.PositiveInfinity;
    }

    /// <summary>Width of a button with this text.</summary>
    public static float ButtonWidth(string text)
    {
        try
        {
            return Measure(text) + 64f;
        }
        catch (Exception)
        {
            return 700f; // character missing from the font
        }
    }

    /// <summary>Width of a text field that shares its settings row with a button (<c>Row(label, Line(Field(input), Button(text)))</c>).</summary>
    public static float WidthBeside(string buttonText) => MathF.Max(300f, FieldWidth - 22f - ButtonWidth(buttonText));

    /// <summary>A text field on the game's dark background.</summary>
    public static Element Field(TextInput input) => new Panel(0f, 0f, InputBackground, null, new Padding(14f)).Add(input);

    public static string Percent(string f) => MathF.Round(float.Parse(f) * 100f) + "%";

    public static string Seconds(string f) => MathF.Round(float.Parse(f)) + " s";

    public static string Whole(string f) => MathF.Round(float.Parse(f)).ToString();
}
