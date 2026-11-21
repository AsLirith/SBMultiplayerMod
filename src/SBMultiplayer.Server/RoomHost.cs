using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <summary>
/// Runs many rooms in one process behind one HTTP port. The website (the gateway) creates and deletes rooms
/// through /rooms (GET lists them), and bridges each player's WebSocket to /room/{id}. Each room is one GameServer.
/// Everything that touches a room runs on one thread (<see cref="Run"/>), fed by a queue.
/// </summary>
public sealed class RoomHost
{
    private sealed class Room
    {
        public required int Id { get; init; }
        public required GameServer Game { get; init; }
        public HashSet<WebPeer> Peers { get; } = new();
    }


    private readonly HttpListener listener = new();
    private readonly string secret;
    private readonly int maxRooms;
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> queue = new();
    private readonly Dictionary<int, Room> rooms = new();
    private readonly TournamentReporter? reporter;
    private int nextPeerId;

    public RoomHost(int port, string secret, int maxRooms, TournamentReporter? reporter = null)
    {
        if (secret.Length < 32)
        {
            throw new ArgumentException("the gateway secret must be at least 32 characters", nameof(secret));
        }
        this.secret = secret;
        this.maxRooms = maxRooms;
        this.reporter = reporter;
        listener.Prefixes.Add($"http://*:{port}/");
    }

    public void Start()
    {
        listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Stops accepting HTTP requests. <see cref="Run"/> closes the rooms when it returns.</summary>
    public void Stop() => listener.Stop();

    /// <summary>Runs the rooms until cancelled, then closes them. Call from the main thread.</summary>
    public void Run(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            while (queue.TryDequeue(out Action? action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Room error: {ex}");
                }
            }
            foreach (Room room in rooms.Values.ToList())
            {
                room.Game.Poll();
            }
            Thread.Sleep(2);
        }
        foreach (Room room in rooms.Values.ToList())
        {
            CloseRoom(room, "server stopped");
        }
    }

    private void Enqueue(Action action) => queue.Enqueue(action);

