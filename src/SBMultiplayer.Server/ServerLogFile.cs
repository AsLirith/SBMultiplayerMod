namespace SBMultiplayer.Server;

/// <summary>
/// The dedicated server's console output, also kept in SBMultiplayer.Server.log next to the server (appended across
/// runs, trimmed when it gets big): something to send when a player reports a problem.
/// </summary>
internal static class ServerLogFile
{
    private const long MaxBytes = 16 * 1024 * 1024;
    private static readonly object Gate = new();
    private static StreamWriter? writer;

    public static void Start()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SBMultiplayer.Server.log");
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                // Keep the latest half, from a line start.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Position = fs.Length - MaxBytes / 2;
                string kept = new StreamReader(fs).ReadToEnd();
                fs.Dispose();
                File.WriteAllText(path, "(older lines removed)\n" + kept[(kept.IndexOf('\n') + 1)..]);
            }
            writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            writer.WriteLine();
            writer.WriteLine($"===== SBMultiplayer server {Protocol.ProtocolInfo.ModVersion} started {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}"
                + $" on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} =====");
            writer.WriteLine($"Arguments: {string.Join(' ', Environment.GetCommandLineArgs().Skip(1))}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"No log file ({path}): {e.Message}");
            return;
        }
        GameServer.LogSink = line =>
        {
            Console.WriteLine(line);
            Write(line);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write($"[{DateTime.Now:HH:mm:ss}] Unhandled error, the server stops: {e.ExceptionObject}");
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            writer?.WriteLine(line);
        }
    }
}
