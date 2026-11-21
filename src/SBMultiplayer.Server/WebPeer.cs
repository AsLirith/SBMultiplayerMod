using System.IO;
using System.Net.WebSockets;
using System.Threading.Channels;
using LiteNetLib;
using LiteNetLib.Utils;

namespace SBMultiplayer.Server;

/// <summary>
/// One player's connection, carried by a WebSocket. Each binary message is one game message, delivered in order.
/// Outgoing messages go through a queue and a background task, so the game loop never waits on the network.
/// </summary>
public sealed class WebPeer : IPeer
{
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>Backlog above which board states (sent unreliable over UDP) are dropped: a newer one follows anyway.</summary>
    private const int DropUnreliableBacklog = 64;

    /// <summary>Backlog at which the player is too slow to keep up: disconnected instead of filling the memory.</summary>
    private const int MaxBacklog = 4096;

    private readonly WebSocket socket;
    private readonly Channel<byte[]> outgoing = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private volatile bool closed;

    /// <summary>Messages queued and not sent yet (the channel can't count with a single reader).</summary>
    private int backlog;
    private string closeReason = "";

    public WebPeer(int id, WebSocket socket)
    {
        Id = id;
        this.socket = socket;
        _ = Task.Run(SendLoop);
    }

    /// <param name="steamId">Steam ID the website verified for this connection (tournament rooms).</param>
    /// <param name="steamName">Steam persona name the website looked up.</param>
    public WebPeer(int id, WebSocket socket, string? steamId, string? steamName)
        : this(id, socket)
    {
        SteamId = steamId;
        SteamName = steamName;
    }

    public int Id { get; }

    public string? SteamId { get; }

    public string? SteamName { get; }

    /// <summary>Round-trip time. Not measured over WebSocket yet, so it's 0.</summary>
    public int Ping => 0;

    public override string ToString() => $"web #{Id}";

    public bool IsClosed => closed;

    /// <summary>Queues one game message. The delivery method is ignored: WebSocket is ordered and reliable.</summary>
    public void Send(NetDataWriter writer, DeliveryMethod method)
    {
        if (closed)
        {
            return;
        }
        int queued = Volatile.Read(ref backlog);
        if (queued >= MaxBacklog)
        {
            Close("too slow");
            return;
        }
        if (queued > DropUnreliableBacklog && method is DeliveryMethod.Unreliable or DeliveryMethod.Sequenced)
        {
            return;
        }
        if (outgoing.Writer.TryWrite(writer.CopyData()))
        {
            Interlocked.Increment(ref backlog);
        }
    }

    /// <summary>Closes the connection after the messages already queued have been sent.</summary>
    public void Disconnect(string reason) => Close(reason);

    private void Close(string reason)
    {
        if (closed)
        {
            return;
        }
        closed = true;
        closeReason = reason;
        outgoing.Writer.TryComplete();
    }

    private async Task SendLoop()
    {
        try
        {
            await foreach (byte[] data in outgoing.Reader.ReadAllAsync())
            {
                Interlocked.Decrement(ref backlog);
                if (socket.State != WebSocketState.Open)
                {
                    break;
                }
                await socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
            }
            if (socket.State == WebSocketState.Open)
            {
                // WebSocket close reasons are limited to 123 bytes of UTF-8; keep it short.
                string reason = closeReason.Length > 100 ? closeReason[..100] : closeReason;
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch (Exception)
        {
            // The connection is already gone. The receive side reports the disconnection.
        }
    }

    /// <summary>
    /// Reads game messages until the player leaves. Calls onMessage for each one, then onClosed once.
    /// </summary>
    public async Task ReceiveAsync(Action<WebPeer, byte[]> onMessage, Action<WebPeer, string> onClosed)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        string reason = "connection lost";
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    reason = "player left";
                    break;
                }
                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    reason = "message too large";
                    break;
                }
                if (result.EndOfMessage)
                {
                    onMessage(this, message.ToArray());
                    message.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            reason = "connection lost";
        }
        finally
        {
            Close("");
            if (socket.State == WebSocketState.CloseReceived)
            {
                // The player sent a close frame: answer it so the connection ends cleanly.
                try
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
                catch (Exception)
                {
                    // Already gone.
                }
            }
            onClosed(this, reason);
        }
    }
}
