using System.Reflection;
using BytingLib;
using BytingLib.UI;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.Ghosts;
using SE;

namespace SBMultiplayer.Client.Rooms;

/// <summary>
/// The game's own building blocks for its full-screen menus (the Rooms screen and the level / challenge selection).
/// They are private or internal, so we reach them by reflection. The Multiplayer screens are assembled from them and
/// look and behave like the game's: same panels, buttons, fonts, tilt, background and sounds.
/// Nothing here is patched. Every member is looked up once, and a missing one throws (the caller falls back to F8).
/// </summary>
internal static class GameScreens
{
    /// <summary>Skew of the room buttons (GlobalGame.levelSelectionSkewAngleTopRight).</summary>
    public static readonly float RoomSkew = MathHelper.ToRadians(18f);

    /// <summary>Skew of the level selection's side panels (Leaderboard.SkewAngle).</summary>
    public static readonly float PanelSkew = MathHelper.ToRadians(21f);

    // The game's Colors (internal): Dark, PanelSub, TransparentBar, Primary.
    public static readonly Color PanelBackground = new(0x16, 0x16, 0x16);
    public static readonly Color PanelSub = Color.White * 0.05f;
    public static readonly Color TransparentBar = Color.Black * 0.75f;
    public static readonly Color Primary = new(0xE5, 0x50, 0x00);

    /// <summary>The game's leaderboard rows (Colors.LeaderboardBackground), and our own row (Colors.LeaderboardMyBackColor).</summary>
    public static readonly Color LeaderboardRow = GameColor("LeaderboardBackground", new Color(0x21, 0x21, 0x21));
    public static readonly Color LeaderboardMyRow = GameColor("LeaderboardMyBackColor", new Color(0x44, 0x44, 0x44));

    private static readonly Type G = typeof(GlobalGame);
    private static readonly Type RoomButtonType = GameType("SE.RoomButton");
    private static readonly Type LevelNameType = GameType("SE.LabelLevelName");
    private static readonly Type BackgroundType = GameType("SE.ScreenBackground");
    private static readonly Type LocaReloadType = GameType("SE.SubscribeToLocaReload");
    private static readonly Type PanelSkewedType = GameType("SE.PanelSkewed");
    private static readonly Type RightPanelType = GameType("SE.LevelSelectionRightPanel");
    private static readonly Type FilterButtonType = GameType("SE.LeaderboardFilterButton");
    private static readonly ConstructorInfo FilterButtonCtor = AccessTools.FirstConstructor(FilterButtonType, c => c.GetParameters().Length == 6)
        ?? throw new MissingMethodException("SE.LeaderboardFilterButton", ".ctor");
    private static readonly PropertyInfo FilterButtonActivated = AccessTools.Property(FilterButtonType, "Activated")
        ?? throw new MissingMemberException("SE.LeaderboardFilterButton", "Activated");

    /// <summary>The strip under the leaderboard's tabs (LeaderboardList.MedalPanelColor).</summary>
    private static readonly Color TabStrip = Color.Lerp(LeaderboardRow, Color.Black, 0.25f);

    private static readonly MethodInfo SimpleScene = Method("CreateSimpleScene",
        new[] { typeof(Action), typeof(Action), typeof(bool), typeof(Func<Element?[]>), typeof(Tooltip) });
    private static readonly MethodInfo TooltipMethod = Method("CreateTooltip");
    private static readonly MethodInfo LoaderMethod = Method("CreateContentLoader", new[] { typeof(DisposableContainer) });
    private static readonly MethodInfo TopPanelMethod = Method("CreateLevelSelectTopPanel");
    private static readonly MethodInfo BottomPanelMethod = Method("CreateLevelSelectBottomPanel");
    private static readonly MethodInfo SkewedButtonMethod = Method("CreateSkewedButtonWithoutFace");
    private static readonly MethodInfo ButtonMethod = Method("CreateButton");
    private static readonly MethodInfo ModifyCanvasMethod = Method("ModifyRoomOrLevelSelectionCanvas");
    private static readonly MethodInfo MenuSceneMethod = Method("CreateMenueScene");
    private static readonly MethodInfo ClickSoundMethod = Method("OnButtonClick", Type.EmptyTypes);
    private static readonly MethodInfo HoverSoundMethod = Method("OnUIHover");
    private static readonly FieldInfo ShadersField = Field("shaders");
    private static readonly FieldInfo WindowManagerField = Field("windowManager");
    private static readonly FieldInfo CreatorField = Field("creator");
    private static readonly FieldInfo DeviceField = Field("gDevice");
    private static readonly FieldInfo AudioField = Field("audioLayer");

