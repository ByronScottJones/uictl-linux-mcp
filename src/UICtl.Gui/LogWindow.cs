using System.Text.Json;
using UICtl.Ipc;

namespace UICtl.Gui;

/// <summary>
/// `log show`'s window (MCP_INTERFACE.md's Activity log section). A
/// scrolling read-only Gtk.TextView, one line per command - not a full
/// sortable Gtk.ColumnView (ENGINEERING.md's original plan named that
/// specifically, but it needs a custom GObject-derived row type in C#,
/// real boilerplate GirCore doesn't shortcut). A plain formatted-text log
/// delivers the same "see everything this daemon has done" value for a
/// first version - revisit if this needs sorting/filtering/clicking a
/// row for full params/result detail, none of which exists here yet.
///
/// Hosts the "commands-enabled" kill switch (UICtlGate.cs, via the
/// internal `__gate_set__` command) - MCP_INTERFACE.md is explicit that
/// the toggle only takes effect while this window is open, so closing it
/// re-enables, regardless of the checkbox's own state at that point.
/// There's deliberately no public command to disable commands - only
/// this on-screen checkbox can. The re-enable-on-close send is
/// fire-and-forget (SetGateEnabled's own doc comment explains why it
/// can't block the close), so this is "re-enables" as a strong default,
/// not an absolute guarantee against every failure mode (e.g. the daemon
/// being unreachable at the exact moment of close).
/// </summary>
internal sealed class LogWindow
{
    private readonly Adw.Window _window;
    private readonly Gtk.TextView _textView;
    private readonly Gtk.TextBuffer _buffer;

    public LogWindow(Adw.Application app)
    {
        _window = Adw.Window.New();
        _window.SetDefaultSize(820, 480);
        _window.SetTitle("uictl activity log");
        app.AddWindow(_window);

        var enabledCheck = Gtk.CheckButton.New();
        enabledCheck.SetLabel("Commands enabled");
        enabledCheck.SetActive(true);
        enabledCheck.OnToggled += (sender, _) => SetGateEnabled(((Gtk.CheckButton)sender).GetActive());

        var exportButton = Gtk.Button.NewWithLabel("Export");
        exportButton.OnClicked += (_, _) => ExportLog();

        var headerBar = Adw.HeaderBar.New();
        headerBar.PackStart(enabledCheck);
        headerBar.PackEnd(exportButton);

        _buffer = Gtk.TextBuffer.New(null);
        _textView = Gtk.TextView.NewWithBuffer(_buffer);
        _textView.SetEditable(false);
        _textView.SetCursorVisible(false);
        _textView.SetMonospace(true);
        _textView.SetWrapMode(Gtk.WrapMode.WordChar);
        _textView.SetMarginTop(8);
        _textView.SetMarginBottom(8);
        _textView.SetMarginStart(8);
        _textView.SetMarginEnd(8);

        // The default GTK4 TextView right-click menu shows Copy/Select All
        // but they were confirmed live to render permanently insensitive in
        // this app (root cause not identified - GirCore/libadwaita binding
        // quirk, not anything this code disables). SetExtraMenu adds an
        // extra, unambiguously-working "Copy log" item alongside the
        // built-in (broken) ones, rather than depending on root-causing the
        // built-in menu's action-state wiring.
        var copyAction = Gio.SimpleAction.New("copy-log", null);
        copyAction.OnActivate += (_, _) => CopySelectionOrAll();
        var actions = Gio.SimpleActionGroup.New();
        actions.AddAction(copyAction);
        _textView.InsertActionGroup("logview", actions);

        var extraMenu = Gio.Menu.New();
        extraMenu.Append("Copy log", "logview.copy-log");
        _textView.SetExtraMenu(extraMenu);

        var scrolled = Gtk.ScrolledWindow.New();
        scrolled.SetChild(_textView);
        scrolled.SetVexpand(true);

        var toolbarView = Adw.ToolbarView.New();
        toolbarView.AddTopBar(headerBar);
        toolbarView.SetContent(scrolled);
        _window.SetContent(toolbarView);

        _window.OnCloseRequest += (_, _) =>
        {
            // "The toggle only takes effect while the window is open" -
            // MCP_INTERFACE.md. Re-enable unconditionally on close so a
            // closed (or crashed) window can never leave commands
            // silently disabled with no visible reminder.
            SetGateEnabled(true);
            return false; // let the window actually close
        };
    }

    public void Present() => _window.Present();

