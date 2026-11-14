using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.Ghosts;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// 2D text with the game's font. Careful: the font doesn't have every character
/// (no em dash, no ellipsis character, no emojis): they are replaced automatically.
/// </summary>
internal sealed class TextDrawer
{
    private SpriteFont? font;
    private HashSet<char>? chars;
    private readonly Dictionary<string, string> printableCache = new();

    public SpriteFont Font => font!;

    public bool EnsureFont()
    {
        if (font != null)
        {
            return true;
        }
        font = GameAccess.UiFont;
        if (font == null)
        {
            return false;
        }
        chars = new HashSet<char>(font.Characters);
        return true;
    }

    public Vector2 Measure(string text, float scale) => font!.MeasureString(Printable(text)) * scale;

    public float LineHeight(float scale) => font!.LineSpacing * scale;

    /// <summary>Shortens the text with "..." so that it fits in <paramref name="maxWidth"/> pixels.</summary>
    public string Fit(string value, float maxWidth, float scale)
    {
        if (Measure(value, scale).X <= maxWidth)
        {
            return value;
        }
        for (int length = value.Length - 1; length > 1; length--)
        {
            string candidate = value[..length].TrimEnd() + "...";
            if (Measure(candidate, scale).X <= maxWidth)
            {
                return candidate;
            }
        }
        return value[..1] + "...";
    }

    /// <summary>Text with a dark outline, readable over any background.</summary>
    public void DrawOutlined(SpriteBatch sb, string text, Vector2 pos, Color color, float scale, float alpha = 1f)
    {
        string t = Printable(text);
        Color shadow = Color.Black * (0.75f * alpha);
        float o = Math.Max(1f, 2f * scale);
        sb.DrawString(font!, t, pos + new Vector2(-o, 0), shadow, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        sb.DrawString(font!, t, pos + new Vector2(o, 0), shadow, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        sb.DrawString(font!, t, pos + new Vector2(0, -o), shadow, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        sb.DrawString(font!, t, pos + new Vector2(0, o), shadow, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
        sb.DrawString(font!, t, pos, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
    }

    /// <summary>Draws a right-aligned line and moves down <paramref name="y"/>.</summary>
    public void DrawRightAligned(SpriteBatch sb, string text, float right, ref float y, Color color, float scale, float alpha = 1f)
    {
        Vector2 size = Measure(text, scale);
        DrawOutlined(sb, text, new Vector2(right - size.X, y), color * alpha, scale, alpha);
        y += LineHeight(scale);
    }

    /// <summary>Draws a horizontally centered line and moves down <paramref name="y"/>.</summary>
    public void DrawCentered(SpriteBatch sb, string text, float centerX, ref float y, Color color, float scale, float alpha = 1f)
    {
        Vector2 size = Measure(text, scale);
        DrawOutlined(sb, text, new Vector2(centerX - size.X / 2f, y), color * alpha, scale, alpha);
        y += LineHeight(scale);
    }

    public string Printable(string text)
    {
        if (printableCache.TryGetValue(text, out string? cached))
        {
            return cached;
        }
        var sb = new StringBuilder(text.Length);
        char fallback = font!.DefaultCharacter ?? (chars!.Contains('?') ? '?' : ' ');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length)
            {
                i++; // surrogate pair (emoji): a single replacement
                sb.Append(fallback);
            }
            else
            {
                sb.Append(chars!.Contains(c) || c == ' ' ? c : fallback);
            }
        }
        string result = sb.ToString();
        if (printableCache.Count > 512)
        {
            printableCache.Clear();
        }
        printableCache[text] = result;
        return result;
    }
}
