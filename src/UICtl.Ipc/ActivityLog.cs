using System.Text.Json;
using UICtl.Core;

namespace UICtl.Ipc;

/// <summary>
/// In-memory, capped (~2000 entries) record of every command
/// CommandDispatcher has handled - the data half of `log.export`/
/// `log.show` (see MCP_INTERFACE.md's Activity log section; `log.show`'s
/// GTK4 window is a deliberate follow-up, not built here - this class is
/// what it will read from once it exists).
///
/// Redaction: `text` is replaced with a placeholder only for `type`'s and
/// `clipboard.set`'s params, and `clipboard.get`'s result - the exact
/// three cases MCP_INTERFACE.md calls out. `ocr`/`elements`/`screenshot`
/// output (which can also carry meaningful text - recognized words,
/// element titles) is deliberately left alone, matching the documented
/// contract and AGENTS.md's own gotcha about it. Everything else is
/// logged verbatim - this is an audit trail, not a second redaction
/// layer for arbitrary command params.
/// </summary>
internal static class ActivityLog
{
    private const int Capacity = 2000;
    private const string Redacted = "[redacted]";

    private static readonly HashSet<string> ParamTextRedactedCommands = new() { "type", "clipboard.set" };
    private static readonly HashSet<string> ResultTextRedactedCommands = new() { "clipboard.get" };

    private static readonly object Lock = new();
    private static readonly Queue<ActivityLogEntry> Entries = new();
    private static int _nextId = 1;

    public static void Record(string command, JsonElement rawParams, bool success, object? result, string? error, TimeSpan duration)
    {
        object? loggedParams = RedactParamsIfNeeded(command, rawParams);
        object? loggedResult = RedactResultIfNeeded(command, result);

        lock (Lock)
        {
            Entries.Enqueue(new ActivityLogEntry(_nextId++, DateTimeOffset.UtcNow, command, loggedParams, success, loggedResult, error, duration.TotalMilliseconds));
            while (Entries.Count > Capacity) Entries.Dequeue();
        }
    }

    public static IReadOnlyList<ActivityLogEntry> GetAll()
    {
        lock (Lock) return Entries.ToArray();
    }

    private static object? RedactParamsIfNeeded(string command, JsonElement rawParams)
    {
        if (rawParams.ValueKind != JsonValueKind.Object)
            return ToPlainObject(rawParams);

        bool redact = ParamTextRedactedCommands.Contains(command);
        var dict = new Dictionary<string, object?>();
        foreach (JsonProperty prop in rawParams.EnumerateObject())
            dict[prop.Name] = redact && prop.NameEquals("text") ? Redacted : ToPlainObject(prop.Value);
        return dict;
    }

    private static object? RedactResultIfNeeded(string command, object? result)
    {
        if (ResultTextRedactedCommands.Contains(command) && result is Dictionary<string, object?> dict && dict.ContainsKey("text"))
            return new Dictionary<string, object?>(dict) { ["text"] = Redacted };
        return result;
    }

    /// <summary>JsonElement -&gt; plain CLR values (Dictionary/List/string/number/bool/null), so ActivityLogEntry.Params serializes the same way whether it came from a redacted or a passed-through command, and doesn't hold a JsonElement past the request it was parsed from.</summary>
    private static object? ToPlainObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ToPlainObject(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(ToPlainObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out long l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
