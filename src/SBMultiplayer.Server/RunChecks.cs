using System.Globalization;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <summary>
/// Game speed seen by the server. The mod sends one board state per game update, 60 per second; a game slowed down
/// (speed hack, or a PC far too slow) sends fewer per real second while still racing. Paused or frozen games send
/// almost nothing (a state every 0.5 s), so those seconds are left out.
/// </summary>
internal sealed class TickRateTracker
{
    /// <summary>Seconds kept (longest run that can be checked).</summary>
    private const int Seconds = 1800;

    /// <summary>A second with fewer states than this is a pause, a loading screen or the start line, not racing.</summary>
    private const int ActiveMin = 10;

    private readonly int[] counts = new int[Seconds];
    private long lastSecond = -1;

    public void Record(double now)
    {
        long second = (long)Math.Floor(now);
        Advance(second);
        counts[second % Seconds]++;
    }

    private void Advance(long second)
    {
        if (lastSecond < 0)
        {
            lastSecond = second;
            return;
        }
        // Seconds without any state are zeroed (only the slots skipped since the last call).
        for (long s = lastSecond + 1; s <= second && s <= lastSecond + Seconds; s++)
        {
            counts[s % Seconds] = 0;
        }
        lastSecond = Math.Max(lastSecond, second);
    }

    /// <summary>
    /// Average states per second over the last <paramref name="runSeconds"/> seconds of racing before
    /// <paramref name="now"/> (the current second, still filling, is left out). Seconds next to a pause are left out too:
    /// they're only partly active. Null if there isn't enough to judge.
    /// </summary>
    public double? RacingRate(double now, double runSeconds)
    {
        long current = (long)Math.Floor(now);
        Advance(current);
        // Short runs: the seconds before the start (restarts, approach) ran at the same speed.
        int wanted = Math.Max(5, (int)Math.Ceiling(runSeconds));
        int total = 0, used = 0;
        for (long s = current - 1; s > current - Seconds && s >= 0 && used < wanted; s--)
        {
            int c = counts[s % Seconds];
            if (c < ActiveMin)
            {
                continue;
            }
            bool prevActive = s - 1 < 0 || counts[(s - 1) % Seconds] >= ActiveMin;
            bool nextActive = s + 1 >= current || counts[(s + 1) % Seconds] >= ActiveMin;
            if (prevActive && nextActive)
            {
                total += c;
                used++;
            }
        }
        return used >= 3 ? total / (double)used : null;
    }
}

/// <summary>A finish time waiting for (or checked with) its replay.</summary>
internal sealed class RunProof
{
    public required int RoundId { get; init; }
    public required int RunId { get; init; }
    public required uint TimeMs { get; init; }
    public required uint LostMs { get; init; }
    public required string MapKey { get; init; }
    public required double ReceivedAt { get; init; }

    /// <summary>Problems found: the time is reported (RunCheck Report) or refused (Enforce).</summary>
    public List<string> Flags { get; } = new();

    /// <summary>Worth knowing but not a problem by itself (e.g. the game was paused during the run).</summary>
    public List<string> Notes { get; } = new();

    /// <summary>Game version and physics version recorded in the replay.</summary>
    public string? Game { get; set; }

    public byte[]? Replay { get; set; }
    public int Received { get; set; }
    public bool ReplayChecked { get; set; }

    /// <summary>Already in the leaderboard (Report mode, or accepted in Enforce mode).</summary>
    public bool Counted { get; set; }

    /// <summary>Became the player's best time: its replay is saved with the results.</summary>
    public bool IsBest { get; set; }
    public string? ReplayFile { get; set; }

    public string Status => !ReplayChecked ? "no replay" : Flags.Count > 0 ? "flagged" : "verified";
}

internal static class RunChecks
{
    /// <summary>Below this many states per second while racing, the game ran slow (normal: 60, minus a little packet loss).</summary>
    public const double MinUpdatesPerSecond = 52;

    /// <summary>Time to wait for the replay after the finish message, before giving up on it.</summary>
    public const double ReplayTimeoutSeconds = 15;

    /// <summary>The game reads its timer a tick apart for the replay and for the finish: up to one update (16.7 ms).</summary>
    private const uint TimeToleranceMs = 20;

    /// <summary>Checks a run's replay against the finish message. Adds its findings to <paramref name="run"/>.</summary>
    public static void CheckReplay(RunProof run, byte[] data)
    {
        ReplayFile replay;
        try
        {
            replay = ReplayFile.Parse(data);
        }
        catch (InvalidDataException e)
        {
            run.Flags.Add($"unreadable replay ({e.Message})");
            return;
        }
        if (replay.GoalReached != true)
        {
            run.Flags.Add("the replay doesn't reach the finish");
        }
        uint replayTime = replay.Ms + run.LostMs;
        uint diff = replayTime > run.TimeMs ? replayTime - run.TimeMs : run.TimeMs - replayTime;
        if (diff > TimeToleranceMs)
        {
            run.Flags.Add($"time differs from the replay's ({Msg.FormatTime(replayTime)})");
        }
        if (!replay.IsOnMap(run.MapKey))
        {
            string where = replay.CustomMapKey != null ? "c:" + replay.CustomMapKey : $"o:{replay.LevelId}:{replay.LevelVersion}";
            run.Flags.Add($"replay recorded on another map ({where})");
        }
        if (replay.StartSpeed is float speed && speed >= 0)
        {
            run.Flags.Add($"custom start speed ({speed.ToString("0.##", CultureInfo.InvariantCulture)})");
        }
        if (!string.IsNullOrEmpty(replay.ChallengeModifier))
        {
            run.Flags.Add($"challenge modifier ({replay.ChallengeModifier})");
        }
        if (replay.WallSeconds is double wall)
        {
            double run_ = replay.Ms / 1000.0;
            if (wall < run_ * 0.97 - 0.2)
            {
                // The game's timer ran faster than the PC's clock: the file was edited (playing faster never helps).
                run.Flags.Add($"replay clock inconsistent ({wall.ToString("0.0", CultureInfo.InvariantCulture)} s of real time)");
            }
            else if (wall > run_ + Math.Max(2, run_ * 0.1))
            {
                run.Notes.Add($"took {wall.ToString("0.0", CultureInfo.InvariantCulture)} s of real time (paused, or slowed down)");
            }
        }
        run.Game = $"{replay.GameVersion ?? "?"} (physics {replay.PhysicsVersion})";
        run.ReplayChecked = true;
    }
}
