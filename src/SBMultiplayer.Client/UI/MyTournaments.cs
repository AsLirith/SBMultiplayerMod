using System.Collections.Concurrent;
using BytingLib.UI;
using Microsoft.Xna.Framework;
using SBMultiplayer.Client.Net;
using static SBMultiplayer.Client.UI.PopupMenu;

namespace SBMultiplayer.Client.UI;

/// <summary>
/// The host's own tournaments (any status), with what can be done to each. An upcoming one can be edited, cancelled
/// (after a confirmation) or have its waiting room opened. A paused one can be resumed. An open one can be rejoined or
/// have its waiting room closed (after a confirmation: back to upcoming, or to paused after a resume). A live one can be
/// rejoined. Opening and resuming join the room as its admin. The screen is reached from the Tournaments screen ("My
/// tournaments", approved hosts only). The website's answers arrive off the game thread and are taken by the screen's tick.
/// </summary>
internal static class MyTournaments
{
    private const string ScreenId = "my-tournaments";

    /// <summary>Rows visible at once (the list scrolls beyond that).</summary>
    private const int VisibleRows = 7;

    private enum HostAction { Cancel, Open, Resume, CloseRoom }

    /// <summary>An answer to the list request: null List when it couldn't be loaded.</summary>
    private sealed record Loaded(IReadOnlyList<Tournaments.Tournament>? List);

    private sealed class State
    {
        /// <summary>Our tournaments; null until the first answer, or when the first one failed (<see cref="Failed"/>).</summary>
        public IReadOnlyList<Tournaments.Tournament>? List { get; set; }

        /// <summary>The last list request failed (website or Steam unreachable).</summary>
        public bool Failed { get; set; }

        /// <summary>The latest list asked for, set off the game thread and taken by the screen's tick.</summary>
        public volatile Loaded? PendingList;

        /// <summary>Numbers the list requests: only the latest one's answer is kept.</summary>
        public int Requests;

        /// <summary>The tournament an action was sent for (null: none waiting), and which action.</summary>
        public Tournaments.Tournament? Busy { get; set; }
        public HostAction BusyAction { get; set; }

        /// <summary>The website's answer to the action, set off the game thread and taken by the screen's tick.</summary>
        public volatile Tournaments.HostResult? Result;

        public string? Error { get; set; }
        public ScrollMemory Scroll { get; } = new();
    }

    private static State? state;

    /// <summary>
    /// The room admin passwords the website gave when we opened or resumed a tournament, by tournament id. They stay in
    /// memory and are never logged. "Rejoin" uses them when the list doesn't carry one yet. The list from the website
    /// carries it too, so a host who restarted the game still rejoins as admin.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> AdminPasswords = new();

    /// <summary>
    /// The admin password we know for a tournament's room, if we host it: from our last list from the website (/mine),
    /// else from this session's opens and resumes. Null otherwise. Game thread.
    /// </summary>
    public static string? KnownAdminPassword(string id) =>
        state?.List?.FirstOrDefault(t => t.Id == id)?.AdminPassword
            ?? (AdminPasswords.TryGetValue(id, out string? known) ? known : null);

    public static void Open()
    {
        if (IsOpen(ScreenId))
        {
            return;
        }
        state = new State();
        Load(state);
        Show(ScreenId, Build);
    }

    /// <summary>Asks for the list again if the screen is open (a tournament was just created or edited).</summary>
    public static void Reload()
    {
        if (state is { } s && IsOpen(ScreenId))
        {
            Load(s);
        }
    }