    private static GlobalGame Game => GameAccess.Game ?? throw new InvalidOperationException("the game isn't ready");

    public static UIAssets Ui => GameAccess.UiAssets;

    public static Creator Creator => (Creator)CreatorField.GetValue(Game)!;

    public static GraphicsDevice Device => (GraphicsDevice)DeviceField.GetValue(Game)!;

    // ---------- Scenes ----------

    /// <summary>
    /// A full-screen menu like the game's Rooms screen: <paramref name="build"/> makes its elements (called again when
    /// the language changes), Escape runs <paramref name="back"/>, and the canvas gets the game's tilt and user bar.
    /// </summary>
    public static Scene CreateScene(Action back, Func<Tooltip, Element?[]> build, out ContentLoader load)
    {
        var disposables = new DisposableContainer();
        load = (ContentLoader)LoaderMethod.Invoke(Game, new object[] { disposables })!;
        Tooltip tooltip = (Tooltip)TooltipMethod.Invoke(Game, null)!;
        Func<Element?[]> elements = () => build(tooltip);
        // The tooltip goes in once, by ModifyRoomOrLevelSelectionCanvas below, as in the game's own screens. If the scene
        // got it too, it would be updated twice per frame, and the second update resets the wait before it shows.
        Scene scene = (Scene)SimpleScene.Invoke(Game, new object?[] { back, null, false, elements, null })!;
        scene.Add(disposables);
        object shaders = ShadersField.GetValue(Game)!;
        object? gammaPost = Traverse.Create(shaders).Property("GammaPost").Property("Effect").GetValue();
        scene.Insert(0, Activator.CreateInstance(BackgroundType,
            load.Textures.Backgrounds.Screens.RoomSelectTex, WindowManagerField.GetValue(Game), gammaPost)!);
        Action modify = () => ModifyCanvasMethod.Invoke(Game, new object?[] { scene, tooltip, null, null, null, null });
        scene.Add(Activator.CreateInstance(LocaReloadType, modify, true)!);
        return scene;
    }

    /// <summary>
    /// Switches to a scene of the mod. If building it fails (a game update moved something), the game's main menu is
    /// shown instead, with the F8 menu on top. The mod's menus still work, and the game never sees the error.
    /// </summary>
    public static void ChangeScene(Func<Scene> create) => Game.ChangeScene(() =>
    {
        try
        {
            return create();
        }
        catch (Exception e)
        {
            ModLog.Error("Could not build a Multiplayer screen (the F8 menu opens instead)", e);
            Scene menu = (Scene)MenuSceneMethod.Invoke(Game, null)!;
            menu.Add(new OnFirstUpdate(UI.MultiplayerMenu.Open));
            return menu;
        }
    });

    /// <summary>Runs an action once, on the scene's first update (once it's the current scene).</summary>
    private sealed class OnFirstUpdate(Action action) : IUpdate
    {
        private Action? pending = action;

        public void Update()
        {
            Action? run = pending;
            pending = null;
            run?.Invoke();
        }
    }

    /// <summary>Back to the game's main menu, as its Rooms screen does.</summary>
    public static void ToMainMenu()
    {
        PlayBackSound();
        ChangeScene(() => (Scene)MenuSceneMethod.Invoke(Game, null)!);
    }

