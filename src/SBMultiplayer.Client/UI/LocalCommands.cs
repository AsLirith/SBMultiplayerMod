using SBMultiplayer.Client.Ghosts;
using SBMultiplayer.Client.Net;
using SBMultiplayer.Protocol;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Chat commands handled by the mod itself, without the server (it already knows every player's map):
/// /locate, /goto, /ignore, /unignore. Everything else is sent to the server. Game thread only.
/// </summary>
internal static class LocalCommands
{
    private static MultiplayerClient Client => MultiplayerClient.Instance;

    /// <summary>Returns true if the text was a local command (handled here, not sent to the server).</summary>
    public static bool TryHandle(string text)
    {
        if (!text.StartsWith('/'))
        {
            return false;
        }
        string[] parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string command = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string arg = parts.Length > 1 ? parts[1] : "";
        switch (command)
        {
            case "locate":
                Locate(arg);
                return true;
            case "goto":
                GoTo(arg);
                return true;
            case "ignore":
                Ignore(arg, ignore: true);
                return true;
            case "unignore":
                Ignore(arg, ignore: false);
                return true;
            default:
                return false;
        }
    }

    private static void Reply(string text) => ChatLog.AddSystem(text, new Microsoft.Xna.Framework.Color(255, 170, 80));

    /// <summary>Every player, us included: (id, name, map key, spectating).</summary>
    private static List<(int Id, string Name, string MapKey, bool Spectating)> Everyone()
    {
        var list = Client.Players.Values.Where(p => p.Id < 1_000_000)
            .Select(p => (p.Id, p.Name, p.MapKey, (bool)p.IsSpectator)).ToList();
        if (Client.LocalId is int me)
        {
            list.Add((me, Client.PlayerName, Client.CurrentMapKey, Client.IsSpectating));
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"#id", exact name, or unique name prefix (case-insensitive), like the server's commands.</summary>
    private static (int Id, string Name, string MapKey, bool Spectating)? Find(string query)
    {
        var all = Everyone();
        if (query.StartsWith('#') && int.TryParse(query[1..], out int id))
        {
            return all.FirstOrDefault(p => p.Id == id) is { Name: not null } byId ? byId : null;
        }
        var exact = all.Where(p => string.Equals(p.Name, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }
        var prefix = all.Where(p => p.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefix.Count == 1 ? prefix[0] : null;
    }

    /// <summary>Readable place of a player: level code and name, Workshop map name, local map or menus.</summary>
    public static string DescribePlace(string mapKey, bool spectating)
    {
        string place = mapKey == "" ? Tr.LocateMenus
            : mapKey.StartsWith("l:", StringComparison.Ordinal) ? Tr.LocateLocalMap
            : GameAccess.DescribeMap(mapKey);
        return spectating ? $"{place} ({Tr.SpectatorTitle.ToLowerInvariant()})" : place;
    }

    private static void Locate(string arg)
    {
        if (!Client.IsReady)
        {
            Reply(Tr.AdminNeedsConnection);
            return;
        }
        if (arg == "")
        {
            Reply(Tr.Usage("/locate <name> | all"));
            return;
        }
        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in Everyone())
            {
                Reply($"{p.Name}: {DescribePlace(p.MapKey, p.Spectating)}");
            }
            return;
        }
        if (Find(arg) is not { } found)
        {
            Reply(Tr.NoPlayerMatching(arg));
            return;
        }
        Reply($"{found.Name}: {DescribePlace(found.MapKey, found.Spectating)}");
    }

    private static void GoTo(string arg)
    {
        if (!Client.IsReady)
        {
            Reply(Tr.AdminNeedsConnection);
            return;
        }
        if (arg == "")
        {
            Reply(Tr.Usage("/goto <name>"));
            return;
        }
        if (Find(arg) is not { } target)
        {
            Reply(Tr.NoPlayerMatching(arg));
            return;
        }
        string key = target.MapKey;
        if (key == "")
        {
            Reply(Tr.GotoInMenus(target.Name));
            return;
        }
        if (key.StartsWith("l:", StringComparison.Ordinal))
        {
            Reply(Tr.GotoLocalMap(target.Name));
            return;
        }
        if (key == Client.CurrentMapKey)
        {
            Reply(Tr.GotoSameMap(target.Name));
            return;
        }
        // Official level: any version ("o:<id>"); Workshop map: its latest version ("c:M<id>").
        string load = Msg.WorkshopId(key) is ulong workshopId ? $"c:M{workshopId}" : string.Join(':', key.Split(':').Take(2));
        Reply(Tr.GotoLoading(target.Name, DescribePlace(key, false)));
        Client.GoToMap(load);
    }

    private static void Ignore(string arg, bool ignore)
    {
        List<string> ignored = ModConfig.Current.IgnoredPlayers;
        if (arg == "")
        {
            Reply(ignored.Count == 0 ? Tr.IgnoreListEmpty : Tr.IgnoreList(string.Join(", ", ignored)));
            return;
        }
        // Players currently connected are matched like the other commands; anyone else by the exact name typed.
        string name = Find(arg)?.Name ?? ignored.FirstOrDefault(n => n.Equals(arg, StringComparison.OrdinalIgnoreCase)) ?? arg;
        if (ignore)
        {
            if (name == Client.PlayerName)
            {
                return;
            }
            if (!ignored.Contains(name))
            {
                ignored.Add(name);
                ModConfig.Save();
            }
            Reply(Tr.Ignored(name));
        }
        else
        {
            ignored.RemoveAll(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
            ModConfig.Save();
            Reply(Tr.Unignored(name));
        }
    }

    /// <summary>Chat messages of ignored players are not shown.</summary>
    public static bool IsIgnored(string name) =>
        ModConfig.Current.IgnoredPlayers.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
}
