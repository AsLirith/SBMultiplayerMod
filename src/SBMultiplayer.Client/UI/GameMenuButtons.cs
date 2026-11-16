using System.Reflection;
using System.Runtime.CompilerServices;
using BytingLib;
using BytingLib.UI;
using HarmonyLib;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SE;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// "Multiplayer" button in the game's own menus (main menu and pause menu), so the mod's menu can be found
/// without knowing its key. Nothing is patched: once a tick, the canvases of these two menus are looked at,
/// and a button built like their own is inserted into each new one (the game recreates them, e.g. when the
/// language changes). A menu this code doesn't recognize is left alone.
/// </summary>
internal static class GameMenuButtons
{
    /// <summary>Step of the main menu's "staircase": each button is indented this much more than the one above.</summary>
    private const float MainMenuIndent = 48f;

    private static readonly Type? PauseSceneType = AccessTools.TypeByName("SE.PauseScene");
    private static readonly Type? MainMenuButtonType = AccessTools.Inner(typeof(GlobalGame), "ButtonMainMenuePanel");
    private static readonly MethodInfo? HoverSound = AccessTools.Method(typeof(GlobalGame), "OnUIHover");
    private static readonly MethodInfo? ClickSound = AccessTools.Method(typeof(GlobalGame), "OnButtonClick", Type.EmptyTypes);

    /// <summary>Canvases already handled (button added, or not one of the two menus).</summary>
    private static readonly ConditionalWeakTable<Canvas, object> Seen = new();
    private static readonly object Marker = new();
    private static bool failed;

    /// <summary>Called every global tick with the game's current scene.</summary>
    public static void Tick(Scene? current)
    {
        if (failed || !ModConfig.Current.GameMenuButton)
        {
            return;
        }
        try
        {
            // The pause menu is a popup of the level: look at the whole chain of popups.
            for (Scene? scene = current; scene != null; scene = scene.PopupScene)
            {
                Type type = scene.GetType();
                bool pause = type == PauseSceneType;
                if (!pause && type != typeof(Scene))
                {
                    continue; // the main menu is a plain Scene; levels and the other screens aren't looked at
                }
                foreach (Canvas canvas in scene.AllThings.OfType<Canvas>())
                {
                    if (canvas.Children.Count == 0 || Seen.TryGetValue(canvas, out _))
                    {
                        continue;
                    }
                    Seen.Add(canvas, Marker);
                    if (pause)
                    {
                        AddToPauseMenu(canvas);
                    }
                    else
                    {
                        AddToMainMenu(canvas);
                    }
                }
            }
        }
        catch (Exception e)
        {
            failed = true; // the key still opens the menu
            ModLog.Error("Could not add the Multiplayer button to the game's menus", e);
        }
    }

    /// <summary>Main menu: under "Start", as one more step of the staircase (the buttons below move one step).</summary>
    private static void AddToMainMenu(Canvas canvas)
    {
        if (MainMenuButtonType == null || GameAccess.Game == null)
        {
            return;
        }
        Element? start = canvas.GetAllChildren().FirstOrDefault(e => e.GetType() == MainMenuButtonType);
        if (start?.Parent is not { } stack)
        {
            return; // not the main menu
        }
        int index = stack.Children.IndexOf(start) + 1;
        foreach (Element below in stack.Children.Skip(index))
        {
            foreach (Element e in below.GetAllChildren().Prepend(below))
            {
                if (e.GetType() == MainMenuButtonType && e.Padding != null)
                {
                    e.Padding.Left += MainMenuIndent;
                }
            }
        }
        var hoverStyle = new Style { FontColor = PopupMenu.Highlight, TextureColor = PopupMenu.Highlight };
        // (width, index, onClick, text, hoverStyle, onHoverEnter, onBeforeClick, navigationStart, creator, height, favorVerticalNavigation)
        var button = (Element)Activator.CreateInstance(MainMenuButtonType, 700f, 1, (Action)MultiplayerMenu.Open, Tr.MultiplayerButton,
            hoverStyle, Sound(HoverSound), Sound(ClickSound), false, null, 90f, true)!;
        stack.Insert(index, button);
    }

    /// <summary>Pause menu: above "Exit", built like the menu's own buttons (without an icon).</summary>
    private static void AddToPauseMenu(Canvas canvas)
    {
        PanelStack? stack = canvas.GetAllChildren().OfType<PanelStack>()
            .FirstOrDefault(p => p.Vertical && p.Children.OfType<Button>().Count() >= 3);
        if (stack == null)
        {
            return;
        }
        Element button = GameAccess.CreateButton(MultiplayerMenu.Open)
            .Add(new Panel(0f, 0f, null, new Vector2(0f, 0.5f)) { Padding = Padding.GetRight(250f) }
                .Add(new Label(Tr.MultiplayerButton) { Anchor = new Vector2(0f, 0.5f) }.SetStyle(GameAccess.UiAssets.StrongStyle)));
        stack.Insert(stack.Children.Count - 1, button);
    }

    /// <summary>One of the game's UI sounds (hover, click), or nothing if it can't be found.</summary>
    private static Action Sound(MethodInfo? method)
    {
        if (method != null && GameAccess.Game is { } game)
        {
            try
            {
                return method.CreateDelegate<Action>(game);
            }
            catch (Exception)
            {
                // silent button
            }
        }
        return () => { };
    }
}