    public void AppendEntry(JsonElement entry)
    {
        string timestamp = entry.TryGetProperty("timestamp", out var t) ? t.GetString() ?? "" : "";
        string command = entry.GetProperty("command").GetString() ?? "?";
        bool success = entry.TryGetProperty("success", out var s) && s.GetBoolean();
        double durationMs = entry.TryGetProperty("durationMs", out var d) ? d.GetDouble() : 0;
        string error = entry.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? $" - {e.GetString()}" : "";
        string paramsJson = entry.TryGetProperty("params", out var p) && p.ValueKind != JsonValueKind.Null ? p.GetRawText() : "{}";
        string resultJson = entry.TryGetProperty("result", out var r) && r.ValueKind != JsonValueKind.Null ? r.GetRawText() : "null";

        string line = $"{timestamp}  {(success ? "OK " : "ERR")}  {command,-24} {durationMs,7:F0}ms{error}\n"
                     + $"    params: {paramsJson}\n"
                     + $"    result: {resultJson}\n";

        _buffer.GetEndIter(out Gtk.TextIter end);
        _buffer.Insert(end, line, line.Length);
        _buffer.GetEndIter(out Gtk.TextIter scrollTo);
        _textView.ScrollToIter(scrollTo, 0, false, 0, 0);
    }

    private void CopySelectionOrAll()
    {
        Gtk.TextIter start, end;
        if (!_buffer.GetSelectionBounds(out start, out end))
        {
            _buffer.GetStartIter(out start);
            _buffer.GetEndIter(out end);
        }
        string text = _buffer.GetText(start, end, false);
        _textView.GetClipboard().SetText(text);
    }

    /// <summary>
    /// Fire-and-forget on a background thread - never call DaemonClient.Send
    /// directly from a GTK event handler (this runs on the GLib main loop
    /// thread). Confirmed live as a real bug, not a theoretical one: doing
    /// that here froze the whole window solid on the very first checkbox
    /// click, the same class of problem Program.cs's poll loop was already
    /// fixed for - this call site was simply missed the first time around.
    /// No result to marshal back to the UI (the checkbox already reflects
    /// the click immediately; OnCloseRequest doesn't need to wait either),
    /// so unlike Program.cs's poll there's no GLib.Functions.IdleAdd
    /// call-back here - just don't block the main thread waiting for it.
    /// </summary>
    private static void SetGateEnabled(bool enabled)
    {
        Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse(enabled ? "{\"enabled\":true}" : "{\"enabled\":false}");
                DaemonClient.Send("__gate_set__", doc.RootElement.Clone());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"uictl-gui: failed to set commands-enabled to {enabled}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// The CLI already has `log export`; this just calls the same public
    /// `log.export` command (no explicit `out`, so the daemon picks its own
    /// default timestamped filename - the response's `path` field is always
    /// the fully-resolved path regardless, which is what gets shown here) so
    /// the window doesn't need to duplicate ActivityLog's serialization
    /// logic. Same background-thread-then-IdleAdd shape as the poll loop in
    /// Program.cs - this one DOES need to marshal back to the main thread,
    /// unlike SetGateEnabled, since it has a result to show the user.
    /// </summary>
    private void ExportLog()
    {
        Task.Run(() =>
        {
            try
            {
                using var doc = JsonDocument.Parse("{}");
                string response = DaemonClient.Send("log.export", doc.RootElement.Clone());
                GLib.Functions.IdleAdd(0, () =>
                {
                    ShowExportResult(response);
                    return false; // one-shot
                });
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                GLib.Functions.IdleAdd(0, () =>
                {
                    ShowExportFailure(message);
                    return false; // one-shot
                });
            }
        });
    }

    /// <summary>
    /// Runs back on the GLib main loop thread (via IdleAdd) - safe to touch
    /// GTK widgets here. A modal dialog, not a toast: the export itself is a
    /// logged `log.export` call, so the general per-call floating toast
    /// (Program.cs's Toast, one per command) fires for it at essentially the
    /// same moment - confirmed live, that toast was covering an
    /// Adw.Toast-in-window version of this message. A modal dialog can't be
    /// covered by anything and stays up until dismissed, which is also just
    /// a better fit than a 4-second timeout for "here's the exact path,
    /// possibly worth copying."
    /// </summary>
    private void ShowExportResult(string response)
    {
        using var responseDoc = JsonDocument.Parse(response);
        bool ok = responseDoc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
        if (ok && responseDoc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("path", out var path))
        {
            var dialog = Adw.MessageDialog.New(_window, "Export complete", path.GetString());
            dialog.AddResponse("ok", "OK");
            dialog.SetDefaultResponse("ok");
            dialog.SetCloseResponse("ok");
            dialog.Present();
        }
        else
        {
            string error = responseDoc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown error" : "unknown error";
            ShowExportFailure(error);
        }
    }

    private void ShowExportFailure(string message)
    {
        var dialog = Adw.MessageDialog.New(_window, "Export failed", message);
        dialog.AddResponse("ok", "OK");
        dialog.SetDefaultResponse("ok");
        dialog.SetCloseResponse("ok");
        dialog.Present();
    }
}
