namespace SBMultiplayer.Client;

/// <summary>Log fichier minimaliste, utilisable avant que le jeu n'initialise Serilog.</summary>
internal static class ModLog
{
    private static readonly object Gate = new();
    private static StreamWriter? writer;

    public static void Init(string path)
    {
        lock (Gate)
        {
            writer = new StreamWriter(path, append: false) { AutoFlush = true };
        }
    }

    public static void Info(string message) => Write("INF", message);

    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERR", e == null ? message : $"{message}\n{e}");

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            writer?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff} {level}] {message}");
        }
    }
}
