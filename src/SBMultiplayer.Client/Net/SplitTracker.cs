using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Temps intermédiaires : temps à chaque checkpoint de la run en cours, comparés à ceux de notre
/// meilleure run sur la même map pendant le même round. Global (les scènes sont recréées à chaque retry).
/// Thread du jeu uniquement.
/// </summary>
internal static class SplitTracker
{
    /// <summary>Dernier intermédiaire franchi, pour l'affichage (null = rien à afficher).</summary>
    public readonly record struct Split(int Index, uint Ms, int? DeltaMs, double ShownAt, bool IsFinish);

    private static string context = "";
    private static readonly List<uint> current = new();
    private static List<uint>? best;
    private static uint? bestTotal;

    public static Split? Last { get; private set; }

    /// <summary>Change de référence quand la map ou le round change.</summary>
    private static void EnsureContext(string mapKey, int roundId)
    {
        string key = $"{roundId}|{mapKey}";
        if (key != context)
        {
            context = key;
            best = null;
            bestTotal = null;
            current.Clear();
            Last = null;
        }
    }

    public static void RunStarted(string mapKey, int roundId)
    {
        EnsureContext(mapKey, roundId);
        current.Clear();
        // Le retry automatique relance le niveau juste après l'arrivée : on laisse le temps final affiché.
        if (Last is not { IsFinish: true } last || NetClock.Now - last.ShownAt > 2)
        {
            Last = null;
        }
    }

    public static void CheckpointTaken(string mapKey, int roundId, uint ms)
    {
        EnsureContext(mapKey, roundId);
        int index = current.Count;
        current.Add(ms);
        int? delta = best != null && index < best.Count ? (int)ms - (int)best[index] : null;
        Last = new Split(index, ms, delta, NetClock.Now, IsFinish: false);
    }

    /// <summary>Arrivée : la run devient la référence si c'est notre meilleur temps. Renvoie l'écart avec l'ancien meilleur.</summary>
    public static int? Finished(string mapKey, int roundId, uint totalMs)
    {
        EnsureContext(mapKey, roundId);
        int? delta = bestTotal.HasValue ? (int)totalMs - (int)bestTotal.Value : null;
        Last = new Split(current.Count, totalMs, delta, NetClock.Now, IsFinish: true);
        if (!bestTotal.HasValue || totalMs < bestTotal.Value)
        {
            bestTotal = totalMs;
            best = new List<uint>(current);
        }
        return delta;
    }

    /// <summary>"-0.234", "+1.050", "+1:02.300".</summary>
    public static string FormatDelta(int deltaMs)
    {
        int abs = Math.Abs(deltaMs);
        string body = abs >= 60_000 ? Msg.FormatTime((uint)abs) : $"{abs / 1000}.{abs % 1000:000}";
        return (deltaMs < 0 ? "-" : "+") + body;
    }
}