    private Task<T> Post<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    private async Task AcceptLoop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (!listener.IsListening)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            HttpListenerRequest request = context.Request;
            string path = request.Url?.AbsolutePath ?? "/";
            if (path == "/health")
            {
                await ReplyAsync(context, 200, "{\"ok\":true}");
                return;
            }
            if (!Authorized(request))
            {
                await ReplyAsync(context, 403, "{\"error\":\"forbidden\"}");
                return;
            }
            if (path == "/rooms" && request.HttpMethod == "POST")
            {
                await CreateRoomAsync(context);
                return;
            }
            if (path == "/rooms" && request.HttpMethod == "GET")
            {
                await ListRoomsAsync(context);
                return;
            }
            var deleteMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/rooms/(\d+)$");
            if (deleteMatch.Success && request.HttpMethod == "DELETE")
            {
                await DeleteRoomAsync(context, int.Parse(deleteMatch.Groups[1].Value));
                return;
            }
            var joinMatch = System.Text.RegularExpressions.Regex.Match(path, @"^/room/(\d+)$");
            if (joinMatch.Success && request.IsWebSocketRequest)
            {
                await JoinRoomAsync(context, int.Parse(joinMatch.Groups[1].Value));
                return;
            }
            await ReplyAsync(context, 404, "{\"error\":\"not found\"}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Request error: {ex.Message}");
            try
            {
                await ReplyAsync(context, 500, "{\"error\":\"server error\"}");
            }
            catch (Exception)
            {
                // The client has gone; nothing more to do.
            }
        }
    }

    private bool Authorized(HttpListenerRequest request)
    {
        string? given = request.Headers["x-gateway-secret"];
        if (string.IsNullOrEmpty(given))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(secret));
    }

    private async Task CreateRoomAsync(HttpListenerContext context)
    {
        CreateRoomRequest? body;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            body = JsonSerializer.Deserialize(await reader.ReadToEndAsync(), ServerJson.Default.CreateRoomRequest);
        }
        catch (JsonException)
        {
            body = null;
        }
        if (body == null || body.Id <= 0 || body.MaxPlayers <= 0)
        {
            await ReplyAsync(context, 400, "{\"error\":\"bad request\"}");
            return;
        }

        if (body.Tournament != null && (body.Tournament.Settings == null || body.Tournament.Settings.Maps == null))
        {
            await ReplyAsync(context, 400, "{\"error\":\"bad tournament settings\"}");
            return;
        }
        if (body.Tournament?.Resume is TournamentSave bad && !TournamentRoomRequest.SaveIsComplete(bad))
        {
            await ReplyAsync(context, 400, "{\"error\":\"bad tournament save\"}");
            return;
        }

        (int status, string error) = await Post(() =>
        {
            if (rooms.ContainsKey(body.Id))
            {
                return (409, "room exists");
            }
            if (rooms.Count >= maxRooms)
            {
                return (503, "no free room");
            }
            var options = new ServerOptions
            {
                ServerName = body.Name,
                Password = body.Password ?? "",
                AdminPassword = body.AdminPassword ?? "",
                MaxPlayers = body.MaxPlayers,
                // Tournament rooms keep results and replays, and GameServer adds tournament-{id}. Other rooms keep nothing.
                ResultsDirectory = body.Tournament != null ? "results" : "",
                Tournament = body.Tournament?.Settings,
            };
            // No UDP listener: players reach this room only through the website's WebSocket bridge.
            var game = new GameServer(options, listenUdp: false);
            game.Start();
            if (body.Tournament?.Resume is TournamentSave resume)
            {
                try
                {
                    game.RestoreTournament(resume);
                }
                catch (Exception e)
                {
                    // Don't leave a started room unregistered.
                    Console.Error.WriteLine($"Tournament save refused: {e.GetType().Name}: {e.Message}");
                    game.Stop();
                    return (400, "bad tournament save");
                }
            }
            if (body.Tournament != null)
            {
                // Every report goes to the website, including the second "started" of a resumed tournament.
                game.TournamentReported += r => _ = reporter?.SendAsync(r);
            }
            rooms[body.Id] = new Room { Id = body.Id, Game = game };
            return (201, "");
        });
        await ReplyAsync(context, status, status == 201 ? "{\"ok\":true}" : $"{{\"error\":\"{error}\"}}");
    }

    /// <summary>
    /// Every open room with what a lobby list shows: name, mode, map and players. The website decides which rooms are
    /// public; this only reports. The summaries are refreshed once a second by each room's own Poll.
    /// </summary>
    private async Task ListRoomsAsync(HttpListenerContext context)
    {
        List<RoomListEntry> list = await Post(() => rooms.Values
            .Select(room => (room.Id, Summary: room.Game.Summary))
            .Where(r => r.Summary != null)
            .Select(r => new RoomListEntry(r.Id, r.Summary!.Name, r.Summary.Mode, r.Summary.MapKey, r.Summary.Players,
                r.Summary.MaxPlayers, r.Summary.HasPassword))
            .ToList());
        await ReplyAsync(context, 200, JsonSerializer.Serialize(list, ServerJson.Default.ListRoomListEntry));
    }

    private async Task DeleteRoomAsync(HttpListenerContext context, int id)
    {
        bool removed = await Post(() =>
        {
            if (!rooms.TryGetValue(id, out Room? room))
            {
                return false;
            }
            CloseRoom(room, "room closed");
            return true;
        });
        await ReplyAsync(context, removed ? 204 : 404, removed ? "" : "{\"error\":\"not found\"}");
    }

    private void CloseRoom(Room room, string reason)
    {
        rooms.Remove(room.Id);
        foreach (WebPeer peer in room.Peers)
        {
            peer.Disconnect(reason);
        }
        room.Peers.Clear();
        room.Game.Stop();
    }

    private async Task JoinRoomAsync(HttpListenerContext context, int id)
    {
        string version = context.Request.Headers["x-sbm-version"] ?? "";
        if (version != ProtocolInfo.ConnectionKey)
        {
            await ReplyAsync(context, 426, $"{{\"error\":\"version\",\"version\":\"{ProtocolInfo.Version}\"}}");
            return;
        }

        string password = context.Request.Headers["x-room-password"] ?? "";
        (string? steamId, string? steamName) = SteamHeaders.Read(context.Request.Headers);
        (int status, Room? room, string? reason) = await Post(() =>
        {
            if (!rooms.TryGetValue(id, out Room? found))
            {
                return (404, (Room?)null, (string?)null);
            }
            // The website has checked the room password already, so only the room's own limits apply here.
            string? refusal = found.Game.RefuseReason(version, password, checkPassword: false, steamId: steamId);
            return refusal == null
                ? (200, found, (string?)null)
                : (refusal == ServerReasons.TournamentStarted ? 403 : 503, (Room?)null, refusal);
        });
        if (room == null)
        {
            string error = status == 404 ? "not found" : reason == ServerReasons.TournamentStarted ? "tournament started" : "room full";
            await ReplyAsync(context, status, $"{{\"error\":\"{error}\"}}");
            return;
        }

        WebSocketContext wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
        var peer = new WebPeer(Interlocked.Increment(ref nextPeerId), wsContext.WebSocket, steamId, steamName);
        Enqueue(() =>
        {
            if (rooms.GetValueOrDefault(id) != room)
            {
                peer.Disconnect("room closed");
                return;
            }
            // Checked again here: several players may have passed the first check while the room had one seat left.
            if (room.Game.RefuseReason(version, password, checkPassword: false, steamId: steamId) is string refusal)
            {
                peer.Disconnect(refusal);
                return;
            }
            room.Peers.Add(peer);
            room.Game.OnWebPeerConnected(peer);
        });

        await peer.ReceiveAsync(
            (p, data) => Enqueue(() =>
            {
                if (room.Peers.Contains(p))
                {
                    room.Game.OnWebMessage(p, data);
                }
            }),
            (p, reason) => Enqueue(() =>
            {
                if (room.Peers.Remove(p))
                {
                    room.Game.OnWebPeerDisconnected(p, reason);
                }
            }));
    }

    private static async Task ReplyAsync(HttpListenerContext context, int status, string json)
    {
        context.Response.StatusCode = status;
        if (json.Length > 0)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        context.Response.Close();
    }
}