    // ---------- Panels ----------

    /// <summary>Top bar: small room name above the big title (the selected entry's name).</summary>
    public static Element TopPanel(string smallText, Label title, Tooltip tooltip) =>
        (Element)TopPanelMethod.Invoke(Game, new object?[] { smallText, title, null, tooltip, null })!;

    /// <summary>Bottom bar, with the given content on its right (START, other buttons).</summary>
    public static Element BottomPanel(params Element[] inner) =>
        (Element)BottomPanelMethod.Invoke(Game, new object?[] { null, inner })!;

    /// <summary>Room for the big title: from the left of the top bar to the user bar (achievements...).</summary>
    private const float TitleWidth = 2100f;

    /// <summary>The big title of the top bar (the game's LabelLevelName).</summary>
    public static Label Title(string text)
    {
        var label = (Label)Activator.CreateInstance(LevelNameType, text, 8f, 0f, 0f, true)!;
        label.Anchor = Vector2.Zero;
        label.SetStyle(new Style { FontAndEdit = Ui.FontStrongBigItalicAndEdit, StringEdit = Ui.StringEditStrong });
        SetTitle(label, text);
        return label;
    }

    /// <summary>Changes the big title, shrinking a long one so it doesn't run into the user bar.</summary>
    public static void SetTitle(Label title, string text)
    {
        title.Text = text;
        if (title.Style == null)
        {
            return;
        }
        try
        {
            float width = Ui.FontStrongBigItalic.Value.MeasureString(text).X;
            title.Style.FontScale = width > TitleWidth ? new Vector2(TitleWidth / width) : null;
        }
        catch (Exception)
        {
            title.Style.FontScale = null; // a character the font doesn't have: full size
        }
    }

    /// <summary>
    /// The level selection's own right panel (SE.LevelSelectionRightPanel), built the way the game builds it for the
    /// leaderboard. It is as wide as <paramref name="inner"/>. Its tilted edge pivots at the top and runs on past the
    /// screen's top, bottom and right. The content starts at the top, and its stacks use <see cref="EdgeSkew"/> to follow
    /// the edge.
    /// </summary>
    public static Element RightPanel(Element inner)
    {
        // The leaderboard's box (Leaderboard.Width and its height with tabs), widened by how far its lowest line moves right.
        const float Width = 970f;
        const float Height = 1072f;
        // The game's box also holds the medal and time under the leaderboard. It is centred in the panel, so its top sits
        // this much higher (measured on screen against a level's leaderboard). Ours is as tall and starts at the same height.
        const float Below = 500f;
        float oversize = EdgeSkew * Height;
        var panel = (Panel)Activator.CreateInstance(RightPanelType, PanelSkew, 0f, -1f, (Color?)TransparentBar, null, null)!;
        panel.Anchor = new Vector2(1f, 0.5f);
        panel.Padding = new Padding(32f);
        Element box = new Panel(Width + oversize, Height + Below).Add(inner);
        return new Panel { Padding = new Padding(0f, 430f, 0f, 420f) }.Add(panel.Add(box));
    }

    /// <summary>
    /// How far a line of the right panel moves sideways per unit down, to follow the panel's tilted edge (set as the Skew
    /// of the vertical stacks inside it, as the game's leaderboard list does).
    /// </summary>
    public static float EdgeSkew => MathF.Tan(PanelSkew);

    // ---------- Buttons ----------

