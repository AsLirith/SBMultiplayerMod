using System.Reflection;
using BytingLib;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>
/// Board skin textures, loaded on demand and kept for the session.
/// A skin is just a texture: id > 0 = unlocked skin ("Textures/Achievements/Skins/id"),
/// id &lt; 0 = base variant ("Models/Resources/Board/-id"), 0 = default board.
/// </summary>
internal static class SkinCache
{
    private static readonly AccessTools.FieldRef<GlobalGame, ContentLoader> ContentLoaderRef =
        AccessTools.FieldRefAccess<GlobalGame, ContentLoader>("globalContentLoader");

    private static readonly AccessTools.FieldRef<GlobalGame, GlobalAssets> GlobalAssetsRef =
        AccessTools.FieldRefAccess<GlobalGame, GlobalAssets>("globalAssets");

    // WARNING: the "Models/Resources/Board/0" Ref is shared and the game puts OUR current skin in it.
    // The real default board is kept separately by GlobalAssets (private field defaultSkin).
    private static readonly FieldInfo DefaultSkinField = AccessTools.Field(typeof(GlobalAssets), "defaultSkin");

    private static readonly PropertyInfo SaveStateProperty = AccessTools.Property(typeof(GlobalGame), "SaveState");

    private static readonly Dictionary<int, Ref<Texture2D>?> Cache = new();

    /// <summary>Skin chosen by the local player in the game.</summary>
    public static int LocalSkinId(GlobalGame game) => ((SaveState)SaveStateProperty.GetValue(game)!).Skin.Id;

    /// <summary>A skin's texture, or null if not found (the current texture is then kept).</summary>
    public static Ref<Texture2D>? Get(int id)
    {
        if (Cache.TryGetValue(id, out Ref<Texture2D>? cached))
        {
            return cached;
        }
        Ref<Texture2D>? result = null;
        GlobalGame? game = GameAccess.Game;
        if (game != null)
        {
            try
            {
                if (id == 0)
                {
                    var tex = (Texture2D)DefaultSkinField.GetValue(GlobalAssetsRef(game))!;
                    result = new Ref<Texture2D>(new Promise<Texture2D>(tex), _ => { }); // the game's texture: don't release it
                }
                else
                {
                    string folder = id > 0 ? "Textures/Achievements/Skins/" : "Models/Resources/Board/";
                    result = ContentLoaderRef(game).Use<Texture2D>(folder + Math.Abs(id));
                }
            }
            catch (Exception e)
            {
                ModLog.Warn($"Skin {id} not found ({e.GetType().Name}): using the default texture");
            }
        }
        Cache[id] = result;
        return result;
    }
}
