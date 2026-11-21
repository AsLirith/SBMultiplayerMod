using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SBMultiplayer.Client.UI;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// "Join in game" on the website's lobby page. While the game runs, the mod answers on 127.0.0.1:<see cref="Port"/>,
/// on this computer only (never on the network). The page asks it to join a lobby by its code, and the mod joins
/// it the same way as clicking the lobby in F8 > Join a game.
///
/// Only pages from the lobby websites (<see cref="ModConfig.RoomSite"/> and <see cref="ModConfig.WebJoinSites"/>)
/// are listened to, checked on the browser's Origin header, and the only thing they can ask is to join a lobby
/// from the room site by its code. Requests addressed to another host name are refused too, so a site can't reach
/// this through a domain pointed at 127.0.0.1. Turned off with WebJoin = false in SBMultiplayer.json.
///
/// This is a few lines of HTTP over a socket rather than HttpListener, which on Windows can need an admin to
/// reserve the address.
/// </summary>
internal static class WebJoin
{
    /// <summary>Fixed, because the website has to know it. The website's static/js/lobbies.js uses the same.</summary>
    public const int Port = 24810;

    private const int MaxRequestBytes = 8192;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private static readonly ConcurrentQueue<string> pendingJoins = new();
    private static TcpListener? listener;
    private static CancellationTokenSource? stop;

    /// <summary>Starts listening, once, when the mod loads. Another copy of the game already listening is fine: it keeps the port.</summary>
    public static void Start()
    {
        if (!ModConfig.Current.WebJoin || listener != null)
        {
            return;
        }
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
        }
        catch (SocketException e)
        {
            ModLog.Warn($"Join in game from the website is off: port {Port} is in use ({e.SocketErrorCode})");
            listener = null;
            return;
        }
        stop = new CancellationTokenSource();
        CancellationToken token = stop.Token;
        TcpListener server = listener;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await server.AcceptTcpClientAsync(token);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    ModLog.Warn($"Join in game: {e.Message}");
                    continue;
                }
                _ = Task.Run(() => ServeAsync(client));
            }
        });
        ModLog.Info($"Join in game from the website: listening on 127.0.0.1:{Port}");
    }

    public static void Stop()
    {
        stop?.Cancel();
        try
        {
            listener?.Stop();
        }
        catch (Exception)
        {
            // closing anyway
        }
        listener = null;
    }

    /// <summary>On the game thread, every frame: joins the lobby the website asked for, if any.</summary>
    public static void Tick(MultiplayerClient client)
    {
        string? code = null;
        while (pendingJoins.TryDequeue(out string? next))
        {
            code = next; // only the last click counts
        }
        if (code == null)
        {
            return;
        }
        Notifications.Info(Tr.JoiningFromWebsite(code));
        client.JoinRoom(code, null); // listed lobbies have no password
    }

    private static async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                using var timeout = new CancellationTokenSource(ReadTimeout);
                Request? request = await ReadRequestAsync(stream, timeout.Token);
                byte[] reply = request == null ? Reply(400, null, "bad request") : Handle(request);
                await stream.WriteAsync(reply, timeout.Token);
            }
            catch (Exception)
            {
                // the browser went away or sent garbage: nothing to answer
            }
        }
    }

    private sealed record Request(string Method, string Path, string Query, string Host, string? Origin, bool PrivateNetworkPreflight);

    private static byte[] Handle(Request r)
    {
        // A site's own domain pointed at 127.0.0.1 would arrive with its own Host: refuse it.
        if (r.Host != $"127.0.0.1:{Port}" && r.Host != $"localhost:{Port}")
        {
            return Reply(421, null, "wrong host");
        }
        string? origin = AllowedOrigin(r.Origin);
        if (origin == null)
        {
            return Reply(403, null, "this site can't ask the game to join");
        }
        if (r.Method == "OPTIONS")
        {
            return Reply(204, origin, null, r.PrivateNetworkPreflight);
        }
        if (r.Method == "GET" && r.Path == "/status")
        {
            return Reply(200, origin, $"{{\"game\":\"SBMultiplayer\",\"version\":\"{ProtocolInfo.ModVersion}\"}}");
        }
        if (r.Method == "POST" && r.Path == "/join")
        {
            string code = QueryValue(r.Query, "code").Trim().ToUpperInvariant();
            if (!IsLobbyCode(code))
            {
                return Reply(400, origin, "bad lobby code");
            }
            pendingJoins.Enqueue(code);
            return Reply(202, origin, "{\"joining\":true}");
        }
        return Reply(404, origin, "not found");
    }

    /// <summary>The page's origin, when it's one of the lobby websites; otherwise null.</summary>
    private static string? AllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return null;
        }
        ModConfig config = ModConfig.Current;
        foreach (string site in config.WebJoinSites.Append(config.RoomSite))
        {
            if (Uri.TryCreate(site.Trim(), UriKind.Absolute, out Uri? uri)
                && string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
            {
                return origin;
            }
        }
        return null;
    }

    /// <summary>Lobby codes are a word, a dash and two digits (MAPLE-42).</summary>
    private static bool IsLobbyCode(string code)
    {
        int dash = code.IndexOf('-');
        return dash > 0 && dash <= 16 && code.Length == dash + 3
            && code[..dash].All(c => c is >= 'A' and <= 'Z')
            && char.IsAsciiDigit(code[dash + 1]) && char.IsAsciiDigit(code[dash + 2]);
    }

    private static string QueryValue(string query, string name)
    {
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == name)
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }
        return "";
    }

    /// <summary>Reads the request line and headers (the page sends no body).</summary>
    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxRequestBytes];
        int length = 0;
        int end;
        while ((end = IndexOfHeaderEnd(buffer, length)) < 0)
        {
            if (length == buffer.Length)
            {
                return null;
            }
            int read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0)
            {
                return null;
            }
            length += read;
        }
        string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length != 3)
        {
            return null;
        }
        string target = first[1];
        int q = target.IndexOf('?');
        string path = q < 0 ? target : target[..q];
        string query = q < 0 ? "" : target[(q + 1)..];
        string host = "";
        string? origin = null;
        bool privateNetwork = false;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }
            string name = line[..colon].Trim().ToLowerInvariant();
            string value = line[(colon + 1)..].Trim();
            switch (name)
            {
                case "host": host = value.ToLowerInvariant(); break;
                case "origin": origin = value; break;
                case "access-control-request-private-network": privateNetwork = value == "true"; break;
            }
        }
        return new Request(first[0], path, query, host, origin, privateNetwork);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
            {
                return i - 3;
            }
        }
        return -1;
    }

    private static byte[] Reply(int status, string? origin, string? body, bool privateNetwork = false)
    {
        string reason = status switch
        {
            200 => "OK", 202 => "Accepted", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden",
            404 => "Not Found", 421 => "Misdirected Request", _ => "Error",
        };
        bool json = body != null && body.StartsWith('{');
        byte[] content = Encoding.UTF8.GetBytes(body ?? "");
        var head = new StringBuilder($"HTTP/1.1 {status} {reason}\r\n");
        if (origin != null)
        {
            head.Append($"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\n");
            head.Append("Access-Control-Allow-Methods: GET, POST\r\nAccess-Control-Max-Age: 600\r\n");
            if (privateNetwork)
            {
                head.Append("Access-Control-Allow-Private-Network: true\r\n");
            }
        }
        head.Append($"Content-Type: {(json ? "application/json" : "text/plain")}; charset=utf-8\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append($"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        return Encoding.ASCII.GetBytes(head.ToString()).Concat(content).ToArray();
    }
}
