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
    // Adaptive display delay: the past is drawn so there are always two states to interpolate between.
    // The delay is the smallest that stays smooth for this player's connection: about one and a half send
    // intervals (16.7 ms at 60 states/s) plus the recent worst lateness of their packets (jitter), and it
    // follows changes gently (the ghost plays a bit faster or slower, it never jumps).
    private const double MinDelay = 0.025;
    private const double MaxDelay = 0.2;
    private const double SafetyMargin = 0.005;
    /// <summary>Worst recent lateness halves every 2 seconds once the connection calms down.</summary>
    private const double JitterHalfLife = 2.0;
    /// <summary>Delay change per second of real time: up quickly on a lag spike, down slowly.</summary>
    private const double DelayRiseRate = 0.25;
    private const double DelayFallRate = 0.03;

    private readonly object gate = new();
    private readonly SnapshotBuffer buffer = new();
    private double? clockOffset;
    private string mapKey;
    private double lastSendTime = double.NaN;
    private double lastReceive = double.NaN;
    private double sendInterval = 1 / 30.0;
    private double jitterPeak;
    private double delay = 0.1;
    private double lastDelayUpdate = double.NaN;

    /// <summary>Current display delay, in seconds (for the log and debugging).</summary>
    public double DisplayDelay
    {
        get
        {
            lock (gate)
            {
                return delay;
            }
        }
    }

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

            // Send interval (30 or 60 states/s depending on the player's mod version) and lateness of this packet
            // compared with the fastest one seen (the clock offset is the minimum): that's the jitter to absorb.
            if (!double.IsNaN(lastSendTime))
            {
                double interval = s.Time - lastSendTime;
                if (interval > 0.001 && interval < 0.5)
                {
                    sendInterval += (interval - sendInterval) * 0.1;
                }
            }
            if (double.IsNaN(lastSendTime) || s.Time > lastSendTime)
            {
                lastSendTime = s.Time;
            }
            double lateness = Math.Max(0, sample - clockOffset.Value);
            double elapsed = double.IsNaN(lastReceive) ? 0 : now - lastReceive;
            lastReceive = now;
            jitterPeak = Math.Max(lateness, jitterPeak * Math.Pow(0.5, elapsed / JitterHalfLife));

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
            double remoteTime = NetClock.Now - offset - UpdateDelay();
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
            return buffer.TrySampleCamera(NetClock.Now - offset - UpdateDelay(), out position, out orientation);
        }
    }

    /// <summary>Moves the display delay towards its target, at a limited rate. Called under the lock.</summary>
    private double UpdateDelay()
    {
        double now = NetClock.Now;
        double dt = double.IsNaN(lastDelayUpdate) ? 0 : Math.Min(0.1, now - lastDelayUpdate);
        lastDelayUpdate = now;
        double target = Math.Clamp(sendInterval * 1.5 + jitterPeak + SafetyMargin, MinDelay, MaxDelay);
        double step = target > delay ? DelayRiseRate * dt : DelayFallRate * dt;
        delay += Math.Clamp(target - delay, -step, step);
        return delay;
    }

    /// <summary>The player is spectating: no ghost, can't be followed. Written by the network thread.</summary>
    public volatile bool IsSpectator;
}
