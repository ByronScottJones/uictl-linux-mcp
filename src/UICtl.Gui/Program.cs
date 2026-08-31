using System.Text.Json;
using Adw;
using Gtk;
using UICtl.Core;
using UICtl.Ipc;

namespace UICtl.Gui;

/// <summary>
/// `uictl-gui` - the toast + `log show` window, launched lazily by the
/// daemon (GuiLauncher.cs) the first time a real command runs, and
/// revealed on demand by `uictl log show`. One Adw.Application instance
/// per desktop session: GApplication's own single-instance handling
/// (confirmed live - a second launch registers, sees `IsRemote`, forwards
/// via a named GAction, and exits in ~0.3s) means every subsequent
/// `uictl-gui`/`uictl-gui --show-log` invocation is a cheap handoff to
/// the one already-running instance, not a new process doing real work.
///
/// This process is a plain client of the daemon's existing request/
/// response socket (DaemonClient.Send, the same one CLI commands use) -
/// no new IPC mechanism. It polls `__log_list__` (internal-only, not
/// recorded into the log it reads from - see CommandDispatcher.cs's doc
/// comment for why that's load-bearing) on a timer rather than the
/// daemon pushing to it, since the socket protocol is strictly
/// request/response (see DaemonServer.cs); polling is simple, and a
/// ~300ms interval is imperceptible for a "something just happened"
/// indicator.
///
/// Each poll's actual network I/O runs on a background thread pool
/// thread (Task.Run), not the GLib main loop thread the timer callback
/// itself fires on - live-verified as necessary, the hard way: a daemon
/// that's slow to answer *any* request (see the pre-existing, unrelated
/// windows.list AT-SPI slowness this project already knows about)
/// previously froze this entire window solid, unresponsive to all input,
/// for as long as the daemon took to reply, since the poll callback was
/// calling DaemonClient.Send synchronously right there on the UI thread.
/// GTK widgets are only ever touched back on the main loop, via
/// GLib.Functions.IdleAdd from the background thread's continuation -
/// touching them directly from the Task.Run thread would be a
/// use-GTK-off-the-main-thread bug, a different but equally real problem.
/// </summary>
internal static class Program
{
    private const string AppId = "org.byronscottjones.uictl.Gui";
    private const int PollIntervalMs = 300;
    private const int ToastFadeMs = 5000;
    private const int MaxConsecutivePollFailures = 20; // ~6s of a gone daemon before this process gives up and quits

    private static int _lastSeenId;
    private static uint? _toastFadeTimeoutId;
    private static int _pollFailures;
    private static bool _pollInFlight;

    private static Toast? _toast;
    private static LogWindow? _logWindow;

    private static int Main(string[] args)
    {
        var app = Adw.Application.New(AppId, Gio.ApplicationFlags.FlagsNone);

        var showLogAction = Gio.SimpleAction.New("show-log", null);
        showLogAction.OnActivate += (_, _) => ShowLogWindow(app);
        app.AddAction(showLogAction);

        bool registered = app.Register(null);
        if (!registered)
        {
            Console.Error.WriteLine("uictl-gui: failed to register with the session bus");
            return 1;
        }

        if (app.IsRemote)
        {
            if (args.Contains("--show-log"))
                app.ActivateAction("show-log", null);
            return 0;
        }

        app.OnActivate += (sender, _) =>
        {
            ((Gio.Application)sender).Hold(); // stay alive with no window - this is the persistent toast host
            StartPolling(app);
        };

        if (args.Contains("--show-log"))
            app.OnActivate += (_, _) => ShowLogWindow(app);

        // Plain Run, not RunWithSynchronizationContext - nothing here uses
        // async/await, and DaemonClient.Send's blocking
        // .GetAwaiter().GetResult() calls (invoked from a GLib timeout
        // callback, i.e. on this same main-loop thread) risk deadlocking
        // against a GLib-pumped SynchronizationContext waiting to dispatch
        // their own continuation back onto the thread that's blocked
        // waiting for them - confirmed live as the actual cause of a totally
        // silent hang (process alive, ~0% CPU, no window, no error) the
        // first time this was wired up with RunWithSynchronizationContext.
        return app.Run(args);
    }

    private static void ShowLogWindow(Adw.Application app)
    {
        bool firstOpen = _logWindow is null;
        _logWindow ??= new LogWindow(app);
        _logWindow.Present();

        // The window only receives entries incrementally, via the ongoing
        // poll loop above (which only ever hands it entries newer than
        // _lastSeenId, i.e. whatever wasn't already consumed by earlier
        // toast-only polling before this window existed) - confirmed live:
        // opening the window after a few commands had already run showed
        // just the one *next* entry, nothing from before. Backfill once on
        // first open with a separate, unfiltered __log_list__ call so it
        // starts with full history; the regular poll loop's afterId cursor
        // is untouched by this; a request that lands in the narrow gap
        // between this snapshot and the next regular poll could in theory
        // be missed by both, but that's a single-entry, low-stakes edge case
        // not worth extra machinery for.
        if (firstOpen)
            BackfillLogWindow();
    }

