using BytingLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Global interface layer (IDrawBatch2 in GlobalGame's object list): drawn over
/// every scene, menus included. Round panel at the top right, notifications at the top center.
/// </summary>
internal sealed class GlobalOverlay : SE.IDrawBatch2
{
    private const int MaxLeaderboardLines = 10;

    private static readonly Color Gold = new(255, 210, 90);

    private readonly TextDrawer text = new();
    private bool disabled;

    /// <summary>Round whose end-of-game screen was closed with Escape (it stays closed for that round).</summary>
    private static int dismissedEndScreenRound = -1;

    /// <summary>The end-of-game screen is on screen right now.</summary>
    public static bool EndScreenVisible
    {
        get
        {
            MultiplayerClient client = MultiplayerClient.Instance;
            RoundState.Snapshot round = client.Round.Current;
            return client.IsReady && round.Info.MatchEnded && round.Info.Phase == RoundPhase.Results
                && round.Info.RoundId != dismissedEndScreenRound;
        }
    }

    /// <summary>Closes the end-of-game screen until the next game.</summary>
    public static void DismissEndScreen() => dismissedEndScreenRound = MultiplayerClient.Instance.Round.Current.Info.RoundId;

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
                bool endScreen = EndScreenVisible;
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
                if (MultiplayerClient.Instance.IsReady && HideSeek.IsActive && !Spectator.IsWatching && GameAccess.IsIngameScene(GameAccess.CurrentScene))
                {
                    DrawHideSeekHud(sb, vp, scale);
                }
                if (ModConfig.Current.ShowRoundPanel && MultiplayerClient.Instance.IsReady)
                {
                    DrawRoundPanel(sb, vp, scale);
                }
            }
            finally
            {
                if (MultiplayerClient.ScoreboardHeld && MultiplayerClient.Instance.IsReady)
                {
                    DrawScoreboard(sb, vp, scale); // on top of everything while the key is held
                }
                sb.End();
            }
        }
        catch (Exception e)
        {
            disabled = true;
            ModLog.Error("Global overlay disabled after an error", e);
        }
    }

    // ---------- Player list (held key) ----------

    private readonly record struct ScoreRow(string Name, string Place, string Status, string Points, Color Color);

    /// <summary>Everyone connected: name, map, status (spectator, hunter, eliminated...) and match points.</summary>
    private void DrawScoreboard(SpriteBatch sb, Viewport vp, float scale)
    {
        MultiplayerClient client = MultiplayerClient.Instance;
        RoundState.Snapshot round = client.Round.Current;
        IReadOnlyList<StandingEntry> standings = client.Round.Standings;

        var players = client.Players.Values.Where(p => p.Id < 1_000_000)
            .Select(p => (Id: p.Id, p.Name, p.MapKey, Spectating: (bool)p.IsSpectator, Me: false)).ToList();
        if (client.LocalId is int myId)
        {
            players.Add((myId, client.PlayerName, client.CurrentMapKey, client.IsSpectating, true));
        }

        string StatusOf(int id, string name, bool spectating)
        {
            if (spectating)
            {
                return Tr.SpectatorTitle.ToLowerInvariant();
            }
            switch (HideSeek.RoleOf(id))
            {
                case HideSeek.Role.Hunter:
                    return Tr.RoleHunter;
                case HideSeek.Role.Mouse:
                    return HideSeek.IsFound(id) ? Tr.RoleFound : Tr.RoleMouse;
            }
            return standings.FirstOrDefault(s => s.Name == name).Status switch
            {
                StandingStatus.Finalist => Tr.FinalistTag,
                StandingStatus.Eliminated => Tr.EliminatedTag,
                StandingStatus.Winner => Tr.WinnerTag,
                _ => "",
            };
        }

        var rows = players
            .Select(p => (p, Points: standings.FirstOrDefault(s => s.Name == p.Name).Points))
            .OrderByDescending(x => x.Points).ThenBy(x => x.p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ScoreRow(
                x.p.Me ? $"{x.p.Name} {Tr.YouTag}" : x.p.Name,
                LocalCommands.DescribePlace(x.p.MapKey, false),
                StatusOf(x.p.Id, x.p.Name, x.p.Spectating),
                standings.Count > 0 ? x.Points.ToString() : "",
                x.p.Me ? Gold : x.p.Spectating ? new Color(170, 170, 170) : Color.Gainsboro))
            .ToList();

        const int MaxRows = 24;
        float rowScale = scale * (rows.Count > 16 ? 0.48f : 0.58f);
        float lineHeight = text.LineHeight(rowScale) * 1.1f;
        float titleScale = scale * 0.8f;
        int shown = Math.Min(rows.Count, MaxRows);
        int panelWidth = (int)Math.Min(vp.Width - 32, 1250 * scale);
        int panelHeight = (int)(text.LineHeight(titleScale) * 1.6f + lineHeight * (shown + 1 + (shown < rows.Count ? 1 : 0)) + 50 * scale);
        var panel = new Rectangle((vp.Width - panelWidth) / 2, (int)(vp.Height * 0.14f), panelWidth, panelHeight);
        FillRect(sb, panel, new Color(20, 20, 24) * 0.85f);

        float cx = vp.Width / 2f;
        float y = panel.Y + 18 * scale;
        string subtitle = round.IsRound ? $"  -  {Tr.ModeName(round.Info.Mode)}: {GameAccess.DescribeMap(round.Info.MapKey)}" : "";
        text.DrawCentered(sb, Tr.ScoreboardTitle(players.Count) + subtitle, cx, ref y, Color.White, titleScale);
        y += 8 * scale;

        // Columns: name | map | status | points.
        float left = panel.X + 30 * scale;
        float width = panelWidth - 60 * scale;
        float[] at = { 0f, 0.3f, 0.72f, 0.9f };
        void Columns(string a, string b, string c, string d, Color color, float s)
        {
            text.DrawOutlined(sb, text.Fit(a, width * 0.28f, s), new Vector2(left + width * at[0], y), color, s);
            text.DrawOutlined(sb, text.Fit(b, width * 0.4f, s), new Vector2(left + width * at[1], y), color, s);
            text.DrawOutlined(sb, text.Fit(c, width * 0.17f, s), new Vector2(left + width * at[2], y), color, s);
            text.DrawOutlined(sb, d, new Vector2(left + width * at[3], y), color, s);
            y += lineHeight;
        }
        Columns(Tr.ScoreName, Tr.ScoreMap, Tr.ScoreStatus, standings.Count > 0 ? Tr.ScorePoints : "", Gold, rowScale);
        foreach (ScoreRow row in rows.Take(shown))
        {
            Columns(row.Name, row.Place, row.Status, row.Points, row.Color, rowScale);
        }
        if (shown < rows.Count)
        {
            text.DrawCentered(sb, Tr.AndMore(rows.Count - shown), cx, ref y, Color.LightGray, rowScale * 0.9f);
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
                     ?? (mode is GameMode.Rounds or GameMode.HideAndSeek && standings.Count > 0 ? standings[0].Name : null);
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

        // Layout: the standings shrink to fit the screen, then split into two columns, then get truncated.
        const float BaseRow = 0.6f;     // row text scale, relative to the 1080p font
        const float MinRow = 0.42f;
        const int MaxRowsPerColumn = 20;
        float maxListHeight = vp.Height * 0.5f;
        float rowGap = 1.08f;
        int count = rows.Count;
        int columns = count > MaxRowsPerColumn / 2 && text.LineHeight(scale * MinRow) * rowGap * count > maxListHeight ? 2 : 1;
        int perColumn = Math.Min(MaxRowsPerColumn, (int)Math.Ceiling(count / (float)columns));
        int shown = Math.Min(count, perColumn * columns);
        float rowScale = Math.Clamp(maxListHeight / (Math.Max(1, perColumn) * text.LineHeight(scale) * rowGap), MinRow, BaseRow) * scale;
        float lineHeight = text.LineHeight(rowScale) * rowGap;

        float titleScale = scale * 0.9f;
        float winnerScale = scale * 1.15f;
        float footerScale = scale * 0.55f;
        float columnWidth = 720 * scale * (rowScale / (BaseRow * scale));
        int panelWidth = (int)Math.Max(820 * scale, columns * columnWidth + 120 * scale);
        float listHeight = Math.Max(1, perColumn) * lineHeight + (shown < count ? lineHeight : 0);
        int panelHeight = (int)(text.LineHeight(titleScale) + text.LineHeight(winnerScale) + listHeight
                                + text.LineHeight(footerScale) + 130 * scale);

        FillRect(sb, new Rectangle(0, 0, vp.Width, vp.Height), Color.Black * 0.55f);
        var panel = new Rectangle((vp.Width - panelWidth) / 2, Math.Max(0, (vp.Height - panelHeight) / 2 - (int)(vp.Height * 0.05f)), panelWidth, panelHeight);
        FillRect(sb, panel, new Color(20, 20, 24) * 0.85f);

        float cx = vp.Width / 2f;
        float y = panel.Y + 26 * scale;
        text.DrawCentered(sb, Tr.EndTitle(mode), cx, ref y, Color.White, titleScale);
        y += 6 * scale;
        text.DrawCentered(sb, winner != null ? Tr.WinnerAnnounce(winner) : Tr.NoWinner, cx, ref y,
            winner != null ? new Color(255, 215, 90) : Color.LightGray, winnerScale);
        y += 20 * scale;

        if (count == 0)
        {
            text.DrawCentered(sb, Tr.NoTimesYet, cx, ref y, Color.LightGray, rowScale);
        }
        float listTop = y;
        float listLeft = cx - columns * columnWidth / 2f;
        for (int i = 0; i < shown; i++)
        {
            EndRow row = rows[i];
            float x = listLeft + (i / perColumn) * columnWidth;
            float rowY = listTop + (i % perColumn) * lineHeight;
            // Columns inside a standings column: rank | name | score | detail.
            text.DrawOutlined(sb, $"{i + 1}.", new Vector2(x, rowY), row.Color, rowScale);
            text.DrawOutlined(sb, text.Fit(row.Name, columnWidth * 0.4f, rowScale), new Vector2(x + columnWidth * 0.1f, rowY), row.Color, rowScale);
            text.DrawOutlined(sb, row.Score, new Vector2(x + columnWidth * 0.52f, rowY), row.Color, rowScale);
            text.DrawOutlined(sb, row.Detail, new Vector2(x + columnWidth * 0.74f, rowY), row.Color * 0.9f, rowScale * 0.85f);
        }
        if (shown < count)
        {
            float moreY = listTop + perColumn * lineHeight;
            text.DrawCentered(sb, Tr.AndMore(count - shown), cx, ref moreY, Color.LightGray, rowScale * 0.9f);
        }

        float footerY = panel.Bottom - text.LineHeight(footerScale) - 22 * scale;
        text.DrawCentered(sb, $"{Tr.NextGameIn((int)Math.Ceiling(round.RemainingSeconds))}    {Tr.EndScreenClose}", cx, ref footerY, Color.LightGray, footerScale);
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

    /// <summary>
    /// Hide and Seek: role and countdowns in the middle of the screen, the hunter's crosshair, and the frozen
    /// mouse's controls at the bottom.
    /// </summary>
    private void DrawHideSeekHud(SpriteBatch sb, Viewport vp, float scale)
    {
        if (HideSeek.SeekStart is not double seekStart)
        {
            return;
        }
        double now = NetClock.Now;
        float cx = vp.Width / 2f;
        HideSeek.Role role = HideSeek.LocalRole;
        MultiplayerClient client = MultiplayerClient.Instance;
        bool found = client.LocalId is int me && HideSeek.IsFound(me);

        if (client.Round.Current.GoAt is not double start || now < start)
        {
            return; // the 3, 2, 1 of the start is shown alone
        }
        if (now < seekStart)
        {
            int left = (int)Math.Ceiling(seekStart - now);
            float y = vp.Height * 0.2f;
            if (role == HideSeek.Role.Hunter)
            {
                text.DrawCentered(sb, Tr.HsYouAreHunter, cx, ref y, new Color(255, 110, 90), scale * 1.1f);
                text.DrawCentered(sb, Tr.HsMiceHiding, cx, ref y, Color.White, scale * 0.8f);
                text.DrawCentered(sb, $"{left / 60}:{left % 60:00}", cx, ref y, Gold, scale * 2.2f);
            }
            else if (role == HideSeek.Role.Mouse && now - start >= 1.0) // after the "GO!"
            {
                text.DrawCentered(sb, Tr.HsYouAreMouse, cx, ref y, new Color(140, 220, 255), scale * 0.9f);
                text.DrawCentered(sb, $"{Tr.HsHide} {left / 60}:{left % 60:00}", cx, ref y, Gold, scale * 1.4f);
            }
            return;
        }

        if (now - seekStart < 2.0)
        {
            float y = vp.Height * 0.25f;
            text.DrawCentered(sb, Tr.HsHuntBegins, cx, ref y, new Color(255, 110, 90), scale * 1.8f);
        }

        if (role == HideSeek.Role.Hunter)
        {
            // Crosshair: what the hunter aims at with the grind button.
            string cross = "+";
            Vector2 size = text.Measure(cross, scale * 1.2f);
            text.DrawOutlined(sb, cross, new Vector2(cx, vp.Height / 2f) - size / 2f, Color.White, scale * 1.2f);
            float y = vp.Height * 0.9f;
            text.DrawCentered(sb, Tr.HsAimHint, cx, ref y, Color.LightGray, scale * 0.6f);
        }
        else if (role == HideSeek.Role.Mouse && HideSeek.KeepFrozen)
        {
            float y = vp.Height * 0.86f;
            string title = found ? Tr.HsFoundTitle : Tr.HsFrozenTitle;
            string? name = HideSeek.FollowRequested ? Spectator.Target?.Name : null;
            text.DrawCentered(sb, name != null ? $"{title} - {Tr.Following(name)}" : title, cx, ref y, found ? new Color(255, 120, 120) : new Color(140, 220, 255), scale * 0.75f);
            text.DrawCentered(sb, Tr.HsFrozenKeys, cx, ref y, Color.LightGray, scale * 0.6f);
        }
        else if (role == HideSeek.Role.Mouse && !found)
        {
            float y = vp.Height * 0.3f;
            text.DrawCentered(sb, Tr.HsLandToFreeze, cx, ref y, Gold, scale * 0.8f);
        }
    }

    /// <summary>Hide and Seek round panel: hunters, then the mice and whether they've been found.</summary>
    private void DrawHideSeekPanel(SpriteBatch sb, MultiplayerClient client, float right, ref float y, float s)
    {
        RoundState.HideSeekState hs = client.Round.HideSeek;
        if (hs.RoundId != client.Round.Current.Info.RoundId || hs.Hunters.Count + hs.Mice.Count == 0)
        {
            return;
        }
        string NameOf(int id) => id == client.LocalId ? client.PlayerName
            : client.Players.TryGetValue(id, out RemotePlayer? p) ? p.Name : "?";
        // Infection: found mice are hunters too, but stay listed with the mice.
        var hunters = hs.Hunters.Where(id => !hs.Mice.Contains(id)).Select(NameOf).ToList();
        text.DrawRightAligned(sb, Tr.HsHunters(string.Join(", ", hunters)), right, ref y, new Color(255, 130, 110), s);
        int hidden = hs.Mice.Count(id => !hs.Found.Contains(id));
        text.DrawRightAligned(sb, Tr.HsMiceLeft(hidden, hs.Mice.Count), right, ref y, Gold, s);
        foreach (int id in hs.Mice.OrderBy(id => hs.Found.Contains(id)).ThenBy(NameOf, StringComparer.OrdinalIgnoreCase).Take(MaxLeaderboardLines))
        {
            bool isFound = hs.Found.Contains(id);
            Color color = isFound ? new Color(150, 150, 150) : id == client.LocalId ? Gold : Color.Gainsboro;
            text.DrawRightAligned(sb, isFound ? $"{NameOf(id)}  {Tr.HsFoundTag}" : NameOf(id), right, ref y, color, s);
        }
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
        bool hideAndSeek = round.Info.Mode == GameMode.HideAndSeek;
        string clockPrefix = "";
        if (hideAndSeek && HideSeek.SeekStart is double seekStart && NetClock.Now < seekStart && round.Info.Phase == RoundPhase.Playing)
        {
            // Hiding time: the clock counts down to the hunt.
            remaining = (int)Math.Ceiling(seekStart - NetClock.Now);
            clockPrefix = Tr.HsHidingClock + " ";
        }
        string clock = $"{remaining / 60}:{remaining % 60:00}";
        string roundPart = round.Info.RoundsPerMap > 1 ? $" - {Tr.RoundOf(round.Info.RoundInMap, round.Info.RoundsPerMap)}" : "";
        string header = results
            ? Tr.ResultsHeader(remaining)
            : round.Info.Phase == RoundPhase.Preparing
                ? $"{GameAccess.DescribeMap(round.Info.MapKey)} ({round.Info.MapIndex + 1}/{round.Info.MapCount}){roundPart}"
                : $"{GameAccess.DescribeMap(round.Info.MapKey)} ({round.Info.MapIndex + 1}/{round.Info.MapCount}){roundPart} - "
                  + (round.Info.Paused ? $"{Tr.PausedHeader} {clock}" : clockPrefix + clock);
        text.DrawRightAligned(sb, header, right, ref y, results || round.Info.Paused ? Gold : Color.White, s * 1.15f);
        if (!client.IsOnRoundMap && !results)
        {
            text.DrawRightAligned(sb, Tr.NotOnRoundMap(ModConfig.Current.RejoinKey), right, ref y, Color.Orange, s * 0.85f);
        }
        y += text.LineHeight(s) * 0.3f;

        IReadOnlyList<LeaderboardEntry> board = round.Leaderboard;
        if (hideAndSeek)
        {
            if (!results)
            {
                DrawHideSeekPanel(sb, client, right, ref y, s);
            }
        }
        else if (board.Count == 0)
        {
            text.DrawRightAligned(sb, Tr.NoTimesYet, right, ref y, Color.LightGray, s);
        }
        else
        {
            DrawLeaderboard(sb, board, client.LocalId, right, ref y, s);
        }

        // During results (and the whole Hide and Seek round, points come as mice are found): match standings.
        IReadOnlyList<StandingEntry> standings = client.Round.Standings;
        if ((results || hideAndSeek) && standings.Count > 0)
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
