using System.Reflection;
using BytingLib;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SE;

namespace SBMultiplayer.Client.Ghosts;

/// <summary>Access to the game's non-public members, resolved once.</summary>
internal static class GameAccess
{
    public const int TicksPerSecond = 60;

    public static readonly Func<Board, GhostState> GetGhostState =
        AccessTools.MethodDelegate<Func<Board, GhostState>>(AccessTools.Method(typeof(Board), "GetGhostState"));

    private static readonly Type Scene3DType = AccessTools.TypeByName("SE.Scene3D");
    private static readonly Type Scene3DIngameType = AccessTools.TypeByName("SE.Scene3DIngame");

    // Scene3D is internal: its TransparencyBatch property is read through reflection.
    private static readonly MethodInfo TransparencyBatchGetter = AccessTools.PropertyGetter(Scene3DType, "TransparencyBatch");

    public static TransparencyBatchSwapper GetTransparencyBatch(Scene scene) =>
        (TransparencyBatchSwapper)TransparencyBatchGetter.Invoke(scene, null)!;

    // ---------- Scene camera (to project names on screen) ----------

    private static readonly FieldInfo SceneDeps = AccessTools.Field(Scene3DType, "d");
    private static readonly Type DepsType = AccessTools.TypeByName("SE.Scene3DDependencies");
    private static readonly MethodInfo DepsCamera = AccessTools.PropertyGetter(DepsType, "camera");
    private static readonly MethodInfo DepsProjection = AccessTools.PropertyGetter(DepsType, "projection");

    /// <summary>The level scene's camera stack (the last camera of the list is the one shown).</summary>
    public static IngameCameras? GetIngameCameras(Scene scene)
    {
        object? deps = Scene3DType.IsInstanceOfType(scene) ? SceneDeps.GetValue(scene) : null;
        return deps == null ? null : DepsCamera.Invoke(deps, null) as IngameCameras;
    }

    /// <summary>View × projection matrix and position of the scene's camera.</summary>
    public static (Matrix ViewProjection, Vector3 CameraPosition) GetCamera(Scene scene)
    {
        object deps = SceneDeps.GetValue(scene)!;
        var camera = (ICamera)DepsCamera.Invoke(deps, null)!;
        var projection = (IProjection)DepsProjection.Invoke(deps, null)!;
        return (camera.View * projection.Transform, camera.Position);
    }

    // ---------- GlobalGame (captured by GlobalTickPatch) ----------

    public static GlobalGame? Game { get; set; }

    private static AccessTools.FieldRef<GlobalGame, Binds>? bindsRef;
    private static MethodInfo? toMarkup;

    /// <summary>The player's key for the game's free camera ("F" by default), or null if it can't be shown as text (mouse, gamepad glyph).</summary>
    public static string? FlyKeyName => KeyName(b => b.FlyToggle);

    /// <summary>The player's key to restart the level ("Q" by default), or null if it can't be shown as text.</summary>
    public static string? RestartKeyName => KeyName(b => b.RestartLevel);