    private static void BackfillLogWindow()
    {
        Task.Run(() =>
        {
            try
            {
                JsonElement emptyParams;
                using (var doc = JsonDocument.Parse("{}"))
                    emptyParams = doc.RootElement.Clone();

                string response = DaemonClient.Send("__log_list__", emptyParams);
                GLib.Functions.IdleAdd(0, () =>
                {
                    ApplyBackfill(response);
                    return false; // one-shot
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"uictl-gui: failed to load activity log history: {ex.Message}");
            }
        });
    }

    /// <summary>Runs back on the GLib main loop thread (via IdleAdd) - safe to touch GTK widgets here.</summary>
    private static void ApplyBackfill(string response)
    {
        using var responseDoc = JsonDocument.Parse(response);
        if (!responseDoc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            return;
        if (!responseDoc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("entries", out var entries))
            return;

        foreach (var entry in entries.EnumerateArray())
            _logWindow?.AppendEntry(entry);
    }

    private static void StartPolling(Adw.Application app)
    {
        GLib.Functions.TimeoutAdd(0, PollIntervalMs, () =>
        {
            // A local file check, not routed through the daemon poll below -
            // checked every tick regardless of whether a poll is already
            // in flight, so a suppression window doesn't have to wait its
            // turn behind a slow/overlapping daemon request.
            _toast?.SetSuppressed(ToastSuppression.IsActive());
            SchedulePoll(app);
            return true; // keep repeating
        });
    }

    /// <summary>Runs on the GLib main loop thread - kicks off the actual request on a background thread and returns immediately, never blocking here.</summary>
    private static void SchedulePoll(Adw.Application app)
    {
        // A previous poll is still waiting on a slow daemon response -
        // don't pile up additional overlapping requests on top of it.
        // Once it (eventually) completes, polling resumes at the normal
        // interval.
        if (_pollInFlight) return;
        _pollInFlight = true;

        JsonElement paramsEl;
        using (var doc = JsonDocument.Parse($$"""{"afterId":{{_lastSeenId}}}"""))
            paramsEl = doc.RootElement.Clone();

        Task.Run(() =>
        {
            try
            {
                string response = DaemonClient.Send("__log_list__", paramsEl);
                GLib.Functions.IdleAdd(0, () =>
                {
                    HandlePollSuccess(app, response);
                    _pollInFlight = false;
                    return false; // one-shot
                });
            }
            catch (Exception)
            {
                GLib.Functions.IdleAdd(0, () =>
                {
                    HandlePollFailure(app);
                    _pollInFlight = false;
                    return false; // one-shot
                });
            }
        });
    }

    /// <summary>Runs back on the GLib main loop thread (via IdleAdd) - safe to touch GTK widgets here.</summary>
    private static void HandlePollSuccess(Adw.Application app, string response)
    {
        _pollFailures = 0;

        using var responseDoc = JsonDocument.Parse(response);
        if (!responseDoc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            return;
        if (!responseDoc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("entries", out var entries))
            return;

        // Entries arrive in true completion order, never out of order: they
        // come straight from ActivityLog's insertion order, and
        // DaemonServer.cs processes one connection/command at a time, so
        // "recorded" and "completed" are the same event for every entry -
        // no concurrent completions to reorder.
        JsonElement? latest = null;
        foreach (var entry in entries.EnumerateArray())
        {
            latest = entry;
            _lastSeenId = entry.GetProperty("id").GetInt32();
            _logWindow?.AppendEntry(entry);
        }

        if (latest is { } newest)
            ShowOrUpdateToast(app, newest);
    }

    /// <summary>
    /// Runs back on the GLib main loop thread (via IdleAdd). _pollFailures
    /// only resets on the *next successful* poll (HandlePollSuccess's first
    /// line), not immediately on reconnect - if the daemon dies again
    /// before that next poll lands, the streak keeps counting from where it
    /// left off rather than restarting. That's intentional: it's a "how
    /// long has the daemon actually been unreachable" counter, not a
    /// per-attempt one.
    /// </summary>
    private static void HandlePollFailure(Adw.Application app)
    {
        if (++_pollFailures >= MaxConsecutivePollFailures)
        {
            Console.Error.WriteLine("uictl-gui: daemon unreachable for too long, exiting");
            app.Quit();
        }
    }

    private static void ShowOrUpdateToast(Adw.Application app, JsonElement entry)
    {
        _toast ??= new Toast(app);
        _toast.Update(entry);

        if (_toastFadeTimeoutId is { } existing)
            GLib.Functions.SourceRemove(existing);

        _toastFadeTimeoutId = GLib.Functions.TimeoutAdd(0, ToastFadeMs, () =>
        {
            _toast?.FadeOut();
            _toastFadeTimeoutId = null;
            return false; // one-shot
        });
    }
}
