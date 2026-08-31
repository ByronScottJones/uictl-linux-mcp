using System.Diagnostics;
using System.Text.Json;
using UICtl.Core;

namespace UICtl.Ipc;

/// <summary>
/// The one switch every request goes through, regardless of front end (CLI or
/// MCP) - see ENGINEERING.md. Each case is a thin translator: parse JSON
/// params into typed Core calls, then shape the result into exactly the JSON
/// MCP_INTERFACE.md documents for that tool.
///
/// Phase 1: apps.list/windows.list/elements/type(--element), AT-SPI-based
/// (see Accessibility.cs). Phase 2: permissions.status/click/move/scroll/
/// key/type(synthesized), uinput-based (see InputSynthesis.cs). Phase 3:
/// activate/focus.hold/focus.release/focus.status (see WindowActivation.cs/
/// FocusHoldStore.cs) - click/move/scroll/key/type are wrapped with
/// WithFocusHold so a held window gets re-activated before every
/// focus-sensitive action. Phase 4: displays.list/screenshot/pixel (see
/// DisplayConfig.cs/Screenshot.cs/Pixel.cs), plus ocr (see Ocr.cs/
/// TesseractEngine.cs), clipboard.get/clipboard.set (see Clipboard.cs),
/// and waitFor (see WaitFor.cs) - Phase 4 is done. Phase 5: feedback.*
/// (see FeedbackStore.cs/FeedbackGitHub.cs), log.export/log.show (see
/// ActivityLog.cs/GuiLauncher.cs/UICtl.Gui) - every non-double-underscore
/// command recorded to ActivityLog from Dispatch itself, so no
/// per-command wiring was needed, and the daemon lazily launches
/// UICtl.Gui the same way on the first one. The commands-enabled kill
/// switch (UICtlGate.cs) is checked here too, for every non-internal
/// command, before Execute runs.
/// </summary>
public static class CommandDispatcher
{
    public static string Dispatch(string command, JsonElement @params)
    {
        // Double-underscore commands (__ping__, __gate_set__, __log_list__,
        // ...) are daemon lifecycle/internal plumbing - not gated (so
        // UICtl.Gui can always re-enable commands even while disabled), not
        // something a human reviewing "what has this daemon actually done"
        // cares about (see DaemonServer.cs's own doc comment for
        // __daemon_log_warning__, handled the same way by never reaching
        // this method at all), and not what should trigger lazily starting
        // the GUI helper - a daemon that's only ever received health checks
        // hasn't done anything a human needs a toast/log window for yet.
        //
        // __log_list__ specifically *must* stay exempt from ActivityLog
        // recording, not just as a style choice: it returns the log's own
        // current contents, so recording it would make each poll's own
        // result embed every previous entry (including previous polls'
        // results, which already embedded everything before *them*) -
        // confirmed live as a real, fast-onset bug the first time this was
        // wired up with UICtl.Gui actually polling every 300ms: within a
        // few seconds this exponential nesting blew past System.Text.Json's
        // serialization depth limit and the resulting oversized object
        // graph made the single-connection-at-a-time daemon (DaemonServer.cs)
        // slow enough on every subsequent request that it looked hung from
        // the outside.
        bool isInternal = command.StartsWith("__", StringComparison.Ordinal);
        if (!isInternal) GuiLauncher.EnsureStarted();

        var stopwatch = Stopwatch.StartNew();
        object? result = null;
        string? error = null;
        bool success = false;
        string response;
        try
        {
            if (!isInternal && !UICtlGate.Enabled)
                throw new UiCtlException("commands are currently disabled - see the uictl activity log window");
            result = Execute(command, @params);
            response = Envelope.Success(result);
            success = true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            response = Envelope.Failure(error);
        }
        stopwatch.Stop();

        if (!isInternal)
            ActivityLog.Record(command, @params, success, result, error, stopwatch.Elapsed);

        return response;
    }

    private static object Execute(string command, JsonElement p) => command switch
    {
        "__ping__" => new Dictionary<string, object?> { ["pong"] = true },
        "__gate_set__" => GateSet(p),
        "__log_list__" => LogList(p),