    private static string? KeyName(Func<Binds, InputBool> bind)
    {
        if (Game == null)
        {
            return null;
        }
        try
        {
            bindsRef ??= AccessTools.FieldRefAccess<GlobalGame, Binds>("binds");
            toMarkup ??= AccessTools.Method("SE.BindToMarkup:ToMarkup") ?? throw new MissingMethodException("SE.BindToMarkup.ToMarkup");
            string markup = (string)toMarkup.Invoke(null, new object[] { bind(bindsRef(Game)), false })!;
            return markup.Length == 0 || markup.Contains('#') || markup.Contains('_') ? null : markup;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AccessTools.FieldRef<GlobalGame, InputOutgame>? inputRef;

    /// <summary>The game's menu-level inputs (restart, free camera...), or null.</summary>
    public static InputOutgame? Input
    {
        get
        {
            try
            {
                inputRef ??= AccessTools.FieldRefAccess<GlobalGame, InputOutgame>("inputOutgame");
                return Game != null ? inputRef(Game) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static AccessTools.FieldRef<GlobalGame, SettingsManager<Settings>>? settingsManagerRef;
    private static bool mirrorUnavailable;

    /// <summary>
    /// The game's mirror easter egg (or Ctrl+M) flips the final 3D image horizontally: whatever the mod draws
    /// over it at a 3D position (name tags) must be flipped too.
    /// </summary>
    public static bool IsMirrored
    {
        get
        {
            if (Game == null || mirrorUnavailable)
            {
                return false;
            }
            try
            {
                settingsManagerRef ??= AccessTools.FieldRefAccess<GlobalGame, SettingsManager<Settings>>("settingsManager");
                return settingsManagerRef(Game).Settings.Graphics.MirrorX;
            }
            catch (Exception e)
            {
                mirrorUnavailable = true; // optional: name tags just won't follow the mirror
                ModLog.Warn($"Mirror mode not detected: {e.Message}");
                return false;
            }
        }
    }

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

    private static readonly MethodInfo IsNavigatingInTextInputMethod = AccessTools.Method(typeof(GlobalGame), "IsNavigatingInTextInput");

    /// <summary>A text field of the game (or of the mod) has the focus: hotkeys must not react.</summary>
    public static bool IsTyping()
    {
        try
        {
            return Game != null && (bool)IsNavigatingInTextInputMethod.Invoke(Game, null)!;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsIngameScene(Scene? scene) => scene != null && Scene3DIngameType.IsInstanceOfType(scene);

    public static SpriteFont? UiFont => Game == null ? null : UiAssetsRef(Game)?.FontSmall?.Value;

    /// <summary>Starts an official level, like the level selection menu. Call from the game thread.</summary>
    public static bool StartOfficialLevel(uint id, uint? version, bool quickRetry)
    {
        GlobalGame? game = Game;
        Level? level = game?.levelOrder.GetLevelByID(id);
        if (game == null || level == null)
        {
            ModLog.Warn($"Official level {id} not found");
            return false;
        }
        if (version.HasValue && level.Version != version.Value)
        {
            ModLog.Warn($"Level {id}: the server asks for version {version}, we have {level.Version} (game update?)");
            return false;
        }

        var getMaxMedal = (Func<Medal>)Delegate.CreateDelegate(typeof(Func<Medal>), game, GetMaxNonSpoileryMedalMethod);
        SetCurrentChallengeMethod.Invoke(game, new object[] { Challenge.FromNormalLevel(level, getMaxMedal) });
        StartLevelMethod.Invoke(game, new object?[] { level, false, quickRetry });
        ModLog.Info($"Loading level {id} ({level.Name}){(quickRetry ? " — retry" : "")}");
        return true;
    }

    // ---------- Game UI (for the Multiplayer screen) ----------

    private static readonly AccessTools.FieldRef<GlobalGame, StuffDisposable> StuffRef =
        AccessTools.FieldRefAccess<GlobalGame, StuffDisposable>("stuff");

    private static readonly AccessTools.FieldRef<GlobalGame, OptionsUI> OptionsUiRef =
        AccessTools.FieldRefAccess<GlobalGame, OptionsUI>("optionsUI");

    private static readonly FieldInfo UpdateSpeedField = AccessTools.Field(typeof(GamePrototype), "updateSpeed");
    private static readonly MethodInfo ShowPopupMethod = AccessTools.Method(typeof(GlobalGame), "ShowPopup",
        new[] { typeof(Func<Action, Scene>), typeof(Scene), typeof(bool), typeof(bool) });
    private static readonly MethodInfo CreateCanvasMethod = AccessTools.Method(typeof(GlobalGame), "CreateCanvas");
    private static readonly MethodInfo CreateButtonMethod = AccessTools.Method(typeof(GlobalGame), "CreateButton");
    private static readonly MethodInfo CreateMainPanelMethod = AccessTools.Method(typeof(UIAssets), "CreateMainPanel");

    /// <summary>The game's global object list (drawn and updated whatever the scene).</summary>
    public static StuffDisposable GlobalStuff => StuffRef(Game!);

    public static UIAssets UiAssets => UiAssetsRef(Game!);

    public static OptionsUI OptionsUi => OptionsUiRef(Game!);

    public static GameSpeed UpdateSpeed => (GameSpeed)UpdateSpeedField.GetValue(Game)!;

    /// <summary>Opens a popup over the current scene, like the game's options screens.</summary>
    public static void ShowPopup(Func<Action, Scene> create) =>
        ShowPopupMethod.Invoke(Game, new object?[] { create, CurrentScene, true, false });

    public static BytingLib.UI.CanvasScale CreateCanvas() =>
        (BytingLib.UI.CanvasScale)CreateCanvasMethod.Invoke(Game, new object[] { true })!;

    public static BytingLib.UI.Button CreateButton(Action onClick) =>
        (BytingLib.UI.Button)CreateButtonMethod.Invoke(Game, new object?[] { onClick, 0f, 0f, null, null })!;

    public static BytingLib.UI.Element CreateMainPanel(BytingLib.UI.Element content) =>
        (BytingLib.UI.Element)CreateMainPanelMethod.Invoke(UiAssets, new object[] { content })!;

    /// <summary>Level code shown in the game's menus ("A5") for a map key ("o:2"), or the key itself if unknown.</summary>
    public static string MapKeyToCode(string mapKey)
    {
        if (SBMultiplayer.Protocol.Msg.WorkshopId(mapKey) is ulong workshopId)
        {
            return $"M{workshopId}"; // the game's own Workshop code
        }
        string[] parts = mapKey.Split(':');
        if (Game != null && parts.Length >= 2 && parts[0] == "o" && uint.TryParse(parts[1], out uint id)
            && Game.levelOrder.GetLevelByID(id) is { } level)
        {
            return Challenge.LevelToKey(level).ToUpperInvariant();
        }
        return mapKey;
    }

    /// <summary>Map key ("o:2") from a level code ("A5", case-insensitive) or a key; null if unknown.</summary>
    public static string? CodeToMapKey(string codeOrKey)
    {
        string s = codeOrKey.Trim();
        if (SBMultiplayer.Protocol.Msg.IsValidRotationKey(s))
        {
            return s;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[Mm][1-9]\d*$"))
        {
            return "c:M" + s[1..]; // Workshop code, latest version
        }
        if (Game == null)
        {
            return null;
        }
        // includingDevLevels: the hidden room F (test levels, trailer levels, easter eggs like F18) too.
        // Levels without an id (0) can't be told apart, so they can't be put in a rotation.
        foreach (Level level in Game.levelOrder.GetLevels(normalOrBonus: null, includingDevLevels: true))
        {
            if (level.ID != 0 && string.Equals(Challenge.LevelToKey(level), s, StringComparison.OrdinalIgnoreCase))
            {
                return $"o:{level.ID}";
            }
        }
        return null;
    }

    private static readonly Dictionary<string, string> MapLabels = new();

    /// <summary>Readable name of a round map: "o:5" → "A5 Jump", "c:M123" → the Workshop map's name.</summary>
    public static string DescribeMap(string mapKey)
    {
        if (MapLabels.TryGetValue(mapKey, out string? label))
        {
            return label;
        }
        if (SBMultiplayer.Protocol.Msg.WorkshopId(mapKey) is ulong workshopId)
        {
            // Not cached while unknown: the name appears once the map is downloaded.
            string? name = WorkshopMapName(workshopId);
            if (name == null)
            {
                return $"Workshop M{workshopId}";
            }
            return MapLabels[mapKey] = name;
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
                ModLog.Warn($"Name of map {mapKey} unavailable: {e.Message}");
            }
        }
        MapLabels[mapKey] = label;
        return label;
    }

    // ---------- Workshop maps ----------

    private static readonly Type CatalogueEntryType = AccessTools.TypeByName("SE.CustomMapCatalogue+Entry");
    private static readonly FieldInfo CustomMapsField = AccessTools.Field(typeof(GlobalGame), "customMaps");
    private static readonly MethodInfo DownloadWorkshopCodeMethod = AccessTools.Method(typeof(GlobalGame), "DownloadWorkshopCode");
    private static readonly MethodInfo StartCustomMapEntryMethod = AccessTools.Method(typeof(GlobalGame), "StartCustomMapEntry");
    private static readonly MethodInfo ReadMapFromEntry = AccessTools.Method(AccessTools.TypeByName("SE.CustomMapCatalogue"), "ReadMap", new[] { CatalogueEntryType });

    /// <summary>Name of an installed Workshop map (from the game's custom map catalogue), or null.</summary>
    public static string? WorkshopMapName(ulong workshopId)
    {
        if (Game == null)
        {
            return null;
        }
        object catalogue = CustomMapsField.GetValue(Game)!;
        var maps = (System.Collections.IEnumerable)AccessTools.Property(catalogue.GetType(), "Maps").GetValue(catalogue)!;
        foreach (object entry in maps)
        {
            var id = (ulong?)AccessTools.Property(CatalogueEntryType, "WorkshopItemId").GetValue(entry);
            if (id == workshopId)
            {
                return (string)AccessTools.Property(CatalogueEntryType, "Name").GetValue(entry)!;
            }
        }
        return null;
    }

    /// <summary>
    /// Plays a Workshop map, through the game's own flow: downloads it first if needed (without subscribing,
    /// with the game's download popup). <paramref name="requiredKey"/>: exact version required, or null for any.
    /// </summary>
    public static void StartWorkshopMap(ulong workshopId, string? requiredKey)
    {
        if (Game == null)
        {
            return;
        }
        WorkshopCallback.Handler = entry =>
        {
            try
            {
                if (requiredKey != null)
                {
                    object map = ReadMapFromEntry.Invoke(null, new[] { entry })!;
                    string? onlineKey = (string?)AccessTools.Property(map.GetType(), "OnlineKey").GetValue(map);
                    if (onlineKey != requiredKey)
                    {
                        UI.Notifications.Warn(UI.Tr.WorkshopVersionMismatch);
                    }
                }
                StartCustomMapEntryMethod.Invoke(Game, new[] { entry });
                ModLog.Info($"Loading Workshop map M{workshopId}");
            }
            catch (Exception e)
            {
                ModLog.Error($"Could not start Workshop map M{workshopId}", e);
                UI.Notifications.Warn(UI.Tr.MapLoadFailed($"M{workshopId}"));
            }
        };
        // DownloadWorkshopCode(ulong, Action<CustomMapCatalogue.Entry>): the entry type is internal, so the
        // callback is a generic method instantiated for it at runtime.
        MethodInfo invoke = typeof(WorkshopCallback).GetMethod(nameof(WorkshopCallback.Invoke))!.MakeGenericMethod(CatalogueEntryType);
        Delegate callback = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(CatalogueEntryType), invoke);
        DownloadWorkshopCodeMethod.Invoke(Game, new object[] { workshopId, callback });
    }

    private static class WorkshopCallback
    {
        public static Action<object>? Handler;

        public static void Invoke<T>(T entry) => Handler?.Invoke(entry!);
    }

    /// <summary>Restarts the current level without the intro (used instead of the end screen during a round).</summary>
    public static void RetryCurrentLevel()
    {
        if (Game is { } game && CurrentLevelRef(game) is { } level)
        {
            StartLevelMethod.Invoke(game, new object?[] { level, false, true });
        }
    }
}
