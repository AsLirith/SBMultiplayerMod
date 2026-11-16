using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework.Input;
using SBMultiplayer.Client.Net;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// F8 > Keys: the mod's keys, changed by clicking an action and pressing the new key.
/// A key already used by another action of the mod is swapped with it. Escape cancels. Mouse buttons work too.
/// </summary>
internal static class KeyBindings
{
    private sealed record Binding(Func<string> Label, Func<ModConfig, string> Get, Action<ModConfig, string> Set, string Default);

    private static readonly Binding[] All =
    {
        new(() => Tr.KeyMenu, c => c.MenuKey, (c, k) => c.MenuKey = k, "F8"),
        new(() => Tr.KeyRejoin, c => c.RejoinKey, (c, k) => c.RejoinKey = k, "F7"),
        new(() => Tr.KeyChat, c => c.ChatKey, (c, k) => c.ChatKey = k, "Enter"),
        new(() => Tr.KeyScoreboard, c => c.ScoreboardKey, (c, k) => c.ScoreboardKey = k, "Tab"),
        new(() => Tr.KeyCloseEndScreen, c => c.CloseEndScreenKey, (c, k) => c.CloseEndScreenKey = k, "Escape"),
        new(() => Tr.KeySpectatePrevious, c => c.SpectatePreviousKey, (c, k) => c.SpectatePreviousKey = k, "Left"),
        new(() => Tr.KeySpectateNext, c => c.SpectateNextKey, (c, k) => c.SpectateNextKey = k, "Right"),
        new(() => Tr.KeyStopFollowing, c => c.StopFollowingKey, (c, k) => c.StopFollowingKey = k, "Down"),
        new(() => Tr.KeyCatchMouse, c => c.CatchKey, (c, k) => c.CatchKey = k, "RightButton"),
    };

    private static Binding? capturing;

    /// <summary>Keys already down when the capture started: ignored until released (e.g. Enter used to click).</summary>
    private static readonly HashSet<string> heldAtStart = new();

    /// <summary>Escape was used to cancel: the menu mustn't close on the same press.</summary>
    private static bool escapeUsed;

    /// <summary>Changes when a key or the capture state changes: the screen rebuilds itself.</summary>
    private static int version;

    public static bool IsCapturing => capturing != null;

    /// <summary>The menus ignore Escape while it serves to cancel a capture.</summary>
    public static bool BlocksEscape => capturing != null || (escapeUsed && Keyboard.GetState().IsKeyDown(Keys.Escape));

    public static void Open() => Show("keys", Build, () => version);

    private static Element Build(Action close, Screen screen)
    {
        ModConfig cfg = ModConfig.Current;
        var rows = All.Select(b => Row(b.Label(), Button(
            ReferenceEquals(capturing, b) ? Tr.PressAKey : Tr.KeyName(b.Get(cfg)),
            () => StartCapture(b))));
        return Stack(new[] { Title(Tr.KeysTitle), Text(Tr.KeysHelp) }
            .Concat(rows)
            .Append(Buttons(Button(Tr.KeysReset, ResetAll), Button(Tr.Back, close)))
            .ToArray());
    }

    private static void StartCapture(Binding b)
    {
        capturing = b;
        heldAtStart.Clear();
        heldAtStart.UnionWith(ModInput.AllDown());
        version++;
    }

    private static void ResetAll()
    {
        capturing = null;
        foreach (Binding b in All)
        {
            b.Set(ModConfig.Current, b.Default);
        }
        ModConfig.Save();
        version++;
    }

    /// <summary>Called by the hotkey handler while capturing, instead of the hotkeys.</summary>
    public static void TickCapture()
    {
        if (capturing == null)
        {
            return;
        }
        var down = ModInput.AllDown().ToList();
        heldAtStart.IntersectWith(down);
        string? name = down.FirstOrDefault(k => !heldAtStart.Contains(k));
        if (name == null)
        {
            return;
        }
        Binding target = capturing;
        capturing = null;
        version++;
        if (name == "Escape")
        {
            escapeUsed = true; // cancel
            return;
        }

        ModConfig cfg = ModConfig.Current;
        string previous = target.Get(cfg);
        // Already used by another action: the two actions swap their keys.
        foreach (Binding other in All.Where(o => !ReferenceEquals(o, target)
            && o.Get(cfg).Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            other.Set(cfg, previous);
            Notifications.Info(Tr.KeySwapped(other.Label(), Tr.KeyName(previous)));
        }
        target.Set(cfg, name);
        ModConfig.Save();
    }

    /// <summary>The capture was interrupted (menu closed).</summary>
    public static void Cancel()
    {
        if (capturing != null)
        {
            capturing = null;
            version++;
        }
    }

    /// <summary>Forget the Escape that cancelled a capture once it's released.</summary>
    public static void Tick()
    {
        if (escapeUsed && !Keyboard.GetState().IsKeyDown(Keys.Escape))
        {
            escapeUsed = false;
        }
        if (capturing != null && !IsOpen("keys"))
        {
            Cancel();
        }
    }
}
