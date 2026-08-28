using System.Text.Json;

namespace UICtl.Ipc;

/// <summary>
/// The {"ok":true,"data":...} / {"ok":false,"error":...} response shape from
/// MCP_INTERFACE.md. Built as two distinct dictionaries (not one record with
/// nullable Data/Error) so success/failure never carry the other's key -
/// JsonSerializerOptions' null-suppression settings would otherwise also hide
/// legitimate nulls elsewhere in the payload (e.g. OCR confidence).
/// </summary>
public static class Envelope
{
    public static string Success(object data) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = true, ["data"] = data }, JsonOptions.Default);

    public static string Failure(string error) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["ok"] = false, ["error"] = error }, JsonOptions.Default);
}
