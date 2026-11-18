using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using SE;

namespace SBMultiplayer.Client.Verify;

/// <summary>
/// PROTOTYPE - replay verification by re-simulation. When SBMULTI_VERIFY is set (path of the JSON report) and the game
/// is launched with .rpl files or folders as arguments, the multiplayer mod stays off and each replay is played through
/// the game's own test harness (the one its developers use for replay tests): no Steam, many updates per frame, desync
/// detection forced to Strict (the replayed board must be bit-for-bit where the file says it was, every update).
/// One JSON line per replay is appended to the report, then the game quits.
/// </summary>
internal static class ReplayVerifier
{
    public const string EnvVar = "SBMULTI_VERIFY";

    public static string? ReportPath => Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } p ? p : null;

    /// <summary>Optional: SBMULTI_VERIFY_SHOW=1 keeps the window visible (to watch the re-simulation).</summary>
    private static bool ShowWindow => Environment.GetEnvironmentVariable("SBMULTI_VERIFY_SHOW") == "1";

    /// <summary>Most time one replay may take, game start included (SBMULTI_VERIFY_TIMEOUT, seconds).</summary>
    private static int ChildTimeoutSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("SBMULTI_VERIFY_TIMEOUT"), out int s) && s > 0 ? s : 120;

    internal static VerifyTest? Current;

    public static void Install(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(Program), nameof(Program.MainInner)),
            prefix: new HarmonyMethod(typeof(ReplayVerifier), nameof(MainInnerPrefix)));
        harmony.Patch(AccessTools.Method(typeof(TestCase), "Fail"),
            prefix: new HarmonyMethod(typeof(ReplayVerifier), nameof(FailPrefix)));
        foreach (ConstructorInfo ctor in typeof(ReplayDesyncDetection).GetConstructors())
        {
            harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(ReplayVerifier), nameof(DesyncDetectionCreated)));
        }
        foreach (ConstructorInfo ctor in typeof(ReplayInput).GetConstructors())
        {
            harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(ReplayVerifier), nameof(ReplayInputCreated)));
        }
        harmony.Patch(AccessTools.Method(typeof(TimeData), nameof(TimeData.RewindFractionOfAnUpdate)),
            postfix: new HarmonyMethod(typeof(ReplayVerifier), nameof(TimeRewound)));
        foreach (ConstructorInfo ctor in CatalogueType.GetConstructors())
        {
            harmony.Patch(ctor, postfix: new HarmonyMethod(typeof(ReplayVerifier), nameof(CatalogueCreated)));
        }
        ModLog.Info($"Replay verification mode: report to {ReportPath}");
    }

    // ---------- Patches ----------

    private static bool MainInnerPrefix(string[] args, TestCase? test, ref Task<int> __result)
    {
        if (test != null)
        {
            return true; // our own per-replay calls
        }
        List<string> files = args.SelectMany(ExpandReplays).ToList();
        if (files.Count == 0)
        {
            ModLog.Warn("Replay verification: no .rpl file in the arguments, starting the game normally");
            return true;
        }
        __result = RunAll(files);
        return false;
    }

    private static IEnumerable<string> ExpandReplays(string arg)
    {
        if (Directory.Exists(arg))
        {
            return Directory.GetFiles(arg, "*.rpl").OrderBy(f => f, StringComparer.Ordinal);
        }
        return arg.EndsWith(".rpl", StringComparison.OrdinalIgnoreCase) && File.Exists(arg) ? new[] { arg } : Array.Empty<string>();
    }

    /// <summary>The game's desync callback ends with test.Fail("desync"), which throws: record it and stop instead.</summary>
    private static bool FailPrefix(TestCase __instance, string message)
    {
        if (__instance is VerifyTest v)
        {
            v.OnFail(message);
            return false;
        }
        return true;
    }

    private static void DesyncDetectionCreated(ReplayDesyncDetection __instance, DesyncDetection detectionStrictness)
    {
        if (Current == null)
        {
            return;
        }
        Current.DesyncDetection = detectionStrictness.ToString();
        // Keep the detailed message (position, ms) that the game only logs.
        FieldInfo? field = AccessTools.Field(typeof(ReplayDesyncDetection), "onDesync");
        if (field?.GetValue(__instance) is Action<string> original)
        {
            VerifyTest test = Current;
            field.SetValue(__instance, (Action<string>)(msg =>
            {
                test.DesyncMessage ??= msg;
                original(msg);
            }));
        }
    }

    private static void ReplayInputCreated(ReplayInput __instance, bool replayGhost, LevelTimer levelTimer)
    {
        if (Current != null && !replayGhost)
        {
            Current.Input = __instance;
            Current.Timer = levelTimer;
        }
    }

    /// <summary>After the goal, the level timer is rewound by the fraction of the update spent in the goal: that's the final time.</summary>
    private static void TimeRewound(TimeData __instance)
    {
        if (Current is { } t && t.Timer != null && ReferenceEquals(__instance, t.Timer.Counter))
        {
            t.OnTimerRewound(__instance.MS);
        }
    }

    /// <summary>
    /// Steam is off in the replay harness, so the game doesn't know the installed Workshop maps: hand it the Workshop
    /// download folder ourselves (SBMULTI_VERIFY_WORKSHOP, or steamapps/workshop/content/&lt;app id&gt; next to the game).
    /// </summary>
    private static Type CatalogueType => AccessTools.TypeByName("SE.CustomMapCatalogue"); // internal

    private static void CatalogueCreated(object __instance)
    {
        try
        {
            string folder = Environment.GetEnvironmentVariable("SBMULTI_VERIFY_WORKSHOP") is { Length: > 0 } f ? f
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "workshop", "content", "2999500"));
            if (!Directory.Exists(folder))
            {
                return;
            }
            Type itemType = CatalogueType.GetNestedType("WorkshopItem", BindingFlags.NonPublic | BindingFlags.Public)!;
            var dirs = Directory.GetDirectories(folder).Where(d => ulong.TryParse(Path.GetFileName(d), out _)).ToArray();
            Array items = Array.CreateInstance(itemType, dirs.Length);
            for (int i = 0; i < dirs.Length; i++)
            {
                items.SetValue(Activator.CreateInstance(itemType, ulong.Parse(Path.GetFileName(dirs[i])), dirs[i]), i);
            }
            AccessTools.Method(CatalogueType, "SetWorkshopItems").Invoke(__instance, new object[] { items });
            AccessTools.Method(CatalogueType, "Refresh").Invoke(__instance, null);
            var maps = (System.Collections.ICollection)AccessTools.Property(CatalogueType, "Maps").GetValue(__instance)!;
            ModLog.Info($"Replay verification: {dirs.Length} Workshop map(s) from {folder}, {maps.Count} map(s) known");
        }
        catch (Exception e)
        {
            ModLog.Error("Replay verification: could not list the Workshop maps", e);
        }
    }

    // ---------- Driver ----------

    private static async Task<int> RunAll(List<string> files)
    {
        Environment.CurrentDirectory = AppContext.BaseDirectory;
        string report = Path.GetFullPath(ReportPath!);
        if (files.Count == 1)
        {
            await VerifyOne(files[0], report);
            return 0;
        }
        // The game can't be created twice in one process (native crash on the second one): one child process per replay.
        ModLog.Info($"Replay verification: {files.Count} replay(s), one process each, report {report}");
        string exe = Environment.ProcessPath!;
        foreach (string file in files)
        {
            string original = Path.GetFullPath(file);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add(original);
            using Process? child = Process.Start(start);
            if (child == null)
            {
                continue;
            }
            // A replay the game can't open leaves it in a menu forever: kill it after a while.
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(ChildTimeoutSeconds));
            string? failure = null;
            try
            {
                await child.WaitForExitAsync(watchdog.Token);
                if (child.ExitCode != 0)
                {
                    failure = $"process exit code {child.ExitCode}";
                }
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                failure = $"timeout after {ChildTimeoutSeconds} s (replay not opened?)";
            }
            if (failure != null)
            {
                // The child crashed or hung, maybe before writing its line.
                var crashed = new VerifyResult(original, "error", 0, null, null, 0, 0, false, null, null, null,
                    failure, 0, null, null, 0);
                File.AppendAllText(report, JsonSerializer.Serialize(crashed, VerifyJson.Default.VerifyResult) + Environment.NewLine);
            }
        }
        return 0;
    }

    private static async Task VerifyOne(string file, string report)
    {
        string original = Path.GetFullPath(file);
        // The game's replay harness writes a UserDataTemp folder next to the replay: work on a copy.
        string dir = Path.Combine(Path.GetTempPath(), $"sbmulti-verify-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        string copy = Path.Combine(dir, Path.GetFileName(original));
        File.Copy(original, copy, overwrite: true);

        var test = new VerifyTest(copy, ShowWindow);
        test.ReadHeader();
        Current = test;
        var sw = Stopwatch.StartNew();
        try
        {
            await Program.MainInner(new[] { copy }, test);
        }
        catch (Exception e)
        {
            test.Error = e.GetType().Name + ": " + e.Message;
            ModLog.Error($"Replay verification: {original}", e);
        }
        Current = null;
        VerifyResult result = test.Result(original, sw.Elapsed.TotalSeconds);
        File.AppendAllText(report, JsonSerializer.Serialize(result, VerifyJson.Default.VerifyResult) + Environment.NewLine);
        ModLog.Info($"Replay verification: {result.Verdict} {Path.GetFileName(original)} file {result.FileMs} ms, resim {result.ResimMs} ms, {result.WallSeconds:0.00} s");
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception)
        {
            // temp folder, not important
        }
    }
}

