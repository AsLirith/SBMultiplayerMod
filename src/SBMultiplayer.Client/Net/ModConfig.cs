using System.Text.Json;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// Configuration lue dans SBMultiplayer.json (à côté de la DLL), créée avec les valeurs par défaut si absente.
/// Variables d'environnement prioritaires (pratique pour lancer deux instances) : SBMULTI_SERVER, SBMULTI_NAME.
/// </summary>
internal sealed class ModConfig
{
    /// <summary>Adresse du serveur, "hôte:port" ou "hôte".</summary>
    public string Server { get; set; } = $"127.0.0.1:{ProtocolInfo.DefaultPort}";

    /// <summary>Pseudo affiché. Vide = pseudo Steam.</summary>
    public string PlayerName { get; set; } = "";

    public bool AutoConnect { get; set; } = true;

    /// <summary>Debug : log de la position chaque seconde.</summary>
    public bool DebugProbe { get; set; }

    /// <summary>Debug : ghost local qui suit le joueur avec 1,5 s de retard (étape 2).</summary>
    public bool DebugDelayedSelfGhost { get; set; }

    public static ModConfig Current { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void Load(string modDir)
    {
        string path = Path.Combine(modDir, "SBMultiplayer.json");
        try
        {
            if (File.Exists(path))
            {
                Current = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path), JsonOptions) ?? new ModConfig();
            }
            else
            {
                File.WriteAllText(path, JsonSerializer.Serialize(Current, JsonOptions));
                ModLog.Info($"Configuration par défaut créée : {path}");
            }
        }
        catch (Exception e)
        {
            ModLog.Error($"Configuration illisible ({path}), valeurs par défaut utilisées", e);
            Current = new ModConfig();
        }

        if (Environment.GetEnvironmentVariable("SBMULTI_SERVER") is { Length: > 0 } server)
        {
            Current.Server = server;
        }
        if (Environment.GetEnvironmentVariable("SBMULTI_NAME") is { Length: > 0 } name)
        {
            Current.PlayerName = name;
        }
    }

    public (string Host, int Port) ParseServer()
    {
        string s = Server.Trim();
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out int port))
        {
            return (s[..colon], port);
        }
        return (s, ProtocolInfo.DefaultPort);
    }
}
