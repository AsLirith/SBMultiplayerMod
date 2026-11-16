using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Shared plumbing for the mod's menus: popups built with the game's UI elements (same buttons, sliders and
/// checkboxes as its options screens), closed with Escape, optionally rebuilt when their content changes,
/// plus small layout helpers so every screen looks the same.
/// </summary>
internal static class PopupMenu
{
    public static readonly Color InputBackground = new(0x5F, 0x5A, 0x59); // Colors.DarkBrighter of the game

    private sealed record Definition(string Id, Func<Action, Scene, Element> Build, Func<int>? Signature);

    private static readonly HashSet<string> OpenIds = new();

    /// <summary>The open popups with their close action, in opening order (the game stacks them as a chain).</summary>
    private static readonly List<(string Id, Action Close)> OpenPopups = new();
    private static Definition? pendingReopen;

    private static SE.UIAssets Ui => GameAccess.UiAssets;

    /// <summary>Opens a popup over the current scene (ignored if this popup is already open).</summary>
    /// <param name="build">Builds the content; can add updaters to the scene.</param>
    /// <param name="signature">When its value changes, the popup is rebuilt (e.g. admin status, player list).</param>
    public static void Show(string id, Func<Action, Scene, Element> build, Func<int>? signature = null) =>
        Show(new Definition(id, build, signature));

    public static bool IsOpen(string id) => OpenIds.Contains(id) || pendingReopen?.Id == id;

    /// <summary>One of the mod's menus is open.</summary>
    public static bool AnyOpen => OpenIds.Count > 0;

    /// <summary>Closes every menu of the mod, topmost first (the menu key pressed while a menu is open).</summary>
    public static void CloseAll()
    {
        pendingReopen = null;
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
            ModLog.Error($"Could not open the menu {def.Id}", e);
            Notifications.Warn(Tr.MenuOpenFailed);
        }
    }

    /// <summary>Called every global tick: reopens a popup that asked to be rebuilt.</summary>
    public static void Tick()
    {
        if (pendingReopen is { } def && !OpenIds.Contains(def.Id))
        {
            pendingReopen = null;
            Show(def);
        }
    }

    private static Scene Create(Definition def, Action close)
    {
        OpenIds.Add(def.Id);
        var scene = new Scene();
        Element content = def.Build(close, scene)
            .SetStyle(Ui.TextSmallStyle)
            .SetNavigationStartToFirstNavigatable();
        CanvasScale canvas = GameAccess.CreateCanvas();
        canvas.Add(GameAccess.CreateMainPanel(content));
        scene.Add(canvas);
        scene.Add(new Updater(def, close));
        OpenPopups.Add((def.Id, close));
        scene.OnDispose += () =>
        {
            OpenIds.Remove(def.Id);
            OpenPopups.RemoveAll(p => p.Id == def.Id);
        };
        return scene;
    }

    private sealed class Updater(Definition def, Action close) : IUpdate
    {
        private readonly int builtSignature = def.Signature?.Invoke() ?? 0;
        private bool escapeWasDown = true; // ignores an Escape still held when the popup opens

        public void Update()
        {
            if (def.Signature != null && def.Signature() != builtSignature)
            {
                pendingReopen = def;
                close();
                return;
            }
            bool escapeDown = Keyboard.GetState().IsKeyDown(Keys.Escape);
            if (escapeDown && !escapeWasDown && !KeyBindings.BlocksEscape)
            {
                close();
            }
            escapeWasDown = escapeDown;
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

    public static Element Button(string text, Action onClick)
    {
        string label = Fit(text, MenuWidth - 100f);
        Element button = GameAccess.CreateButton(onClick)
            .SetStyle(new Style { ButtonAnimation = Ui.ButtonAnimation, ButtonPadding = new Padding(32f, 14f) })
            .Add(new Label(label));
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

    /// <summary>Grid of elements, <paramref name="perRow"/> per line.</summary>
    public static Element Grid(IReadOnlyList<Element> items, int perRow)
    {
        var rows = new List<Element>();
        for (int i = 0; i < items.Count; i += perRow)
        {
            rows.Add(Line(items.Skip(i).Take(perRow).ToArray()));
        }
        return new PanelStack(18f).Add(rows);
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

    /// <summary>Width of a text field that shares its settings row with a button (<c>Row(label, Line(Field(input), Button(text)))</c>).</summary>
    public static float WidthBeside(string buttonText)
    {
        float button;
        try
        {
            button = Measure(buttonText) + 64f;
        }
        catch (Exception)
        {
            button = 700f;
        }
        return MathF.Max(300f, FieldWidth - 22f - button);
    }

    /// <summary>A text field on the game's dark background.</summary>
    public static Element Field(TextInput input) => new Panel(0f, 0f, InputBackground, null, new Padding(14f)).Add(input);

    public static string Percent(string f) => MathF.Round(float.Parse(f) * 100f) + "%";

    public static string Seconds(string f) => MathF.Round(float.Parse(f)) + " s";

    public static string Whole(string f) => MathF.Round(float.Parse(f)).ToString();
}
