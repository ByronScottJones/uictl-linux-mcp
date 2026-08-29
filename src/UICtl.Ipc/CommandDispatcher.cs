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
/// Phase 1 so far: apps.list/windows.list/elements/type, all AT-SPI-based
/// (see Accessibility.cs). Everything else (activate/focus/screenshot/
/// click/move/scroll/key/ocr/pixel/clipboard, feedback, log) still throws
/// "not implemented yet" - later phases. ActivityLog/UICtlGate gating (both
/// still forward through unconditionally) arrives in Phase 5, same as
/// macOS/Windows.
/// </summary>
public static class CommandDispatcher
{
    public static string Dispatch(string command, JsonElement @params)
    {
        var stopwatch = Stopwatch.StartNew();
        string response = DispatchInner(command, @params);
        _ = stopwatch.Elapsed; // timing wired up properly once ActivityLog lands (Phase 5)
        return response;
    }

    private static string DispatchInner(string command, JsonElement @params)
    {
        try
        {
            return Envelope.Success(Execute(command, @params));
        }
        catch (Exception ex)
        {
            return Envelope.Failure(ex.Message);
        }
    }

    private static object Execute(string command, JsonElement p) => command switch
    {
        "__ping__" => new Dictionary<string, object?> { ["pong"] = true },

        "apps.list" => new Dictionary<string, object?> { ["apps"] = Accessibility.ListApps(p.GetBoolOrDefault("all")) },
        "windows.list" => WindowsList(p),
        "elements" => Elements(p),
        "type" => TypeText(p),

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

    private static object TypeText(JsonElement p)
    {
        string text = p.GetStringOrThrow("text");
        string? elementId = p.GetStringOrNull("element");
        if (elementId is null)
            throw new UiCtlException("typing into whatever has focus (no --element) needs synthesized keystrokes, not implemented yet - see ENGINEERING.md's build plan");

        string method = Accessibility.SetElementText(elementId, text);
        return new Dictionary<string, object?> { ["method"] = method, ["element"] = elementId };
    }
}
