using BytingLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Couche d'interface globale (IDrawBatch2 dans la liste d'objets de GlobalGame) : dessinée par-dessus
/// toutes les scènes, menus compris. Panneau du round en haut à droite, notifications en haut au centre.
/// </summary>
internal sealed class GlobalOverlay : SE.IDrawBatch2
{
    private const int MaxLeaderboardLines = 10;

    private static readonly Color Gold = new(255, 210, 90);

    private readonly TextDrawer text = new();
    private bool disabled;

    public void DrawBatch2(SpriteBatch sb)
    {
        if (disabled || !text.EnsureFont())
        {
            return;
        }
        try
        {
            Viewport vp = sb.GraphicsDevice.Viewport;
            float scale = vp.Height / 1080f;
            sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
            try
            {
                DrawNotifications(sb, vp, scale);
                if (ModConfig.Current.ShowRoundPanel && MultiplayerClient.Instance.IsReady)
                {
                    DrawRoundPanel(sb, vp, scale);
                }
            }
            finally
            {
                sb.End();
            }
        }
        catch (Exception e)
        {
            disabled = true;
            ModLog.Error("Overlay global désactivé suite à une erreur", e);
        }
    }

    private void DrawNotifications(SpriteBatch sb, Viewport vp, float scale)
    {
        float y = 24f * scale;
        float s = scale * 0.7f;
        foreach (var (item, alpha) in Notifications.Visible())
        {
            text.DrawCentered(sb, item.Text, vp.Width / 2f, ref y, item.Color, s, alpha);
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

        float s = scale * 0.75f * ModConfig.Current.RoundPanelScale;
        float right = vp.Width - 24f * scale;
        float y = 24f * scale;

        bool results = round.Info.Phase == RoundPhase.Results;
        int remaining = (int)Math.Ceiling(round.RemainingSeconds);
        string header = results
            ? $"Résultats - map suivante dans {remaining} s"
            : $"{GameAccess.DescribeMap(round.Info.MapKey)} ({round.Info.MapIndex + 1}/{round.Info.MapCount}) - {remaining / 60}:{remaining % 60:00}";
        text.DrawRightAligned(sb, header, right, ref y, results ? Gold : Color.White, s * 1.15f);
        if (!client.IsOnRoundMap && !results)
        {
            text.DrawRightAligned(sb, $"Tu n'es pas sur la map du round ({ModConfig.Current.RejoinKey} pour la rejoindre)", right, ref y, Color.Orange, s * 0.85f);
        }
        y += text.LineHeight(s) * 0.3f;

        IReadOnlyList<LeaderboardEntry> board = round.Leaderboard;
        if (board.Count == 0)
        {
            text.DrawRightAligned(sb, "Aucun temps pour l'instant", right, ref y, Color.LightGray, s);
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
            text.DrawRightAligned(sb, "...", right, ref y, Color.LightGray, s);
            DrawEntry(sb, board, myIndex, me, right, ref y, s);
        }
    }

    private void DrawEntry(SpriteBatch sb, IReadOnlyList<LeaderboardEntry> board, int i, int? me, float right, ref float y, float s)
    {
        LeaderboardEntry e = board[i];
        string gap = i == 0 ? "" : $"  +{(e.BestMs - board[0].BestMs) / 1000f:0.000}";
        string line = $"{i + 1}. {e.Name}   {Msg.FormatTime(e.BestMs)}{gap}";
        Color color = e.PlayerId == me ? Gold : i == 0 ? Color.White : Color.Gainsboro;
        text.DrawRightAligned(sb, line, right, ref y, color, s);
    }
}
