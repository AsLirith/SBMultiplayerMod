using LiteNetLib.Utils;

namespace SBMultiplayer.Protocol;

public static class ProtocolInfo
{
    /// <summary>À incrémenter à chaque changement incompatible du format des messages.</summary>
    public const int Version = 3;

    public const int DefaultPort = 24800;

    public const int MaxNameLength = 24;

    /// <summary>Clé de connexion LiteNetLib : un client d'une autre version est refusé dès la poignée de main.</summary>
    public static string ConnectionKey => $"SBMultiplayer/{Version}";
}

public enum MessageType : byte
{
    /// <summary>C→S, fiable. Premier message après connexion.</summary>
    Hello = 1,

    /// <summary>S→C, fiable. Réponse à Hello : notre id et la liste des joueurs déjà présents.</summary>
    Welcome = 2,

    /// <summary>S→C, fiable.</summary>
    PlayerJoined = 3,

    /// <summary>S→C, fiable.</summary>
    PlayerLeft = 4,

    /// <summary>C→S (sans id) et S→C (avec id), fiable. Map sur laquelle se trouve le joueur ("" = aucune).</summary>
    MapChanged = 5,

    /// <summary>C→S (sans id) et S→C (avec id), non fiable. Position de la planche.</summary>
    State = 6,

    /// <summary>S→C, fiable. Round en cours : map imposée, phase, temps restant.</summary>
    RoundInfo = 7,

    /// <summary>C→S, fiable. Arrivée franchie pendant un round.</summary>
    Finish = 8,

    /// <summary>S→C, fiable. Classement du round (meilleur temps de chacun).</summary>
    Leaderboard = 9,

    /// <summary>C→S (sans id) et S→C (avec id), fiable. Skin de planche du joueur (id du jeu).</summary>
    SkinChanged = 10,
}

public enum RoundPhase : byte
{
    /// <summary>Pas de rounds : chacun roule où il veut (serveur sans liste de maps).</summary>
    Free = 0,

    /// <summary>Round en cours sur la map imposée.</summary>
    Playing = 1,

    /// <summary>Round terminé : affichage des résultats avant la map suivante.</summary>
    Results = 2,
}

/// <param name="MapKey">Map imposée ("o:&lt;id&gt;" ou "o:&lt;id&gt;:&lt;version&gt;"), "" en phase Free.</param>
/// <param name="RemainingSeconds">Temps restant dans la phase, au moment de l'envoi.</param>
public readonly record struct RoundInfo(int RoundId, string MapKey, RoundPhase Phase, float RemainingSeconds, int MapIndex, int MapCount);

public readonly record struct LeaderboardEntry(int PlayerId, string Name, uint BestMs);

public readonly record struct Vec3(float X, float Y, float Z);

/// <param name="Time">Horloge de l'émetteur, en secondes. Seuls les écarts entre deux états comptent.</param>
/// <param name="Segment">Incrémenté à chaque téléportation (restart, respawn) : pas d'interpolation entre deux segments.</param>
public readonly record struct PlayerState(double Time, Vec3 Position, Vec3 Orientation, ushort Segment);

public readonly record struct PlayerInfo(int Id, string Name, string MapKey, int Skin);

/// <summary>Sérialisation des messages. Chaque message commence par son <see cref="MessageType"/>.</summary>
public static class Msg
{
    public static NetDataWriter Hello(NetDataWriter w, string name, int skin)
    {
        w.Reset();
        w.Put((byte)MessageType.Hello);
        w.Put(name);
        w.Put(skin);
        return w;
    }

    public static NetDataWriter SkinChanged(NetDataWriter w, int? id, int skin)
    {
        w.Reset();
        w.Put((byte)MessageType.SkinChanged);
        if (id.HasValue)
        {
            w.Put(id.Value);
        }
        w.Put(skin);
        return w;
    }

    public static NetDataWriter Welcome(NetDataWriter w, int yourId, IReadOnlyCollection<PlayerInfo> players)
    {
        w.Reset();
        w.Put((byte)MessageType.Welcome);
        w.Put(yourId);
        w.Put(players.Count);
        foreach (PlayerInfo p in players)
        {
            Put(w, p);
        }
        return w;
    }

    public static NetDataWriter PlayerJoined(NetDataWriter w, PlayerInfo player)
    {
        w.Reset();
        w.Put((byte)MessageType.PlayerJoined);
        Put(w, player);
        return w;
    }

    public static NetDataWriter PlayerLeft(NetDataWriter w, int id)
    {
        w.Reset();
        w.Put((byte)MessageType.PlayerLeft);
        w.Put(id);
        return w;
    }

