using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Chat input (Enter by default): a small popup with the game's text field. Enter sends, Escape cancels.
/// Commands the mod handles itself (<see cref="LocalCommands"/>) are not sent to the server.
/// Like any game popup, it pauses the local game while typing.
/// </summary>
internal static class ChatInput
{
    private static readonly Color InputBackground = new(0x5F, 0x5A, 0x59);

    public static bool IsOpen { get; private set; }

    /// <summary>The key that opened the chat may also be Enter: ignore it for a moment, or the chat would send at once.</summary>
    private const double OpenGuardSeconds = 0.2;

    private static double openedAt;

    private static Action? closeChat;

    /// <summary>Text of a chat closed by the mod (start of a round): put back the next time it opens.</summary>
    private static string draft = "";

    /// <summary>
    /// Closes the chat without sending, keeping what was typed: the start-line popup under it must update
    /// (synchronized start), the chat must never make a player miss it.
    /// </summary>
    public static void CloseKeepingDraft()
    {
        if (IsOpen && closeChat != null)
        {
            ModLog.Info("Chat closed for the start");
            closeChat();
        }
    }

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
        openedAt = NetClock.Now;
        SE.UIAssets ui = GameAccess.UiAssets;
        var input = new TextInput(GameAccess.UpdateSpeed, draft, 1800f, ui.FontSmall.Value.LineSpacing) { MaxTextLength = ProtocolInfo.MaxChatLength };
        draft = "";
        bool sent = false;
        closeChat = close;
        input.OnEnter += () =>
        {
            if (NetClock.Now - openedAt < OpenGuardSeconds)
            {
                return true;
            }
            string text = input.Text.Trim();
            if (text.Length > 0 && !LocalCommands.TryHandle(text))
            {
                MultiplayerClient.Instance.SendChat(text);
            }
            sent = true;
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
        scene.OnDispose += () =>
        {
            IsOpen = false;
            closeChat = null;
            if (!sent)
            {
                draft = input.Text; // cancelled with Escape or closed by the mod: kept for next time
            }
        };
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
