using System.Diagnostics;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>Monotonic local clock, in seconds.</summary>
internal static class NetClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public static double Now => Watch.Elapsed.TotalSeconds;
}

/// <summary>
/// Another player. Written from the network thread, read from the render thread: everything goes through <see cref="gate"/>.
/// </summary>
internal sealed class RemotePlayer : IGhostPoseSource
{
    /// <summary>
    /// Deliberate display delay: the past is drawn so there are always two states to interpolate between.
    /// At 30 states/s (33 ms apart), 100 ms absorb jitter and an occasional lost packet.
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
        IsSpectator = info.IsSpectator;
    }

    /// <summary>Player's board skin (game id). Changed by the network thread.</summary>
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
            // Offset between the sender's clock and ours (latency included). The minimum observed is kept,
            // i.e. the fastest packet, and allowed to creep up very slowly if latency increases.
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
                s.Segment,
                new Vector3(s.CamPosition.X, s.CamPosition.Y, s.CamPosition.Z),
                new Vector3(s.CamOrientation.X, s.CamOrientation.Y, s.CamOrientation.Z)));
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

    /// <summary>The player's own camera, interpolated like their board (spectators see what they see).</summary>
    public bool TryGetCamera(out Vector3 position, out Vector3 orientation)
    {
        lock (gate)
        {
            if (clockOffset is not double offset)
            {
                position = default;
                orientation = default;
                return false;
            }
            return buffer.TrySampleCamera(NetClock.Now - offset - InterpolationDelay, out position, out orientation);
        }
    }

    /// <summary>The player is spectating: no ghost, can't be followed. Written by the network thread.</summary>
    public volatile bool IsSpectator;
}
