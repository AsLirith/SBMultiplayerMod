using BytingLib;
using Microsoft.Xna.Framework;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>
/// State of a board at a given moment. This is what goes over the network.
/// </summary>
/// <param name="Time">Timestamp in seconds (sender's local clock).</param>
/// <param name="Orientation">(pitch, yaw, roll) like <c>SE.GhostState.Orientation</c>.</param>
/// <param name="Segment">Incremented on every teleport (restart, respawn): never interpolate between two segments.
/// More robust than a simple flag when packets are lost or arrive out of order.</param>
/// <param name="CamPosition">Camera of the player (position, pitch/yaw/roll), for spectators following them.</param>
internal readonly record struct Snapshot(double Time, Vector3 Position, Vector3 Orientation, ushort Segment,
    Vector3 CamPosition = default, Vector3 CamOrientation = default);

/// <summary>
/// Buffer of timestamped states, sorted by time, with interpolation.
/// Used for remote players (and the debug delayed ghost).
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
        // Network packets may arrive out of order: sorted insertion, duplicates ignored.
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

        // Only the last seconds are kept.
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

    /// <summary>Board state interpolated at the requested time. Out of bounds, the closest state is used.</summary>
    public bool TrySample(double time, out Vector3 position, out Vector3 orientation) =>
        TrySample(time, static s => (s.Position, s.Orientation), out position, out orientation);

    /// <summary>Player's camera interpolated at the requested time (spectators' view).</summary>
    public bool TrySampleCamera(double time, out Vector3 position, out Vector3 orientation) =>
        TrySample(time, static s => (s.CamPosition, s.CamOrientation), out position, out orientation);

    private bool TrySample(double time, Func<Snapshot, (Vector3 Position, Vector3 Orientation)> pick, out Vector3 position, out Vector3 orientation)
    {
        position = default;
        orientation = default;
        if (snapshots.Count == 0)
        {
            return false;
        }

        if (time <= snapshots[0].Time)
        {
            (position, orientation) = pick(snapshots[0]);
            return true;
        }
        if (time >= snapshots[^1].Time)
        {
            (position, orientation) = pick(snapshots[^1]);
            return true;
        }

        // Find the [a, b] interval around "time" (small buffer: linear search from the end).
        int bi = snapshots.Count - 1;
        while (snapshots[bi - 1].Time > time)
        {
            bi--;
        }
        Snapshot a = snapshots[bi - 1];
        Snapshot b = snapshots[bi];
        var (pa, ra) = pick(a);
        if (a.Segment != b.Segment)
        {
            (position, orientation) = (pa, ra);
            return true;
        }
        var (pb, rb) = pick(b);
        float t = (float)((time - a.Time) / (b.Time - a.Time));
        position = Vector3.Lerp(pa, pb, t);
        orientation = Vector3Extension.LerpRotation(ra, rb, t);
        return true;
    }
}
