using Microsoft.Xna.Framework.Input;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// The mod's keys: a MonoGame key name ("F8", "Enter"...) or a mouse button ("LeftButton", "RightButton",
/// "MiddleButton", "XButton1", "XButton2"). States are read once per global tick (<see cref="Update"/>),
/// so "pressed" means "went down since the previous tick" for every reader of that tick.
/// </summary>
internal static class ModInput
{
    public static readonly string[] MouseButtons = { "LeftButton", "RightButton", "MiddleButton", "XButton1", "XButton2" };

    private static KeyboardState keys, previousKeys;
    private static MouseState mouse, previousMouse;

    /// <summary>Called first thing in the global tick.</summary>
    public static void Update()
    {
        previousKeys = keys;
        previousMouse = mouse;
        keys = Keyboard.GetState();
        mouse = Mouse.GetState();
    }

    public static bool Down(string name) => IsDown(name, keys, mouse);

    public static bool Pressed(string name) => IsDown(name, keys, mouse) && !IsDown(name, previousKeys, previousMouse);

    /// <summary>Everything down right now, as names (for the key capture of F8 > Keys).</summary>
    public static IEnumerable<string> AllDown()
    {
        foreach (Keys k in keys.GetPressedKeys())
        {
            yield return k.ToString();
        }
        foreach (string b in MouseButtons)
        {
            if (IsDown(b, keys, mouse))
            {
                yield return b;
            }
        }
    }

    private static bool IsDown(string name, KeyboardState k, MouseState m) => name switch
    {
        "LeftButton" => m.LeftButton == ButtonState.Pressed,
        "RightButton" => m.RightButton == ButtonState.Pressed,
        "MiddleButton" => m.MiddleButton == ButtonState.Pressed,
        "XButton1" => m.XButton1 == ButtonState.Pressed,
        "XButton2" => m.XButton2 == ButtonState.Pressed,
        _ => Enum.TryParse(name, ignoreCase: true, out Microsoft.Xna.Framework.Input.Keys key) && k.IsKeyDown(key),
    };
}