/// <summary>The website's request to open a room (JSON, property names case-insensitive).</summary>
internal sealed record CreateRoomRequest(int Id, string Name, string? Password, string? AdminPassword, int MaxPlayers, TournamentRoomRequest? Tournament = null);

/// <summary>Makes a room a tournament room. Resume holds the saved state when the host reopens a paused tournament.</summary>
internal sealed record TournamentRoomRequest(TournamentSettings Settings, TournamentSave? Resume)
{
    /// <summary>A save from JSON may lack parts. Every list and map must exist before a room is started from it.</summary>
    public static bool SaveIsComplete(TournamentSave s) =>
        s.Roster != null && s.Standings != null && s.EliminatedOrder != null && s.Wins != null && s.Flagged != null;
}

/// <summary>The player's verified Steam identity, passed on by the website's gateway in x-sbm-steam-id and x-sbm-steam-name (URI-encoded).</summary>
internal static class SteamHeaders
{
    public static (string? SteamId, string? SteamName) Read(System.Collections.Specialized.NameValueCollection headers)
    {
        string? id = headers["x-sbm-steam-id"];
        if (id == null || id.Length != 17 || !id.All(char.IsAsciiDigit))
        {
            id = null;
        }
        string? name = headers["x-sbm-steam-name"];
        if (name != null)
        {
            // The gateway URI-encodes the name so that any persona name fits in a header.
            try
            {
                name = Uri.UnescapeDataString(name);
            }
            catch (UriFormatException)
            {
            }
        }
        if (name != null && name.Length > 64)
        {
            name = name[..64];
        }
        return (id, string.IsNullOrWhiteSpace(name) ? null : name);
    }
}

/// <summary>One room in the reply to GET /rooms.</summary>
internal sealed record RoomListEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] int Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("mode")] GameMode Mode,
    [property: System.Text.Json.Serialization.JsonPropertyName("map")] string Map,
    [property: System.Text.Json.Serialization.JsonPropertyName("players")] int Players,
    [property: System.Text.Json.Serialization.JsonPropertyName("maxPlayers")] int MaxPlayers,
    [property: System.Text.Json.Serialization.JsonPropertyName("hasPassword")] bool HasPassword);
