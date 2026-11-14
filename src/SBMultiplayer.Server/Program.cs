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
                SBMultiplayer.Server — serveur dédié (protocole v{ProtocolInfo.Version})

                  --port <n>          port UDP (défaut {ProtocolInfo.DefaultPort})
                  --max-players <n>   joueurs max (défaut 32)
                  --echo              renvoie à chaque joueur sa propre planche retardée (test en solo)
                  --echo-delay <s>    retard de l'écho en secondes (défaut 2)
                  --maps <liste>      rotation, ex. o:1,o:2,o:57 (id de niveau officiel ; vide = mode libre)
                  --round <s>         durée d'un round en secondes (défaut 300)
                  --results <s>       durée de l'écran de résultats (défaut 15)

                Commandes console : list, round, next, quit
                """);
            return;
        default:
            Console.Error.WriteLine($"Argument inconnu : {args[i]} (voir --help)");
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
                Console.WriteLine($"{server.PlayerCount} joueur(s) connecté(s)");
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
                Console.WriteLine("Commandes : list, round, next, quit");
                break;
        }
    }
    Thread.Sleep(2);
}

server.Stop();
Console.WriteLine("Serveur arrêté.");
