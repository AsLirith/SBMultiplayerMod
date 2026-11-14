using SBMultiplayer.Client.Net;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Textes de l'interface du mod. La langue suit celle du jeu (français si le jeu est en français,
/// anglais sinon), sauf si "Language" est forcé à "fr" ou "en" dans SBMultiplayer.json.
/// NB : la police du jeu n'a ni « — » ni « … » : s'en tenir à l'ASCII et aux accents courants.
/// </summary>
internal static class Tr
{
    public static bool French
    {
        get
        {
            string forced = ModConfig.Current.Language.Trim().ToLowerInvariant();
            if (forced is "fr" or "en")
            {
                return forced == "fr";
            }
            try
            {
                return SE.Loca.L?.LanguageKey?.StartsWith("fr", StringComparison.OrdinalIgnoreCase) == true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string T(string en, string fr) => French ? fr : en;

    private static string Players(int n) => French
        ? $"{n} autre{(n > 1 ? "s" : "")} joueur{(n > 1 ? "s" : "")}"
        : $"{n} other player{(n > 1 ? "s" : "")}";

    // ---------- Menu ----------
    public static string MenuTitle(string version) => T($"Multiplayer  (SBMultiplayer v{version})", $"Multijoueur  (SBMultiplayer v{version})");
    public static string ServerField => T("Server (address:port)", "Serveur (adresse:port)");
    public static string NameField => T("Name (empty = Steam name)", "Pseudo (vide = pseudo Steam)");
    public static string Connect => T("Connect", "Se connecter");
    public static string Disconnect => T("Disconnect", "Se déconnecter");
    public static string JoinRound => T("Join round", "Rejoindre le round");
    public static string ShowOtherPlayers => T("Show other players", "Afficher les autres joueurs");
    public static string OtherPlayersOpacity => T("Other players' opacity", "Opacité des autres joueurs");
    public static string ShowNameTags => T("Show names", "Afficher les pseudos");
    public static string NameTagDistance => T("Name display distance", "Distance des pseudos");
    public static string RoundPanel => T("Round panel", "Panneau du round");
    public static string RoundPanelSize => T("Round panel size", "Taille du panneau du round");
    public static string KeysHint(string menuKey, string rejoinKey) =>
        T($"{menuKey}: this menu      {rejoinKey}: join the round's map", $"{menuKey} : ce menu      {rejoinKey} : rejoindre la map du round");
    public static string Back => T("Back", "Retour");
    public static string MenuOpenFailed => T("Could not open the multiplayer menu (see SBMultiplayer.log)", "Impossible d'ouvrir le menu multijoueur (voir SBMultiplayer.log)");

    // ---------- Statut ----------
    public static string StatusConnected(string server, string name, int others) =>
        T($"Connected to {server} as {name} ({Players(others)})", $"Connecté à {server} en tant que {name} ({Players(others)})");
    public static string StatusConnecting(string server) => T($"Connecting to {server}...", $"Connexion à {server}...");
    public static string StatusDisconnected => T("Disconnected", "Déconnecté");
    public static string StatusOffline => T("Offline - retrying automatically", "Hors ligne - nouvelle tentative automatique");

    // ---------- Panneau du round ----------
    public static string ResultsHeader(int seconds) => T($"Results - next map in {seconds} s", $"Résultats - map suivante dans {seconds} s");
    public static string NotOnRoundMap(string rejoinKey) =>
        T($"You're not on the round's map ({rejoinKey} to join it)", $"Tu n'es pas sur la map du round ({rejoinKey} pour la rejoindre)");
    public static string NoTimesYet => T("No times yet", "Aucun temps pour l'instant");

    // ---------- Notifications ----------
    public static string Connected(string server, int others, string menuKey) =>
        T($"Connected to {server} ({Players(others)}) - {menuKey}: multiplayer menu", $"Connecté à {server} ({Players(others)}) - {menuKey} : menu multijoueur");
    public static string DisconnectedByUser => T("Disconnected from the server", "Déconnecté du serveur");
    public static string RetryingEvery(string reason, int seconds) =>
        T($"{reason} - retrying every {seconds} s", $"{reason} - nouvelle tentative toutes les {seconds} s");
    public static string CannotConnect(string server, string error) => T($"Cannot connect to {server} ({error})", $"Connexion impossible à {server} ({error})");
    public static string ServerUnreachable(string server) => T($"Server unreachable ({server})", $"Serveur injoignable ({server})");
    public static string ConnectionLost => T("Connection to the server lost", "Connexion au serveur perdue");
    public static string ServerClosed => T("The server closed the connection", "Le serveur a fermé la connexion");
    public static string ConnectionRejected => T("Connection rejected by the server", "Connexion refusée par le serveur");
    public static string ServerFull => T("The server is full", "Le serveur est plein");
    public static string VersionMismatch(string serverProtocol) =>
        T($"Incompatible mod version (server uses protocol v{serverProtocol}). Please update the mod.",
          $"Version du mod incompatible (le serveur utilise le protocole v{serverProtocol}). Mets le mod à jour.");
    public static string PlayerJoined(string name) => T($"{name} joined the game", $"{name} a rejoint la partie");
    public static string PlayerLeft(string name) => T($"{name} left the game", $"{name} a quitté la partie");
    public static string RoundStarted(int id, string map) => T($"Round {id}: {map}", $"Round {id} : {map}");
    public static string RoundEndNoTimes => T("Round over: no times", "Fin du round : aucun temps");
    public static string RoundEndWinner(string name, string time) => T($"Round over: {name} wins with {time}", $"Fin du round : {name} gagne en {time}");
    public static string NoRound => T("No round in progress", "Aucun round en cours");
    public static string AlreadyOnRoundMap => T("You're already on the round's map", "Tu es déjà sur la map du round");
    public static string MapLoadFailedVersion(string key) => T($"Could not load map {key} (different game version?)", $"Impossible de charger la map {key} (version du jeu différente ?)");
    public static string MapLoadFailed(string key) => T($"Could not load map {key}", $"Impossible de charger la map {key}");
    public static string MapNotSupported(string key) => T($"Map not supported by this mod version: {key}", $"Map non supportée par cette version du mod : {key}");
    public static string NewBest(string time) => T($"{time} - new personal best!", $"{time} - nouveau meilleur temps !");
    public static string TimeVsBest(string time, string best, float gapSeconds) =>
        T($"{time} (best: {best}, +{gapSeconds:0.000})", $"{time} (meilleur : {best}, +{gapSeconds:0.000})");
}
