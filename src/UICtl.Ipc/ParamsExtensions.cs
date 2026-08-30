using System.Text.Json;
using UICtl.Core;

namespace UICtl.Ipc;

/// <summary>Typed accessors over the raw JSON params object every command receives, whether it arrived from the CLI or an MCP tool call.</summary>
internal static class ParamsExtensions
{
    /// <summary>A reusable empty JSON object, for commands dispatched with no params (e.g. daemon lifecycle requests).</summary>
    public static readonly JsonElement Empty = JsonDocument.Parse("{}").RootElement;

    public static string? GetStringOrNull(this JsonElement element, string name) =>
        TryGetNonNull(element, name, out var v) ? v.GetString() : null;

    public static string GetStringOrThrow(this JsonElement element, string name) =>
        GetStringOrNull(element, name) ?? throw new UiCtlException($"\"{name}\" is required");

    public static bool GetBoolOrDefault(this JsonElement element, string name, bool defaultValue = false) =>
        TryGetNonNull(element, name, out var v) ? v.GetBoolean() : defaultValue;

    public static int? GetIntOrNull(this JsonElement element, string name) =>
        TryGetNonNull(element, name, out var v) ? v.GetInt32() : null;

    public static long? GetLongOrNull(this JsonElement element, string name) =>
        TryGetNonNull(element, name, out var v) ? v.GetInt64() : null;

    public static double? GetDoubleOrNull(this JsonElement element, string name) =>
        TryGetNonNull(element, name, out var v) ? v.GetDouble() : null;

    /// <summary>Parses an "x,y" string param (e.g. click/move/scroll's `at`) into a Point.</summary>
    public static Point? GetPointOrNull(this JsonElement element, string name)
    {
        string? raw = element.GetStringOrNull(name);
        if (raw is null) return null;

        string[] parts = raw.Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0].Trim(), out double x)
            || !double.TryParse(parts[1].Trim(), out double y))
            throw new UiCtlException($"\"{name}\" must be \"x,y\" (got \"{raw}\")");
        return new Point(x, y);
    }

    public static Point GetPointOrThrow(this JsonElement element, string name) =>
        GetPointOrNull(element, name) ?? throw new UiCtlException($"\"{name}\" is required");

    /// <summary>Parses an "x,y,w,h" string param (e.g. ocr's `region`) into a Frame.</summary>
    public static Frame? GetFrameOrNull(this JsonElement element, string name)
    {
        string? raw = element.GetStringOrNull(name);
        return raw is null ? null : Parsing.ParseFrame(raw);
    }

    private static bool TryGetNonNull(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null)
            return true;
        value = default;
        return false;
    }
}