/// <summary>One replay played by the game's test harness. In the ingame scene, so it gets Update() and GoalReached().</summary>
internal sealed class VerifyTest : TestReplay, IGoalReached
{
    /// <summary>Updates to keep simulating after the end of the input stream, waiting for the goal.</summary>
    private const int GraceUpdates = 120;

    public string? DesyncDetection;
    public string? DesyncMessage;
    public string? FailMessage;
    public string? Error;
    public ReplayInput? Input;
    public LevelTimer? Timer;

    private int updates;
    private int? goalAtUpdate;
    private int? streamEndAtUpdate;
    private uint? resimMs;
    private bool goal;

    // From the file header
    private uint fileMs;
    private uint fileRestarts;
    private uint fileTotalMs;
    private bool? fileGoal;
    private uint levelId;
    private uint? userId;
    private string? headerError;

    public VerifyTest(string rplFile, bool showWindow) : base(rplFile, null, SE.DesyncDetection.Strict)
    {
        EditSettings = (Action<Settings>)Delegate.Combine(EditSettings, (Action<Settings>)(s =>
        {
            // Don't stop at the end of the input stream: the goal is detected by the re-simulated physics.
            s.IgnoreReplayEnding = true;
            // Replay everything as it was played, respawns included (the game can skip the failed attempts).
            s.SkipReplayFails = false;
            s.FrameRecorder.HideWindow = !showWindow;
            // The video-conversion settings record audio to a file with a library missing on Linux: no audio at all.
            s.Audio.OutputToFile = false;
            s.Audio.Engine = SE.Audio.AudioEngines.Stub;
        }));
    }

