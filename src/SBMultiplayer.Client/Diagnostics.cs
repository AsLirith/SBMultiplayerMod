using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace SBMultiplayer.Client;

/// <summary>
/// Makes SBMultiplayer.log the only file a player needs to send: system and install details at startup, the game's
/// own log and crash report when something went wrong, unhandled errors, and a status line every minute while connected.
/// </summary>
internal static class Diagnostics
{
    /// <summary>The game's log keeps every session ever played: only the end of the session is copied, plus its problems.</summary>
    private const int GameLogTailLines = 250;
    private const int GameLogMaxProblemLines = 150;

    private static string modDir = "";
    private static bool gameLogCopied;

    /// <summary>Called by the startup hook, right after the log is opened (before the game starts).</summary>
    public static void OnStartup(string dir)
    {
        modDir = dir;
        try
        {
            ModLog.Block("system", SystemInfo());
            ModLog.Block("mod files", ModFiles());
        }
        catch (Exception e)
        {
            ModLog.Error("Could not describe the system", e);
        }

        // The previous launch didn't close normally (crash, killed): bring what the game wrote about it.
        var (started, endedNormally) = ModLog.PreviousSession;
        if (!endedNormally)
        {
            ModLog.Warn($"The previous session{(started is DateTime s ? $" (started {s:yyyy-MM-dd HH:mm})" : "")} didn't end normally (crash or game killed)");
            CopyCrashLog(started);
            CopyGameLog("game log of the previous session");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            ModLog.Error($"Unhandled error, the game is closing: {(e.ExceptionObject as Exception)?.GetType().Name}", e.ExceptionObject as Exception);
            CopyGameLog("game log of this session");
        };
        TaskScheduler.UnobservedTaskException += (_, e) => ModLog.Warn($"Unobserved background error: {e.Exception.GetBaseException()}");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            CopyGameLog("game log of this session");
            ModLog.EndSession();
        };
    }

    private static string SystemInfo()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        if (Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH") != null || Environment.GetEnvironmentVariable("WINEPREFIX") != null)
        {
            sb.AppendLine("Running through Proton / Wine");
        }
        sb.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}, process {RuntimeInformation.ProcessArchitecture}, {Environment.ProcessorCount} logical CPUs");
        sb.AppendLine($"Memory: {GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024)} MB available to the game");
        sb.AppendLine($"Language: {System.Globalization.CultureInfo.CurrentUICulture.Name}, time zone {TimeZoneInfo.Local.Id}");
        sb.AppendLine($"Game: {Environment.ProcessPath}");
        sb.AppendLine($"Arguments: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        sb.AppendLine($"Startup hooks: {Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS")}");
        sb.AppendLine($"Game data: {GameDataDir() ?? "not found"}");
        return sb.ToString();
    }

    /// <summary>The mod's files with their versions: a half-updated install (DLLs of two versions) shows here.</summary>
    private static string ModFiles()
    {
        var sb = new StringBuilder();
        foreach (string file in Directory.GetFiles(modDir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var info = new FileInfo(file);
            string version = "";
            if (file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    version = " v" + (AssemblyName.GetAssemblyName(file).Version?.ToString() ?? "?");
                }
                catch (Exception)
                {
                    version = " (not a .NET assembly)";
                }
            }
            sb.AppendLine($"{info.Name}{version}, {info.Length / 1024.0:0.#} KB, {info.LastWriteTime:yyyy-MM-dd HH:mm}");
        }
        return sb.ToString();
    }

    /// <summary>Where the game keeps its log, saves and replays (it can also be next to the game, in UserData).</summary>
    private static string? GameDataDir()
    {
        string nextToGame = Path.Combine(AppContext.BaseDirectory, "UserData");
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "STUNTBOOST");
        return Directory.Exists(nextToGame) ? nextToGame : Directory.Exists(standard) ? standard : null;
    }

    private static void CopyCrashLog(DateTime? since)
    {
        try
        {
            string? dir = GameDataDir();
            string file = dir == null ? "" : Path.Combine(dir, "crash.log");
            if (dir != null && File.Exists(file) && (since == null || File.GetLastWriteTime(file) >= since.Value))
            {
                ModLog.Block($"game crash report ({File.GetLastWriteTime(file):yyyy-MM-dd HH:mm})", ModLog.ReadTail(file, 64 * 1024));
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"Could not read the game's crash report: {e.Message}");
        }
    }

    /// <summary>
    /// The game's log of its latest session (the end of it, and every warning and error before that). Read before the
    /// game starts its new session at startup, or at the end of this session.
    /// </summary>
    private static void CopyGameLog(string title)
    {
        lock (typeof(Diagnostics))
        {
            if (gameLogCopied && title.EndsWith("this session", StringComparison.Ordinal))
            {
                return; // once (crash then exit)
            }
            if (title.EndsWith("this session", StringComparison.Ordinal))
            {
                gameLogCopied = true;
            }
        }
        try
        {
            string? dir = GameDataDir();
            string file = dir == null ? "" : Path.Combine(dir, "log.txt");
            if (dir == null || !File.Exists(file))
            {
                return;
            }
            string tail = ModLog.ReadTail(file, 4 * 1024 * 1024);
            int start = tail.LastIndexOf("] Started SE ", StringComparison.Ordinal);
            if (start >= 0)
            {
                int lineStart = tail.LastIndexOf('\n', start);
                tail = tail[(lineStart + 1)..];
            }
            string[] lines = tail.Split('\n');
            int tailFrom = Math.Max(0, lines.Length - GameLogTailLines);
            // Problems from earlier in the session, then the whole end of it.
            var problems = lines.Take(tailFrom).Where(l => l.Contains(" WRN ") || l.Contains(" ERR ") || l.Contains(" FTL ")).ToList();
            var sb = new StringBuilder();
            if (lines.Length > 0 && tailFrom > 0)
            {
                sb.AppendLine(lines[0].TrimEnd()); // "Started SE <version> <system>"
                if (problems.Count > 0)
                {
                    sb.AppendLine($"... warnings and errors among the {tailFrom - 1} lines not copied:");
                    foreach (string l in problems.TakeLast(GameLogMaxProblemLines))
                    {
                        sb.AppendLine(l.TrimEnd());
                    }
                }
                sb.AppendLine($"... last {GameLogTailLines} lines:");
            }
            foreach (string l in lines.Skip(tailFrom))
            {
                sb.AppendLine(l.TrimEnd());
            }
            ModLog.Block(title, sb.ToString());
        }
        catch (Exception e)
        {
            ModLog.Warn($"Could not read the game's log: {e.Message}");
        }
    }

    /// <summary>Opens the folder of the log in the file manager (F8 > Settings).</summary>
    public static void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(modDir) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            ModLog.Warn($"Could not open the log folder: {e.Message}");
            UI.Notifications.Info(ModLog.Path);
        }
    }
}
