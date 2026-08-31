using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using UICtl.Core;

namespace UICtl.Ipc;

/// <summary>
/// Accepts one Unix domain socket connection at a time and runs each request
/// through CommandDispatcher - see ENGINEERING.md for why one connection at a
/// time (no locking needed around whatever per-window state Core ends up
/// caching, e.g. the future AT-SPI element-id store).
/// </summary>
public static class DaemonServer
{
    private const string StopCommand = "__daemon_stop__";

    /// <summary>
    /// Lets a caller running in a different process get a line into
    /// daemon.log without racing the daemon's own StreamWriter (which holds
    /// the file open with no write-sharing) - see uictl-win-mcp's
    /// ENGINEERING.md for the bug this pattern avoids. Bypasses
    /// CommandDispatcher/the commands-enabled gate/ActivityLog on purpose:
    /// this is a diagnostic log line, not an automation command.
    /// </summary>
    private const string LogWarningCommand = "__daemon_log_warning__";

    public static async Task RunForegroundAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(DaemonPaths.BaseDir);
        RedirectConsoleToLogFile();

        // Confirmed empirically: DaemonClient.SpawnDaemon()'s Process.Start
        // leaves the child in the same session as the CLI process that
        // spawned it (no Windows-style CREATE_NO_WINDOW detachment exists on
        // Linux). Once that CLI process exits and its controlling
        // terminal/session goes away (e.g. the SSH/WSL session that ran
        // `uictl daemon start` closes), the kernel sends SIGHUP to every
        // process still in that session - which killed this daemon outright
        // the first time this was tested, with no "uictl daemon stopped"
        // log line (an abrupt kill, not a graceful exit). Ignoring SIGHUP is
        // the standard, dependency-free way a background process survives
        // its launching session ending; the process is then reparented to
        // init and keeps running exactly like a *nix daemon should.
        using var _ = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context => context.Cancel = true);

        using var listenSocket = Bind();
        Console.WriteLine($"[{DateTime.UtcNow:O}] uictl daemon starting, socket {DaemonPaths.SocketPath}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket accepted;
                try
                {
                    accepted = await listenSocket.AcceptAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using var stream = new NetworkStream(accepted, ownsSocket: true);
                if (await HandleConnectionAsync(stream, ct))
                    break;
            }
        }
        finally
        {
            TryDeleteSocketFile();
        }

        Console.WriteLine($"[{DateTime.UtcNow:O}] uictl daemon stopped");
    }

    /// <summary>
    /// A stale socket file left over from an unclean shutdown makes Bind()
    /// fail with "address already in use" even though nothing is listening -
    /// remove it first, but only after confirming nothing is actually
    /// answering on it (this method may run because a human directly typed
    /// `daemon start --foreground` against an already-running daemon, not
    /// only via DaemonClient's own spawn-on-miss path).
    /// </summary>
    private static Socket Bind()
    {
        if (File.Exists(DaemonPaths.SocketPath) && !DaemonClient.IsRunning())
            File.Delete(DaemonPaths.SocketPath);

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(DaemonPaths.SocketPath));
        // A backlog of 1 was fine when only occasional, human-paced CLI
        // calls connected, but UICtl.Gui (Phase 5) polls every ~300ms in
        // the background - confirmed live: with backlog 1, a second client
        // connecting while that poll's connection is still pending gets
        // refused outright, DaemonClient.Connect() reads that as "the
        // daemon is dead" and spawns a redundant one (which exits quickly
        // once Bind() here hits "address already in use", since the real
        // daemon is fine - not a duplicate daemon problem), then retries
        // for up to 5s - from the outside this looked exactly like the
        // whole daemon hanging. 16 gives enough queue depth that a request
        // arriving mid-poll just waits its turn instead of being refused.
        socket.Listen(backlog: 16);
        return socket;
    }

    private static void TryDeleteSocketFile()
    {
        try { File.Delete(DaemonPaths.SocketPath); } catch { /* best effort */ }
    }

    /// <returns>true if this was a stop request and the accept loop should exit.</returns>
    private static async Task<bool> HandleConnectionAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            string requestJson = await Framing.ReadMessageAsync(stream, ct);
            using var requestDoc = JsonDocument.Parse(requestJson);
            var request = requestDoc.RootElement;

            string command = request.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
            var paramsElement = request.TryGetProperty("params", out var p) ? p : ParamsExtensions.Empty;

            if (command == StopCommand)
            {
                await Framing.WriteMessageAsync(stream, Envelope.Success(new Dictionary<string, object?> { ["stopped"] = true }), ct);
                // A hard, immediate exit rather than breaking the accept loop
                // and unwinding "gracefully" - mirrors macOS's/Windows'
                // DaemonServer, in case a future phase adds a blocking GUI
                // message pump on another thread that nothing else would
                // otherwise signal to shut down.
                Environment.Exit(0);
                return true;
            }

            if (command == LogWarningCommand)
            {
                string message = paramsElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                Console.WriteLine($"[{DateTime.UtcNow:O}] WARNING: {message}");
                await Framing.WriteMessageAsync(stream, Envelope.Success(new Dictionary<string, object?> { ["logged"] = true }), ct);
                return false;
            }

            string response = CommandDispatcher.Dispatch(command, paramsElement);
            await Framing.WriteMessageAsync(stream, response, ct);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.UtcNow:O}] request failed: {ex}");
            try
            {
                await Framing.WriteMessageAsync(stream, Envelope.Failure(ex.Message), ct);
            }
            catch
            {
                // client already disconnected; nothing to report to
            }
        }
        return false;
    }

    private static void RedirectConsoleToLogFile()
    {
        var writer = new StreamWriter(DaemonPaths.LogPath, append: true) { AutoFlush = true };
        Console.SetOut(writer);
        Console.SetError(writer);
    }
}
