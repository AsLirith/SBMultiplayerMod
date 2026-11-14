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
                if (ModConfig.Current.ShowSplits)
                {
                    DrawSplit(sb, vp, scale);
                }
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

    private const double SplitDisplaySeconds = 2.5;

    /// <summary>Dernier intermédiaire au centre de l'écran : temps et écart (vert = en avance, rouge = en retard).</summary>
    private void DrawSplit(SpriteBatch sb, Viewport vp, float scale)
    {
        if (SplitTracker.Last is not { } split || !GameAccess.IsIngameScene(GameAccess.CurrentScene))
        {
            return;
        }
        double age = NetClock.Now - split.ShownAt;
        if (age > SplitDisplaySeconds)
        {
            return;
        }
        float alpha = (float)Math.Clamp((SplitDisplaySeconds - age) / 0.4, 0, 1);
        float s = scale * 0.9f * ModConfig.Current.SplitsScale;
        bool finish = split.IsFinish;
        string label = finish ? Tr.SplitFinish : Tr.SplitCheckpoint(split.Index + 1);
        float y = vp.Height * 0.28f;
        text.DrawCentered(sb, $"{label}   {Msg.FormatTime(split.Ms)}", vp.Width / 2f, ref y, Color.White, s, alpha);
        if (split.DeltaMs is int delta)
        {
            Color color = delta < 0 ? new Color(110, 160, 255) : delta > 0 ? new Color(255, 110, 110) : Color.White;
            text.DrawCentered(sb, SplitTracker.FormatDelta(delta), vp.Width / 2f, ref y, color, s * 1.2f, alpha);
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
        string clock = $"{remaining / 60}:{remaining % 60:00}";
        string header = results
            ? Tr.ResultsHeader(remaining)
            : $"{GameAccess.DescribeMap(round.Info.MapKey)} ({round.Info.MapIndex + 1}/{round.Info.MapCount}) - "
              + (round.Info.Paused ? $"{Tr.PausedHeader} {clock}" : clock);
        text.DrawRightAligned(sb, header, right, ref y, results || round.Info.Paused ? Gold : Color.White, s * 1.15f);
        if (!client.IsOnRoundMap && !results)
        {
            text.DrawRightAligned(sb, Tr.NotOnRoundMap(ModConfig.Current.RejoinKey), right, ref y, Color.Orange, s * 0.85f);
        }
        y += text.LineHeight(s) * 0.3f;

        IReadOnlyList<LeaderboardEntry> board = round.Leaderboard;
        if (board.Count == 0)
        {
            text.DrawRightAligned(sb, Tr.NoTimesYet, right, ref y, Color.LightGray, s);
        }
        else
        {
            DrawLeaderboard(sb, board, client.LocalId, right, ref y, s);
        }

        // Pendant les résultats : classement du match (points cumulés sur les rounds).
        IReadOnlyList<StandingEntry> standings = client.Round.Standings;
        if (results && standings.Count > 0)
        {
            y += text.LineHeight(s) * 0.5f;
            text.DrawRightAligned(sb, Tr.MatchStandings, right, ref y, Gold, s * 1.05f);
            string myName = client.PlayerName;
            for (int i = 0; i < Math.Min(standings.Count, MaxLeaderboardLines); i++)
            {
                StandingEntry e = standings[i];
                text.DrawRightAligned(sb, $"{i + 1}. {e.Name}   {Tr.Points(e.Points)}", right, ref y, e.Name == myName ? Gold : Color.Gainsboro, s);
            }
        }
    }

    private void DrawLeaderboard(SpriteBatch sb, IReadOnlyList<LeaderboardEntry> board, int? me, float right, ref float y, float s)
    {

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
