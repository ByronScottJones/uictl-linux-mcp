using System.Text.Json;
using UICtl.Ipc;

namespace UICtl.Cli;

/// <summary>
/// Every leaf command funnels through here: build a params JsonElement, send
/// it to the daemon, print exactly one JSON object to stdout, exit 0/non-zero
/// based on its "ok" field. A DaemonClient failure (e.g. couldn't reach/spawn
/// the daemon) is caught here too, so the CLI's "always one JSON object"
/// contract holds even for infrastructure failures, not just capability ones.
/// </summary>
internal static class CliRunner
{
    public static async Task<int> RunAsync(string command, Dictionary<string, object?> args, CancellationToken ct)
    {
        string response;
        try
        {
            var paramsElement = JsonSerializer.SerializeToElement(args);
            response = await Task.Run(() => DaemonClient.Send(command, paramsElement), ct);
        }
        catch (Exception ex)
        {
            response = Envelope.Failure(ex.Message);
        }

        return Print(response);
    }

    public static int Print(string response)
    {
        Console.WriteLine(response);
        bool ok = JsonDocument.Parse(response).RootElement.TryGetProperty("ok", out var okProp) && okProp.GetBoolean();
        return ok ? 0 : 1;
    }
}