        "apps.list" => new Dictionary<string, object?> { ["apps"] = Accessibility.ListApps(p.GetBoolOrDefault("all")) },
        "windows.list" => WindowsList(p),
        "displays.list" => new Dictionary<string, object?> { ["displays"] = DisplayConfig.List() },
        "elements" => Elements(p),
        "type" => WithFocusHold(() => TypeText(p)),
        "permissions.status" => Permissions.GetStatus(),
        "click" => WithFocusHold(() => Click(p)),
        "move" => WithFocusHold(() => Move(p)),
        "scroll" => WithFocusHold(() => Scroll(p)),
        "key" => WithFocusHold(() => Key(p)),
        "activate" => Activate(p),
        "focus.hold" => FocusHold(p),
        "focus.release" => FocusHoldStore.Release(),
        "focus.status" => FocusHoldStore.Status(),
        "screenshot" => CaptureScreenshot(p),
        "pixel" => Pixel.At(p.GetPointOrThrow("at")),
        "ocr" => RunOcr(p),
        "clipboard.get" => new Dictionary<string, object?> { ["text"] = Clipboard.Get() },
        "clipboard.set" => ClipboardSet(p),
        "waitFor" => RunWaitFor(p),
        "feedback.create" => FeedbackCreate(p),
        "feedback.list" => new Dictionary<string, object?> { ["entries"] = FeedbackStore.List() },
        "feedback.get" => FeedbackStore.Get(GetIdOrThrow(p)),
        "feedback.update" => FeedbackUpdate(p),
        "feedback.delete" => FeedbackDelete(p),
        "feedback.checkDuplicates" => FeedbackCheckDuplicates(p),
        "feedback.submit" => FeedbackSubmit(p),
        "log.export" => LogExport(p),
        "log.show" => LogShow(),

        _ => throw new UiCtlException($"not implemented yet: {command}"),
    };

    private static object WindowsList(JsonElement p)
    {
        int? pidFilter = p.GetStringOrNull("app") is { } app ? AppSelector.Resolve(app) : null;
        return new Dictionary<string, object?> { ["windows"] = Accessibility.ListWindows(pidFilter) };
    }

    private static object Elements(JsonElement p)
    {
        var resolved = WindowResolver.Resolve(p.GetLongOrNull("window"), p.GetStringOrNull("app"));
        var options = new ElementWalkOptions(
            RoleFilter: p.GetStringOrNull("role"),
            TitleContains: p.GetStringOrNull("title"),
            MaxDepth: p.GetIntOrNull("maxDepth") ?? 25,
            MaxElements: p.GetIntOrNull("maxElements") ?? 500);
        var walk = Accessibility.WalkWindow(resolved.WindowId, options);

        var data = new Dictionary<string, object?>
        {
            ["windowId"] = resolved.WindowId,
            ["count"] = walk.Elements.Count,
            ["elements"] = walk.Elements,
        };
        if (walk.Truncated) data["truncated"] = true;
        return data;
    }

    private static Dictionary<string, object?> TypeText(JsonElement p)
    {
        string text = p.GetStringOrThrow("text");
        string? elementId = p.GetStringOrNull("element");
        if (elementId is null)
        {
            InputSynthesis.TypeText(text);
            return new Dictionary<string, object?> { ["method"] = "synthesizedKeystrokes" };
        }

        string method = Accessibility.SetElementText(elementId, text);
        return new Dictionary<string, object?> { ["method"] = method, ["element"] = elementId };
    }

    private static Dictionary<string, object?> Click(JsonElement p)
    {
        string? elementId = p.GetStringOrNull("element");
        if (elementId is null && p.GetStringOrNull("at") is null)
            throw new UiCtlException("either \"at\" or \"element\" is required");
        Point at = elementId is { } id ? Accessibility.GetElementFrame(id).Center : p.GetPointOrThrow("at");

        MouseButton button = ParseButton(p.GetStringOrNull("button"));
        int count = p.GetIntOrNull("count") ?? (p.GetBoolOrDefault("double") ? 2 : 1);

        InputSynthesis.Click(at, button, count);
        return new Dictionary<string, object?> { ["clicked"] = at };
    }

