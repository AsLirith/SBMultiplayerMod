using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using SBMultiplayer.Server;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// "Host a game" from the F8 menu: runs the same server as the dedicated one, inside the game, on its own
/// thread (it keeps running when the game window loses focus). The host connects to it automatically and is
/// its admin. Settings are kept in hosted-server.json, next to the mod.
/// </summary>
internal static class HostedServer
{
    private static GameServer? server;
    private static Thread? thread;
    private static volatile bool running;

    public static bool IsRunning => running;

    public static int Port { get; private set; }

    /// <summary>Admin password of the hosted game (random if none is set): the host logs in with it automatically.</summary>
    public static string AdminPassword { get; private set; } = "";

    private static string ConfigPath => Path.Combine(Path.GetDirectoryName(typeof(HostedServer).Assembly.Location)!, "hosted-server.json");

    /// <summary>Starts hosting. Returns an error message, or null.</summary>
    public static string? Start(int port)
    {
        if (running)
        {
            return null;
        }
        try
        {
            if (!File.Exists(ConfigPath))
            {
                File.WriteAllText(ConfigPath, ServerOptions.Template);
            }
            ServerOptions options = ServerOptions.Load(ConfigPath);
            options.Port = port;
            options.Echo = false;
            if (string.IsNullOrEmpty(options.AdminPassword))
            {
                options.AdminPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            }
            if (options.ServerName == "SBMultiplayer server")
            {
                options.ServerName = $"{MultiplayerClient.Instance.PlayerName}'s game";
            }

            GameServer.LogSink = line => ModLog.Info("[server] " + line);
            var instance = new GameServer(options, ConfigPath);
            instance.Start(); // throws if the port is already used
            server = instance;
            Port = port;
            AdminPassword = options.AdminPassword;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "SBMultiplayer hosted server" };
            thread.Start();
            ModLog.Info($"Hosting a game on UDP port {port}");
            return null;
        }
        catch (Exception e)
        {
            ModLog.Error("Could not host a game", e);
            server = null;
            return e.Message;
        }
    }

    private static void Loop()
    {
        GameServer? s = server;
        try
        {
            while (running && s != null)
            {
                s.Poll();
                Thread.Sleep(2);
            }
        }
        catch (Exception e)
        {
            ModLog.Error("Hosted server stopped after an error", e);
            running = false;
        }
        finally
        {
            s?.Stop();
        }
    }

    public static void Stop()
    {
        if (!running)
        {
            return;
        }
        running = false;
        thread?.Join(1000);
        thread = null;
        server = null;
        ModLog.Info("Hosted game stopped");
    }

    /// <summary>This PC's addresses on the local network (what friends on the same network connect to).</summary>
    public static List<string> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)
                            && !a.ToString().StartsWith("172.17.", StringComparison.Ordinal)) // link-local, Docker bridge
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch (Exception)
        {
            return new List<string>();
        }
    }
}
