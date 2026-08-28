using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using UICtl.Core;

namespace UICtl.Ipc;

/// <summary>
/// Sends one JSON request and reads one JSON response per call, auto-spawning
/// the daemon if the Unix domain socket isn't reachable. Used by both CLI
/// subcommands and MCP tool handlers, so element ids and permission state are
/// shared no matter which front end a caller used to get there - see
/// ENGINEERING.md.
/// </summary>
public static class DaemonClient
{
    private const int SpawnWaitMs = 5000;

    public static string Send(string command, JsonElement @params)
    {
        using var stream = Connect();
        string request = JsonSerializer.Serialize(new { command, @params }, JsonOptions.Default);
        Framing.WriteMessageAsync(stream, request).GetAwaiter().GetResult();
        return Framing.ReadMessageAsync(stream).GetAwaiter().GetResult();
    }

    public static bool IsRunning()
    {
        var stream = TryConnectOnce(200);
        stream?.Dispose();
        return stream is not null;
    }

    public static string RequestStop()
    {
        using var stream = TryConnectOnce(500) ?? throw new UiCtlException("the daemon is not running");
        string request = JsonSerializer.Serialize(new { command = "__daemon_stop__", @params = ParamsExtensions.Empty }, JsonOptions.Default);
        Framing.WriteMessageAsync(stream, request).GetAwaiter().GetResult();
        return Framing.ReadMessageAsync(stream).GetAwaiter().GetResult();
    }

    /// <summary>Connects (spawning and waiting for the daemon if needed) without sending a real command - used by "daemon start" to report readiness without dispatching a capability.</summary>
    public static void EnsureRunning()
    {
        using var stream = Connect();
    }

    private static NetworkStream Connect()
    {
        if (TryConnectOnce(200) is { } stream) return stream;

        SpawnDaemon();

        var deadline = DateTime.UtcNow.AddMilliseconds(SpawnWaitMs);
        while (DateTime.UtcNow < deadline)
        {
            if (TryConnectOnce(250) is { } spawned) return spawned;
            Thread.Sleep(100);
        }
        throw new UiCtlException($"could not reach the uictl daemon after spawning it - check {DaemonPaths.LogPath}");
    }

    private static NetworkStream? TryConnectOnce(int timeoutMs)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            var connectTask = socket.ConnectAsync(new UnixDomainSocketEndPoint(DaemonPaths.SocketPath));
            if (!connectTask.Wait(timeoutMs))
            {
                socket.Dispose();
                return null;
            }
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch (Exception)
        {
            socket.Dispose();
            return null;
        }
    }

    private static void SpawnDaemon()
    {
        string exePath = Environment.ProcessPath ?? throw new UiCtlException("could not determine this process's own path to spawn the daemon");
        Directory.CreateDirectory(DaemonPaths.BaseDir);

        var startInfo = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("daemon");
        startInfo.ArgumentList.Add("start");
        startInfo.ArgumentList.Add("--foreground");

        _ = Process.Start(startInfo) ?? throw new UiCtlException("failed to start the daemon process");
    }
}
