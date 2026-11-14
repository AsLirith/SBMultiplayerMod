using System.Text;
using BytingLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Affichage 2D par-dessus le jeu : pseudos au-dessus des ghosts proches, panneau du round
/// (map, temps restant, classement). Dessiné avec la police du jeu, à la fin du rendu de la scène.
/// </summary>
internal sealed class MultiplayerOverlay
{
    private const int MaxLeaderboardLines = 10;

    private static readonly Color Gold = new(255, 210, 90);

    private SpriteFont? font;
    private HashSet<char>? fontChars;
    private readonly Dictionary<string, string> printableCache = new();
    private bool disabled;

    public void Disable(Exception e)
    {
        if (!disabled)
        {
            disabled = true;
            ModLog.Error("Overlay désactivé suite à une erreur", e);
        }
    }

    public void Draw(SpriteBatch sb, Scene scene, Vector3? localPosition, IEnumerable<RemotePlayer> shownPlayers, float extrapolation)
    {
        if (disabled || !EnsureFont())
        {
            return;
        }

        Viewport vp = sb.GraphicsDevice.Viewport;
        float scale = vp.Height / 1080f;

        sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
        try
        {
            if (localPosition is Vector3 local)
            {
                DrawNameTags(sb, scene, vp, scale, local, shownPlayers, extrapolation);
            }
            if (ModConfig.Current.ShowRoundPanel)
            {
                DrawRoundPanel(sb, vp, scale);
            }
        }
        finally
        {
            sb.End();
        }
    }

    private void DrawNameTags(SpriteBatch sb, Scene scene, Viewport vp, float scale, Vector3 localPosition,
        IEnumerable<RemotePlayer> players, float extrapolation)
    {
        float maxDistance = ModConfig.Current.NameTagMaxDistance;
        if (maxDistance <= 0)
        {
            return;
        }

        var (viewProjection, _) = GameAccess.GetCamera(scene);
        foreach (RemotePlayer p in players)
        {
            if (!p.TryGetPose(extrapolation, out Vector3 pos, out _))
            {
                continue;
            }
            float distance = Vector3.Distance(pos, localPosition);
            if (distance > maxDistance)
            {
                continue;
            }

            Vector3 anchor = pos + Vector3.Up * ModConfig.Current.NameTagHeight;
            Vector4 clip = Vector4.Transform(new Vector4(anchor, 1f), viewProjection);
            if (clip.W <= 0.01f)
            {
                continue; // derrière la caméra
            }
            var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
            if (Math.Abs(ndc.X) > 1.2f || Math.Abs(ndc.Y) > 1.2f)
            {
                continue;
            }
            var screen = new Vector2((ndc.X * 0.5f + 0.5f) * vp.Width, (0.5f - ndc.Y * 0.5f) * vp.Height);

            // Fondu sur le dernier quart de la distance, pour éviter l'apparition brutale.
            float alpha = MathHelper.Clamp((maxDistance - distance) / (maxDistance * 0.25f), 0f, 1f);
            string text = Printable(p.Name);
            float textScale = scale * ModConfig.Current.NameTagScale;
            Vector2 size = font!.MeasureString(text) * textScale;
            DrawOutlined(sb, text, screen - new Vector2(size.X / 2f, size.Y), Color.White * alpha, textScale, alpha);
        }
    }

