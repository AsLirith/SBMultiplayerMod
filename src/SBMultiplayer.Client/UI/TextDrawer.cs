using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.Ghosts;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Texte 2D avec la police du jeu. Attention : la police n'a pas tous les caractères
/// (pas de « — » ni de « … », pas d'émojis) : ils sont remplacés automatiquement.
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

    /// <summary>Texte avec un contour sombre, lisible sur n'importe quel décor.</summary>
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

    /// <summary>Dessine une ligne alignée à droite et fait descendre <paramref name="y"/>.</summary>
    public void DrawRightAligned(SpriteBatch sb, string text, float right, ref float y, Color color, float scale, float alpha = 1f)
    {
        Vector2 size = Measure(text, scale);
        DrawOutlined(sb, text, new Vector2(right - size.X, y), color * alpha, scale, alpha);
        y += LineHeight(scale);
    }

    /// <summary>Dessine une ligne centrée horizontalement et fait descendre <paramref name="y"/>.</summary>
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
                i++; // paire de substitution (émoji) : un seul remplaçant
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
