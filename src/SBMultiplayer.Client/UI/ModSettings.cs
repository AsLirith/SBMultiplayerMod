using BytingLib;
using SBMultiplayer.Client.Net;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// Settings editable in game, as ValueEvent (the type expected by the game's sliders and checkboxes).
/// Every change applies immediately and is saved to SBMultiplayer.json.
/// </summary>
internal static class ModSettings
{
    public static ValueEvent<float> GhostOpacity { get; } = Bind(ModConfig.Current.GhostOpacity, v => ModConfig.Current.GhostOpacity = v);

    public static ValueEvent<bool> ShowOtherPlayers { get; } = Bind(ModConfig.Current.ShowOtherPlayers, v => ModConfig.Current.ShowOtherPlayers = v);

    public static ValueEvent<bool> ShowNameTags { get; } = Bind(ModConfig.Current.ShowNameTags, v => ModConfig.Current.ShowNameTags = v);

    public static ValueEvent<bool> ShowNameTagDistance { get; } = Bind(ModConfig.Current.ShowNameTagDistance, v => ModConfig.Current.ShowNameTagDistance = v);

    public static ValueEvent<float> NameTagMaxDistance { get; } = Bind(ModConfig.Current.NameTagMaxDistance, v => ModConfig.Current.NameTagMaxDistance = v);

    public static ValueEvent<bool> ShowRoundPanel { get; } = Bind(ModConfig.Current.ShowRoundPanel, v => ModConfig.Current.ShowRoundPanel = v);

    public static ValueEvent<float> RoundPanelScale { get; } = Bind(ModConfig.Current.RoundPanelScale, v => ModConfig.Current.RoundPanelScale = v);

    public static ValueEvent<bool> ShowSplits { get; } = Bind(ModConfig.Current.ShowSplits, v => ModConfig.Current.ShowSplits = v);

    public static ValueEvent<bool> ShowChat { get; } = Bind(ModConfig.Current.ShowChat, v => ModConfig.Current.ShowChat = v);

    public static ValueEvent<float> ChatScale { get; } = Bind(ModConfig.Current.ChatScale, v => ModConfig.Current.ChatScale = v);

    public static ValueEvent<bool> NotificationsOnTop { get; } = Bind(ModConfig.Current.NotificationsOnTop, v => ModConfig.Current.NotificationsOnTop = v);

    public static ValueEvent<bool> PublicGame { get; } = Bind(ModConfig.Current.PublicGame, v => ModConfig.Current.PublicGame = v);

    public static ValueEvent<bool> SteamP2PHost { get; } = Bind(ModConfig.Current.SteamP2PHost, v => ModConfig.Current.SteamP2PHost = v);

    public static ValueEvent<bool> CheckForUpdates { get; } = Bind(ModConfig.Current.CheckForUpdates, v => ModConfig.Current.CheckForUpdates = v);

    public static ValueEvent<bool> SubmitToLeaderboards { get; } = Bind(ModConfig.Current.SubmitToLeaderboards, v => ModConfig.Current.SubmitToLeaderboards = v);

    public static ValueEvent<bool> GameMenuButton { get; } = Bind(ModConfig.Current.GameMenuButton, v => ModConfig.Current.GameMenuButton = v);

    private static ValueEvent<T> Bind<T>(T initial, Action<T> apply)
    {
        var value = new ValueEvent<T>(initial);
        value.OnChange += v =>
        {
            apply(v);
            ModConfig.Save();
        };
        return value;
    }
}