    private void DrawRoundPanel(SpriteBatch sb, Viewport vp, float scale)
    {
        MultiplayerClient client = MultiplayerClient.Instance;
        RoundState.Snapshot round = client.Round.Current;
        if (!round.IsRound)
        {
            return;
        }

        float s = scale * 0.75f;
        float lineHeight = font!.LineSpacing * s;
        float right = vp.Width - 24f * scale;
        float y = 24f * scale;

        bool results = round.Info.Phase == RoundPhase.Results;
        int remaining = (int)Math.Ceiling(round.RemainingSeconds);
        string header = results
            ? $"Résultats - map suivante dans {remaining} s"
            : $"{GameAccess.DescribeMap(round.Info.MapKey)} ({round.Info.MapIndex + 1}/{round.Info.MapCount}) - {remaining / 60}:{remaining % 60:00}";
        DrawRightAligned(sb, header, right, ref y, results ? Gold : Color.White, s * 1.15f);
        if (!client.IsOnRoundMap && !results)
        {
            DrawRightAligned(sb, "(tu n'es pas sur la map du round)", right, ref y, Color.Orange, s * 0.85f);
        }
        y += lineHeight * 0.3f;

        IReadOnlyList<LeaderboardEntry> board = round.Leaderboard;
        if (board.Count == 0)
        {
            DrawRightAligned(sb, "Aucun temps pour l'instant", right, ref y, Color.LightGray, s);
            return;
        }

        int? me = client.LocalId;
        int myIndex = -1;
        for (int i = 0; i < board.Count; i++)
        {
            if (board[i].PlayerId == me)
            {
                myIndex = i;
            }
            if (i < MaxLeaderboardLines)
            {
                DrawEntry(sb, board, i, me, right, ref y, s);
            }
        }
        if (myIndex >= MaxLeaderboardLines)
        {
            DrawRightAligned(sb, "...", right, ref y, Color.LightGray, s);
            DrawEntry(sb, board, myIndex, me, right, ref y, s);
        }
    }

    private void DrawEntry(SpriteBatch sb, IReadOnlyList<LeaderboardEntry> board, int i, int? me, float right, ref float y, float s)
    {
        LeaderboardEntry e = board[i];
        string gap = i == 0 ? "" : $"  +{(e.BestMs - board[0].BestMs) / 1000f:0.000}";
        string line = $"{i + 1}. {Printable(e.Name)}   {Msg.FormatTime(e.BestMs)}{gap}";
        Color color = e.PlayerId == me ? Gold : i == 0 ? Color.White : Color.Gainsboro;
        DrawRightAligned(sb, line, right, ref y, color, s);
    }

    private void DrawRightAligned(SpriteBatch sb, string text, float right, ref float y, Color color, float s)
    {
        string printable = Printable(text);
        Vector2 size = font!.MeasureString(printable) * s;
        DrawOutlined(sb, printable, new Vector2(right - size.X, y), color, s, 1f);
        y += font.LineSpacing * s;
    }

    // NB : la police du jeu n'a pas tous les caractères (pas de « — » ni de « … ») : s'en tenir à l'ASCII
    // et aux lettres accentuées courantes dans les textes du mod.

    /// <summary>Texte avec un contour sombre, lisible sur n'importe quel décor.</summary>
    private void DrawOutlined(SpriteBatch sb, string text, Vector2 pos, Color color, float s, float alpha)
    {
        Color shadow = Color.Black * (0.75f * alpha);
        float o = Math.Max(1f, 2f * s);
        sb.DrawString(font!, text, pos + new Vector2(-o, 0), shadow, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
        sb.DrawString(font!, text, pos + new Vector2(o, 0), shadow, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
        sb.DrawString(font!, text, pos + new Vector2(0, -o), shadow, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
        sb.DrawString(font!, text, pos + new Vector2(0, o), shadow, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
        sb.DrawString(font!, text, pos, color, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
    }

    private bool EnsureFont()
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
        fontChars = new HashSet<char>(font.Characters);
        return true;
    }

    /// <summary>
    /// SpriteFont plante sur un caractère absent de la police (émoji, alphabet non latin…) :
    /// on les remplace par '?' ou par le caractère par défaut de la police.
    /// </summary>
    private string Printable(string text)
    {
        if (printableCache.TryGetValue(text, out string? cached))
        {
            return cached;
        }
        var sb = new StringBuilder(text.Length);
        char fallback = font!.DefaultCharacter ?? (fontChars!.Contains('?') ? '?' : ' ');
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
                sb.Append(fontChars!.Contains(c) || c == ' ' ? c : fallback);
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