    public static NetDataWriter MapChanged(NetDataWriter w, int? id, string mapKey)
    {
        w.Reset();
        w.Put((byte)MessageType.MapChanged);
        if (id.HasValue)
        {
            w.Put(id.Value);
        }
        w.Put(mapKey);
        return w;
    }

    public static NetDataWriter State(NetDataWriter w, int? id, in PlayerState s)
    {
        w.Reset();
        w.Put((byte)MessageType.State);
        if (id.HasValue)
        {
            w.Put(id.Value);
        }
        Put(w, s);
        return w;
    }

    public static NetDataWriter Round(NetDataWriter w, in RoundInfo r)
    {
        w.Reset();
        w.Put((byte)MessageType.RoundInfo);
        w.Put(r.RoundId);
        w.Put(r.MapKey);
        w.Put((byte)r.Phase);
        w.Put(r.RemainingSeconds);
        w.Put(r.MapIndex);
        w.Put(r.MapCount);
        return w;
    }

    public static RoundInfo GetRound(NetDataReader r) =>
        new(r.GetInt(), r.GetString(256), (RoundPhase)r.GetByte(), r.GetFloat(), r.GetInt(), r.GetInt());

    public static NetDataWriter Finish(NetDataWriter w, int roundId, uint timeMs)
    {
        w.Reset();
        w.Put((byte)MessageType.Finish);
        w.Put(roundId);
        w.Put(timeMs);
        return w;
    }

    public static NetDataWriter Leaderboard(NetDataWriter w, int roundId, IReadOnlyCollection<LeaderboardEntry> entries)
    {
        w.Reset();
        w.Put((byte)MessageType.Leaderboard);
        w.Put(roundId);
        w.Put(entries.Count);
        foreach (LeaderboardEntry e in entries)
        {
            w.Put(e.PlayerId);
            w.Put(e.Name);
            w.Put(e.BestMs);
        }
        return w;
    }

    public static (int RoundId, List<LeaderboardEntry> Entries) GetLeaderboard(NetDataReader r)
    {
        int roundId = r.GetInt();
        int count = Math.Clamp(r.GetInt(), 0, 256);
        var list = new List<LeaderboardEntry>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(new LeaderboardEntry(r.GetInt(), r.GetString(ProtocolInfo.MaxNameLength * 4), r.GetUInt()));
        }
        return (roundId, list);
    }

    /// <summary>Vrai si la map d'un joueur ("o:1:2001") correspond à l'entrée de rotation ("o:1" ou "o:1:2001").</summary>
    public static bool MapMatches(string playerMapKey, string roundMapKey) =>
        roundMapKey.Length > 0 && (playerMapKey == roundMapKey || playerMapKey.StartsWith(roundMapKey + ":", StringComparison.Ordinal));

    public static string FormatTime(uint ms) => $"{ms / 60000}:{ms / 1000 % 60:00}.{ms % 1000:000}";

    public static void Put(NetDataWriter w, in PlayerState s)
    {
        w.Put(s.Time);
        Put(w, s.Position);
        Put(w, s.Orientation);
        w.Put(s.Segment);
    }

    public static PlayerState GetState(NetDataReader r) =>
        new(r.GetDouble(), GetVec3(r), GetVec3(r), r.GetUShort());

    public static void Put(NetDataWriter w, in PlayerInfo p)
    {
        w.Put(p.Id);
        w.Put(p.Name);
        w.Put(p.MapKey);
        w.Put(p.Skin);
    }

    public static PlayerInfo GetPlayerInfo(NetDataReader r) =>
        new(r.GetInt(), r.GetString(ProtocolInfo.MaxNameLength * 4), r.GetString(256), r.GetInt());

    private static void Put(NetDataWriter w, in Vec3 v)
    {
        w.Put(v.X);
        w.Put(v.Y);
        w.Put(v.Z);
    }

    private static Vec3 GetVec3(NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());

    /// <summary>
    /// Nettoie un pseudo reçu : longueur bornée (en caractères visibles), et suppression des caractères
    /// de contrôle et de format invisibles (contrôles bidi, caractères « tag », espaces de largeur nulle…)
    /// qui permettent d'usurper un pseudo ou de casser l'affichage.
    /// </summary>
    public static string SanitizeName(string? name)
    {
        var sb = new System.Text.StringBuilder();
        int count = 0;
        foreach (System.Text.Rune rune in (name ?? "").EnumerateRunes())
        {
            var category = System.Text.Rune.GetUnicodeCategory(rune);
            if (category is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }
            if (count++ == ProtocolInfo.MaxNameLength)
            {
                break;
            }
            sb.Append(rune.ToString());
        }
        string clean = sb.ToString().Trim();
        return clean.Length == 0 ? "Joueur" : clean;
    }
}
