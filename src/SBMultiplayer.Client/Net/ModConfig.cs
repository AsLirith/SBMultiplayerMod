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

    /// <summary>Distance max (en unités du jeu, 1 unité = 1 cm) pour afficher le pseudo d'un joueur. 0 = jamais.</summary>
    public float NameTagMaxDistance { get; set; } = 30f;

    /// <summary>Hauteur du pseudo au-dessus de la planche, en unités du jeu.</summary>
    public float NameTagHeight { get; set; } = 0.8f;

    /// <summary>Taille du texte des pseudos (1 = taille de la police du jeu en 1080p).</summary>
    public float NameTagScale { get; set; } = 0.4f;

    /// <summary>Panneau du round (temps restant, classement) en haut à droite.</summary>
    public bool ShowRoundPanel { get; set; } = true;

    /// <summary>Taille du panneau du round (1 = taille d'origine de la v0.4, jugée trop grosse).</summary>
    public float RoundPanelScale { get; set; } = 0.6f;

    /// <summary>Affiche les ghosts des autres joueurs.</summary>
    public bool ShowOtherPlayers { get; set; } = true;

    /// <summary>Opacité des ghosts des autres joueurs (0 à 1).</summary>
    public float GhostOpacity { get; set; } = 0.5f;

    /// <summary>Affiche les pseudos au-dessus des ghosts proches.</summary>
    public bool ShowNameTags { get; set; } = true;

    /// <summary>Touche du menu multijoueur (nom d'une touche MonoGame : F8, F10, Insert…).</summary>
    public string MenuKey { get; set; } = "F8";

    /// <summary>Touche pour rejoindre la map du round en cours.</summary>
    public string RejoinKey { get; set; } = "F7";

    /// <summary>Debug : log de la position chaque seconde.</summary>
    public bool DebugProbe { get; set; }

    /// <summary>Debug : ghost local qui suit le joueur avec 1,5 s de retard (étape 2).</summary>
    public bool DebugDelayedSelfGhost { get; set; }

    public static ModConfig Current { get; private set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string path = "";

    // Valeurs du fichier, quand une variable d'environnement les surcharge : on ne les écrase pas en sauvegardant.
    private static (string Server, string Name)? fileValuesBeforeEnv;

    /// <summary>Enregistre la configuration (appelé quand un réglage change dans le menu).</summary>
    public static void Save()
    {
        try
        {
            ModConfig toWrite = Current;
            if (fileValuesBeforeEnv is { } fileValues)
            {
                toWrite = (ModConfig)Current.MemberwiseClone();
                toWrite.Server = fileValues.Server;
                toWrite.PlayerName = fileValues.Name;
            }
            File.WriteAllText(path, JsonSerializer.Serialize(toWrite, JsonOptions));
        }
        catch (Exception e)
        {
            ModLog.Error($"Impossible d'enregistrer la configuration ({path})", e);
        }
    }

    /// <summary>L'adresse ou le pseudo ont été changés dans le menu : ils remplacent la surcharge d'environnement.</summary>
    public static void ForgetEnvironmentOverride() => fileValuesBeforeEnv = null;

    public static void Load(string modDir)
    {
        path = Path.Combine(modDir, "SBMultiplayer.json");
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

        string envServer = Environment.GetEnvironmentVariable("SBMULTI_SERVER") ?? "";
        string envName = Environment.GetEnvironmentVariable("SBMULTI_NAME") ?? "";
        if (envServer.Length > 0 || envName.Length > 0)
        {
            fileValuesBeforeEnv = (Current.Server, Current.PlayerName);
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
