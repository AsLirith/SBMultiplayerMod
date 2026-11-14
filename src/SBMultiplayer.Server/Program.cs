using SBMultiplayer.Protocol;
using SBMultiplayer.Server;

var options = new ServerOptions();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port":
            options.Port = int.Parse(args[++i]);
            break;
        case "--max-players":
            options.MaxPlayers = int.Parse(args[++i]);
            break;
        case "--echo":
            options.Echo = true;
            break;
        case "--echo-delay":
            options.EchoDelaySeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--maps":
            options.Maps = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            break;
        case "--round":
            options.RoundSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "--results":
            options.ResultsSeconds = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        case "-h":
        case "--help":
            Console.WriteLine($"""
                SBMultiplayer.Server - dedicated server (protocol v{ProtocolInfo.Version})

                  --port <n>          UDP port (default {ProtocolInfo.DefaultPort})
                  --max-players <n>   max players (default 32)
                  --echo              sends each player their own delayed board back (solo testing)
                  --echo-delay <s>    echo delay in seconds (default 2)
                  --maps <list>       rotation, e.g. o:1,o:2,o:57 (official level IDs; empty = free mode)
                  --round <s>         round duration in seconds (default 300)
                  --results <s>       results screen duration (default 15)

                Console commands: list, round, next, quit
                """);
            return;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]} (see --help)");
            return;
    }
}

var server = new GameServer(options);
server.Start();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Commandes console sur un thread à part ; la logique serveur reste sur le thread principal.
var commands = new System.Collections.Concurrent.ConcurrentQueue<string>();
var inputThread = new Thread(() =>
{
    string? line;
    while ((line = Console.ReadLine()) != null)
    {
        commands.Enqueue(line.Trim());
    }
})
{ IsBackground = true };
inputThread.Start();

while (!cts.IsCancellationRequested)
{
    server.Poll();
    while (commands.TryDequeue(out string? cmd))
    {
        switch (cmd)
        {
            case "list":
                Console.WriteLine($"{server.PlayerCount} player(s) connected");
                foreach (string line in server.DescribePlayers())
                {
                    Console.WriteLine("  " + line);
                }
                break;
            case "round":
                Console.WriteLine(server.DescribeRound());
                break;
            case "next":
                server.SkipPhase();
                break;
            case "quit":
            case "exit":
                cts.Cancel();
                break;
            case "":
                break;
            default:
                Console.WriteLine("Commands: list, round, next, quit");
                break;
        }
    }
    Thread.Sleep(2);
}

server.Stop();
Console.WriteLine("Server stopped.");
