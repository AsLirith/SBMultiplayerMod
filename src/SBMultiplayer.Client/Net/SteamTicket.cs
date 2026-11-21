using HarmonyLib;
using SBMultiplayer.Client.Ghosts;
using SE;
using Steamworks;

namespace SBMultiplayer.Client.Net;

/// <summary>
/// A Steam Web API ticket (hex) that proves who we are to the website's tournament routes (header x-steam-ticket).
/// <para>
/// The game asks Steam for these tickets itself, for its leaderboard login. But it keeps Steam's whole 2560-byte
/// buffer and ignores the length Steam gives. The bytes after the real ticket are leftovers, and Steam refuses the ticket
/// with them. So we listen to the same Steam answer (<see cref="Listen"/>, on the game thread) and keep each ticket at its
/// real length. We never ask Steam ourselves (SteamUser.GetAuthTicketForWebApi), because the game would take our answer
/// as its own.
/// </para>
/// <para>
/// Sometimes we have no ticket yet (the game got its first one before we listened), or the website refused ours
/// (<see cref="Invalidate"/>). Then we ask the game for a new one through its internal
/// <c>Task&lt;AuthTicket?&gt; RecreateAuthTicket()</c> and catch it on the way. Recreating swaps the game's TicketReady
/// and holds up its server queue, so we do it rarely. A ticket is reused for 5 minutes (the website keeps a verified
/// ticket for 10), and a refused one is replaced at most once per 5 minutes.
/// </para>
/// </summary>
internal static class SteamTicket
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WaitFor = TimeSpan.FromSeconds(5);

    private sealed record Cached(string Hex, DateTime At);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static AccessTools.FieldRef<GlobalGame, SE.Steam?>? steamRef;
    private static Func<SE.Steam, Task<AuthTicket?>>? recreate;
    private static bool reflectionFailed; // under Gate
    private static Cached? cached; // handed out, for CacheFor
    private static Cached? latest; // the last ticket Steam gave the game, at its real length
    private static volatile bool forceNew;
    private static DateTime lastForced = DateTime.MinValue; // under Gate: when a refused ticket was last replaced

    private static Callback<GetTicketForWebApiResponse_t>? listener; // game thread
    private static bool listenFailed; // game thread

    /// <summary>Starts catching the game's tickets. Game thread, every tick (does nothing once listening).</summary>
    public static void Listen()
    {
        if (listener != null || listenFailed)
        {
            return;
        }
        try
        {
            listener = Callback<GetTicketForWebApiResponse_t>.Create(OnTicket);
        }
        catch (Exception e)
        {
            listenFailed = true;
            ModLog.Error("Steam ticket: couldn't listen to Steam, tournaments are off", e);
        }
    }

    private static void OnTicket(GetTicketForWebApiResponse_t answer)
    {
        if (answer.m_eResult != EResult.k_EResultOK || answer.m_rgubTicket == null || answer.m_cubTicket <= 0)
        {
            return;
        }
        int length = Math.Min(answer.m_cubTicket, answer.m_rgubTicket.Length);
        Volatile.Write(ref latest, new Cached(Convert.ToHexString(answer.m_rgubTicket, 0, length).ToLowerInvariant(), DateTime.UtcNow));
    }

    /// <summary>The ticket as lowercase hex, or null without Steam (or if Steam doesn't answer within 5 s).</summary>
    public static async Task<string?> GetHexAsync()
    {
        if (Fresh() is { } known)
        {
            return known;
        }
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Fresh() is { } again)
            {
                return again;
            }
            if (reflectionFailed || listenFailed)
            {
                return null;
            }
            if (listener == null || GameAccess.Game is not { } game)
            {
                ModLog.Warn("Steam ticket: the game isn't ready yet");
                return null;
            }
            SE.Steam? steam;
            try
            {
                steamRef ??= AccessTools.FieldRefAccess<GlobalGame, SE.Steam?>("steam");
                recreate ??= AccessTools.MethodDelegate<Func<SE.Steam, Task<AuthTicket?>>>(
                    AccessTools.Method(typeof(SE.Steam), "RecreateAuthTicket")
                    ?? throw new MissingMethodException("SE.Steam.RecreateAuthTicket"));
                steam = steamRef(game);
            }
            catch (Exception e)
            {
                reflectionFailed = true; // the game changed: warn once, then stay quiet
                ModLog.Error("Steam ticket: the game's Steam members weren't found, tournaments are off", e);
                return null;
            }
            if (steam == null)
            {
                ModLog.Warn("Steam ticket: the game runs without Steam");
                return null;
            }
            // A refused ticket is replaced at most once per CacheFor: recreating holds up the game's own server queue.
            bool force = forceNew && DateTime.UtcNow - lastForced >= CacheFor;
            forceNew = false;
            Cached? ticket = force ? null : Volatile.Read(ref latest);
            if (ticket == null)
            {
                if (force)
                {
                    lastForced = DateTime.UtcNow;
                }
                ticket = await Recreate(steam).ConfigureAwait(false);
                if (ticket == null)
                {
                    return null;
                }
                ModLog.Info($"Steam ticket: a new one from the game ({ticket.Hex.Length / 2} bytes)");
            }
            Volatile.Write(ref cached, new Cached(ticket.Hex, DateTime.UtcNow));
            return ticket.Hex;
        }
        catch (Exception e)
        {
            ModLog.Warn($"Steam ticket unavailable: {e.Message}");
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The website refused our ticket: forget it, and have the game make a new one next time.</summary>
    public static void Invalidate()
    {
        Volatile.Write(ref cached, null);
        forceNew = true;
    }

    /// <summary>Asks the game for a new ticket and catches it (<see cref="OnTicket"/>): null if none comes.</summary>
    private static async Task<Cached?> Recreate(SE.Steam steam)
    {
        DateTime asked = DateTime.UtcNow;
        Task<AuthTicket?> asking = recreate!(steam);
        // The game completes the ticket inside its Steam callback, on the game thread. Carry on from the thread pool.
        Task onPool = asking.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        if (await Task.WhenAny(onPool, Task.Delay(WaitFor)).ConfigureAwait(false) != onPool)
        {
            ModLog.Warn("Steam ticket: no answer from Steam");
            return null;
        }
        // Our listener gets the same Steam answer in the same callback run as the game's. Give it a moment.
        for (int i = 0; i < 20; i++)
        {
            if (Volatile.Read(ref latest) is { } t && t.At >= asked)
            {
                return t;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        ModLog.Warn("Steam ticket: Steam answered the game, but not us");
        return null;
    }

    private static string? Fresh() =>
        Volatile.Read(ref cached) is { } c && DateTime.UtcNow - c.At < CacheFor ? c.Hex : null;
}
