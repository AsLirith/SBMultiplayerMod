using System.Reflection;
using BytingLib;
using HarmonyLib;
using Microsoft.Xna.Framework.Graphics;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>
/// Textures des skins de planche, chargées à la demande et gardées pour la session.
/// Un skin n'est qu'une texture : id > 0 = skin débloqué ("Textures/Achievements/Skins/id"),
/// id &lt; 0 = variante de base ("Models/Resources/Board/-id"), 0 = planche par défaut.
/// </summary>
internal static class SkinCache
{
    private static readonly AccessTools.FieldRef<GlobalGame, ContentLoader> ContentLoaderRef =
        AccessTools.FieldRefAccess<GlobalGame, ContentLoader>("globalContentLoader");

    private static readonly AccessTools.FieldRef<GlobalGame, GlobalAssets> GlobalAssetsRef =
        AccessTools.FieldRefAccess<GlobalGame, GlobalAssets>("globalAssets");

    // ATTENTION : la Ref "Models/Resources/Board/0" est partagée et le jeu y met NOTRE skin courant.
    // La vraie planche par défaut est gardée à part par GlobalAssets (champ privé defaultSkin).
    private static readonly FieldInfo DefaultSkinField = AccessTools.Field(typeof(GlobalAssets), "defaultSkin");

    private static readonly PropertyInfo SaveStateProperty = AccessTools.Property(typeof(GlobalGame), "SaveState");

    private static readonly Dictionary<int, Ref<Texture2D>?> Cache = new();

    /// <summary>Skin choisi par le joueur local dans le jeu.</summary>
    public static int LocalSkinId(GlobalGame game) => ((SaveState)SaveStateProperty.GetValue(game)!).Skin.Id;

    /// <summary>Texture d'un skin, ou null si introuvable (on garde alors la texture courante).</summary>
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
                    result = new Ref<Texture2D>(new Promise<Texture2D>(tex), _ => { }); // texture du jeu : ne pas la libérer
                }
                else
                {
                    string folder = id > 0 ? "Textures/Achievements/Skins/" : "Models/Resources/Board/";
                    result = ContentLoaderRef(game).Use<Texture2D>(folder + Math.Abs(id));
                }
            }
            catch (Exception e)
            {
                ModLog.Warn($"Skin {id} introuvable ({e.GetType().Name}) : texture par défaut utilisée");
            }
        }
        Cache[id] = result;
        return result;
    }
}
