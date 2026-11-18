using System.Text;

namespace SBMultiplayer.Protocol;

/// <summary>
/// Metadata of a STUNTBOOST replay (.rpl), read without the game: the header and the sections written when the
/// recording stops. The recorded inputs and board states that follow are not decoded (that would need the game's
/// own stream format). Used by the server to check a finish time against the run's replay.
/// </summary>
public sealed class ReplayFile
{
    /// <summary>Oldest and newest replay formats this reader knows.</summary>
    public const int MinFormat = 4, MaxFormat = 11;

    /// <summary>Format that added the time penalty per checkpoint (one more field per checkpoint).</summary>
    private const int CheckpointPenaltyFormat = 7;

    private const int MillisecondsFormat = 5;

    public int Format { get; private set; }
    public uint LevelVersion { get; private set; }
    public uint LevelId { get; private set; }
    public uint UserId { get; private set; }
    public uint PhysicsVersion { get; private set; }

    /// <summary>Final time of the run, in milliseconds (the game's timer).</summary>
    public uint Ms { get; private set; }
    public uint RestartCount { get; private set; }
    public uint TotalMs { get; private set; }
    public List<(uint Id, uint Ms)> Checkpoints { get; } = new();

    public bool? GoalReached { get; private set; }
    public byte? TutorialAbilities { get; private set; }
    public string? GameVersion { get; private set; }
    public string? CustomMapKey { get; private set; }
    public string? Username { get; private set; }
    public ulong? SpeedrunTech { get; private set; }

    /// <summary>Custom start speed (challenges), -1 or null when none.</summary>
    public float? StartSpeed { get; private set; }
    public string? ChallengeModifier { get; private set; }

    public DateTime? WallStart { get; private set; }
    public DateTime? WallEnd { get; private set; }

    /// <summary>Sound card clock (samples) and rate (samples per second) at the start and the end of the recording.</summary>
    public (int RateStart, ulong ClockStart, int RateEnd, ulong ClockEnd)? SoundClock { get; private set; }

    /// <summary>Real duration of the recording by the wall clock, in seconds.</summary>
    public double? WallSeconds => WallStart is { } s && WallEnd is { } e ? (e - s).TotalSeconds : null;

    /// <summary>Real duration of the recording by the sound card's clock, in seconds (independent of the PC's timers).</summary>
    public double? SoundSeconds => SoundClock is { RateStart: > 0 } c && c.RateEnd == c.RateStart && c.ClockEnd >= c.ClockStart
        ? (c.ClockEnd - c.ClockStart) / (double)c.RateStart
        : null;

    /// <summary>Reads a replay. Throws <see cref="InvalidDataException"/> if it is not one.</summary>
    public static ReplayFile Parse(byte[] data)
    {
        var replay = new ReplayFile();
        try
        {
            replay.Read(new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8));
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException("truncated replay");
        }
        return replay;
    }

    private void Read(BinaryReader br)
    {
        Format = br.ReadInt32();
        if (Format < MinFormat || Format > MaxFormat)
        {
            throw new InvalidDataException($"unknown replay format {Format}");
        }
        LevelVersion = br.ReadUInt32();
        LevelId = br.ReadUInt32();
        UserId = br.ReadUInt32();
        br.ReadInt64(); // local recording date
        PhysicsVersion = br.ReadUInt32();

        long length = br.BaseStream.Length;
        for (int guard = 0; guard < 64; guard++)
        {
            uint section = br.ReadUInt32();
            switch (section)
            {
                case 0: // the recorded stream starts here
                    return;
                case 1: // level time: not length-prefixed
                {
                    Ms = ReadTime(br);
                    RestartCount = br.ReadUInt32();
                    TotalMs = ReadTime(br);
                    uint count = br.ReadUInt32();
                    if (count > 10_000)
                    {
                        throw new InvalidDataException("too many checkpoints");
                    }
                    for (uint i = 0; i < count; i++)
                    {
                        uint id = br.ReadUInt32();
                        uint ms = ReadTime(br);
                        if (Format >= CheckpointPenaltyFormat)
                        {
                            br.ReadUInt32(); // restarts on this checkpoint
                        }
                        Checkpoints.Add((id, ms));
                    }
                    break;
                }
                case 2: // expected file length: not length-prefixed
                    if (br.ReadInt64() != length)
                    {
                        throw new InvalidDataException("replay length does not match its header");
                    }
                    break;
                case 3: // tutorial abilities: not length-prefixed
                    TutorialAbilities = br.ReadByte();
                    break;
                default:
                {
                    uint size = br.ReadUInt32();
                    long end = br.BaseStream.Position + size;
                    if (end > length)
                    {
                        throw new InvalidDataException("truncated replay section");
                    }
                    ReadSection(br, section);
                    br.BaseStream.Position = end;
                    break;
                }
            }
        }
        throw new InvalidDataException("too many replay sections");
    }

    private void ReadSection(BinaryReader br, uint section)
    {
        switch (section)
        {
            case 5:
                SoundClock = (br.ReadInt32(), br.ReadUInt64(), br.ReadInt32(), br.ReadUInt64());
                break;
            case 6:
                WallStart = DateTime.FromBinary(br.ReadInt64());
                WallEnd = DateTime.FromBinary(br.ReadInt64());
                break;
            case 7:
                GameVersion = br.ReadString();
                break;
            case 9:
                GoalReached = br.ReadBoolean();
                break;
            case 11:
                Username = br.ReadString();
                break;
            case 12:
                SpeedrunTech = br.ReadUInt64();
                break;
            case 14:
                StartSpeed = br.ReadSingle();
                break;
            case 15:
                CustomMapKey = br.ReadString();
                break;
            case 16:
                ChallengeModifier = br.ReadString();
                break;
            // 4 system info, 8 restart updates, 10 skin: not needed
        }
    }

    private uint ReadTime(BinaryReader br)
    {
        uint value = br.ReadUInt32();
        return Format < MillisecondsFormat ? (uint)Math.Round(value * 1000.0 / 60) : value;
    }

    /// <summary>
    /// True if the replay was recorded on this map key: official "o:&lt;id&gt;[:&lt;version&gt;]" or
    /// Workshop "c:&lt;online key&gt;" (the replay holds the exact Workshop revision).
    /// </summary>
    public bool IsOnMap(string mapKey)
    {
        if (mapKey.StartsWith("c:", StringComparison.Ordinal))
        {
            return CustomMapKey != null && Msg.MapMatches("c:" + CustomMapKey, mapKey);
        }
        string[] parts = mapKey.Split(':');
        return CustomMapKey == null && parts.Length >= 2 && parts[0] == "o"
            && uint.TryParse(parts[1], out uint id) && id == LevelId
            && (parts.Length < 3 || (uint.TryParse(parts[2], out uint version) && version == LevelVersion));
    }
}