    public void ReadHeader()
    {
        try
        {
            using Stream? s = ReplayInput.ReadReplay(RplFile, null, null, new StorageFactory(timeTravel: false), out LevelTimeData? data, out levelId,
                out uint _, out TutorialAbility? _, out userId, out BytingLib.DateTimeMS? _, out ReplayVersion _, out bool _, out fileGoal, out int _,
                out string? _, out float? _);
            if (data != null)
            {
                fileMs = data.MS;
                fileRestarts = data.RestartCount;
                fileTotalMs = data.TotalMS;
            }
            if (s == null)
            {
                headerError = "unreadable replay";
            }
        }
        catch (Exception e)
        {
            headerError = e.Message;
        }
    }

    public void GoalReached()
    {
        goal = true;
        goalAtUpdate ??= updates;
    }

    public override void Update()
    {
        updates++;
        if (Done)
        {
            return;
        }
        // The timer is rewound by a fraction of an update right after the goal (next update): read it after that.
        if (goalAtUpdate is { } g && updates >= g + 2)
        {
            resimMs ??= Timer?.Counter.MS;
            Stop();
            return;
        }
        if (Input != null && streamEndAtUpdate == null && Input.IsStreamAtEnd())
        {
            streamEndAtUpdate = updates;
        }
        if (streamEndAtUpdate is { } e && updates > e + GraceUpdates)
        {
            resimMs = Timer?.Counter.MS;
            Stop();
        }
        // Safety net: never more than the file's duration plus a minute.
        if (updates > (fileMs + fileRestarts * 10000u) * 60 / 1000 + 3600 + 60 * 60)
        {
            FailMessage ??= "timeout";
            resimMs = Timer?.Counter.MS;
            Stop();
        }
    }

