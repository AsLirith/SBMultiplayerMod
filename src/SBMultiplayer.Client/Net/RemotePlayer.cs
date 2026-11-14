using System.Diagnostics;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>Horloge locale monotone, en secondes.</summary>
internal static class NetClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public static double Now => Watch.Elapsed.TotalSeconds;
}

/// <summary>
/// Un autre joueur. Écrit depuis le thread réseau, lu depuis le thread de rendu : tout passe par <see cref="gate"/>.
/// </summary>
internal sealed class RemotePlayer : IGhostPoseSource
{
    /// <summary>
    /// Retard volontaire d'affichage : on dessine le passé pour toujours avoir deux états entre lesquels interpoler.
    /// À 30 états/s (33 ms d'écart), 100 ms absorbent la gigue et un paquet perdu de temps en temps.
    /// </summary>
    public const double InterpolationDelay = 0.1;

    private readonly object gate = new();
    private readonly SnapshotBuffer buffer = new();
    private double? clockOffset;
    private string mapKey;

    public RemotePlayer(PlayerInfo info)
    {
        Id = info.Id;
        Name = info.Name;
        mapKey = info.MapKey;
        Skin = info.Skin;
    }

    /// <summary>Skin de planche du joueur (id du jeu). Modifié par le thread réseau.</summary>
    public volatile int Skin;

    public int? SkinId => Skin;

    public int Id { get; }

    public string Name { get; }

    public string MapKey
    {
        get
        {
            lock (gate)
            {
                return mapKey;
            }
        }
    }

    public void SetMap(string key)
    {
        lock (gate)
        {
            if (key != mapKey)
            {
                mapKey = key;
                buffer.Clear();
            }
        }
    }

    public void AddState(in PlayerState s)
    {
        double now = NetClock.Now;
        lock (gate)
        {
            // Écart entre l'horloge de l'émetteur et la nôtre (latence comprise). On retient le minimum observé,
            // c.-à-d. le paquet le plus rapide, et on le laisse remonter très lentement si la latence augmente.
            double sample = now - s.Time;
            if (clockOffset is not double offset || sample < offset)
            {
                clockOffset = sample;
            }
            else
            {
                clockOffset = offset + (sample - offset) * 0.002;
            }

            buffer.Add(new Snapshot(s.Time,
                new Vector3(s.Position.X, s.Position.Y, s.Position.Z),
                new Vector3(s.Orientation.X, s.Orientation.Y, s.Orientation.Z),
                s.Segment));
        }
    }

    public bool TryGetPose(float extrapolation, out Vector3 position, out Vector3 orientation)
    {
        lock (gate)
        {
            if (clockOffset is not double offset)
            {
                position = default;
                orientation = default;
                return false;
            }
            double remoteTime = NetClock.Now - offset - InterpolationDelay;
            return buffer.TrySample(remoteTime, out position, out orientation);
        }
    }
}
