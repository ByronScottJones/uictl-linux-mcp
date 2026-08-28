using System.CommandLine;
using UICtl.Ipc;

namespace UICtl.Cli.Commands;

internal static class DaemonCommands
{
    public static Command Daemon()
    {
        var cmd = new Command("daemon", "Manage the background uictl daemon that holds platform state (AT-SPI/D-Bus connections, the future element-id cache) across invocations.");
        cmd.Add(Start());
        cmd.Add(Stop());
        cmd.Add(Status());
        return cmd;
    }

    private static Command Start()
    {
        var foreground = new Option<bool>("--foreground")
        {
            Description = "Run in the foreground instead of spawning a detached background process. Used internally by other commands to bootstrap the daemon.",
        };

        var cmd = new Command("start", "Start the daemon (auto-invoked by any other command if it isn't already running).");
        cmd.Add(foreground);
        cmd.SetAction(async (pr, ct) =>
        {
            if (pr.GetValue(foreground))
            {
                // A dedicated GTK GUI thread (toast + activity log window)
                // joins this in Phase 5, mirroring macOS/Windows - for now
                // the accept loop just runs directly.
                await DaemonServer.RunForegroundAsync(ct);
                return 0;
            }

            DaemonClient.EnsureRunning();
            return CliRunner.Print(Envelope.Success(new Dictionary<string, object?> { ["started"] = true }));
        });
        return cmd;
    }

    private static Command Stop()
    {
        var cmd = new Command("stop", "Stop the running daemon.");
        cmd.SetAction((_) =>
        {
            if (!DaemonClient.IsRunning())
                return CliRunner.Print(Envelope.Success(new Dictionary<string, object?> { ["stopped"] = false, ["reason"] = "not running" }));

            try
            {
                return CliRunner.Print(DaemonClient.RequestStop());
            }
            catch (Exception ex)
            {
                return CliRunner.Print(Envelope.Failure(ex.Message));
            }
        });
        return cmd;
    }

    private static Command Status()
    {
        var cmd = new Command("status", "Report whether the daemon is running.");
        cmd.SetAction((_) =>
        {
            bool running = DaemonClient.IsRunning();
            return CliRunner.Print(Envelope.Success(new Dictionary<string, object?> { ["running"] = running, ["socket"] = DaemonSocketPath }));
        });
        return cmd;
    }

    // Kept in sync with DaemonPaths.SocketPath by hand (Ipc's DaemonPaths type
    // is internal to that assembly) - just a display string for `daemon status`.
    private const string DaemonSocketPath = "~/.uictl/uictl.sock";
}