    /// <summary>One of the big room pictures of the Rooms screen.</summary>
    public static Button RoomButton(Ref<Texture2D> picture, string name, Action onClick, float width, float height)
    {
        object shaders = ShadersField.GetValue(Game)!;
        object? grayscale = Traverse.Create(shaders).Property("Grayscale").Property("Effect").GetValue();
        var button = (Button)Activator.CreateInstance(RoomButtonType, picture, onClick, RoomSkew, grayscale, width, height, null, null)!;
        button.Style = new Style { FontAndEdit = Ui.FontStrongItalicAndEdit, ButtonPadding = new Padding(0f) };
        button.HoverStyle = new Style { FontColor = Primary, ButtonPadding = new Padding(0f) };
        Traverse.Create(button).Property("HoverColor").SetValue(Color.White * 0.3f);
        button.OnBeforeClick += ClickSound;
        button.OnHoverEnter += HoverSound;
        // The room's name, on a skewed band across the picture (as the game's own rooms).
        var padding = new Padding(-MathF.Sin(RoomSkew) * 16f, 16f, -40f, -16f);
        button.Add(
            new Panel(-1f, -1f, null, null, padding).Add(Skewed(-RoomSkew, PanelBackground, height: 200f)),
            Skewed(-RoomSkew, Color.White, height: 200f).Add(Fit(new LabelMarkup(name, Creator) { Anchor = new Vector2(0.5f, 0.5f) }, name, Ui.FontStrongItalic, width - 110f)));
        return button;
    }

    /// <summary>
    /// Shrinks a label's text so its widest line fits in <paramref name="maxWidth"/> (canvas units), measured with the
    /// font it's drawn with. Text that already fits is left alone.
    /// </summary>
    public static T Fit<T>(T label, string text, Ref<SpriteFont> font, float maxWidth) where T : Element
    {
        try
        {
            float widest = text.Split('\n').Max(line => font.Value.MeasureString(line).X);
            if (widest > maxWidth)
            {
                label.SetStyle(new Style { FontScale = new Vector2(maxWidth / widest) });
            }
        }
        catch (Exception)
        {
            // a character the font doesn't have: drawn at full size
        }
        return label;
    }

    /// <summary>A band tilted like <see cref="RightPanel"/>'s edge (a leaderboard row); -1 fills the parent.</summary>
    public static Panel RightPanelBand(Color color, float width = -1f, float height = -1f) => Skewed(PanelSkew, color, width, height);

    /// <summary>
    /// A row of tabs like the leaderboard's Global / Friends ones. It uses the game's LeaderboardFilterButton on a darker
    /// tilted strip. The selected tab is dark and still, the others are dimmed until hovered. There are
    /// <paramref name="perRow"/> tabs per strip, with more strips under it following the panel's edge. Each strip's tabs
    /// share <paramref name="width"/> in proportion to their text. All are drawn at one scale, shrunk when the widest
    /// strip would not fit.
    /// </summary>
    public static Element FilterTabs(IReadOnlyList<(string Text, bool Selected, Action Pick)> tabs, float width, int perRow, float height = 64f)
    {
        const float Pad = 28f; // room around each tab's text
        Ref<SpriteFont> font = Ui.FontLeaderboardLatin;
        float[] widths = tabs.Select(t => MeasureOrZero(font, t.Text)).ToArray();
        var rows = Enumerable.Range(0, (tabs.Count + perRow - 1) / perRow)
            .Select(r => Enumerable.Range(r * perRow, Math.Min(perRow, tabs.Count - r * perRow)).ToArray()).ToList();
        float scale = rows.Min(r => MathF.Min(1f, (width - Pad * r.Length) / MathF.Max(1f, r.Sum(i => widths[i]))));
        var strips = new PanelStack(0f) { Anchor = Vector2.Zero, Skew = EdgeSkew };
        foreach (int[] indexes in rows)
        {
            strips.Add(new Panel(width, height) { Anchor = Vector2.Zero }.Add(Skewed(PanelSkew, TabStrip), TabRow(indexes)));
        }
        return strips;

        Element TabRow(int[] indexes)
        {
            // A strip wider than its tabs need: the room left is shared out.
            float spare = (width - indexes.Sum(i => widths[i] * scale + Pad)) / indexes.Length;
            var row = new PanelStack(0f, vertical: false) { Anchor = Vector2.Zero };
            foreach (int i in indexes)
            {
                row.Add(Tab(tabs[i], widths[i] * scale + Pad + spare));
            }
            return row;
        }

        Element Tab((string Text, bool Selected, Action Pick) tab, float tabWidth)
        {
            (string text, bool selected, Action pick) = tab;
            var button = (Button)FilterButtonCtor.Invoke(new object?[] { pick, -PanelSkew, tabWidth, height, null, null });
            var label = new Label(text) { Anchor = new Vector2(0.5f, 0.5f) };
            if (scale < 1f)
            {
                label.SetStyle(new Style { FontScale = new Vector2(scale) });
            }
            button.Add(label);
            // Leaderboard.EnableTabButton / DisableTabButton.
            button.Style = new Style();
            button.HoverStyle = new Style();
            button.Enabled = !selected;
            if (!selected)
            {
                button.Style.FontColor = Color.White * 0.5f;
                button.Style.TextureColor = Color.White * 0.5f;
                button.HoverStyle.FontColor = Color.White;
                button.HoverStyle.TextureColor = Color.White;
                button.OnBeforeClick += ClickSound;
            }
            FilterButtonActivated.SetValue(button, selected);
            return button;
        }
    }

