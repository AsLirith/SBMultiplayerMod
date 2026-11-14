using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Chat input (Tab by default): a small popup with the game's text field. Enter sends, Escape cancels.
/// Like any game popup, it pauses the local game while typing.
/// </summary>
internal static class ChatInput
{
    private static readonly Color InputBackground = new(0x5F, 0x5A, 0x59);

    public static bool IsOpen { get; private set; }

    public static void Open()
    {
        if (IsOpen || GameAccess.Game == null || !MultiplayerClient.Instance.IsReady)
        {
            return;
        }
        try
        {
            GameAccess.ShowPopup(Create);
        }
        catch (Exception e)
        {
            IsOpen = false;
            ModLog.Error("Could not open the chat", e);
        }
    }

    private static Scene Create(Action close)
    {
        IsOpen = true;
        SE.UIAssets ui = GameAccess.UiAssets;
        var input = new TextInput(GameAccess.UpdateSpeed, "", 1800f, ui.FontSmall.Value.LineSpacing) { MaxTextLength = ProtocolInfo.MaxChatLength };
        input.OnEnter += () =>
        {
            string text = input.Text.Trim();
            if (text.Length > 0)
            {
                MultiplayerClient.Instance.SendChat(text);
            }
            close();
            return true;
        };
        input.FocusAndSelectAll();
        input.NavigationStart = UINavigationStart.ToThisElement;

        Element panel = new PanelStack(20f).SetStyle(ui.TextSmallStyle).Add(
            new Label(Tr.ChatTitle).SetStyle(ui.StrongStyle),
            new Panel(0f, 0f, InputBackground, null, new Padding(16f)).Add(input),
            new Label(Tr.ChatHint));

        var scene = new Scene();
        CanvasScale canvas = GameAccess.CreateCanvas();
        canvas.Add(GameAccess.CreateMainPanel(panel));
        scene.Add(canvas);
        scene.Add(new EscapeCloser(close));
        scene.OnDispose += () => IsOpen = false;
        return scene;
    }

    private sealed class EscapeCloser(Action close) : IUpdate
    {
        private bool escapeWasDown = true;

        public void Update()
        {
            bool down = Keyboard.GetState().IsKeyDown(Keys.Escape);
            if (down && !escapeWasDown)
            {
                close();
            }
            escapeWasDown = down;
        }
    }
}