    private static Dictionary<string, object?> Move(JsonElement p)
    {
        Point at = p.GetPointOrThrow("at");
        InputSynthesis.Move(at);
        return new Dictionary<string, object?> { ["moved"] = true };
    }

    private static Dictionary<string, object?> Scroll(JsonElement p)
    {
        Point at = p.GetPointOrThrow("at");
        int dx = p.GetIntOrNull("dx") ?? 0;
        int dy = p.GetIntOrNull("dy") ?? 0;
        InputSynthesis.Scroll(at, dx, dy);
        return new Dictionary<string, object?> { ["scrolled"] = true };
    }

    private static Dictionary<string, object?> Key(JsonElement p)
    {
        string combo = p.GetStringOrThrow("combo");
        InputSynthesis.SendKeyCombo(combo);
        return new Dictionary<string, object?> { ["sent"] = combo };
    }

    private static MouseButton ParseButton(string? button) => button?.ToLowerInvariant() switch
    {
        null or "left" => MouseButton.Left,
        "right" => MouseButton.Right,
        "center" or "middle" => MouseButton.Center,
        _ => throw new UiCtlException($"unknown button \"{button}\" (expected left, right, or center)"),
    };

    private static object Activate(JsonElement p)
    {
        string app = p.GetStringOrThrow("app");
        int pid = AppSelector.Resolve(app);
        long? windowId = p.GetLongOrNull("window");
        WindowActivation.ActivateForApp(pid, windowId);
        return new Dictionary<string, object?> { ["pid"] = pid };
    }

    private static object FocusHold(JsonElement p)
    {
        string? appSelector = p.GetStringOrNull("app");
        var resolved = WindowResolver.Resolve(p.GetLongOrNull("window"), appSelector);
        string label = appSelector ?? resolved.Pid.ToString();
        return FocusHoldStore.Hold(label, resolved.Pid, resolved.WindowId);
    }

    private static Dictionary<string, object?> CaptureScreenshot(JsonElement p)
    {
        long? windowId = p.GetLongOrNull("window");
        string? app = p.GetStringOrNull("app");
        int? screen = p.GetIntOrNull("screen");
        bool annotate = p.GetBoolOrDefault("annotate");
        string? role = p.GetStringOrNull("role");
        string outPath = p.GetStringOrThrow("out");

        var result = Screenshot.Capture(windowId, app, screen, annotate, role, outPath);
        var data = new Dictionary<string, object?>
        {
            ["path"] = result.Path,
            ["width"] = result.Width,
            ["height"] = result.Height,
        };
        if (result.Elements is not null) data["elements"] = result.Elements;
        return data;
    }

    private static Dictionary<string, object?> RunOcr(JsonElement p)
    {
        var blocks = Ocr.Read(
            imagePath: p.GetStringOrNull("image"),
            windowId: p.GetLongOrNull("window"),
            appSelector: p.GetStringOrNull("app"),
            region: p.GetFrameOrNull("region"));
        return new Dictionary<string, object?> { ["textBlocks"] = blocks };
    }

    private static Dictionary<string, object?> ClipboardSet(JsonElement p)
    {
        Clipboard.Set(p.GetStringOrThrow("text"));
        return new Dictionary<string, object?> { ["set"] = true };
    }

    private static int GetIdOrThrow(JsonElement p) => p.GetIntOrNull("id") ?? throw new UiCtlException("\"id\" is required");

    private static FeedbackEntry FeedbackCreate(JsonElement p) =>
        FeedbackStore.Create(p.GetStringOrThrow("category"), p.GetStringOrThrow("title"), p.GetStringOrThrow("body"));

    private static FeedbackEntry FeedbackUpdate(JsonElement p) =>
        FeedbackStore.Update(GetIdOrThrow(p), p.GetStringOrNull("category"), p.GetStringOrNull("title"), p.GetStringOrNull("body"));

    private static Dictionary<string, object?> FeedbackDelete(JsonElement p)
    {
        int id = GetIdOrThrow(p);
        FeedbackStore.Delete(id);
        return new Dictionary<string, object?> { ["deleted"] = true, ["id"] = id };
    }

