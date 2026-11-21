using BytingLib;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SBMultiplayer.Client.UI;
using SE;

namespace SBMultiplayer.Client.Rooms;

/// <summary>
/// The Multiplayer page, opened by the main menu's Multiplayer button. It's a screen like the game's Rooms screen, with
/// one picture per part of the mod. The lists (public games, SBLB lobbies, tournaments) open screens of their own.
/// The settings-like parts (host a game, settings) open the mod's menus over the page, like the game's options.
/// If anything about the game's screens can't be found, the F8 menu opens instead (see GameScreens.ChangeScene).
/// </summary>
internal static class MultiplayerRooms
{
    private sealed record Tile(string Key, Func<string> Name, Action Open);

    private static readonly Tile[] Tiles =
    {
        new("public", () => Tr.RoomPublicGames, () => ListScreen.Open(new PublicGamesSource())),
        new("sblb", () => Tr.RoomSblbLobbies, () => ListScreen.Open(new SblbLobbiesSource())),
        new("tournaments", () => Tr.RoomTournaments, () => ListScreen.Open(new TournamentsSource())),
        new("host", () => Tr.RoomHost, MultiplayerMenu.OpenHost),
        new("settings", () => Tr.RoomSettings, MultiplayerMenu.OpenSettings),
    };

    /// <summary>The tile last opened: selected again when coming back.</summary>
    private static string lastTile = "public";

    public static void Open()
    {
        PopupMenu.CloseAll();
        try
        {
            GameScreens.ChangeScene(Create);
        }
        catch (Exception e)
        {
            ModLog.Error("Could not open the Multiplayer screen (the F8 menu opens instead)", e);
            MultiplayerMenu.Open();
        }
    }

    private static Scene Create() => GameScreens.CreateScene(GameScreens.ToMainMenu, tooltip =>
    {
        Label title = GameScreens.Title(Tr.MultiplayerButton);
        var tiles = Tiles.Select(t => (Element)Button(t)).ToArray();
        return new Element?[]
        {
            GameScreens.TopPanel(" ", title, tooltip),
            GameScreens.BottomPanel(new Panel(1f, 390f)).Add(GameScreens.BackButton(GameScreens.ToMainMenu)),
            new PanelStack(64f) { Anchor = new Vector2(0.5f, 0.46f) }
                .SetStyle(GameScreens.Ui.StrongStyle)
                .Add(new PanelStack(110f, vertical: false, null, new Vector2(0.5f, 0.5f)).Add(tiles)),
        };
    }, out _);

    private static Button Button(Tile tile)
    {
        // Same size as the game's rooms when it shows five of them.
        Button button = GameScreens.RoomButton(RoomArt.Get(tile.Key), tile.Name(), () =>
        {
            lastTile = tile.Key;
            tile.Open();
        }, 510f, 1000f);
        if (tile.Key == lastTile)
        {
            button.SetNavigationStart();
        }
        return button;
    }
}

/// <summary>The pictures of the Multiplayer page, embedded in the mod (Rooms/Art/*.jpg, 800 x 1250; sources in art/rooms).</summary>
internal static class RoomArt
{
    private static readonly Dictionary<string, Ref<Texture2D>> Loaded = new();

    public static Ref<Texture2D> Get(string key)
    {
        if (Loaded.TryGetValue(key, out Ref<Texture2D>? known))
        {
            return known;
        }
        string resource = $"SBMultiplayer.Rooms.{key}.jpg";
        using Stream stream = typeof(RoomArt).Assembly.GetManifestResourceStream(resource)
            ?? throw new FileNotFoundException("missing room picture", resource);
        Texture2D texture = Texture2D.FromStream(GameScreens.Device, stream);
        var picture = new Ref<Texture2D>(new Promise<Texture2D>(texture), null);
        Loaded[key] = picture; // kept for the whole game: five small pictures
        return picture;
    }
}
