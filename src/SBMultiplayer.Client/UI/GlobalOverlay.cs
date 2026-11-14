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
                RoundState.Snapshot round = MultiplayerClient.Instance.Round.Current;
                bool endScreen = MultiplayerClient.Instance.IsReady && round.Info.MatchEnded && round.Info.Phase == RoundPhase.Results;
                if (endScreen)
                {
                    DrawEndScreen(sb, vp, scale, round); // under the chat, which stays readable on top
                }

                // Notifications go to the chat; at the top only if asked, or if the chat is hidden.
                if (ModConfig.Current.NotificationsOnTop || !ModConfig.Current.ShowChat)
                {
                    DrawNotifications(sb, vp, scale);
                }
                if (ModConfig.Current.ShowChat)
                {
                    DrawChat(sb, vp, scale); // also when disconnected: connection errors stay visible
                }
                if (endScreen)
                {
                    return; // the end-of-game screen replaces the round panel and the start-line messages
                }
                if (Spectator.IsWatching && GameAccess.IsIngameScene(GameAccess.CurrentScene))
                {
                    DrawSpectatorBanner(sb, vp, scale);
                }
                else if (MultiplayerClient.Instance.IsReady && StartGate.Applies)
                {
                    DrawStartStatus(sb, vp, scale);
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

    // ---------- End-of-game screen ----------

    private Texture2D? pixel;

    private const int MaxEndScreenRows = 12;

    private void FillRect(SpriteBatch sb, Rectangle rect, Color color)
    {
        if (pixel == null)
        {
            pixel = new Texture2D(sb.GraphicsDevice, 1, 1);
            pixel.SetData(new[] { Color.White });
        }
        sb.Draw(pixel, rect, color);
    }

    private readonly record struct EndRow(string Name, string Score, string Detail, Color Color);

    /// <summary>
    /// Full-screen results at the end of a game: title, winner, final standings, countdown to the next game.
    /// Time Attack: best times of the round (+ match points). Rounds / Cup / Elimination: match standings.
    /// </summary>
    private void DrawEndScreen(SpriteBatch sb, Viewport vp, float scale, RoundState.Snapshot round)
    {
        MultiplayerClient client = MultiplayerClient.Instance;
        GameMode mode = round.Info.Mode;
        IReadOnlyList<StandingEntry> standings = client.Round.Standings;
        string me = client.PlayerName;

        string? winner;
        var rows = new List<EndRow>();
        if (mode == GameMode.TimeAttack)
        {
            IReadOnlyList<LeaderboardEntry> board = round.Leaderboard;
            winner = board.Count > 0 ? board[0].Name : null;
            for (int i = 0; i < board.Count; i++)
            {
                LeaderboardEntry e = board[i];
                int points = standings.FirstOrDefault(s => s.Name == e.Name).Points;
                string gap = i == 0 ? "" : $"+{(e.BestMs - board[0].BestMs) / 1000f:0.000}";
                rows.Add(new EndRow(e.Name, Msg.FormatTime(e.BestMs), $"{gap}   {Tr.Points(points)}".Trim(), RowColor(i, e.Name == me, StandingStatus.Normal)));
            }
        }
        else
        {
            winner = standings.FirstOrDefault(s => s.Status == StandingStatus.Winner).Name
                     ?? (mode == GameMode.Rounds && standings.Count > 0 ? standings[0].Name : null);
            for (int i = 0; i < standings.Count; i++)
            {
                StandingEntry e = standings[i];
                string score = mode == GameMode.Elimination ? Tr.RoundsSurvived(e.Points)
                    : round.Info.PointsLimit > 0 ? $"{e.Points}/{round.Info.PointsLimit}" : Tr.Points(e.Points);
                string detail = e.Status switch
                {
                    StandingStatus.Winner => Tr.WinnerTag,
                    StandingStatus.Finalist => Tr.FinalistTag,
                    StandingStatus.Eliminated => Tr.EliminatedTag,
                    _ => "",
                };
                rows.Add(new EndRow(e.Name, score, detail, RowColor(i, e.Name == me, e.Status)));
            }
        }

        // Dimmed background and central panel.
        FillRect(sb, new Rectangle(0, 0, vp.Width, vp.Height), Color.Black * 0.55f);
        int panelWidth = (int)(1100 * scale);
        int panelHeight = (int)((420 + Math.Min(rows.Count, MaxEndScreenRows) * 46) * scale);
        var panel = new Rectangle((vp.Width - panelWidth) / 2, (int)(vp.Height * 0.12f), panelWidth, panelHeight);
        FillRect(sb, panel, new Color(20, 20, 24) * 0.85f);

        float cx = vp.Width / 2f;
        float y = panel.Y + 30 * scale;
        text.DrawCentered(sb, Tr.EndTitle(mode), cx, ref y, Color.White, scale * 1.2f);
        y += 10 * scale;
        text.DrawCentered(sb, winner != null ? Tr.WinnerAnnounce(winner) : Tr.NoWinner, cx, ref y,
            winner != null ? new Color(255, 215, 90) : Color.LightGray, scale * 1.6f);
        y += 24 * scale;

        // Table: rank | name | score | detail.
        float s = scale * 0.8f;
        float left = panel.X + 70 * scale;
        float nameX = left + 90 * scale;
        float scoreX = panel.X + panelWidth * 0.55f;
        float detailX = panel.X + panelWidth * 0.75f;
        float lineHeight = text.LineHeight(s) * 1.1f;
        if (rows.Count == 0)
        {
            text.DrawCentered(sb, Tr.NoTimesYet, cx, ref y, Color.LightGray, s);
        }
        for (int i = 0; i < Math.Min(rows.Count, MaxEndScreenRows); i++)
        {
            EndRow row = rows[i];
            text.DrawOutlined(sb, $"{i + 1}.", new Vector2(left, y), row.Color, s);
            text.DrawOutlined(sb, row.Name, new Vector2(nameX, y), row.Color, s);
            text.DrawOutlined(sb, row.Score, new Vector2(scoreX, y), row.Color, s);
            text.DrawOutlined(sb, row.Detail, new Vector2(detailX, y), row.Color * 0.9f, s * 0.9f);
            y += lineHeight;
        }

        float footerY = panel.Bottom - 50 * scale;
        text.DrawCentered(sb, Tr.NextGameIn((int)Math.Ceiling(round.RemainingSeconds)), cx, ref footerY, Color.LightGray, scale * 0.7f);
    }

    private static Color RowColor(int rank, bool isMe, StandingStatus status) => status switch
    {
        StandingStatus.Winner => new Color(255, 215, 90),
        StandingStatus.Eliminated => new Color(150, 150, 150),
        _ when isMe => Gold,
        _ when rank == 0 => new Color(255, 215, 90),
        _ when rank == 1 => new Color(215, 215, 225),
        _ when rank == 2 => new Color(220, 160, 110),
        _ => Color.Gainsboro,
    };

    /// <summary>Spectator / eliminated: who we follow and the controls, at the bottom of the screen.</summary>
    private void DrawSpectatorBanner(SpriteBatch sb, Viewport vp, float scale)
    {
        float s = scale * 0.75f;
        float y = vp.Height * 0.86f;
        string title = StartGate.IsEliminated ? Tr.EliminatedTag.ToUpperInvariant() : Tr.SpectatorTitle;
        string? name = Spectator.Target?.Name;
        text.DrawCentered(sb, name != null ? $"{title} - {Tr.Following(name)}" : $"{title} - {Tr.NobodyToFollow}", vp.Width / 2f, ref y, Color.White, s);
        text.DrawCentered(sb, Tr.SpectatorKeys, vp.Width / 2f, ref y, Color.LightGray, s * 0.8f);
    }

    /// <summary>Rounds mode: waiting for players, countdown, "GO!", or why we're waiting on the start line.</summary>
    private void DrawStartStatus(SpriteBatch sb, Viewport vp, float scale)
    {
        RoundState.Snapshot round = MultiplayerClient.Instance.Round.Current;
        double now = NetClock.Now;
        float y = vp.Height * 0.3f;
        float cx = vp.Width / 2f;

        if (now - StartGate.ReleasedAt < 1.0)
        {
            text.DrawCentered(sb, Tr.Go, cx, ref y, new Color(120, 230, 120), scale * 2.2f);
            return;
        }
        if (!StartGate.IsHeldOnStartLine)
        {
            return;
        }

        string? message = null;
        float size = scale * 0.9f;
        Color color = Color.White;
        if (StartGate.IsEliminated)
        {
            text.DrawCentered(sb, Tr.EliminatedWaiting, cx, ref y, new Color(255, 120, 120), size);
            return;
        }
        switch (StartGate.State)
        {
            case StartGate.RunState.Waiting when round.Info.Phase == RoundPhase.Preparing:
                message = Tr.WaitingForPlayers(round.Info.ReadyCount, round.Info.PlayerCount);
                break;
            case StartGate.RunState.Waiting when round.GoAt is double goAt && goAt > now:
                message = ((int)Math.Ceiling(goAt - now)).ToString();
                size = scale * 2.6f;
                color = new Color(255, 210, 90);
                break;
            case StartGate.RunState.Finished:
                message = Tr.FinishedWaiting;
                break;
            case StartGate.RunState.GaveUp:
                message = Tr.WaitNextRound;
                break;
            case StartGate.RunState.Late:
                message = Tr.LateWaiting;
                break;
        }
        if (message != null)
        {
            text.DrawCentered(sb, message, cx, ref y, color, size);
        }
    }

    private const double ChatVisibleSeconds = 15;
    private const int ChatLinesIdle = 8;
    private const int ChatLinesOpen = 14;

    /// <summary>Chat in the bottom-left corner: recent lines only, the whole history while typing.</summary>
    private void DrawChat(SpriteBatch sb, Viewport vp, float scale)
    {
        bool open = ChatInput.IsOpen;
        double now = NetClock.Now;
        float s = scale * 0.6f * ModConfig.Current.ChatScale;
        float left = 24f * scale;
        float maxWidth = vp.Width * 0.42f;
        float lineHeight = text.LineHeight(s);

        // Lines are laid out bottom-up, starting above the bottom edge.
        var lines = new List<(string? Name, string Text, Color Color, float Alpha)>();
        foreach (ChatLog.Entry e in ChatLog.Last(open ? ChatLinesOpen : ChatLinesIdle))
        {
            double age = now - e.Time;
            if (!open && age > ChatVisibleSeconds)
            {
                continue;
            }
            float alpha = open ? 1f : (float)Math.Clamp(ChatVisibleSeconds - age, 0, 1);
            string prefix = e.Name != null ? e.Name + ": " : "";
            List<string> wrapped = Wrap(prefix + e.Text, maxWidth, s);
            for (int i = 0; i < wrapped.Count; i++)
            {
                // Only the first line carries the sender name (drawn in gold).
                lines.Add((i == 0 ? e.Name : null, i == 0 && e.Name != null ? wrapped[i][Math.Min(prefix.Length, wrapped[i].Length)..] : wrapped[i], e.Color, alpha));
            }
        }

        float y = vp.Height * 0.78f - lines.Count * lineHeight;
        foreach (var (name, line, color, alpha) in lines)
        {
            float x = left;
            if (name != null)
            {
                string label = name + ": ";
                text.DrawOutlined(sb, label, new Vector2(x, y), Gold * alpha, s, alpha);
                x += text.Measure(label, s).X;
            }
            text.DrawOutlined(sb, line, new Vector2(x, y), color * alpha, s, alpha);
            y += lineHeight;
        }
    }

    /// <summary>Greedy word wrap to the given pixel width.</summary>
    private List<string> Wrap(string value, float maxWidth, float s)
    {
        var result = new List<string>();
        string current = "";
        foreach (string word in value.Split(' '))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && text.Measure(candidate, s).X > maxWidth)
            {
                result.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }
        result.Add(current);
        return result;
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
        string roundPart = round.Info.RoundsPerMap > 1 ? $" - {Tr.RoundOf(round.Info.RoundInMap, round.Info.RoundsPerMap)}" : "";
        string header = results
            ? Tr.ResultsHeader(remaining)
            : round.Info.Phase == RoundPhase.Preparing
                ? $"{GameAccess.DescribeMap(round.Info.MapKey)} ({round.Info.MapIndex + 1}/{round.Info.MapCount}){roundPart}"
                : $"{GameAccess.DescribeMap(round.Info.MapKey)} ({round.Info.MapIndex + 1}/{round.Info.MapCount}){roundPart} - "
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
            bool elimination = round.Info.Mode == GameMode.Elimination;
            for (int i = 0; i < Math.Min(standings.Count, MaxLeaderboardLines); i++)
            {
                StandingEntry e = standings[i];
                string value = elimination ? Tr.RoundsSurvived(e.Points) : round.Info.PointsLimit > 0 ? $"{e.Points}/{round.Info.PointsLimit}" : Tr.Points(e.Points);
                string status = e.Status switch
                {
                    StandingStatus.Finalist => "  " + Tr.FinalistTag,
                    StandingStatus.Eliminated => "  " + Tr.EliminatedTag,
                    StandingStatus.Winner => "  " + Tr.WinnerTag,
                    _ => "",
                };
                Color color = e.Status switch
                {
                    StandingStatus.Winner => new Color(255, 230, 120),
                    StandingStatus.Eliminated => new Color(150, 150, 150),
                    StandingStatus.Finalist => new Color(140, 220, 255),
                    _ => e.Name == myName ? Gold : Color.Gainsboro,
                };
                text.DrawRightAligned(sb, $"{i + 1}. {e.Name}   {value}{status}", right, ref y, color, s);
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
