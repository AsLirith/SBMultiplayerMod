using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Net;

namespace SBMultiplayer.Client.UI;

/// <summary>Messages courts affichés en haut au centre de l'écran (connexion, joueurs, rounds…). Thread-safe.</summary>
internal static class Notifications
{
    public const double DisplaySeconds = 4.5;
    private const int MaxVisible = 5;

    public readonly record struct Item(string Text, Color Color, double ShownAt);

    private static readonly object Gate = new();
    private static readonly List<Item> Items = new();

    public static void Show(string text, Color? color = null)
    {
        ModLog.Info($"[notif] {text}");
        ChatLog.AddSystem(text, color);
        lock (Gate)
        {
            Items.Add(new Item(text, color ?? Color.White, NetClock.Now));
            if (Items.Count > MaxVisible)
            {
                Items.RemoveAt(0);
            }
        }
    }

    public static void Info(string text) => Show(text);

    public static void Good(string text) => Show(text, new Color(140, 230, 140));

    public static void Warn(string text) => Show(text, new Color(255, 170, 80));

    /// <summary>Notifications encore visibles, avec leur opacité (fondu en sortie).</summary>
    public static List<(Item Item, float Alpha)> Visible()
    {
        double now = NetClock.Now;
        lock (Gate)
        {
            Items.RemoveAll(i => now - i.ShownAt > DisplaySeconds);
            return Items.Select(i => (i, (float)Math.Clamp((DisplaySeconds - (now - i.ShownAt)) / 0.5, 0, 1))).ToList();
        }
    }
}
