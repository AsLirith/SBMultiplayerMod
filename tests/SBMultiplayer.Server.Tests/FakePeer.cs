using LiteNetLib;
using LiteNetLib.Utils;
using SBMultiplayer.Protocol;
using SBMultiplayer.Server;

namespace SBMultiplayer.Server.Tests;

/// <summary>A player connection for tests: records what the server sends and why it disconnected.</summary>
public sealed class FakePeer : IPeer
{
    public FakePeer(int id, string? steamId = null, string? steamName = null)
    {
        Id = id;
        SteamId = steamId;
        SteamName = steamName;
    }

    public int Id { get; }
    public int Ping => 0;
    public string? SteamId { get; }
    public string? SteamName { get; }

    public List<byte[]> Sent { get; } = new();
    public string? DisconnectReason { get; private set; }

    public void Send(NetDataWriter writer, DeliveryMethod method) => Sent.Add(writer.CopyData());

    public void Disconnect(string reason) => DisconnectReason = reason;

    private IEnumerable<NetDataReader> OfType(MessageType type) =>
        Sent.Where(d => d.Length > 0 && d[0] == (byte)type).Select(d =>
        {
            var r = new NetDataReader(d);
            r.GetByte();
            return r;
        });

    public List<RoundInfo> Rounds => OfType(MessageType.RoundInfo).Select(Msg.GetRound).ToList();

    public List<string> Events => OfType(MessageType.ServerEvent).Select(r => Msg.GetCodedText(r).Code).ToList();

    public List<string> Replies => OfType(MessageType.CommandReply).Select(r => Msg.GetCodedText(r).Code).ToList();

    public List<bool> AdminStatuses => OfType(MessageType.AdminStatus).Select(r => Msg.GetAdminStatus(r).IsAdmin).ToList();

    public List<MatchSettings> Settings => OfType(MessageType.Settings).Select(r => Msg.GetSettings(r).Settings).ToList();
}