    private static Dictionary<string, object?> FeedbackCheckDuplicates(JsonElement p)
    {
        var entry = FeedbackStore.Get(GetIdOrThrow(p));
        var duplicates = FeedbackGitHub.CheckDuplicates(entry, p.GetStringOrNull("repo"), p.GetStringOrNull("token"));
        return new Dictionary<string, object?> { ["duplicates"] = duplicates };
    }

    private static Dictionary<string, object?> FeedbackSubmit(JsonElement p)
    {
        var entry = FeedbackStore.Get(GetIdOrThrow(p));
        var (duplicates, issueUrl, opened) = FeedbackGitHub.Submit(entry, p.GetStringOrNull("repo"), p.GetStringOrNull("token"));
        return new Dictionary<string, object?> { ["duplicates"] = duplicates, ["issueUrl"] = issueUrl, ["opened"] = opened };
    }

    /// <summary>UICtl.Gui's own polling call - double-underscore-prefixed (`__log_list__`) so it's exempt from ActivityLog recording (see Dispatch's own doc comment for why that's load-bearing, not just style) and never registered in the public CLI/MCP surface. `log.export`'s file-writing sibling is the one humans/scripts use.</summary>
    private static Dictionary<string, object?> LogList(JsonElement p)
    {
        IEnumerable<ActivityLogEntry> entries = ActivityLog.GetAll();
        if (p.GetIntOrNull("afterId") is { } afterId)
            entries = entries.Where(e => e.Id > afterId);
        return new Dictionary<string, object?> { ["entries"] = entries.ToList() };
    }

    /// <summary>UICtl.Gui-only - the `__gate_set__` name is double-underscore-prefixed deliberately (see Dispatch's own isInternal check), so toggling it is itself exempt from the gate it controls and isn't recorded as activity-log noise, and it's never registered in the CLI/MCP command surface - "no command to disable commands, only the on-screen checkbox" per MCP_INTERFACE.md.</summary>
    private static Dictionary<string, object?> GateSet(JsonElement p)
    {
        bool enabled = p.GetBoolOrDefault("enabled", true);
        UICtlGate.Set(enabled);
        return new Dictionary<string, object?> { ["enabled"] = enabled };
    }

    private static Dictionary<string, object?> LogShow()
    {
        GuiLauncher.ShowLogWindow();
        return new Dictionary<string, object?> { ["shown"] = true };
    }

    private static Dictionary<string, object?> LogExport(JsonElement p)
    {
        string outPath = p.GetStringOrNull("out") is { Length: > 0 } o ? o : $"uictl-activity-log-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        string fullOutPath = Path.GetFullPath(outPath);
        string? dir = Path.GetDirectoryName(fullOutPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(fullOutPath, JsonSerializer.Serialize(ActivityLog.GetAll(), JsonOptions.Default));
        return new Dictionary<string, object?> { ["path"] = fullOutPath };
    }

    private static Dictionary<string, object?> RunWaitFor(JsonElement p)
    {
        var (found, element) = WaitFor.Poll(
            windowId: p.GetLongOrNull("window"),
            appSelector: p.GetStringOrNull("app"),
            roleFilter: p.GetStringOrNull("role"),
            titleContains: p.GetStringOrNull("title"),
            timeoutSeconds: p.GetDoubleOrNull("timeout"));
        var data = new Dictionary<string, object?> { ["found"] = found };
        if (element is not null) data["element"] = element;
        return data;
    }

    /// <summary>
    /// Wraps click/move/scroll/key/type: while a hold is active, re-activates
    /// the held window *before* the action runs (a human may have clicked
    /// away since the hold started), then adds the compact "focusHold"
    /// marker to the response - see FocusHoldStore.ReassertIfHeld. Omits
    /// the field entirely when no hold is active, matching this codebase's
    /// existing convention for optional fields (e.g. Elements' "truncated").
    /// </summary>
    private static Dictionary<string, object?> WithFocusHold(Func<Dictionary<string, object?>> action)
    {
        var focusHold = FocusHoldStore.ReassertIfHeld();
        var data = action();
        if (focusHold is not null) data["focusHold"] = focusHold;
        return data;
    }
}
