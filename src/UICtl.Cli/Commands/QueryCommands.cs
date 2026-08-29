using System.CommandLine;

namespace UICtl.Cli.Commands;

internal static class QueryCommands
{
    public static Command Apps()
    {
        var all = new Option<bool>("--all") { Description = "Include background/service processes, not just AT-SPI-registered apps with a window." };

        var cmd = new Command("apps", "List running applications.");
        cmd.Add(all);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?> { ["all"] = pr.GetValue(all) };
            return await CliRunner.RunAsync("apps.list", args, ct);
        });
        return cmd;
    }

    public static Command Windows()
    {
        var app = new Option<string?>("--app") { Description = "Filter to windows owned by this app (name substring or pid)." };

        var cmd = new Command("windows", "List on-screen windows, optionally filtered to one app. AT-SPI-based - see AGENTS.md for Wayland/GNOME-Shell-extension caveats.");
        cmd.Add(app);
        cmd.SetAction(async (pr, ct) =>
        {
            var args = new Dictionary<string, object?>();
            if (pr.GetValue(app) is { } a) args["app"] = a;
            return await CliRunner.RunAsync("windows.list", args, ct);
        });
        return cmd;
    }
}