    private static float MeasureOrZero(Ref<SpriteFont> font, string text)
    {
        try
        {
            return font.Value.MeasureString(text).X;
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    /// <summary>One of the game's colors (the internal SE.Colors), or <paramref name="fallback"/> if it's gone.</summary>
    private static Color GameColor(string name, Color fallback)
    {
        try
        {
            return AccessTools.Field(AccessTools.TypeByName("SE.Colors"), name)?.GetValue(null) is Color c ? c : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    /// <summary>A skewed panel (the game's PanelSkewed); -1 fills the parent.</summary>
    private static Panel Skewed(float skew, Color color, float width = -1f, float height = -1f) =>
        (Panel)Activator.CreateInstance(PanelSkewedType, skew, width, height, (Color?)color, null, null)!;

    /// <summary>Skewed text button without a face: the level selection's "back" and "START".</summary>
    public static Button SkewedButton(Action onClick, float width = 0f, float height = 0f, Vector2? anchor = null) =>
        (Button)SkewedButtonMethod.Invoke(Game, new object?[] { onClick, width, height, anchor, null })!;

    /// <summary>The game's plain button (the rows of the challenge list).</summary>
    public static Button PlainButton(Action onClick, float width = 0f, float height = 0f) =>
        (Button)ButtonMethod.Invoke(Game, new object?[] { onClick, width, height, null, null })!;

    /// <summary>The level selection's "back" button, at its usual place above the bottom bar.</summary>
    public static Element BackButton(Action back) =>
        new Panel(-1f, -1f, null, null, new Padding(75f, -535f, 0f, 0f))
            .Add(SkewedButton(back, 250f, 200f, new Vector2(0f, 0.5f)).Add(new LabelMarkup(Loca.Screen.LevelSelection.back, Creator)));

    public static void ClickSound() => ClickSoundMethod.Invoke(Game, null);

    public static void HoverSound() => HoverSoundMethod.Invoke(Game, null);

    private static void PlayBackSound()
    {
        try
        {
            object audio = AudioField.GetValue(Game)!;
            Type eventType = AccessTools.TypeByName("SE.AudioEvent");
            AccessTools.Method(audio.GetType(), "Fire", new[] { eventType })
                ?.Invoke(audio, new[] { Enum.Parse(eventType, "sfx_ui_back") });
        }
        catch (Exception)
        {
            // no sound
        }
    }

    // ---------- Lookups ----------

    private static Type GameType(string name) => AccessTools.TypeByName(name) ?? throw new TypeLoadException(name);

    /// <summary>A method of GlobalGame; <paramref name="args"/> picks one overload (null: the only one).</summary>
    private static MethodInfo Method(string name, Type[]? args = null) =>
        AccessTools.Method(G, name, args) ?? throw new MissingMethodException(G.Name, name);

    private static FieldInfo Field(string name) => AccessTools.Field(G, name) ?? throw new MissingFieldException(G.Name, name);
}