    public void OnTimerRewound(uint ms)
    {
        if (goal)
        {
            resimMs = ms;
        }
    }

    public void OnFail(string message)
    {
        FailMessage ??= message;
        resimMs = Timer?.Counter.MS;
        Stop();
    }

    private void Stop() => End();

    protected override bool IsSuccess()
    {
        resimMs ??= Timer?.Counter.MS; // ended by the game itself (end of the replay)
        resimTotalMs = Timer?.Counter.TotalMS;
        return true;
    }

    private uint? resimTotalMs;

    public VerifyResult Result(string original, double wallSeconds)
    {
        string verdict;
        if (headerError != null || Error != null)
        {
            verdict = "error";
        }
        else if (fileGoal != true)
        {
            // Abandoned attempt: nothing to verify. (The harness desyncs at the final restart of such files: the
            // recorded board teleports back to the start while the replayed one doesn't.)
            verdict = "dnf";
        }
        else if (DesyncDetection == null)
        {
            verdict = "not-checked"; // the game didn't create its desync detection for this replay
        }
        else if (FailMessage != null || DesyncMessage != null)
        {
            verdict = "desync";
        }
        else if (!goal)
        {
            verdict = "no-goal";
        }
        else
        {
            // Seen once in 30 finishes: trajectory reproduced, but the file counts one more 1 s respawn penalty than the
            // re-simulation. The file is then slower, never faster: told apart from a real mismatch.
            uint penalty = TimeData.CheckpointPenaltyMS((LevelId)levelId);
            bool penaltyOnly = resimMs is { } r && fileMs > r && (fileMs - r) % penalty == 0 && (fileMs - r) / penalty <= fileRestarts;
            verdict = resimMs == fileMs ? "valid" : penaltyOnly ? "penalty-mismatch" : "time-mismatch";
        }
        return new VerifyResult(original, verdict, levelId, userId, fileGoal, fileMs, fileRestarts, goal, resimMs, DesyncDetection,
            DesyncMessage ?? FailMessage, headerError ?? Error, updates, goalAtUpdate, streamEndAtUpdate, Math.Round(wallSeconds, 2),
            fileTotalMs, resimTotalMs);
    }
}

/// <param name="FileTotalMs">Run time including the parts before restarts (the mod's back-to-start penalty is TotalMs - Ms).</param>
internal sealed record VerifyResult(string File, string Verdict, uint LevelId, uint? UserId, bool? FileGoalReached, uint FileMs, uint FileRestarts,
    bool ResimGoalReached, uint? ResimMs, string? DesyncDetection, string? Desync, string? Error, int Updates, int? GoalAtUpdate,
    int? StreamEndAtUpdate, double WallSeconds, uint FileTotalMs = 0, uint? ResimTotalMs = null);

[System.Text.Json.Serialization.JsonSerializable(typeof(VerifyResult))]
internal partial class VerifyJson : System.Text.Json.Serialization.JsonSerializerContext
{
}
