using BytingLib;
using Microsoft.Xna.Framework;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>
/// État d'une planche à un instant donné. C'est ce qui transitera sur le réseau.
/// </summary>
/// <param name="Time">Horodatage en secondes (temps local pour l'instant, temps serveur plus tard).</param>
/// <param name="Orientation">(pitch, yaw, roll) comme <c>SE.GhostState.Orientation</c>.</param>
/// <param name="Teleport">Vrai si la planche a été téléportée (restart, respawn) : pas d'interpolation depuis l'état précédent.</param>
internal readonly record struct Snapshot(double Time, Vector3 Position, Vector3 Orientation, bool Teleport);

/// <summary>
/// Tampon d'états horodatés, triés par temps, avec interpolation.
/// Sert au faux ghost (étape 2), puis tel quel pour les joueurs distants (étape 3).
/// </summary>
internal sealed class SnapshotBuffer
{
    private readonly List<Snapshot> snapshots = new();
    private readonly double keepSeconds;

    public SnapshotBuffer(double keepSeconds = 5)
    {
        this.keepSeconds = keepSeconds;
    }

    public int Count => snapshots.Count;

    public double? NewestTime => snapshots.Count > 0 ? snapshots[^1].Time : null;

    public void Add(Snapshot snapshot)
    {
        // Les paquets réseau peuvent arriver dans le désordre : insertion triée, doublons ignorés.
        int i = snapshots.Count;
        while (i > 0 && snapshots[i - 1].Time > snapshot.Time)
        {
            i--;
        }
        if (i > 0 && snapshots[i - 1].Time == snapshot.Time)
        {
            return;
        }
        snapshots.Insert(i, snapshot);

        // On ne garde que les dernières secondes.
        double limit = snapshots[^1].Time - keepSeconds;
        int remove = 0;
        while (remove < snapshots.Count - 2 && snapshots[remove + 1].Time < limit)
        {
            remove++;
        }
        if (remove > 0)
        {
            snapshots.RemoveRange(0, remove);
        }
    }

    public void Clear() => snapshots.Clear();

    /// <summary>État interpolé au temps demandé. Hors des bornes, on garde l'état le plus proche.</summary>
    public bool TrySample(double time, out Vector3 position, out Vector3 orientation)
    {
        position = default;
        orientation = default;
        if (snapshots.Count == 0)
        {
            return false;
        }

        if (time <= snapshots[0].Time)
        {
            (position, orientation) = (snapshots[0].Position, snapshots[0].Orientation);
            return true;
        }
        if (time >= snapshots[^1].Time)
        {
            (position, orientation) = (snapshots[^1].Position, snapshots[^1].Orientation);
            return true;
        }

        // Recherche du segment [a, b] qui encadre "time" (tampon petit : recherche linéaire depuis la fin).
        int bi = snapshots.Count - 1;
        while (snapshots[bi - 1].Time > time)
        {
            bi--;
        }
        Snapshot a = snapshots[bi - 1];
        Snapshot b = snapshots[bi];

        if (b.Teleport)
        {
            (position, orientation) = (a.Position, a.Orientation);
            return true;
        }

        float t = (float)((time - a.Time) / (b.Time - a.Time));
        position = Vector3.Lerp(a.Position, b.Position, t);
        orientation = Vector3Extension.LerpRotation(a.Orientation, b.Orientation, t);
        return true;
    }
}
