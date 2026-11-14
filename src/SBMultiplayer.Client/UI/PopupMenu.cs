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
    private static Definition? pendingReopen;

    private static SE.UIAssets Ui => GameAccess.UiAssets;

    /// <summary>Opens a popup over the current scene (ignored if this popup is already open).</summary>
    /// <param name="build">Builds the content; can add updaters to the scene.</param>
    /// <param name="signature">When its value changes, the popup is rebuilt (e.g. admin status, player list).</param>
    public static void Show(string id, Func<Action, Scene, Element> build, Func<int>? signature = null) =>
        Show(new Definition(id, build, signature));

    public static bool IsOpen(string id) => OpenIds.Contains(id);

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
        scene.OnDispose += () => OpenIds.Remove(def.Id);
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
            if (escapeDown && !escapeWasDown)
            {
                close();
            }
            escapeWasDown = escapeDown;
        }
    }

    // ---------- Layout helpers ----------

    /// <summary>Vertical stack of the screen's elements (nulls are skipped).</summary>
    public static Element Stack(params Element?[] children) => new PanelStack(26f).AddMaybeNull(children);

    public static Element Title(string text) => new Label(text).SetStyle(Ui.StrongStyle);

    private static readonly Color SectionColor = new(255, 210, 90);

    /// <summary>
    /// Section header: regular font in gold, with some space above it. (The game's small bold font is
    /// hard to read, so the color does the job.)
    /// </summary>
    public static Element Section(string text) =>
        new Label(text) { Padding = Padding.GetTop(18f) }.SetStyle(new Style { Font = Ui.FontSmall, FontColor = SectionColor });

    public static Element Text(string text) => new Label(text);

    public static Element Button(string text, Action onClick) =>
        GameAccess.CreateButton(onClick)
            .SetStyle(new Style { ButtonAnimation = Ui.ButtonAnimation, ButtonPadding = new Padding(32f, 14f) })
            .Add(new Label(text));

    /// <summary>Horizontal line of elements (buttons…).</summary>
    public static Element Line(params Element[] items) => new PanelStack(22f, vertical: false).Add(items);

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

    /// <summary>Label on the left, field on the right (labels aligned on a common width).</summary>
    public static Element Row(string label, Element field, float labelWidth = 640f) =>
        new PanelStack(28f, vertical: false).Add(new Label(label, labelWidth) { Anchor = new Vector2(0f, 0.5f) }, field);

    public static TextInput Input(string text, float width, int maxLength) =>
        new(GameAccess.UpdateSpeed, text, width, Ui.FontSmall.Value.LineSpacing) { MaxTextLength = maxLength };

    /// <summary>A text field on the game's dark background.</summary>
    public static Element Field(TextInput input) => new Panel(0f, 0f, InputBackground, null, new Padding(14f)).Add(input);

    public static string Percent(string f) => MathF.Round(float.Parse(f) * 100f) + "%";

    public static string Seconds(string f) => MathF.Round(float.Parse(f)) + " s";

    public static string Whole(string f) => MathF.Round(float.Parse(f)).ToString();
}
