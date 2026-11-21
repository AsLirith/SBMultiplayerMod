using SBMultiplayer.Protocol;

namespace SBMultiplayer.Server;

/// <summary>Maps verified Steam IDs to stable display names for one tournament.</summary>
public sealed class TournamentRoster
{
    private readonly List<RosterEntry> _entries = new();

    public bool Locked { get; private set; }

    /// <summary>The roster after Lock. Before that, everyone seen so far.</summary>
    public IReadOnlyList<RosterEntry> Entries => _entries;

    /// <summary>Display name for this Steam ID. The first name given sticks, even if the Steam name changes later.</summary>
    public string Join(string steamId, string steamName)
    {
        var existing = _entries.Find(e => e.SteamId == steamId);
        if (existing != null) return existing.Name;

        string baseName = Msg.SanitizeName((steamName ?? "").Replace("#", "")).Trim();
        if (baseName.Length == 0) baseName = "Player";

        string name = baseName;
        for (int n = 2; _entries.Exists(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{baseName} ({n})";

        _entries.Add(new RosterEntry(steamId, name));
        return name;
    }

    public bool CanEnter(string steamId) => !Locked || Contains(steamId);

    public bool Contains(string steamId) => _entries.Exists(e => e.SteamId == steamId);

    /// <summary>Display name for someone who only watches. They aren't added to the roster, and the name never matches a roster name.</summary>
    public string WatcherName(string steamName)
    {
        string baseName = Msg.SanitizeName((steamName ?? "").Replace("#", "")).Trim();
        if (baseName.Length == 0) baseName = "Player";
        string name = baseName;
        for (int n = 2; _entries.Exists(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{baseName} ({n})";
        return name;
    }

    /// <summary>Freezes the roster to the players present now.</summary>
    public void Lock(IEnumerable<string> presentSteamIds)
    {
        var present = new HashSet<string>(presentSteamIds);
        _entries.RemoveAll(e => !present.Contains(e.SteamId));
        Locked = true;
    }

    public string? SteamIdOf(string displayName) => _entries.Find(e => e.Name == displayName)?.SteamId;

    public static TournamentRoster FromSave(IEnumerable<RosterEntry> roster)
    {
        var r = new TournamentRoster();
        r._entries.AddRange(roster);
        r.Locked = true;
        return r;
    }
}
