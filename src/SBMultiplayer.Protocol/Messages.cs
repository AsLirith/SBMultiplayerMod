using LiteNetLib.Utils;

namespace SBMultiplayer.Protocol;

public static class ProtocolInfo
{
    /// <summary>À incrémenter à chaque changement incompatible du format des messages.</summary>
    public const int Version = 1;

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
}

public readonly record struct Vec3(float X, float Y, float Z);

/// <param name="Time">Horloge de l'émetteur, en secondes. Seuls les écarts entre deux états comptent.</param>
/// <param name="Segment">Incrémenté à chaque téléportation (restart, respawn) : pas d'interpolation entre deux segments.</param>
public readonly record struct PlayerState(double Time, Vec3 Position, Vec3 Orientation, ushort Segment);

public readonly record struct PlayerInfo(int Id, string Name, string MapKey);

/// <summary>Sérialisation des messages. Chaque message commence par son <see cref="MessageType"/>.</summary>
public static class Msg
{
    public static NetDataWriter Hello(NetDataWriter w, string name)
    {
        w.Reset();
        w.Put((byte)MessageType.Hello);
        w.Put(name);
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
    }

    public static PlayerInfo GetPlayerInfo(NetDataReader r) =>
        new(r.GetInt(), r.GetString(ProtocolInfo.MaxNameLength * 4), r.GetString(256));

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