    private static void Load(State s)
    {
        int request = Interlocked.Increment(ref s.Requests);
        Tournaments.MineAsync().ContinueWith(task =>
        {
            if (Volatile.Read(ref s.Requests) == request)
            {
                s.PendingList = new Loaded(task.IsCompletedSuccessfully ? task.Result : null);
            }
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Sends a host action. Opening or resuming joins the room as soon as the website answers, whatever the screen does
    /// meanwhile (closed, or every menu closed). The password is kept and the join is queued for the game thread right
    /// there. <see cref="MultiplayerClient.JoinTournament"/> only hands it over through its volatile pending field, which
    /// the client takes on the game thread.
    /// </summary>
    private static void Send(State s, Tournaments.Tournament t, HostAction action)
    {
        Task<Tournaments.HostResult> call = action switch
        {
            HostAction.Cancel => Tournaments.CancelAsync(t.Id),
            HostAction.Open => Tournaments.OpenAsync(t.Id),
            HostAction.CloseRoom => Tournaments.CloseRoomAsync(t.Id),
            _ => Tournaments.ResumeAsync(t.Id),
        };
        call.ContinueWith(task =>
        {
            Tournaments.HostResult result = task.IsCompletedSuccessfully ? task.Result : new Tournaments.HostResult(false, Tr.TournamentHostError);
            if (result.Ok && action == HostAction.CloseRoom)
            {
                AdminPasswords.TryRemove(t.Id, out _); // that room is gone
            }
            if (result.Ok && action is HostAction.Open or HostAction.Resume && result.Code is { } code)
            {
                string? password = result.AdminPassword is { Length: > 0 } p ? p : null;
                if (password != null)
                {
                    AdminPasswords[t.Id] = password;
                }
                MultiplayerClient.Instance.JoinTournament(code, password);
            }
            s.Result = result;
        }, TaskScheduler.Default);
    }

    private static Element Build(Action close, Screen screen)
    {
        State s = state!;
        screen.HoldEscape = s.Busy != null; // the answer must be shown (an error) or acted on

        screen.Every(() =>
        {
            if (s.PendingList is { } loaded)
            {
                s.PendingList = null;
                s.Failed = loaded.List == null;
                s.List = loaded.List ?? s.List;
                screen.Refresh();
            }
            if (s.Result is { } result)
            {
                s.Result = null;
                Finished(s, result, close, screen);
            }
        });

        void Start(Tournaments.Tournament t, HostAction action)
        {
            if (s.Busy != null)
            {
                return;
            }
            s.Busy = t;
            s.BusyAction = action;
            s.Error = null;
            Send(s, t, action);
            screen.Refresh();
        }

        void ConfirmCancel(Tournaments.Tournament t) => Show("tournament-cancel", (closeConfirm, _) => Stack(
            Text(Tr.ConfirmCancelTournament(t.Name)),
            Buttons(
                Button(Tr.CancelTournamentYes, () =>
                {
                    closeConfirm();
                    Start(t, HostAction.Cancel);
                }),
                Button(Tr.KeepTournament, closeConfirm))));

        void ConfirmCloseRoom(Tournaments.Tournament t) => Show("tournament-close-room", (closeConfirm, _) => Stack(
            Text(Tr.ConfirmCloseWaitingRoom(t.Name)),
            Buttons(
                Button(Tr.CloseWaitingRoomYes, () =>
                {
                    closeConfirm();
                    Start(t, HostAction.CloseRoom);
                }),
                Button(Tr.KeepWaitingRoom, closeConfirm))));

        void Rejoin(Tournaments.Tournament t)
        {
            if (t.Code is { } code)
            {
                close();
                string? password = t.AdminPassword ?? KnownAdminPassword(t.Id);
                MultiplayerClient.Instance.JoinTournament(code, password);
            }
        }

        // The actions of each status (none while an action is waiting for the website).
        List<Element> Actions(Tournaments.Tournament t) => s.Busy != null ? new List<Element>() : t.Status switch
        {
            "upcoming" => new List<Element>
            {
                Button(Tr.TournamentEditButton, () => TournamentEditor.OpenEdit(t)),
                Button(Tr.TournamentCancelButton, () => ConfirmCancel(t)),
                Button(Tr.TournamentOpenRoom, () => Start(t, HostAction.Open)),
            },
            "paused" => new List<Element> { Button(Tr.TournamentResume, () => Start(t, HostAction.Resume)) },
            "open" when t.Code != null => new List<Element>
            {
                Button(Tr.RejoinButton, () => Rejoin(t)),
                Button(Tr.TournamentCloseRoom, () => ConfirmCloseRoom(t)),
            },
            "open" => new List<Element> { Button(Tr.TournamentCloseRoom, () => ConfirmCloseRoom(t)) },
            "live" when t.Code != null => new List<Element> { Button(Tr.RejoinButton, () => Rejoin(t)) },
            _ => new List<Element>(),
        };

        // One row per tournament: "Name  -  Upcoming, Sat 22 Nov 20:00" then its buttons; the names keep the width the
        // widest set of buttons leaves, so they line up.
        float buttons = Math.Max(
            ButtonWidth(Tr.TournamentEditButton) + ButtonWidth(Tr.TournamentCancelButton) + ButtonWidth(Tr.TournamentOpenRoom) + 3 * 22f,
            ButtonWidth(Tr.RejoinButton) + ButtonWidth(Tr.TournamentCloseRoom) + 2 * 22f);
        float nameWidth = ScrollRowWidth - buttons;
        Element list;
        if (s.Failed && s.List == null)
        {
            list = TournamentEditor.ErrorText(Tr.OwnTournamentsUnavailable);
        }
        else if (s.List == null)
        {
            list = Text(Tr.PublicGamesSearching);
        }
        else if (s.List.Count == 0)
        {
            list = Text(Tr.NoOwnTournaments);
        }
        else
        {
            var rows = s.List.Select(t =>
            {
                string text = $"{t.Name}  -  {Tr.TournamentStatus(t.Status, t.StartsAt)}";
                var items = new List<Element> { new Label(Fit(text, nameWidth), nameWidth) { Anchor = new Vector2(0f, 0.5f) } };
                items.AddRange(Actions(t));
                return Line(items.ToArray());
            }).ToList();
            list = ScrollList(rows, VisibleRows, screen, s.Scroll);
        }

        return Stack(
            Title(Tr.MyTournamentsButton),
            list,
            // A list already shown stays; the failure of a later refresh is said under it.
            s.Failed && s.List != null ? TournamentEditor.ErrorText(Tr.OwnTournamentsUnavailable) : null,
            s.Busy != null ? Text(Tr.TournamentSending) : null,
            s.Error != null ? TournamentEditor.ErrorText(s.Error) : null,
            Buttons(
                Button(Tr.Refresh, () => Load(s)),
                s.Busy != null ? null : Button(Tr.Back, close)));
    }

    /// <summary>
    /// The website answered an action, so the lists are asked for again. An opened or resumed room is already being joined
    /// (<see cref="Send"/>), and the screen closes.
    /// </summary>
    private static void Finished(State s, Tournaments.HostResult result, Action close, Screen screen)
    {
        Tournaments.Tournament? t = s.Busy;
        HostAction action = s.BusyAction;
        s.Busy = null;
        Tournaments.Refresh();
        Load(s);
        if (!result.Ok || t == null)
        {
            s.Error = result.Error ?? Tr.TournamentHostError;
            screen.Refresh();
            return;
        }
        s.Error = null;
        if (action == HostAction.Cancel)
        {
            Notifications.Good(Tr.TournamentCancelled(t.Name));
            screen.Refresh();
            return;
        }
        if (action == HostAction.CloseRoom)
        {
            Notifications.Good(Tr.WaitingRoomClosed(t.Name));
            screen.Refresh();
            return;
        }
        close();
    }
}
