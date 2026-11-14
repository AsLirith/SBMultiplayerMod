using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Net;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Chat history: player messages, plus game events (notifications) as grey system lines.
/// Written from the network thread and the game thread: everything goes through <see cref="Gate"/>.
/// </summary>
internal static class ChatLog
{
    private const int MaxEntries = 60;

    /// <param name="Name">Sender name, or null for a system line.</param>
    public readonly record struct Entry(string? Name, string Text, Color Color, double Time);

    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();

    public static readonly Color SystemColor = new(170, 170, 170);

    public static void AddPlayer(string name, string text)
    {
        if (!LocalCommands.IsIgnored(name))
        {
            Add(new Entry(name, text, Color.White, NetClock.Now));
        }
    }

    public static void AddSystem(string text, Color? color = null)
    {
        // System lines are a bit dimmer than the notification itself.
        Color c = color.HasValue ? Color.Lerp(color.Value, SystemColor, 0.4f) : SystemColor;
        Add(new Entry(null, text, c, NetClock.Now));
    }

    private static void Add(Entry entry)
    {
        lock (Gate)
        {
            Entries.Add(entry);
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveAt(0);
            }
        }
    }

    /// <summary>The last <paramref name="count"/> entries, oldest first.</summary>
    public static List<Entry> Last(int count)
    {
        lock (Gate)
        {
            return Entries.Skip(Math.Max(0, Entries.Count - count)).ToList();
        }
    }
}
