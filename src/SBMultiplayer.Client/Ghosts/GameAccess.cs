using System.Reflection;
using BytingLib;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>Accès aux membres non publics du jeu, résolus une seule fois.</summary>
internal static class GameAccess
{
    public const int TicksPerSecond = 60;

    public static readonly Func<Board, GhostState> GetGhostState =
        AccessTools.MethodDelegate<Func<Board, GhostState>>(AccessTools.Method(typeof(Board), "GetGhostState"));

    private static readonly Type Scene3DType = AccessTools.TypeByName("SE.Scene3D");
    private static readonly Type Scene3DIngameType = AccessTools.TypeByName("SE.Scene3DIngame");

    // Scene3D est internal : on lit sa propriété TransparencyBatch par réflexion.
    private static readonly MethodInfo TransparencyBatchGetter = AccessTools.PropertyGetter(Scene3DType, "TransparencyBatch");

    public static TransparencyBatchSwapper GetTransparencyBatch(Scene scene) =>
        (TransparencyBatchSwapper)TransparencyBatchGetter.Invoke(scene, null)!;

    // ---------- Caméra de la scène (pour projeter les pseudos à l'écran) ----------

    private static readonly FieldInfo SceneDeps = AccessTools.Field(Scene3DType, "d");
    private static readonly Type DepsType = AccessTools.TypeByName("SE.Scene3DDependencies");
    private static readonly MethodInfo DepsCamera = AccessTools.PropertyGetter(DepsType, "camera");
    private static readonly MethodInfo DepsProjection = AccessTools.PropertyGetter(DepsType, "projection");

    /// <summary>Matrice vue × projection et position de la caméra de la scène.</summary>
    public static (Matrix ViewProjection, Vector3 CameraPosition) GetCamera(Scene scene)
    {
        object deps = SceneDeps.GetValue(scene)!;
        var camera = (ICamera)DepsCamera.Invoke(deps, null)!;
        var projection = (IProjection)DepsProjection.Invoke(deps, null)!;
        return (camera.View * projection.Transform, camera.Position);
    }

    // ---------- GlobalGame (capturé par GlobalTickPatch) ----------

    public static GlobalGame? Game { get; set; }

    private static readonly AccessTools.FieldRef<GlobalGame, Scene> CurrentSceneRef =
        AccessTools.FieldRefAccess<GlobalGame, Scene>("currentScene");

    private static readonly AccessTools.FieldRef<GlobalGame, Level> CurrentLevelRef =
        AccessTools.FieldRefAccess<GlobalGame, Level>("currentLevel");

    private static readonly AccessTools.FieldRef<GlobalGame, UIAssets> UiAssetsRef =
        AccessTools.FieldRefAccess<GlobalGame, UIAssets>("uiAssets");

    private static readonly MethodInfo StartLevelMethod = AccessTools.Method(typeof(GlobalGame), "StartLevel");
    private static readonly MethodInfo SetCurrentChallengeMethod = AccessTools.Method(typeof(GlobalGame), "SetCurrentChallenge");
    private static readonly MethodInfo GetMaxNonSpoileryMedalMethod = AccessTools.Method(typeof(GlobalGame), "GetMaxNonSpoileryMedal");

    public static Scene? CurrentScene => Game == null ? null : CurrentSceneRef(Game);

    public static bool IsIngameScene(Scene? scene) => scene != null && Scene3DIngameType.IsInstanceOfType(scene);

    public static SpriteFont? UiFont => Game == null ? null : UiAssetsRef(Game)?.FontSmall?.Value;

    /// <summary>Lance un niveau officiel, comme le menu de sélection. À appeler depuis le thread du jeu.</summary>
    public static bool StartOfficialLevel(uint id, uint? version, bool quickRetry)
    {
        GlobalGame? game = Game;
        Level? level = game?.levelOrder.GetLevelByID(id);
        if (game == null || level == null)
        {
            ModLog.Warn($"Niveau officiel {id} introuvable");
            return false;
        }
        if (version.HasValue && level.Version != version.Value)
        {
            ModLog.Warn($"Niveau {id} : le serveur demande la version {version}, on a la {level.Version} (mise à jour du jeu ?)");
            return false;
        }

        var getMaxMedal = (Func<Medal>)Delegate.CreateDelegate(typeof(Func<Medal>), game, GetMaxNonSpoileryMedalMethod);
        SetCurrentChallengeMethod.Invoke(game, new object[] { Challenge.FromNormalLevel(level, getMaxMedal) });
        StartLevelMethod.Invoke(game, new object?[] { level, false, quickRetry });
        ModLog.Info($"Chargement du niveau {id} ({level.Name}){(quickRetry ? " — retry" : "")}");
        return true;
    }

    private static readonly Dictionary<string, string> MapLabels = new();

    /// <summary>Nom lisible d'une map de round : "o:5" → "A5 Jump" (code et nom affichés par le jeu).</summary>
    public static string DescribeMap(string mapKey)
    {
        if (MapLabels.TryGetValue(mapKey, out string? label))
        {
            return label;
        }
        label = mapKey;
        string[] parts = mapKey.Split(':');
        if (Game != null && parts.Length >= 2 && parts[0] == "o" && uint.TryParse(parts[1], out uint id)
            && Game.levelOrder.GetLevelByID(id) is { } level)
        {
            try
            {
                label = $"{Challenge.LevelToKey(level).ToUpperInvariant()} {level.DisplayName}";
            }
            catch (Exception e)
            {
                ModLog.Warn($"Nom de la map {mapKey} indisponible : {e.Message}");
            }
        }
        MapLabels[mapKey] = label;
        return label;
    }

    /// <summary>Relance le niveau courant sans intro (utilisé à la place de l'écran de fin en round).</summary>
    public static void RetryCurrentLevel()
    {
        if (Game is { } game && CurrentLevelRef(game) is { } level)
        {
            StartLevelMethod.Invoke(game, new object?[] { level, false, true });
        }
    }
}
