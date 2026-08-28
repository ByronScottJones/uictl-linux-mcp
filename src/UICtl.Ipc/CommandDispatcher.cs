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
/// Currently a stub with no real cases - Core has no AT-SPI/X11/Wayland
/// implementations yet (Phase 1+). This exists now so the daemon/IPC/socket
/// round trip can be proven end-to-end before any platform capability is
/// built on top of it. ActivityLog/UICtlGate gating (both empty daemon calls
/// forward through unconditionally today) arrives in Phase 5, same as
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
        _ => throw new UiCtlException($"not implemented yet: {command}"),
    };
}
