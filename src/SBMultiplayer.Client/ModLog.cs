using System.Globalization;

namespace SBMultiplayer.Client;

/// <summary>
/// The mod's log, usable before the game initializes Serilog: SBMultiplayer.log next to the mod. One file for everything
/// a player may have to send (as a player or as the host: the hosted server writes here too), kept across launches:
/// each launch adds a session, and the oldest sessions are trimmed when the file gets big.
/// </summary>
internal static class ModLog
{
    private static readonly object Gate = new();
    private static StreamWriter? writer;

    /// <summary>The file is trimmed at startup above this size, keeping about half of it (the latest sessions).</summary>
    private const long MaxBytes = 4 * 1024 * 1024;

    public const string SessionMarker = "===== SBMultiplayer session started ";
    public const string EndMarker = "===== Session ended normally";

    public static string Path { get; private set; } = "";

    /// <summary>The previous session in the file: when it started, and whether the game closed normally.</summary>
    public static (DateTime? Started, bool EndedNormally) PreviousSession { get; private set; } = (null, true);

    public static void Init(string path)
    {
        lock (Gate)
        {
            Path = path;
            try
            {
                ReadPreviousSession(path);
                TrimIfNeeded(path);
            }
            catch (Exception)
            {
                // an unreadable old log never prevents logging
            }
            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            writer.WriteLine();
            writer.WriteLine($"{SessionMarker}{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)} =====");
        }
    }

    private static void ReadPreviousSession(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        string tail = ReadTail(path, 2 * 1024 * 1024);
        int start = tail.LastIndexOf(SessionMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            PreviousSession = (null, false); // a log from before 1.3: one session, ended or not
            return;
        }
        int end = tail.IndexOf(" =====", start + SessionMarker.Length, StringComparison.Ordinal);
        DateTime? started = end > 0 && DateTimeOffset.TryParseExact(tail[(start + SessionMarker.Length)..end], "yyyy-MM-dd HH:mm:ss zzz",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset d) ? d.LocalDateTime : null;
        PreviousSession = (started, tail.IndexOf(EndMarker, start, StringComparison.Ordinal) >= 0);
    }

    /// <summary>Keeps the latest half of a file grown too big, from the start of a session if possible.</summary>
    private static void TrimIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }
        string kept = ReadTail(path, MaxBytes / 2);
        int session = kept.IndexOf(SessionMarker, StringComparison.Ordinal);
        int newline = kept.IndexOf('\n');
        kept = session >= 0 ? kept[session..] : newline >= 0 ? kept[(newline + 1)..] : kept;
        File.WriteAllText(path, "(older sessions removed to keep this file small)\n\n" + kept);
    }

    /// <summary>The last <paramref name="maxBytes"/> of a text file, even while another program writes it.</summary>
    public static string ReadTail(string path, long maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - maxBytes);
        fs.Position = start;
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERR", e == null ? message : $"{message}\n{e}");

    /// <summary>A block of text as it is (system information, the game's own log...), under a title.</summary>
    public static void Block(string title, string text)
    {
        lock (Gate)
        {
            writer?.WriteLine($"----- {title} -----");
            writer?.WriteLine(text.TrimEnd());
            writer?.WriteLine($"----- end of {title} -----");
        }
    }

    public static void EndSession()
    {
        lock (Gate)
        {
            writer?.WriteLine($"{EndMarker} at {DateTime.Now:HH:mm:ss} =====");
        }
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} {level}] {message}");
        }
    }
}
